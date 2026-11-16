using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.App.ViewModels;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Util;

namespace Tune.App.ViewModels;

/// <summary>One row in a track list.</summary>
public sealed partial class TrackRowViewModel(Track track, int number) : ObservableObject
{
    [ObservableProperty] private bool _isLiked;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private Tune.Core.Offline.DownloadState _download;
    [ObservableProperty] private double _downloadProgress;

    /// <summary>Offline and not downloaded: shown dimmed.</summary>
    [ObservableProperty] private bool _isUnavailable;

    public bool IsDownloaded => Download == Tune.Core.Offline.DownloadState.Done;
    public bool IsDownloadPending => Download is Tune.Core.Offline.DownloadState.Queued or Tune.Core.Offline.DownloadState.Downloading;
    public string DownloadTip => Download switch
    {
        Tune.Core.Offline.DownloadState.Done => "Downloaded — plays without internet",
        Tune.Core.Offline.DownloadState.Downloading => $"Downloading… {DownloadProgress:P0}",
        Tune.Core.Offline.DownloadState.Queued => "Waiting to download",
        _ => "",
    };

    partial void OnDownloadChanged(Tune.Core.Offline.DownloadState value)
    {
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsDownloadPending));
        OnPropertyChanged(nameof(DownloadTip));
        OnPropertyChanged(nameof(DownloadProgressText));
    }

    partial void OnDownloadProgressChanged(double value)
    {
        OnPropertyChanged(nameof(DownloadTip));
        OnPropertyChanged(nameof(DownloadProgressText));
    }

    /// <summary>"45%" while this song downloads, else empty.</summary>
    public string DownloadProgressText => Download == Tune.Core.Offline.DownloadState.Downloading ? $"{DownloadProgress:P0}" : "";

    /// <summary>Re-reads download state (and availability) from the download manager.</summary>
    public void SyncDownload(Tune.Core.Offline.DownloadManager dm, bool offline)
    {
        Download = dm.StateOf(Track.VideoId);
        DownloadProgress = dm.ProgressOf(Track.VideoId);
        IsUnavailable = offline && Download != Tune.Core.Offline.DownloadState.Done;
    }

    public Track Track { get; } = track;
    public int Number { get; set; } = number;
    public string Title => Track.Title;
    public string ArtistLine => Track.ArtistLine;
    public string? AlbumTitle => Track.Album?.Title;
    public string? ThumbnailUrl => Track.ThumbnailUrl;
    public string DurationText => Track.Duration is { } d ? PlayerBarViewModel.Format(d) : "";
    public string LikeGlyph => IsLiked ? "" : "";

    partial void OnIsLikedChanged(bool value) => OnPropertyChanged(nameof(LikeGlyph));
}

public static class Rows
{
    public static ObservableCollection<TrackRowViewModel> From(ShellViewModel shell, IEnumerable<Track> tracks)
    {
        var current = shell.Services.Playback.Current?.VideoId;
        var rows = tracks.Select((t, i) =>
        {
            var row = new TrackRowViewModel(t, i + 1)
            {
                IsLiked = shell.Services.Library.IsLiked(t.VideoId),
                IsCurrent = t.VideoId == current,
            };
            row.SyncDownload(shell.Services.Downloads, shell.IsOffline);
            return row;
        });
        return new ObservableCollection<TrackRowViewModel>(rows);
    }
}

// ============================== Home ==============================

