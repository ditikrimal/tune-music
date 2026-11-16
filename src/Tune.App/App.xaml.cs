using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Tune.App.Audio;
using Tune.App.Mini;
using Tune.App.Platform;
using Tune.App.Shell;
using Tune.App.ViewModels;
using Tune.Core.Feed;
using Tune.Core.Library;
using Tune.Core.Playback;
using Tune.Core.SponsorBlock;
using Tune.Core.Streams;
using Tune.Core.Util;
using Tune.Core.YtMusic;

namespace Tune.App;

public partial class App : Application
{
    private const string MutexName = "Tune.SingleInstance";
    private const string ActivateEventName = "Tune.Activate";

    private static bool _quitting;
    public static bool IsQuitting => _quitting;
    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private MpvPlayer? _player;
    private static volatile PythonWorkerBackend? _worker;

    /// <summary>Shown in Settings → About.</summary>
    public static string ResolverStatus { get; private set; } = "Standard (yt-dlp.exe)";

    public static void SetMiniStyle(Tune.App.Mini.WidgetStyle style)
    {
        ShowMini();
        _mini?.ApplyStyle(style);
    }

    public static Tune.App.Mini.WidgetStyle MiniStyle =>
        Enum.TryParse<Tune.App.Mini.WidgetStyle>(Services.Library.GetSetting("mini.style"), out var s) ? s : Tune.App.Mini.WidgetStyle.Compact;

    public static void ApplyMiniTopmost()
    {
        if (_mini is not null) _mini.Topmost = Services.Library.GetSetting("mini.topmost") != "0";
    }
    private static string? _python;

