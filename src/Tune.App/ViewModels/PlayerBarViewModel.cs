using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;

namespace Tune.App.ViewModels;

/// <summary>Now-playing state shared by the main player bar and the mini-player.</summary>
public sealed partial class PlayerBarViewModel : ObservableObject
{
    private readonly PlaybackService _playback;
    private readonly LibraryStore _lib;
    private readonly Dispatcher _ui;
    private int _positionQueued;

    [ObservableProperty] private Track? _track;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLiked;
    [ObservableProperty] private bool _shuffleOn;
    [ObservableProperty] private RepeatMode _repeat;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _isSeeking;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Album-tinted background for the widget (WidgetPod style).</summary>
    [ObservableProperty] private System.Windows.Media.Brush _tint = Frozen(System.Windows.Media.Color.FromRgb(0x24, 0x24, 0x24));

    public static System.Windows.Media.Brush Frozen(System.Windows.Media.Color c)
    {
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public PlayerBarViewModel(PlaybackService playback, LibraryStore lib, Dispatcher ui)
    {
        _playback = playback;
        _lib = lib;
        _ui = ui;
        _volume = double.TryParse(lib.GetSetting("volume"), CultureInfo.InvariantCulture, out var v) ? v : 0.8;
        _playback.Player.Volume = _volume;
        _shuffleOn = lib.GetSetting("queue.shuffle") == "1";
        _repeat = Enum.TryParse<RepeatMode>(lib.GetSetting("queue.repeat"), out var r) ? r : RepeatMode.Off;
        _playback.Queue.Shuffle = _shuffleOn;
        _playback.Queue.Repeat = _repeat;

        _playback.StateChanged += () => _ui.BeginInvoke(Refresh);
        _playback.Player.PositionChanged += OnPositionChanged;
    }

    /// <summary>False while no window shows the bar (minimised / hidden) so we don't wake the UI thread.</summary>
    public bool UpdatesEnabled { get; set; } = true;

    public bool HasTrack => Track is not null;
    public string Title => Track?.Title ?? "";
    public string ArtistLine => Track?.ArtistLine ?? "";
    public string? ArtUrl => Track?.ThumbnailUrl;
    public string PlayGlyph => IsPlaying ? "" : "";
    public string LikeGlyph => IsLiked ? "" : "";
    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";
    public string RepeatTag => Repeat == RepeatMode.Off ? "Off" : "On";
    public string ShuffleTag => ShuffleOn ? "On" : "Off";
    public string RepeatTip => Repeat switch { RepeatMode.All => "Repeat all", RepeatMode.One => "Repeat one", _ => "Repeat off" };

    partial void OnTrackChanged(Track? value)
    {
        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ArtistLine));
        OnPropertyChanged(nameof(ArtUrl));
    }

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayGlyph));
    partial void OnIsLikedChanged(bool value) => OnPropertyChanged(nameof(LikeGlyph));
    partial void OnShuffleOnChanged(bool value) => OnPropertyChanged(nameof(ShuffleTag));

    partial void OnRepeatChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatGlyph));
        OnPropertyChanged(nameof(RepeatTag));
        OnPropertyChanged(nameof(RepeatTip));
    }

    partial void OnVolumeChanged(double value)
    {
        _playback.Player.Volume = value;
        _lib.SetSetting("volume", value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public void Refresh()
    {
        var current = _playback.Current;
        if (current?.VideoId != Track?.VideoId)
        {
            Track = current;
            Position = 0;
            PositionText = "0:00";
        }
        IsPlaying = _playback.IsPlaying;
        IsLoading = _playback.IsLoading;
        IsLiked = current is not null && _lib.IsLiked(current.VideoId);
        UpdatePosition();
    }

    private void OnPositionChanged()
    {
        if (!UpdatesEnabled || Interlocked.Exchange(ref _positionQueued, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _positionQueued, 0);
            UpdatePosition();
        });
    }

    private void UpdatePosition()
    {
        var dur = _playback.Player.Duration ?? Track?.Duration;
        Duration = Math.Max(1, dur?.TotalSeconds ?? 1);
        DurationText = Format(dur ?? TimeSpan.Zero);
        if (IsSeeking) return;
        var pos = _playback.Player.Position;
        Position = Math.Min(pos.TotalSeconds, Duration);
        PositionText = IsLoading ? "Loading…" : Format(pos);
    }

    public void BeginSeek() => IsSeeking = true;

    public void CommitSeek(double seconds)
    {
        IsSeeking = false;
        _playback.Seek(TimeSpan.FromSeconds(seconds));
        PositionText = Format(TimeSpan.FromSeconds(seconds));
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    [RelayCommand]
    private void PlayPause()
    {
        if (_playback.Current is null) return;
        _playback.TogglePause();
    }

    [RelayCommand]
    private Task Next() => _playback.NextAsync();

    [RelayCommand]
    private Task Previous() => _playback.PreviousAsync();

    [RelayCommand]
    private void ToggleLike()
    {
        if (_playback.Current is not { } t) return;
        IsLiked = !IsLiked;
        _lib.SetLiked(t, IsLiked);
        LikedChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        ShuffleOn = !ShuffleOn;
        _playback.Queue.Shuffle = ShuffleOn;
        _lib.SetSetting("queue.shuffle", ShuffleOn ? "1" : "0");
        _playback.NotifyQueueChanged();
    }

    [RelayCommand]
    private void CycleRepeat()
    {
        Repeat = Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        _playback.Queue.Repeat = Repeat;
        _lib.SetSetting("queue.repeat", Repeat.ToString());
        _playback.NotifyQueueChanged();
    }

    /// <summary>Raised when the like state changes, so a visible Liked page can refresh.</summary>
    public event Action? LikedChanged;
}