public sealed partial class HomeViewModel : ObservableObject, IPage
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(30);
    private readonly ShellViewModel _shell;
    private DateTimeOffset _loadedAt;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _offline;
    [ObservableProperty] private ObservableCollection<TrackRowViewModel> _offlineTracks = [];

    public HomeViewModel(ShellViewModel shell)
    {
        _shell = shell;
        if (shell.IsOffline) SetOffline(true);
        else _ = LoadAsync();
    }

    public bool HasOfflineTracks => OfflineTracks.Count > 0;
    public string OfflineSummary => OfflineTracks.Count == 0
        ? "Nothing is downloaded yet. When you're back online, use the download button on any playlist or album, or ⋯ → Download on a song."
        : $"{ShellViewModel.Songs(OfflineTracks.Count)} you can play without internet.";

    /// <summary>The connection went or came back: Home becomes your downloads, or the normal feed again.</summary>
    public void SetOffline(bool offline)
    {
        if (offline)
        {
            Offline = true;
            ReloadOffline();
        }
        else
        {
            _ = LoadAsync();
        }
    }

    public void ReloadOffline()
    {
        OfflineTracks = Rows.From(_shell, _shell.Services.Offline.PlayableTracks());
        OnPropertyChanged(nameof(HasOfflineTracks));
        OnPropertyChanged(nameof(OfflineSummary));
    }

    private IReadOnlyList<Track> OfflineList => OfflineTracks.Select(r => r.Track).ToList();

    [RelayCommand]
    private Task PlayOffline()
    {
        if (OfflineTracks.Count == 0) return Task.CompletedTask;
        if (_shell.Player.ShuffleOn) _shell.Player.ToggleShuffleCommand.Execute(null);
        return _shell.Services.Playback.PlayListAsync(OfflineList, 0);
    }

    [RelayCommand]
    private Task ShuffleOffline()
    {
        if (OfflineTracks.Count == 0) return Task.CompletedTask;
        if (!_shell.Player.ShuffleOn) _shell.Player.ToggleShuffleCommand.Execute(null);
        return _shell.Services.Playback.PlayListAsync(OfflineList, Random.Shared.Next(OfflineTracks.Count));
    }

    [RelayCommand]
    private Task PlayOfflineRow(TrackRowViewModel? row) =>
        row is null ? Task.CompletedTask : _shell.Services.Playback.PlayListAsync(OfflineList, OfflineTracks.IndexOf(row));

    public string? NavKey => "home";
    public ObservableCollection<Shelf> Shelves { get; } = [];

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good evening",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>Something on Home changed (pins, layout): rebuild next time it shows.</summary>
    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    /// <summary>"Good evening, Ditik" when signed in.</summary>
    public string GreetingLine => _shell.IsSignedIn && _shell.AccountName.Split(' ')[0] is { Length: > 0 } first ? $"{Greeting}, {first}" : Greeting;

    public void AccountChanged() => OnPropertyChanged(nameof(GreetingLine));

    /// <summary>Called when the page is shown again; reloads if the feed is old.</summary>
    public void OnShown()
    {
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(GreetingLine));
        if (_shell.IsOffline) ReloadOffline();
        else if (!IsLoading && (Offline || _shell.Services.Clock.Now - _loadedAt > RefreshAfter)) _ = LoadAsync();
    }

    [RelayCommand]
    private Task LoadAsync() => BuildAsync(fresh: false);

    /// <summary>The refresh button / F5: a fresh mix (new radios, other favourites, reshuffled Quick picks).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await BuildAsync(fresh: true);
        if (!Offline) _shell.ShowToast("Home updated");
    }

    private async Task BuildAsync(bool fresh)
    {
        if (_shell.IsOffline && !await _shell.Services.Network.CheckNowAsync())
        {
            SetOffline(true);
            return;
        }
        IsLoading = true;
        try
        {
            var feed = await _shell.Services.Feed.BuildAsync(fresh);
            Shelves.Clear();
            // a calm Home: at most 6 rows of 12 — the rest is noise
            foreach (var s in feed.Shelves.Take(7)) Shelves.Add(s with { Items = s.Items.Take(s.IsList ? 20 : 12).ToList() });
            Offline = feed.Offline;
            if (Offline) ReloadOffline();
            _shell.Prefetch(feed.Shelves.SelectMany(sh => sh.Items.Take(6)).Where(c => c.Track is not null).Select(c => c.Track!).Take(12),
                Tune.Core.Streams.PrefetchPriority.Page);
            _loadedAt = _shell.Services.Clock.Now;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error("Home load failed", e);
            Offline = true;
            ReloadOffline();
        }
        finally
        {
            IsLoading = false;
        }
    }
}

// ============================== Search ==============================

