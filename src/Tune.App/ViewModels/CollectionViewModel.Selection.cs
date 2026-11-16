using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Library;
using Tune.Core.Models;

namespace Tune.App.ViewModels;

/// <summary>
/// Selecting many songs on a playlist / album / liked / downloads / history page (Ctrl/Shift+click, or the Select
/// button): then play, add to a playlist, download, remove downloads, remove from the playlist or Liked songs,
/// or mark "not interested" — all at once.
/// </summary>
public sealed partial class CollectionViewModel
{
    [ObservableProperty] private bool _isSelecting;
    [ObservableProperty] private int _selectedCount;
    private TrackRowViewModel? _anchor;

    public IReadOnlyList<TrackRowViewModel> SelectedRows => Tracks.Where(r => r.IsSelected).ToList();
    private List<Track> SelectedTracks => SelectedRows.Select(r => r.Track).ToList();
    public string SelectedText => SelectedCount == 1 ? "1 song selected" : $"{SelectedCount} songs selected";
    public bool AnySelectedDownloaded => SelectedRows.Any(r => r.Download != Tune.Core.Offline.DownloadState.None);
    public bool CanRemoveSelected => IsLocal || IsLiked;
    public string RemoveSelectedText => IsLiked ? "Remove from Liked songs" : "Remove from playlist";

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(AnySelectedDownloaded));
    }

    partial void OnIsSelectingChanged(bool value)
    {
        if (value) return;
        foreach (var r in Tracks) r.IsSelected = false;
        SelectedCount = 0;
        _anchor = null;
    }

    [RelayCommand]
    private void ToggleSelecting() => IsSelecting = !IsSelecting;

    /// <summary>A click while selecting (or Ctrl+click): toggles the row; Shift+click selects the range from the last one.</summary>
    public void Select(TrackRowViewModel row, bool range)
    {
        IsSelecting = true;
        if (range && _anchor is not null && Tracks.IndexOf(_anchor) is var a and >= 0)
        {
            var b = Tracks.IndexOf(row);
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) Tracks[i].IsSelected = true;
        }
        else
        {
            row.IsSelected = !row.IsSelected;
            _anchor = row;
        }
        SelectedCount = Tracks.Count(r => r.IsSelected);
        if (SelectedCount == 0) IsSelecting = false;
    }

    [RelayCommand]
    private void SelectAll()
    {
        IsSelecting = true;
        foreach (var r in Tracks) r.IsSelected = true;
        SelectedCount = Tracks.Count;
    }

    [RelayCommand]
    private async Task PlaySelected()
    {
        var tracks = SelectedTracks;
        IsSelecting = false;
        if (tracks.Count > 0) await _shell.Services.Playback.PlayListAsync(tracks, 0);
    }

    [RelayCommand]
    private void PlaySelectedNext()
    {
        var tracks = SelectedTracks;
        IsSelecting = false;
        foreach (var t in Enumerable.Reverse(tracks)) _shell.Services.Playback.Queue.PlayNext(t);
        _shell.Services.Playback.NotifyQueueChanged();
        _shell.ShowToast($"{ShellViewModel.Songs(tracks.Count)} will play next");
    }

    public void AddSelectedTo(PlaylistInfo? playlist)
    {
        var tracks = SelectedTracks;
        if (tracks.Count == 0) return;
        var lib = _shell.Services.Library;
        long id;
        string name;
        if (playlist is null)
        {
            if (_shell.AskPlaylistName(_shell.SuggestedPlaylistName) is not { } n) return;
            id = lib.CreatePlaylist(n);
            name = n;
        }
        else (id, name) = (playlist.Id, playlist.Name);
        lib.AddManyToPlaylist(id, tracks);
        IsSelecting = false;
        _shell.ReloadPlaylists();
        _shell.ShowToast($"Added {ShellViewModel.Songs(tracks.Count)} to {name}");
        if (PlaylistId == id) Reload();
    }

    [RelayCommand]
    private void DownloadSelected()
    {
        var tracks = SelectedTracks;
        IsSelecting = false;
        _shell.Download(tracks);
    }

    [RelayCommand]
    private void RemoveSelectedDownloads()
    {
        var tracks = SelectedRows.Where(r => r.Download != Tune.Core.Offline.DownloadState.None).Select(r => r.Track).ToList();
        IsSelecting = false;
        if (tracks.Count > 0) _shell.RemoveDownloads(tracks);
    }

    /// <summary>Out of this playlist, or out of Liked songs.</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        var rows = SelectedRows;
        var lib = _shell.Services.Library;
        if (rows.Count == 0 || !_shell.Confirm(IsLiked ? $"Remove {ShellViewModel.Songs(rows.Count)} from Liked songs?" : $"Remove {ShellViewModel.Songs(rows.Count)} from {Title}?",
                "You can add them back any time.", "Remove")) return;
        if (IsLocal && PlaylistId is { } id)
        {
            // positions shift as songs go: remove from the bottom up
            foreach (var i in rows.Select(r => Tracks.IndexOf(r)).Where(i => i >= 0).OrderDescending()) lib.RemoveFromPlaylist(id, i);
            _shell.ReloadPlaylists();
        }
        else if (IsLiked)
        {
            foreach (var r in rows) lib.SetLiked(r.Track, false);
            _shell.Player.Refresh();
            _shell.LikesChanged();
        }
        else return;
        IsSelecting = false;
        _shell.ShowToast($"Removed {ShellViewModel.Songs(rows.Count)}");
        Reload();
    }

    [RelayCommand]
    private void NotInterestedSelected()
    {
        var tracks = SelectedTracks;
        IsSelecting = false;
        foreach (var t in tracks) _shell.Services.Library.SetNotInterested(t.VideoId);
        _shell.ShowToast($"You won't get {(tracks.Count == 1 ? "this song" : "these songs")} in recommendations");
        _shell.HomeChanged();
    }
}