    /// <summary>
    /// YouTube links only work from the IP that fetched them. Fetch one link with the worker and try it from
    /// Tune's own route (the same one mpv uses); if it's refused (e.g. by a VPN rule), the worker is unusable.
    /// </summary>
    private static async Task<bool> WorkerLinksPlayHereAsync(PythonWorkerBackend worker)
    {
        try
        {
            var s = await worker.ExtractAsync("oyr9_tu_F2U", CancellationToken.None);
            var wait = DateTimeOffset.FromUnixTimeSeconds(s.AvailableAtUnix) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromSeconds(1));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            Log.Info($"fast resolver link check: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or StreamResolveException)
        {
            Log.Warn("fast resolver link check failed", e);
            return false;
        }
    }

    /// <summary>Installs Tune's private yt-dlp Python package if needed and starts the resolver worker.</summary>
    private static async Task StartWorkerAsync(ToolManager tools, string dataDir, bool restart = false)
    {
        if (_python is null) return;
        try
        {
            if (!await tools.EnsurePyLibAsync(_python, update: false)) return;
            var py = tools.PreparePrivatePython(_python);
            var fresh = new PythonWorkerBackend(py.Exe, tools.PyLibDir, tools.DenoPath, Path.Combine(dataDir, "worker"), pythonHome: py.Home);
            var version = await fresh.StartAsync();
            if (!await WorkerLinksPlayHereAsync(fresh))
            {
                Log.Warn("fast resolver's links are refused from Tune's network route; staying on yt-dlp.exe");
                fresh.Dispose();
                return;
            }
            var old = _worker;
            _worker = fresh;
            if (restart) old?.Dispose();
            Log.Info($"fast resolver ready (yt-dlp {version})");
            ResolverStatus = $"Fast (yt-dlp {version}, songs prepared ahead)";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn("fast resolver unavailable; using yt-dlp.exe", e);
        }
    }
    private MediaControls? _media;
    private LibraryStore? _library;

    public static AppServices Services { get; private set; } = null!;
    public static ShellViewModel Shell { get; private set; } = null!;
    public static MainWindow? MainWin { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // GPU rendering (default) — software rendering saved ~50 MB but made scrolling choppy.
        SmoothScroll.Register();

        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // second launch: bring the running instance forward and leave
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tune");
        Directory.CreateDirectory(dataDir);
        Log.Init(Path.Combine(dataDir, "logs", "tune.log"));
        Log.Info("Tune starting");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = TimeSpan.FromSeconds(30) };
        // tool downloads can take many minutes on slow links: no overall timeout
        var toolsHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        _library = new LibraryStore(Path.Combine(dataDir, "tune.db"));
        var clock = new SystemClock();
        var runner = new ProcessRunner();
        var innerTube = new InnerTubeClient(http);
        var api = new YtMusicApi(innerTube);
        var lyricsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        lyricsHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Tune/1.0");
        var lyrics = new Tune.Core.Lyrics.LyricsService(innerTube, new Tune.Core.Lyrics.LrcLibClient(lyricsHttp),
            () => _library!.GetSetting("lyrics.synced") != "0");
        var tools = new ToolManager(Path.Combine(dataDir, "tools"), toolsHttp, runner, _library, clock, ToolManifest.Load());
        // fast path: one long-lived Python worker with yt-dlp loaded; yt-dlp.exe per song is the fallback
        _python = ToolManager.FindPython(Environment.GetEnvironmentVariable("PATH"), File.Exists);
        var backend = new FallbackBackend(() => _worker, new YtDlpExeBackend(tools, runner));
        var resolver = new StreamResolver(backend, clock, store: _library);
        var prefetcher = new StreamPrefetcher(resolver, maxConcurrent: 3);
        _player = new MpvPlayer { Normalize = _library.GetSetting("audio.normalize") == "1" };
        var lib = _library;
        var sponsorBlock = new SponsorBlockClient(http, () => lib.GetSetting("sponsorblock.enabled") != "0");
        var playback = new PlaybackService(new PlayQueue(), resolver, _player, _library, sponsorBlock, api,
            ct => tools.UpdateYtDlpIfDueAsync(force: true, ct), clock);
        var art = new ArtCache(Path.Combine(dataDir, "art"), http);
        Art.Cache = art;
        Task.Run(art.Trim);

        Services = new AppServices(_library, api, playback, new FeedBuilder(_library, api, clock), tools, art, clock, dataDir, prefetcher, lyrics);
        ThemeManager.Load(_library);
        Shell = new ShellViewModel(Services, Dispatcher);
        Shell.InitPages();
        MainWin = new MainWindow { DataContext = Shell, Icon = AppIcon.Window };
        MainWin.MiniPlayerRequested += ToggleMini;
        Shell.GoToCommand.Execute("home");
        _tray = new TrayIcon(Shell, ShowMain, ToggleMini, () => _mini?.IsVisible == true, Quit);

        // media keys + Windows media flyout need a window handle; creating it doesn't show the window
        _media = new MediaControls(new System.Windows.Interop.WindowInteropHelper(MainWin).EnsureHandle(), playback, Dispatcher);

        var startHidden = e.Args.Contains("--minimized");
        if (!startHidden) MainWin.Show();
        if (_library.GetSetting("mini.visible") == "1" || startHidden) ShowMini();
        _ = Shell.EnsureToolsAsync().ContinueWith(_ => StartWorkerAsync(tools, dataDir), TaskScheduler.Default);
        // Tune runs for weeks from the tray: check for a newer yt-dlp hourly (it only updates once a day)
        new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background,
            (_, _) => Task.Run(async () =>
            {
                await tools.UpdateYtDlpIfDueAsync(force: false);
                if (_python is not null && await tools.UpdatePyLibIfDueAsync(_python))
                    await StartWorkerAsync(tools, dataDir, restart: true); // load the new yt-dlp
            }), Dispatcher).Start();

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true, Name = "single-instance" }.Start();
    }

    private static MiniPlayerWindow? _mini;
    private static TrayIcon? _tray;

    public static void ToggleMini()
    {
        if (_mini?.IsVisible == true) HideMini();
        else ShowMini();
    }

    private static void ShowMini()
    {
        if (_mini is null)
        {
            _mini = new MiniPlayerWindow(Services.Library) { DataContext = Shell.Player };
            _mini.ExpandRequested += ShowMain;
            _mini.HideRequested += HideMini;
            _mini.PinToggled += () =>
            {
                var lib = Services.Library;
                lib.SetSetting("mini.topmost", lib.GetSetting("mini.topmost") == "0" ? "1" : "0");
                ApplyMiniTopmost();
            };
        }
        ApplyMiniTopmost();
        _mini.Show();
        Services.Library.SetSetting("mini.visible", "1");
        SyncUpdates();
    }

    private static void HideMini()
    {
        if (_mini is null) return;
        _mini.SavePlacement();
        _mini.Hide();
        Services.Library.SetSetting("mini.visible", "0");
        SyncUpdates();
    }

    /// <summary>Position ticks only flow to the UI while something visible shows them.</summary>
    public static void SyncUpdates()
    {
        var mainShowing = MainWin is { IsVisible: true } m && m.WindowState != WindowState.Minimized;
        var miniShowing = _mini?.IsVisible == true;
        if (MainWin is not null) MainWin.MiniVisible = miniShowing;
        Shell.Player.UpdatesEnabled = mainShowing || miniShowing;
        if (Shell.Player.UpdatesEnabled) Shell.Player.Refresh();
    }

    /// <summary>Closing the main window only hides it — music keeps playing from the tray.</summary>
    public static void OnMainClosedToTray()
    {
        SyncUpdates();
        if (Services.Library.GetSetting("tray.hinted") == "1") return;
        Services.Library.SetSetting("tray.hinted", "1");
        _tray?.ShowHint("Tune is still playing", "It lives in the tray now. Right-click the icon to quit.");
    }

    public static void ShowMain()
    {
        if (MainWin is null) return;
        MainWin.Show();
        if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
        MainWin.Activate();
        SyncUpdates();
    }

    public static void Quit()
    {
        Log.Info("Tune quitting");
        _mini?.SavePlacement();
        MainWin?.SavePlacement();
        _quitting = true;
        _tray?.Dispose();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Shell?.Player.SaveVolume();
        _media?.Dispose();
        _worker?.Dispose();
        _player?.Dispose();
        _library?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