public sealed partial class SearchViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private Card? _top;
    [ObservableProperty] private ObservableCollection<TrackRowViewModel> _songs = [];
    [ObservableProperty] private IReadOnlyList<Card> _artists = [];
    [ObservableProperty] private IReadOnlyList<Card> _albums = [];
    [ObservableProperty] private IReadOnlyList<Card> _playlists = [];

    public SearchViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunAsync();
        };
    }

    public string? NavKey => "search";

    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    [RelayCommand]
    private Task SearchNow()
    {
        _debounce.Stop();
        return RunAsync();
    }

    private async Task RunAsync()
    {
        var q = Query.Trim();
        _cts?.Cancel();
        if (q.Length == 0)
        {
            HasResults = false;
            Message = null;
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var r = await _shell.Services.Api.SearchAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            Top = r.Top;
            Songs = Rows.From(_shell, r.Songs.Take(20));
            Artists = r.Artists;
            Albums = r.Albums;
            Playlists = r.Playlists;
            HasResults = r.Songs.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count > 0;
            _shell.Prefetch(r.Songs.Take(6), Tune.Core.Streams.PrefetchPriority.Page);
            Message = HasResults ? null : $"No results for \"{q}\"";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn("Search failed", e);
            HasResults = false;
            Message = "Search isn't available right now — check your connection.";
        }
        finally
        {
            if (cts == _cts) IsLoading = false;
        }
    }

    [RelayCommand]
    private Task PlaySong(TrackRowViewModel? row) =>
        row is null ? Task.CompletedTask : _shell.Services.Playback.PlayRadioAsync(row.Track);
}

// ============================== Album / playlist / liked ==============================

public enum CollectionKind { Album, YtPlaylist, LocalPlaylist, Liked, Downloads, History }

