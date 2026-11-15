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
    private MediaControls? _media;
    private LibraryStore? _library;

    public static AppServices Services { get; private set; } = null!;
    public static ShellViewModel Shell { get; private set; } = null!;
    public static MainWindow? MainWin { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Software rendering: halves WPF's memory on this laptop (the Direct3D device costs ~50 MB of driver
        // memory) and keeps the dedicated GPU asleep. A static music UI renders cheaply on the CPU.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

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
        var api = new YtMusicApi(new InnerTubeClient(http));
        var tools = new ToolManager(Path.Combine(dataDir, "tools"), toolsHttp, runner, _library, clock, ToolManifest.Load());
        var resolver = new StreamResolver(tools, runner, clock);
        _player = new MpvPlayer();
        var lib = _library;
        var sponsorBlock = new SponsorBlockClient(http, () => lib.GetSetting("sponsorblock.enabled") != "0");
        var playback = new PlaybackService(new PlayQueue(), resolver, _player, _library, sponsorBlock, api,
            ct => tools.UpdateYtDlpIfDueAsync(force: true, ct), clock);
        var art = new ArtCache(Path.Combine(dataDir, "art"), http);
        Art.Cache = art;
        Task.Run(art.Trim);

        Services = new AppServices(_library, api, playback, new FeedBuilder(_library, api, clock), tools, art, clock, dataDir);
        Shell = new ShellViewModel(Services, Dispatcher);
        Shell.InitPages();
        MainWin = new MainWindow { DataContext = Shell, Icon = AppIcon.Create(64) };
        MainWin.MiniPlayerRequested += ToggleMini;
        Shell.GoToCommand.Execute("home");
        _tray = new TrayIcon(Shell, ShowMain, ToggleMini, () => _mini?.IsVisible == true, Quit);

        // media keys + Windows media flyout need a window handle; creating it doesn't show the window
        _media = new MediaControls(new System.Windows.Interop.WindowInteropHelper(MainWin).EnsureHandle(), playback, Dispatcher);

        var startHidden = e.Args.Contains("--minimized");
        if (!startHidden) MainWin.Show();
        if (_library.GetSetting("mini.visible") == "1" || startHidden) ShowMini();
        _ = Shell.EnsureToolsAsync();

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
        }
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
        _media?.Dispose();
        _player?.Dispose();
        _library?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
