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

    private readonly Tune.Core.Offline.DownloadManager? _downloads;
    [ObservableProperty] private Tune.Core.Offline.DownloadState _download;

    public string DownloadGlyph => Download == Tune.Core.Offline.DownloadState.Done ? "\uE73E" : "\uE896";
    public string DownloadTag => Download == Tune.Core.Offline.DownloadState.None ? "Off" : "On";
    public string DownloadTip => Download switch
    {
        Tune.Core.Offline.DownloadState.Done => "Downloaded — plays without internet",
        Tune.Core.Offline.DownloadState.None => "Download",
        _ => "Downloading…",
    };

    partial void OnDownloadChanged(Tune.Core.Offline.DownloadState value)
    {
        OnPropertyChanged(nameof(DownloadGlyph));
        OnPropertyChanged(nameof(DownloadTag));
        OnPropertyChanged(nameof(DownloadTip));
    }

    /// <summary>Re-reads the playing song's download state.</summary>
    public void RefreshDownload() =>
        Download = Track is { } t && _downloads is not null ? _downloads.StateOf(t.VideoId) : Tune.Core.Offline.DownloadState.None;

    /// <summary>Asks the shell to download (or explain) — it owns toasts.</summary>
    public event Action<Track>? DownloadRequested;

    [RelayCommand]
    private void DownloadSong()
    {
        if (Track is { } t) DownloadRequested?.Invoke(t);
    }

    public PlayerBarViewModel(PlaybackService playback, LibraryStore lib, Dispatcher ui, Tune.Core.Offline.DownloadManager? downloads = null)
    {
        _downloads = downloads;
        _playback = playback;
        _lib = lib;
        _ui = ui;
        _volume = double.TryParse(lib.GetSetting("volume"), CultureInfo.InvariantCulture, out var v) ? v : 0.8;
        _playback.Player.Volume = _volume;
        _shuffleOn = lib.GetSetting("queue.shuffle") == "1";
        _repeat = Enum.TryParse<RepeatMode>(lib.GetSetting("queue.repeat"), out var r) ? r : RepeatMode.Off;
        _playback.Queue.Shuffle = _shuffleOn;
        _playback.Queue.Repeat = _repeat;

        _saveVolume.Tick += (_, _) => SaveVolume();
        _sleepTick.Tick += (_, _) => SleepTickNow();
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
    public string PlayIconKey => IsPlaying ? "IcoPause" : "IcoPlay";
    public string RepeatIconKey => Repeat == RepeatMode.One ? "IcoRepeatOne" : "IcoRepeat";
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

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayGlyph));
        OnPropertyChanged(nameof(PlayIconKey));
    }
    partial void OnIsLikedChanged(bool value) => OnPropertyChanged(nameof(LikeGlyph));
    partial void OnShuffleOnChanged(bool value) => OnPropertyChanged(nameof(ShuffleTag));

    partial void OnRepeatChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatGlyph));
        OnPropertyChanged(nameof(RepeatIconKey));
        OnPropertyChanged(nameof(RepeatTag));
        OnPropertyChanged(nameof(RepeatTip));
    }

    partial void OnVolumeChanged(double value)
    {
        _playback.Player.Volume = SleepFade(value);
        // a slider drag sends dozens of values a second: save once it settles
        _saveVolume.Stop();
        _saveVolume.Start();
    }

    private readonly DispatcherTimer _saveVolume = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Writes a volume change that is still waiting for the slider to settle (call on exit).</summary>
    public void SaveVolume()
    {
        _saveVolume.Stop();
        _lib.SetSetting("volume", Volume.ToString("0.###", CultureInfo.InvariantCulture));
    }

    // ---------- sleep timer ----------

    private static readonly TimeSpan SleepFadeLength = TimeSpan.FromSeconds(20);
    private readonly DispatcherTimer _sleepTick = new();
    private DateTime? _sleepAt;
    [ObservableProperty] private bool _sleepAtEndOfSong;
    [ObservableProperty] private string _sleepText = "";

    public bool SleepActive => _sleepAt is not null || SleepAtEndOfSong;
    public string SleepTag => SleepActive ? "On" : "Off";
    public string SleepTip => SleepActive ? $"Sleep timer: pauses {SleepText}" : "Sleep timer";

    /// <summary>"15", "30"… minutes, "end" (after this song) or "off".</summary>
    [RelayCommand]
    private void SetSleep(string what)
    {
        _sleepAt = null;
        _sleepTick.Stop();
        SleepAtEndOfSong = false;
        _playback.PauseAfterCurrent = false;
        if (what == "end")
        {
            SleepAtEndOfSong = true;
            _playback.PauseAfterCurrent = true;
        }
        else if (int.TryParse(what, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            _sleepAt = DateTime.UtcNow.AddMinutes(minutes);
            SleepTickNow();
        }
        _playback.Player.Volume = Volume;
        UpdateSleepText();
    }

    private void SleepTickNow()
    {
        if (_sleepAt is not { } at) return;
        var left = at - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _sleepAt = null;
            _sleepTick.Stop();
            if (_playback.IsPlaying) _playback.TogglePause();
            _playback.Player.Volume = Volume; // undo the fade for whenever play is pressed again
            UpdateSleepText();
            return;
        }
        _playback.Player.Volume = SleepFade(Volume);
        UpdateSleepText();
        // tick rarely while far away (the label shows minutes), smoothly during the fade
        _sleepTick.Interval = left > SleepFadeLength + TimeSpan.FromSeconds(5)
            ? TimeSpan.FromSeconds(Math.Min(30, (left - SleepFadeLength).TotalSeconds))
            : TimeSpan.FromMilliseconds(250);
        _sleepTick.Start();
    }

    /// <summary>The volume to play at: the user's volume, faded out over the timer's last seconds.</summary>
    private double SleepFade(double volume)
    {
        if (_sleepAt is not { } at) return volume;
        var left = at - DateTime.UtcNow;
        return left >= SleepFadeLength ? volume : volume * Math.Clamp(left / SleepFadeLength, 0, 1);
    }

    private void UpdateSleepText()
    {
        SleepText = _sleepAt is { } at
            ? $"in {Math.Max(1, (int)Math.Ceiling((at - DateTime.UtcNow).TotalMinutes))} min"
            : SleepAtEndOfSong ? "after this song" : "";
        OnPropertyChanged(nameof(SleepActive));
        OnPropertyChanged(nameof(SleepTag));
        OnPropertyChanged(nameof(SleepTip));
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
        RefreshDownload();
        if (SleepAtEndOfSong && !_playback.PauseAfterCurrent)
        {
            SleepAtEndOfSong = false; // it fired
            UpdateSleepText();
        }
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
        if (_seekTarget is { } target)
        {
            // until the player has actually jumped, ticks still report the old spot: don't snap back to it
            if (Math.Abs(pos.TotalSeconds - target) > 1.5 && DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(3)) return;
            _seekTarget = null;
        }
        Position = Math.Min(pos.TotalSeconds, Duration);
        PositionText = IsLoading ? "Loading…" : Format(pos);
    }

    public void BeginSeek() => IsSeeking = true;

    private double? _seekTarget;
    private DateTime _seekAt;

    public void CommitSeek(double seconds)
    {
        _seekTarget = seconds;
        _seekAt = DateTime.UtcNow;
        Position = seconds;
        PositionText = Format(TimeSpan.FromSeconds(seconds));
        IsSeeking = false;
        _playback.Seek(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Shows where a drag would land, without seeking yet.</summary>
    public void PreviewSeek(double seconds) => PositionText = Format(TimeSpan.FromSeconds(seconds));

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