public sealed partial class CollectionViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;
    private readonly Func<CancellationToken, Task<Collection>> _load;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private ObservableCollection<TrackRowViewModel> _tracks = [];
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isRenaming;

    private CollectionViewModel(ShellViewModel shell, CollectionKind kind, string title, string? navKey,
        Func<CancellationToken, Task<Collection>> load, long? playlistId = null)
    {
        _shell = shell;
        Kind = kind;
        _title = title;
        NavKey = navKey;
        _load = load;
        PlaylistId = playlistId;
        Reload();
    }

    public static CollectionViewModel Album(ShellViewModel s, Card c) =>
        new(s, CollectionKind.Album, c.Title, null, ct => s.Services.Api.GetAlbumAsync(c.BrowseId!, ct)) { ThumbnailUrl = c.ThumbnailUrl, SourceCard = c };

    public static CollectionViewModel YtPlaylist(ShellViewModel s, Card c) =>
        new(s, CollectionKind.YtPlaylist, c.Title, null, ct => s.Services.Api.GetPlaylistAsync(c.PlaylistId!, ct)) { ThumbnailUrl = c.ThumbnailUrl, SourceCard = c };

    /// <summary>The card this page was opened from (albums / YouTube playlists): what "Pin to speed dial" pins.</summary>
    public Card? SourceCard { get; init; }

    /// <summary>What pinning this page puts on the speed dial (null: can't be pinned, e.g. Downloads).</summary>
    public Card? PinCard => Kind switch
    {
        CollectionKind.LocalPlaylist when PlaylistId is { } id && _shell.Services.Library.PlaylistUid(id) is { } uid =>
            Tune.Core.Feed.SpeedDialPins.LocalPlaylistCard(uid, Title, ThumbnailUrl),
        CollectionKind.Album or CollectionKind.YtPlaylist => SourceCard is { } c ? c with { Title = Title, ThumbnailUrl = ThumbnailUrl ?? c.ThumbnailUrl } : null,
        _ => null,
    };

    public static CollectionViewModel Liked(ShellViewModel s) =>
        new(s, CollectionKind.Liked, "Liked songs", "liked", _ =>
        {
            var liked = s.Services.Library.GetLiked();
            return Task.FromResult(new Collection("Liked songs", ShellViewModel.Songs(liked.Count), null, liked));
        });

    public static CollectionViewModel Downloads(ShellViewModel s) =>
        new(s, CollectionKind.Downloads, "Downloads", "downloads", _ =>
        {
            var dm = s.Services.Downloads;
            var all = dm.All();
            var done = all.Count(d => dm.IsDownloaded(d.Track.VideoId));
            var pending = dm.PendingCount;
            var sub = ShellViewModel.Songs(done) + " · " + FormatSize(dm.DownloadedBytes())
                      + (pending > 0 ? $" · {pending} downloading" : "");
            return Task.FromResult(new Collection("Downloads", sub, null, all.Select(d => d.Track).ToList()));
        });

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>Songs you've played, most recent first.</summary>
    public static CollectionViewModel History(ShellViewModel s) =>
        new(s, CollectionKind.History, "History", "history", _ =>
        {
            var recent = s.Services.Library.GetRecent(300);
            return Task.FromResult(new Collection("History", ShellViewModel.Songs(recent.Count) + " you played recently", null, recent));
        });

    public static CollectionViewModel LocalPlaylist(ShellViewModel s, long id) =>
        new(s, CollectionKind.LocalPlaylist, "Playlist", $"playlist:{id}", _ =>
        {
            var info = s.Services.Library.GetPlaylists().FirstOrDefault(p => p.Id == id);
            var tracks = s.Services.Library.GetPlaylistTracks(id);
            return Task.FromResult(new Collection(info?.Name ?? "Playlist", ShellViewModel.Songs(tracks.Count), info?.ThumbnailUrl, tracks));
        }, id);

    public CollectionKind Kind { get; }
    public string? NavKey { get; }
    public long? PlaylistId { get; }
    public bool IsLiked => Kind == CollectionKind.Liked;
    public bool IsDownloads => Kind == CollectionKind.Downloads;
    public bool IsHistory => Kind == CollectionKind.History;
    public bool CanDownloadAll => !IsDownloads && Tracks.Count > 0;

    /// <summary>Every song here is downloaded (or on its way): the header button shows "on".</summary>
    public bool AllDownloaded => Tracks.Count > 0 && Tracks.All(r => r.Download != Tune.Core.Offline.DownloadState.None);
    public bool AnyDownloaded => Tracks.Any(r => r.Download != Tune.Core.Offline.DownloadState.None);
    public string DownloadAllTip => AllDownloaded ? "Downloaded — plays without internet" : AnyDownloaded ? "Download the new songs" : "Download — play without internet";

    /// <summary>Only ever adds: songs added to the playlist later are fetched by pressing it again. Removing is in the ⋯ menu.</summary>
    [RelayCommand]
    private void ToggleDownloadAll()
    {
        if (Tracks.Count == 0) return;
        if (AllDownloaded) _shell.ShowToast("Everything here is downloaded");
        else _shell.Download(AllTracks);
    }

    public void RemoveAllDownloads()
    {
        var mine = Tracks.Where(r => r.Download != Tune.Core.Offline.DownloadState.None).Select(r => r.Track).ToList();
        if (mine.Count > 0) _shell.RemoveDownloads(mine);
    }

    public IReadOnlyList<Track> TrackList => AllTracks;

    /// <summary>"Downloading · 12 of 40 saved" while this page's songs download (null otherwise) and its 0..1 progress.</summary>
    public string? DownloadSummary
    {
        get
        {
            var dm = _shell.Services.Downloads;
            var pending = Tracks.Count(r => r.Download is Tune.Core.Offline.DownloadState.Queued or Tune.Core.Offline.DownloadState.Downloading);
            if (pending == 0 || IsDownloads) return null;
            var done = Tracks.Count(r => r.Download == Tune.Core.Offline.DownloadState.Done);
            return $"Downloading \u2022 {done} of {done + pending} saved";
        }
    }

    public double DownloadFraction
    {
        get
        {
            var pending = Tracks.Count(r => r.Download is Tune.Core.Offline.DownloadState.Queued or Tune.Core.Offline.DownloadState.Downloading);
            var done = Tracks.Count(r => r.Download == Tune.Core.Offline.DownloadState.Done);
            var current = Tracks.FirstOrDefault(r => r.Download == Tune.Core.Offline.DownloadState.Downloading)?.DownloadProgress ?? 0;
            return pending == 0 ? 1 : (done + current) / (done + pending);
        }
    }

    /// <summary>A download state changed somewhere: refresh the header button.</summary>
    public void DownloadsChanged()
    {
        OnPropertyChanged(nameof(DownloadSummary));
        OnPropertyChanged(nameof(DownloadFraction));
        OnPropertyChanged(nameof(AllDownloaded));
        OnPropertyChanged(nameof(AnyDownloaded));
        OnPropertyChanged(nameof(DownloadAllTip));
        OnPropertyChanged(nameof(CanDownloadAll));
    }
    public bool IsLocal => Kind == CollectionKind.LocalPlaylist;
    public bool IsEditable => IsLocal || IsLiked;
    public string KindLabel => Kind switch
    {
        CollectionKind.Album => "Album",
        CollectionKind.Liked => "Playlist",
        CollectionKind.Downloads => "Offline",
        CollectionKind.History => "Your listening",
        _ => "Playlist",
    };

    public async void Reload()
    {
        IsLoading = true;
        Message = null;
        try
        {
            var c = await _load(CancellationToken.None);
            Title = c.Title;
            Subtitle = c.Subtitle;
            ThumbnailUrl = c.ThumbnailUrl ?? ThumbnailUrl ?? c.Tracks.FirstOrDefault()?.ThumbnailUrl;
            Tracks = Rows.From(_shell, c.Tracks);
            DownloadsChanged();
            _shell.Prefetch(c.Tracks.Take(6), Tune.Core.Streams.PrefetchPriority.Page);
            if (c.Tracks.Count == 0)
                Message = IsDownloads ? "Songs you download play without internet. Use the download button on a playlist or album, or ⋯ → Download on a song."
                        : IsLiked ? "Songs you like will appear here. Tap ♡ on any song."
                        : IsLocal ? "This playlist is empty. Use ⋯ → Add to playlist on any song."
                        : "Nothing here.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn("Collection load failed", e);
            Message = "Couldn't load this — check your connection.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private IReadOnlyList<Track> AllTracks => Tracks.Select(r => r.Track).ToList();

    [RelayCommand]
    private Task Play()
    {
        if (Tracks.Count == 0) return Task.CompletedTask;
        if (_shell.Player.ShuffleOn) _shell.Player.ToggleShuffleCommand.Execute(null);
        return _shell.Services.Playback.PlayListAsync(AllTracks, 0);
    }

    [RelayCommand]
    private Task ShufflePlay()
    {
        if (Tracks.Count == 0) return Task.CompletedTask;
        if (!_shell.Player.ShuffleOn) _shell.Player.ToggleShuffleCommand.Execute(null);
        return _shell.Services.Playback.PlayListAsync(AllTracks, Random.Shared.Next(Tracks.Count));
    }

    [RelayCommand]
    private Task PlayRow(TrackRowViewModel? row) =>
        row is null ? Task.CompletedTask : _shell.Services.Playback.PlayListAsync(AllTracks, Tracks.IndexOf(row));

    public void Remove(TrackRowViewModel row)
    {
        if (IsLocal && PlaylistId is { } id)
        {
            _shell.Services.Library.RemoveFromPlaylist(id, Tracks.IndexOf(row));
            _shell.ReloadPlaylists();
        }
        else if (IsLiked)
        {
            _shell.Services.Library.SetLiked(row.Track, false);
            _shell.Player.Refresh();
        }
        Reload();
    }

    public void Move(int from, int to)
    {
        if (!IsLocal || PlaylistId is not { } id || from == to) return;
        _shell.Services.Library.MoveInPlaylist(id, from, to);
        Tracks.Move(from, to);
        for (var i = 0; i < Tracks.Count; i++) Tracks[i].Number = i + 1;
        _shell.ReloadPlaylists();
    }

    [RelayCommand]
    private void BeginRename() => IsRenaming = IsLocal;

    public void CommitRename(string name)
    {
        IsRenaming = false;
        if (!IsLocal || PlaylistId is not { } id || string.IsNullOrWhiteSpace(name)) return;
        _shell.Services.Library.RenamePlaylist(id, name.Trim());
        Title = name.Trim();
        _shell.ReloadPlaylists();
    }

    [RelayCommand]
    private void DeletePlaylist()
    {
        if (!IsLocal || PlaylistId is not { } id) return;
        _shell.Services.Library.DeletePlaylist(id);
        _shell.ReloadPlaylists();
        _shell.ShowToast($"Deleted {Title}");
        _shell.GoToCommand.Execute("home");
    }

    /// <summary>Highlights the playing row.</summary>
    public void MarkCurrent(string? videoId)
    {
        foreach (var r in Tracks) r.IsCurrent = r.Track.VideoId == videoId;
    }
}

// ============================== Artist ==============================

public sealed partial class ArtistViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private ObservableCollection<TrackRowViewModel> _topSongs = [];
    [ObservableProperty] private IReadOnlyList<Shelf> _shelves = [];
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _message;

    public ArtistViewModel(ShellViewModel shell, string browseId, string name)
    {
        _shell = shell;
        _name = name;
        _ = LoadAsync(browseId);
    }

    public string? NavKey => null;

    private async Task LoadAsync(string browseId)
    {
        try
        {
            var a = await _shell.Services.Api.GetArtistAsync(browseId);
            Name = a.Name;
            ThumbnailUrl = a.ThumbnailUrl;
            TopSongs = Rows.From(_shell, a.TopSongs);
            _shell.Prefetch(a.TopSongs.Take(5), Tune.Core.Streams.PrefetchPriority.Page);
            Shelves = a.Shelves;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn("Artist load failed", e);
            Message = "Couldn't load this artist — check your connection.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task Play() => TopSongs.Count == 0 ? Task.CompletedTask
        : _shell.Services.Playback.PlayListAsync(TopSongs.Select(r => r.Track).ToList(), 0);

    [RelayCommand]
    private Task PlayRow(TrackRowViewModel? row) => row is null ? Task.CompletedTask
        : _shell.Services.Playback.PlayListAsync(TopSongs.Select(r => r.Track).ToList(), TopSongs.IndexOf(row));
}

// ============================== Settings ==============================

public sealed partial class SettingsViewModel : ObservableObject, IPage
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ShellViewModel _shell;
    private LibraryStore Lib => _shell.Services.Library;

    [ObservableProperty] private bool _sponsorBlock;
    [ObservableProperty] private bool _normalize;
    [ObservableProperty] private bool _syncedLyrics;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _miniOnTop;
    [ObservableProperty] private Tune.App.Platform.HeaderTint _header = Tune.App.Platform.ThemeManager.Header;

    public string HeaderName => Header.ToString();

    partial void OnHeaderChanged(Tune.App.Platform.HeaderTint value)
    {
        Tune.App.Platform.ThemeManager.Set(Lib, header: value);
        OnPropertyChanged(nameof(HeaderName));
    }

    [RelayCommand]
    private void PickHeader(string h)
    {
        if (Enum.TryParse<Tune.App.Platform.HeaderTint>(h, out var v)) Header = v;
    }
    [ObservableProperty] private bool _translucent = Tune.App.Platform.ThemeManager.Translucent;

    partial void OnTranslucentChanged(bool value) => Tune.App.Platform.ThemeManager.Set(Lib, translucent: value);
    [ObservableProperty] private Tune.App.Platform.AccentChoice _accent;
    [ObservableProperty] private Tune.App.Platform.BackgroundStyle _background;
    [ObservableProperty] private string _updateResult = "";
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string _cacheSize = "";

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _sponsorBlock = Lib.GetSetting("sponsorblock.enabled") != "0";
        _normalize = Lib.GetSetting("audio.normalize") == "1";
        _syncedLyrics = Lib.GetSetting("lyrics.synced") != "0";
        _closeToTray = Lib.GetSetting("window.closeToTray") != "0";
        _miniOnTop = Lib.GetSetting("mini.topmost") != "0";

        _accent = Tune.App.Platform.ThemeManager.Accent;
        SyncPicker(_accent.Color);
        _saveAccent.Tick += (_, _) => { _saveAccent.Stop(); Tune.App.Platform.ThemeManager.SaveAccent(Lib); };
        _background = Tune.App.Platform.ThemeManager.Background;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        _startWithWindows = key?.GetValue("Tune") is not null;
        RefreshCacheSize();
        _smartEnabled = Smart.Enabled;
        _smartCountName = Smart.Target.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _audioCacheEnabled = _shell.Services.Cache.Enabled;
        RefreshSmartStatus();
        RefreshAudioCache();
        _shell.RefreshAccount();
    }

    public string? NavKey => "settings";
    public IReadOnlyList<Tune.App.Platform.AccentChoice> Accents => Tune.App.Platform.ThemeManager.Accents;
    public IReadOnlyList<Tune.App.Platform.BackgroundStyle> Backgrounds { get; } = Enum.GetValues<Tune.App.Platform.BackgroundStyle>();
    public string ResolverStatus => App.ResolverStatus;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    partial void OnSponsorBlockChanged(bool value) => Lib.SetSetting("sponsorblock.enabled", value ? "1" : "0");

    partial void OnNormalizeChanged(bool value)
    {
        Lib.SetSetting("audio.normalize", value ? "1" : "0");
        _shell.Services.Playback.Player.Normalize = value;
    }

    // ---- import a YouTube Music playlist into the local library ----
    [ObservableProperty] private string _importUrl = "";
    [ObservableProperty] private string? _importStatus;
    [ObservableProperty] private bool _isImporting;

    [RelayCommand]
    private async Task Import()
    {
        if (IsImporting) return;
        IsImporting = true;
        ImportStatus = "Importing\u2026";
        try
        {
            var (message, id) = await _shell.ImportPlaylistAsync(ImportUrl);
            ImportStatus = message;
            if (id is not null) ImportUrl = "";
        }
        finally
        {
            IsImporting = false;
        }
    }

    partial void OnSyncedLyricsChanged(bool value) => Lib.SetSetting("lyrics.synced", value ? "1" : "0");

    // ---- offline: smart downloads + streaming cache ----
    [ObservableProperty] private bool _smartEnabled;
    [ObservableProperty] private string _smartCountName = "100";
    [ObservableProperty] private string _smartStatus = "";
    [ObservableProperty] private bool _isSmartRunning;
    [ObservableProperty] private bool _audioCacheEnabled;
    [ObservableProperty] private string _audioCacheStatus = "";

    /// <summary>For the update row, which lives on the shell.</summary>
    public ShellViewModel Shell => _shell;

    private Tune.Core.Offline.SmartDownloads Smart => _shell.Services.Smart;

    private void RefreshSmartStatus()
    {
        var picked = Smart.PickedIds.Count;
        var ready = Smart.ReadyCount;
        SmartStatus = !Smart.Enabled ? "Off" : Smart.LastRun is { } last
            ? $"{ready} of {ShellViewModel.Songs(picked)} ready offline · updated {last.LocalDateTime:d MMM, HH:mm}"
            : "Runs once a day on Wi-Fi or another unmetered connection";
    }

    private void RefreshAudioCache()
    {
        var cache = _shell.Services.Cache;
        AudioCacheStatus = $"{ShellViewModel.Songs(cache.Ids().Count)} · {cache.UsedBytes() / (1024.0 * 1024):0} MB of {cache.MaxBytes / (1024 * 1024 * 1024.0):0.#} GB";
    }

    partial void OnSmartEnabledChanged(bool value)
    {
        Smart.Enabled = value;
        if (!value) _ = RunSmart(); // switching off removes what it picked (your own downloads stay)
        RefreshSmartStatus();
    }

    [RelayCommand]
    private void PickSmartCount(string count)
    {
        if (!int.TryParse(count, out var n)) return;
        Smart.Target = n;
        SmartCountName = count;
    }

    [RelayCommand]
    private async Task RunSmart()
    {
        if (IsSmartRunning) return;
        IsSmartRunning = true;
        SmartStatus = Smart.Enabled ? "Picking songs…" : "Removing smart downloads…";
        try
        {
            var (added, removed) = await Task.Run(() => Smart.RefreshAsync());
            if (Smart.Enabled)
                _shell.ShowToast(added == 0 && removed == 0 ? "Smart downloads are up to date"
                    : $"Smart downloads: {ShellViewModel.Songs(added)} to download, {removed} swapped out");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Tune.Core.Util.Log.Warn("Smart downloads failed", e);
            _shell.ShowToast("Smart downloads couldn't run — are you online?");
        }
        finally
        {
            IsSmartRunning = false;
            RefreshSmartStatus();
        }
    }

    partial void OnAudioCacheEnabledChanged(bool value)
    {
        _shell.Services.Cache.Enabled = value;
        RefreshAudioCache();
    }

    [RelayCommand]
    private void ClearAudioCache()
    {
        _shell.Services.Cache.Clear();
        RefreshAudioCache();
        _shell.ShowToast("Streaming cache cleared");
    }

    [RelayCommand]
    private Task CheckUpdates() => _shell.HasUpdate ? _shell.InstallUpdateCommand.ExecuteAsync(null) : _shell.CheckForUpdateAsync(userAsked: true);

    partial void OnCloseToTrayChanged(bool value) => Lib.SetSetting("window.closeToTray", value ? "1" : "0");

    partial void OnMiniOnTopChanged(bool value)
    {
        Lib.SetSetting("mini.topmost", value ? "1" : "0");
        App.ApplyMiniTopmost();
    }


    // ---- accent: presets + a free HSV/hex picker; applied live, saved once the user stops dragging ----
    private readonly System.Windows.Threading.DispatcherTimer _saveAccent = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _syncing;
    [ObservableProperty] private double _hue;
    [ObservableProperty] private double _sat;
    [ObservableProperty] private double _val;
    [ObservableProperty] private string _hex = "";
    [ObservableProperty] private bool _showPicker;

    public bool IsCustom => Accent.Name == "Custom";

    public System.Windows.Media.Brush SatTrack => Frozen(new System.Windows.Media.LinearGradientBrush(
        Tune.App.Platform.ColorMath.FromHsv(Hue, 0, Math.Max(Val, 0.35)), Tune.App.Platform.ColorMath.FromHsv(Hue, 1, Math.Max(Val, 0.35)), 0));

    public System.Windows.Media.Brush ValTrack => Frozen(new System.Windows.Media.LinearGradientBrush(
        System.Windows.Media.Colors.Black, Tune.App.Platform.ColorMath.FromHsv(Hue, Sat, 1), 0));

    private static System.Windows.Media.Brush Frozen(System.Windows.Media.Brush b) { b.Freeze(); return b; }

    partial void OnHueChanged(double value) => FromPicker();
    partial void OnSatChanged(double value) => FromPicker();
    partial void OnValChanged(double value) => FromPicker();

    private void FromPicker()
    {
        OnPropertyChanged(nameof(SatTrack));
        OnPropertyChanged(nameof(ValTrack));
        if (_syncing) return;
        var c = Tune.App.Platform.ColorMath.FromHsv(Hue, Sat, Val);
        _syncing = true;
        Hex = Tune.App.Platform.ColorMath.ToHex(c);
        _syncing = false;
        Accent = Tune.App.Platform.ThemeManager.CustomAccent(c);
    }

    partial void OnHexChanged(string value)
    {
        if (_syncing || !Tune.App.Platform.ColorMath.TryParseHex(value, out var c)) return;
        SyncPicker(c, keepHex: true);
        Accent = Tune.App.Platform.ThemeManager.CustomAccent(c);
    }

    private void SyncPicker(System.Windows.Media.Color c, bool keepHex = false)
    {
        _syncing = true;
        (Hue, Sat, Val) = Tune.App.Platform.ColorMath.ToHsv(c);
        if (!keepHex) Hex = Tune.App.Platform.ColorMath.ToHex(c);
        _syncing = false;
    }

    [RelayCommand]
    private void ToggleCustom()
    {
        ShowPicker = !ShowPicker;
    }

    partial void OnAccentChanged(Tune.App.Platform.AccentChoice value)
    {
        Tune.App.Platform.ThemeManager.Set(Lib, accent: value, persist: false);
        _saveAccent.Stop();
        _saveAccent.Start();
        OnPropertyChanged(nameof(IsCustom));
        if (!_syncing && value.Name != "Custom") SyncPicker(value.Color);
    }
    partial void OnBackgroundChanged(Tune.App.Platform.BackgroundStyle value) => Tune.App.Platform.ThemeManager.Set(Lib, background: value);

    [RelayCommand]
    private void PickAccent(Tune.App.Platform.AccentChoice? a)
    {
        if (a is not null) Accent = a;
    }

    [RelayCommand]
    private void PickBackground(Tune.App.Platform.BackgroundStyle b) => Background = b;

    [ObservableProperty] private Tune.App.Mini.WidgetStyle _widgetStyle = App.MiniStyle;

    [RelayCommand]
    private void PickWidgetStyle(string style)
    {
        if (!Enum.TryParse<Tune.App.Mini.WidgetStyle>(style, out var s)) return;
        WidgetStyle = s;
        App.SetMiniStyle(s);
    }

    public string WidgetStyleName => WidgetStyle.ToString();

    partial void OnWidgetStyleChanged(Tune.App.Mini.WidgetStyle value) => OnPropertyChanged(nameof(WidgetStyleName));

    partial void OnStartWithWindowsChanged(bool value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (value) key.SetValue("Tune", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("Tune", throwOnMissingValue: false);
    }

    [RelayCommand]
    private async Task UpdateYtDlp()
    {
        IsUpdating = true;
        UpdateResult = "Checking for a newer yt-dlp…";
        try
        {
            var ok = await Task.Run(() => _shell.Services.Tools.UpdateYtDlpIfDueAsync(force: true));
            UpdateResult = ok ? "yt-dlp is up to date." : "Update failed — see the log in %LOCALAPPDATA%\\Tune\\logs.";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private void ClearCache()
    {
        _shell.Services.Art.Clear();
        RefreshCacheSize();
    }

    private void RefreshCacheSize() =>
        CacheSize = $"{_shell.Services.Art.SizeBytes() / (1024.0 * 1024):0.#} MB";
}
