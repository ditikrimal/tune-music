using System.Windows.Threading;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Util;
using Windows.Media;
using Windows.Storage.Streams;

namespace Tune.App.Platform;

/// <summary>
/// Windows media integration (SMTC): keyboard media keys, the volume/media flyout with title, artist and art,
/// and the lock-screen controls. Bound to the main window's HWND.
/// </summary>
public sealed class MediaControls : IDisposable
{
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(5);
    private readonly SystemMediaTransportControls _smtc;
    private readonly PlaybackService _playback;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timeline;
    private string? _shownVideoId;

    public MediaControls(nint hwnd, PlaybackService playback, Dispatcher ui)
    {
        _playback = playback;
        _ui = ui;
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButton;
        _smtc.PlaybackPositionChangeRequested += (_, e) => _ui.BeginInvoke(() => _playback.Seek(e.RequestedPlaybackPosition));

        _playback.StateChanged += () => _ui.BeginInvoke(Update);
        _timeline = new DispatcherTimer(TimelineInterval, DispatcherPriority.Background, (_, _) => UpdateTimeline(), ui);
    }

    private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e) =>
        _ui.BeginInvoke(async () =>
        {
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play when !_playback.IsPlaying:
                case SystemMediaTransportControlsButton.Pause when _playback.IsPlaying:
                    _playback.TogglePause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    await _playback.NextAsync();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    await _playback.PreviousAsync();
                    break;
            }
        });

    private void Update()
    {
        var track = _playback.Current;
        _smtc.PlaybackStatus = track is null ? MediaPlaybackStatus.Stopped
            : _playback.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (_playback.IsPlaying) _timeline.Start(); else _timeline.Stop();

        if (track?.VideoId == _shownVideoId) return;
        _shownVideoId = track?.VideoId;
        ShowTrack(track);
        UpdateTimeline();
    }

    private void ShowTrack(Track? track)
    {
        try
        {
            var u = _smtc.DisplayUpdater;
            u.ClearAll();
            if (track is not null)
            {
                u.Type = MediaPlaybackType.Music;
                u.AppMediaId = "Tune";
                u.MusicProperties.Title = track.Title;
                u.MusicProperties.Artist = track.ArtistLine;
                u.MusicProperties.AlbumTitle = track.Album?.Title ?? "";
                if (track.ThumbnailUrl is { } url)
                    u.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(ArtCache.Sized(url, 300)));
            }
            u.Update();
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn("Media flyout update failed", e);
        }
    }

    private void UpdateTimeline()
    {
        var duration = _playback.Player.Duration ?? _playback.Current?.Duration;
        if (duration is null || _playback.Current is null) return;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = duration.Value,
            MaxSeekTime = duration.Value,
            Position = _playback.Player.Position,
        });
    }

    public void Dispose()
    {
        _timeline.Stop();
        _smtc.ButtonPressed -= OnButton;
        _smtc.IsEnabled = false;
    }
}
