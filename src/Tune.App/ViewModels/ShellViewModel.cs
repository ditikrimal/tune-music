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
    private readonly Stack<IPage> _forward = new();
    private bool _syncingSearch;

    [ObservableProperty] private string _searchText = "";

    /// <summary>Typing in the top search box opens Search with that text.</summary>
    partial void OnSearchTextChanged(string value)
    {
        if (_syncingSearch) return;
        if (PageFactory("search") is SearchViewModel s) s.Query = value; // the search page is cached
        if (Page is not SearchViewModel) GoToCommand.Execute("search");
    }

    public bool CanGoForward => _forward.Count > 0;

    [RelayCommand]
    private void GoForward()
    {
        if (_forward.Count == 0) return;
        if (Page is not null) _back.Push(Page);
        Page = _forward.Pop();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
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
    [ObservableProperty] private bool _isOffline;

    /// <summary>Bumped on every like/unlike so card hearts (bound through LikedTagConverter) refresh.</summary>
    [ObservableProperty] private int _likesVersion;

    /// <summary>A like changed anywhere: refresh hearts on cards and visible rows.</summary>
    public void LikesChanged()
    {
        LikesVersion++;
        foreach (var r in VisibleRows) r.IsLiked = Services.Library.IsLiked(r.Track.VideoId);
    }

    public ShellViewModel(AppServices services, Dispatcher ui)
    {
        Services = services;
        _ui = ui;
        Player = new PlayerBarViewModel(services.Playback, services.Library, ui, services.Downloads);
        Player.DownloadRequested += t =>
        {
            if (Services.Downloads.StateOf(t.VideoId) == Tune.Core.Offline.DownloadState.Done)
                ShowToast("Downloaded — remove it from the ⋯ menu or Downloads");
            else Download([t]);
        };
        Queue = new QueueViewModel(this, ui);
        NowPlaying = new NowPlayingViewModel(Player, services.Lyrics);
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlayerBarViewModel.Track)) return;
            MarkCurrentRows();
            _ = TintHeaderAsync(Player.ArtUrl);
        };
        services.Playback.Notice += msg => _ui.BeginInvoke(() => ShowToast(msg));
        Player.LikedChanged += () =>
        {
            LikesChanged();
            if (Page is CollectionViewModel { IsLiked: true } liked) liked.Reload();
        };
        _isOffline = !services.Network.IsOnline;
        services.Network.Changed += online => _ui.BeginInvoke(() => IsOffline = !online);
        services.Downloads.Changed += id => _ui.BeginInvoke(DispatcherPriority.Background, () => OnDownloadChanged(id));
        services.Downloads.Failed += msg => _ui.BeginInvoke(() => ShowToast(msg));
        ReloadPlaylists();
        ReloadSaved();
        services.Library.SavedChanged += () => _ui.BeginInvoke(ReloadSaved);
    }

    /// <summary>Followed artists and saved albums, shown in Your Library under the playlists.</summary>
    public ObservableCollection<Tune.Core.Models.Card> SavedItems { get; } = [];

    public void ReloadSaved()
    {
        SavedItems.Clear();
        var lib = Services.Library;
        foreach (var a in lib.GetFollowedArtists())
            SavedItems.Add(new Tune.Core.Models.Card(Tune.Core.Models.CardKind.Artist, a.Name, "Artist", a.ThumbnailUrl, null, a.BrowseId, null, null));
        foreach (var a in lib.GetSavedAlbums())
            SavedItems.Add(new Tune.Core.Models.Card(Tune.Core.Models.CardKind.Album, a.Title, a.Artist is null ? "Album" : $"Album · {a.Artist}", a.ThumbnailUrl, null, a.BrowseId, null, null));
    }

    public AppServices Services { get; }
    public PlayerBarViewModel Player { get; }
    public QueueViewModel Queue { get; }
    public NowPlayingViewModel NowPlaying { get; }
    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];
    public bool CanGoBack => _back.Count > 0;
    public string? NavKey => Page?.NavKey;

    /// <summary>Creates the page for a sidebar key; replaced in full by the views task.</summary>
    public Func<string, IPage> PageFactory { get; set; } = key => new PlaceholderPage(key, key);

    partial void OnPageChanged(IPage? value)
    {
        // keep the top search box showing the search page's text (and empty elsewhere)
        _syncingSearch = true;
        SearchText = value is SearchViewModel s ? s.Query : "";
        _syncingSearch = false;
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
        foreach (var r in VisibleRows) r.IsCurrent = r.Track.VideoId == id;
    }

    private IEnumerable<TrackRowViewModel> VisibleRows => Page switch
    {
        CollectionViewModel c => c.Tracks,
        ArtistViewModel a => a.TopSongs,
        SearchViewModel s => s.Songs,
        HomeViewModel h => h.OfflineTracks,
        _ => [],
    };

    partial void OnIsOfflineChanged(bool value)
    {
        ShowToast(value ? "You're offline — your downloads still play" : "Back online");
        foreach (var r in VisibleRows) r.SyncDownload(Services.Downloads, value);
        _home?.SetOffline(value);
    }

    private DateTime _downloadsPageRefreshed;

    private void OnDownloadChanged(string videoId)
    {
        if (Player.Track?.VideoId == videoId) Player.RefreshDownload();
        foreach (var r in VisibleRows)
            if (r.Track.VideoId == videoId) r.SyncDownload(Services.Downloads, IsOffline);
        if (Page is CollectionViewModel c)
        {
            c.DownloadsChanged();
            // the Downloads page shows totals: refresh it when a song finishes or leaves (not on every progress tick)
            var state = Services.Downloads.StateOf(videoId);
            if (c.IsDownloads && state is not Tune.Core.Offline.DownloadState.Downloading
                && (c.Tracks.All(r => r.Track.VideoId != videoId) || state is Tune.Core.Offline.DownloadState.Done or Tune.Core.Offline.DownloadState.None)
                && DateTime.UtcNow - _downloadsPageRefreshed > TimeSpan.FromMilliseconds(300))
            {
                _downloadsPageRefreshed = DateTime.UtcNow;
                c.Reload();
            }
        }
    }

    public void Download(IReadOnlyList<Tune.Core.Models.Track> tracks)
    {
        var fresh = tracks.Count(t => Services.Downloads.StateOf(t.VideoId) == Tune.Core.Offline.DownloadState.None);
        Services.Downloads.Enqueue(tracks);
        ShowToast(fresh == 0 ? "Already downloaded"
            : fresh == 1 ? $"Downloading {tracks.First(t => Services.Downloads.StateOf(t.VideoId) != Tune.Core.Offline.DownloadState.Done).Title}"
            : $"Downloading {fresh} songs");
    }

    public void RemoveDownloads(IReadOnlyList<Tune.Core.Models.Track> tracks)
    {
        if (tracks.Count == 0) return;
        var what = tracks.Count == 1 ? $"“{tracks[0].Title}”" : $"{tracks.Count} songs";
        if (!Confirm(tracks.Count == 1 ? "Remove download?" : $"Remove {tracks.Count} downloads?",
                $"{what} will need internet to play again.", "Remove")) return;
        Services.Downloads.Remove(tracks.Select(t => t.VideoId));
        ShowToast(tracks.Count == 1 ? "Removed download" : $"Removed {tracks.Count} downloads");
        if (Page is CollectionViewModel { IsDownloads: true } c) c.Reload();
        _home?.ReloadOffline();
    }

    public void Navigate(IPage page)
    {
        NowPlaying.IsOpen = false; // going somewhere (e.g. an artist link) leaves the full-screen player
        if (Page is not null) _back.Push(Page);
        _forward.Clear();
        Page = page;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
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
        if (Page is not null) _forward.Push(Page);
        Page = _back.Pop();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    [RelayCommand]
    private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    private async Task TintHeaderAsync(string? artUrl)
    {
        var art = artUrl is null ? null : await Services.Art.GetAsync(artUrl, 96);
        Tune.App.Platform.ThemeManager.SetArt(art);
        Player.Tint = PlayerBarViewModel.Frozen(art is null
            ? System.Windows.Media.Color.FromRgb(0x24, 0x24, 0x24)
            : Tune.App.Platform.AlbumColor.ForWidget(Tune.App.Platform.AlbumColor.FromBitmap(art)));
    }

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
