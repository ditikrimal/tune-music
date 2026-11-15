using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Library;
using Tune.Core.Streams;

namespace Tune.App.ViewModels;

/// <summary>A page shown in the main area. Pages are plain view models matched to views by DataTemplate.</summary>
public interface IPage
{
    /// <summary>Which sidebar entry to highlight: "home", "search", "liked", "playlist:{id}" or null.</summary>
    string? NavKey { get; }
}

public sealed record PlaceholderPage(string Title, string? NavKey) : IPage;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Stack<IPage> _back = new();
    private readonly Dispatcher _ui;
    private DispatcherTimer? _toastTimer;

    [ObservableProperty] private IPage? _page;
    [ObservableProperty] private bool _isSettingUp;
    [ObservableProperty] private string _setupText = "";
    [ObservableProperty] private double _setupProgress;
    [ObservableProperty] private bool _setupIndeterminate = true;
    [ObservableProperty] private string? _setupError;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _isQueueOpen;

    public ShellViewModel(AppServices services, Dispatcher ui)
    {
        Services = services;
        _ui = ui;
        Player = new PlayerBarViewModel(services.Playback, services.Library, ui);
        Queue = new QueueViewModel(this, ui);
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerBarViewModel.Track)) MarkCurrentRows();
        };
        services.Playback.Notice += msg => _ui.BeginInvoke(() => ShowToast(msg));
        ReloadPlaylists();
    }

    public AppServices Services { get; }
    public PlayerBarViewModel Player { get; }
    public QueueViewModel Queue { get; }
    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];
    public bool CanGoBack => _back.Count > 0;
    public string? NavKey => Page?.NavKey;

    /// <summary>Creates the page for a sidebar key; replaced in full by the views task.</summary>
    public Func<string, IPage> PageFactory { get; set; } = key => new PlaceholderPage(key, key);

    partial void OnPageChanged(IPage? value)
    {
        OnPropertyChanged(nameof(NavKey));
        if (value is HomeViewModel home) home.OnShown();
        MarkCurrentRows();
    }

    partial void OnIsQueueOpenChanged(bool value)
    {
        if (value) Queue.Refresh();
    }

    /// <summary>Highlights the playing song in whatever list is on screen.</summary>
    private void MarkCurrentRows()
    {
        var id = Services.Playback.Current?.VideoId;
        IEnumerable<TrackRowViewModel> rows = Page switch
        {
            CollectionViewModel c => c.Tracks,
            ArtistViewModel a => a.TopSongs,
            SearchViewModel s => s.Songs,
            _ => [],
        };
        foreach (var r in rows) r.IsCurrent = r.Track.VideoId == id;
    }

    public void Navigate(IPage page)
    {
        if (Page is not null) _back.Push(Page);
        Page = page;
        OnPropertyChanged(nameof(CanGoBack));
    }

    [RelayCommand]
    private void GoTo(string key)
    {
        if (Page?.NavKey == key) return;
        Navigate(PageFactory(key));
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_back.Count == 0) return;
        Page = _back.Pop();
        OnPropertyChanged(nameof(CanGoBack));
    }

    [RelayCommand]
    private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    public void ReloadPlaylists()
    {
        Playlists.Clear();
        foreach (var p in Services.Library.GetPlaylists()) Playlists.Add(p);
    }

    public void ShowToast(string message)
    {
        Toast = message;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) =>
        {
            Toast = null;
            _toastTimer?.Stop();
        }, _ui);
        _toastTimer.Start();
    }

    /// <summary>First run: download yt-dlp + deno with visible progress. Then a background daily yt-dlp update.</summary>
    public async Task EnsureToolsAsync()
    {
        var tools = Services.Tools;
        var progress = new Progress<ToolProgress>(p =>
        {
            IsSettingUp = true;
            SetupIndeterminate = p.Fraction is null;
            SetupProgress = (p.Fraction ?? 0) * 100;
            SetupText = p.Tool == "yt-dlp"
                ? "Downloading the YouTube helper (yt-dlp)…"
                : "Downloading the JavaScript runtime it needs (deno)…";
        });
        if (!File.Exists(tools.YtDlpPath) || !File.Exists(tools.DenoPath))
        {
            IsSettingUp = true;
            SetupIndeterminate = true;
            SetupText = "Getting things ready…";
        }
        try
        {
            SetupError = null;
            await tools.EnsureInstalledAsync(progress);
            IsSettingUp = false;
            _ = Task.Run(() => tools.UpdateYtDlpIfDueAsync(force: false));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or ToolInstallException or TaskCanceledException)
        {
            IsSettingUp = true;
            SetupError = "Setup couldn't finish: " + e.Message;
        }
    }

    [RelayCommand]
    private Task RetrySetup() => EnsureToolsAsync();
}
