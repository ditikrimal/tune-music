using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Lyrics;

namespace Tune.App.ViewModels;

/// <summary>One lyric line; <see cref="State"/> is Past, Current or Future.</summary>
public sealed partial class LyricLineViewModel(LyricLine line) : ObservableObject
{
    public LyricLine Line { get; } = line;
    public string Text => string.IsNullOrWhiteSpace(Line.Text) ? "♪" : Line.Text;
    [ObservableProperty] private string _state = "Future";
}

/// <summary>The full-screen player: big art, controls and lyrics that follow the song.</summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly LyricsService _lyrics;
    private string? _loadedFor;
    private IReadOnlyList<LyricLine> _timed = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string? _plainLyrics;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _source;
    [ObservableProperty] private int _currentIndex = -1;
    /// <summary>Full screen: art + lyrics over the whole window, the window covering the screen.</summary>
    [ObservableProperty] private bool _isFullScreen;
    /// <summary>Full screen without lyrics: the cover sits in the middle.</summary>
    [ObservableProperty] private bool _showLyrics = true;

    public NowPlayingViewModel(PlayerBarViewModel player, LyricsService lyrics)
    {
        Player = player;
        _lyrics = lyrics;
        Player.PropertyChanged += (_, e) =>
        {
            if (!IsOpen) return;
            if (e.PropertyName == nameof(PlayerBarViewModel.Track)) _ = LoadAsync();
            else if (e.PropertyName == nameof(PlayerBarViewModel.Position)) UpdateCurrent();
        };
    }

    public PlayerBarViewModel Player { get; }
    public ObservableCollection<LyricLineViewModel> Lines { get; } = [];
    public bool HasSynced => Lines.Count > 0;

    partial void OnIsOpenChanged(bool value)
    {
        if (value) _ = LoadAsync();
        else IsFullScreen = false;
    }

    /// <summary>Full screen was opened straight from the player bar: leaving it goes back to the page, not to Now Playing.</summary>
    private bool _closeAfterFullScreen;

    partial void OnIsFullScreenChanged(bool value)
    {
        if (value)
        {
            _closeAfterFullScreen = !IsOpen;
            IsOpen = true; // loads the lyrics
        }
        else if (_closeAfterFullScreen)
        {
            _closeAfterFullScreen = false;
            IsOpen = false;
        }
    }

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    [RelayCommand]
    private void ToggleLyrics() => ShowLyrics = !ShowLyrics;

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void SeekTo(LyricLineViewModel? line)
    {
        if (line is null) return;
        Player.BeginSeek();
        Player.CommitSeek(line.Line.At.TotalSeconds);
    }

    private async Task LoadAsync()
    {
        var t = Player.Track;
        if (t is null)
        {
            _loadedFor = null;
            Reset();
            Status = "Play a song to see its lyrics.";
            return;
        }
        var key = t.VideoId + (_lyrics.SyncedAllowed ? "|s" : "|p"); // the synced-lyrics setting can change between opens
        if (_loadedFor == key)
        {
            UpdateCurrent();
            return;
        }
        _loadedFor = key;
        Reset();
        Status = "Finding lyrics…";
        SongLyrics? l;
        try { l = await _lyrics.GetAsync(t); }
        catch (Exception) { l = null; }
        if (_loadedFor != key) return; // the song changed meanwhile
        Status = null;
        if (l is null)
        {
            _loadedFor = null; // reopening tries again
            Status = "No lyrics for this song yet.";
            return;
        }
        Source = l.Source;
        if (l.Lines is { Count: > 0 } lines)
        {
            _timed = lines;
            foreach (var x in lines) Lines.Add(new LyricLineViewModel(x));
            OnPropertyChanged(nameof(HasSynced));
            UpdateCurrent();
        }
        else
        {
            PlainLyrics = l.Text;
        }
    }

    private void Reset()
    {
        Lines.Clear();
        _timed = [];
        PlainLyrics = null;
        Source = null;
        CurrentIndex = -1;
        OnPropertyChanged(nameof(HasSynced));
    }

    private void UpdateCurrent()
    {
        if (_timed.Count == 0) return;
        // a quarter-second lead so the line lights up as it is sung, not after
        var i = Lrc.IndexAt(_timed, TimeSpan.FromSeconds(Player.Position + 0.25));
        if (i == CurrentIndex) return;
        for (var k = 0; k < Lines.Count; k++)
            Lines[k].State = k < i ? "Past" : k == i ? "Current" : "Future";
        CurrentIndex = i;
    }
}
