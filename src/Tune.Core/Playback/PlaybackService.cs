using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.SponsorBlock;
using Tune.Core.Streams;
using Tune.Core.Util;

namespace Tune.Core.Playback;

/// <summary>
/// Orchestrates playback: resolves streams, drives the audio player, appends radio, records history,
/// skips SponsorBlock segments and recovers from failures. Events may fire on any thread.
/// </summary>
public sealed class PlaybackService
{
    private static readonly TimeSpan RecordAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LookaheadAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReloadAfterPause = TimeSpan.FromMinutes(30);
    private const int MaxConsecutiveFailures = 3;

    private readonly IStreamResolver _resolver;
    private readonly IAudioPlayer _player;
    private readonly LibraryStore _lib;
    private readonly ISponsorBlock _sponsorBlock;
    private readonly IYtMusic _api;
    private readonly Func<CancellationToken, Task<bool>> _updateYtDlp;
    private readonly IClock _clock;

    private readonly Lock _pendingGate = new();
    private readonly List<Task> _pending = [];
    private readonly HashSet<string> _radioFetched = [];

    private int _generation;
    private bool _updatedTools;
    private int _failures;
    private DateTimeOffset? _pausedAt;

    // per-track state
    private bool _recorded;
    private bool _lookaheadDone;
    private IReadOnlyList<SkipSegment> _segments = [];

    public PlaybackService(PlayQueue queue, IStreamResolver resolver, IAudioPlayer player, LibraryStore lib,
        ISponsorBlock sponsorBlock, IYtMusic api, Func<CancellationToken, Task<bool>> updateYtDlp, IClock clock)
    {
        Queue = queue;
        _resolver = resolver;
        _player = player;
        _lib = lib;
        _sponsorBlock = sponsorBlock;
        _api = api;
        _updateYtDlp = updateYtDlp;
        _clock = clock;

        _player.PositionChanged += OnPosition;
        _player.Ended += () => Spawn(OnEndedAsync);
        _player.Failed += why =>
        {
            var gen = _generation;
            if (Current is { } t) Spawn(() => OnTrackFailedAsync(t, why, gen));
        };
    }

    public PlayQueue Queue { get; }
    public IAudioPlayer Player => _player;
    public Track? Current => Queue.Current;
    public bool IsPlaying { get; private set; }

    /// <summary>Something the user should see (track list, play state, queue) changed.</summary>
    public event Action? StateChanged;

    /// <summary>A short message for a toast, e.g. a skipped song.</summary>
    public event Action<string>? Notice;

    /// <summary>Completes when all background work (radio, lookahead, failure handling) has settled.</summary>
    public Task Background => WaitIdleAsync();

    public Task PlayListAsync(IReadOnlyList<Track> list, int index)
    {
        Queue.StartList(list, index);
        return PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true);
    }

    public Task PlayRadioAsync(Track seed)
    {
        Queue.StartRadio(seed);
        Spawn(EnsureRadioAsync);
        return PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true);
    }

    public Task NextAsync() => AdvanceAsync(userInitiated: true);

    public Task PreviousAsync()
    {
        if (_player.Position > RestartThreshold)
        {
            _player.Seek(TimeSpan.Zero);
            return Task.CompletedTask;
        }
        Queue.Previous();
        return PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true);
    }

    public void TogglePause()
    {
        if (Current is null) return;
        if (_player.IsPaused)
        {
            if (_pausedAt is { } at && _clock.Now - at > ReloadAfterPause)
            {
                // the stream URL has probably expired while paused (e.g. laptop slept overnight)
                var pos = _player.Position;
                _resolver.Invalidate(Current.VideoId);
                Spawn(() => PlayCurrentAsync(pos, resetTrackState: false));
            }
            else
            {
                _player.Play();
            }
            _pausedAt = null;
            IsPlaying = true;
        }
        else
        {
            _player.Pause();
            _pausedAt = _clock.Now;
            IsPlaying = false;
        }
        StateChanged?.Invoke();
    }

    public void Seek(TimeSpan pos) => _player.Seek(pos);

    /// <summary>Call after changing <see cref="Queue"/> directly (shuffle, repeat, reorder…).</summary>
    public void NotifyQueueChanged()
    {
        if (Queue.NeedsRadio) Spawn(EnsureRadioAsync);
        StateChanged?.Invoke();
    }

    // ---------- core ----------

    private async Task PlayCurrentAsync(TimeSpan startAt, bool resetTrackState)
    {
        var track = Queue.Current;
        if (track is null)
        {
            Stop();
            return;
        }

        var gen = Interlocked.Increment(ref _generation);
        if (resetTrackState)
        {
            _recorded = false;
            _lookaheadDone = false;
            _segments = [];
        }
        StateChanged?.Invoke();

        Uri url;
        try
        {
            url = await _resolver.ResolveAsync(track.VideoId).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (gen == _generation) await OnTrackFailedAsync(track, e.Message, gen).ConfigureAwait(false);
            return;
        }
        if (gen != _generation) return;

        if (resetTrackState) Spawn(() => LoadSegmentsAsync(track, gen));
        await _player.LoadAsync(url, startAt).ConfigureAwait(false);
        _player.Play();
        _pausedAt = null;
        IsPlaying = true;
        StateChanged?.Invoke();
    }

    private async Task AdvanceAsync(bool userInitiated)
    {
        var next = Queue.Next(userInitiated);
        if (next is null && Queue.NeedsRadio)
        {
            await EnsureRadioAsync().ConfigureAwait(false);
            next = Queue.Next(userInitiated);
        }
        if (next is null)
        {
            Stop();
            return;
        }
        await PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true).ConfigureAwait(false);
    }

    private async Task OnEndedAsync()
    {
        RecordPlay();
        await AdvanceAsync(userInitiated: false).ConfigureAwait(false);
    }

    private async Task OnTrackFailedAsync(Track track, string reason, int gen)
    {
        if (gen != _generation) return;
        Log.Warn($"Playback failed for {track.VideoId}: {reason}");

        if (!_updatedTools)
        {
            // most failures are YouTube changes that a newer yt-dlp already handles
            _updatedTools = true;
            try { await _updateYtDlp(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Warn("yt-dlp update failed", e); }
            _resolver.Invalidate(track.VideoId);
            if (gen == _generation) await PlayCurrentAsync(TimeSpan.Zero, resetTrackState: false).ConfigureAwait(false);
            return;
        }

        _failures++;
        if (_failures >= MaxConsecutiveFailures)
        {
            _failures = 0;
            Stop();
            Notice?.Invoke("Couldn't play 3 songs in a row — check your internet connection and try again.");
            return;
        }
        Notice?.Invoke($"Couldn't play {track.Title} — skipped");
        await AdvanceAsync(userInitiated: true).ConfigureAwait(false);
    }

    private async Task EnsureRadioAsync()
    {
        var seed = Queue.RadioSeedId;
        if (seed is null) return;
        lock (_radioFetched)
            if (!_radioFetched.Add(seed)) return;
        try
        {
            var tracks = await _api.GetRadioAsync(seed).ConfigureAwait(false);
            Queue.AppendRadio(tracks);
            StateChanged?.Invoke();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_radioFetched) _radioFetched.Remove(seed); // allow a later retry
            Log.Warn($"Radio for {seed} unavailable", e);
        }
    }

    private async Task LoadSegmentsAsync(Track track, int gen)
    {
        var segments = await _sponsorBlock.GetSegmentsAsync(track.VideoId).ConfigureAwait(false);
        if (gen == _generation) _segments = segments;
    }

    private void OnPosition()
    {
        var pos = _player.Position;
        if (pos >= HealthyAfter) _failures = 0;
        if (!_recorded && pos >= RecordAfter) RecordPlay();

        if (!_lookaheadDone && pos >= LookaheadAfter)
        {
            _lookaheadDone = true;
            if (Queue.PeekNext() is { } next && next.VideoId != Current?.VideoId)
                Spawn(() => _resolver.ResolveAsync(next.VideoId));
            if (Queue.NeedsRadio) Spawn(EnsureRadioAsync);
        }

        foreach (var s in _segments)
        {
            if (pos.TotalSeconds >= s.Start && pos.TotalSeconds < s.End - 0.5)
            {
                _player.Seek(TimeSpan.FromSeconds(s.End));
                break;
            }
        }
    }

    private void RecordPlay()
    {
        if (_recorded || Current is not { } t) return;
        _recorded = true;
        _lib.RecordPlay(t, _clock.Now);
    }

    private void Stop()
    {
        _player.Stop();
        IsPlaying = false;
        StateChanged?.Invoke();
    }

    // ---------- background bookkeeping ----------

    private void Spawn(Func<Task> work)
    {
        var task = Guard(work);
        lock (_pendingGate) _pending.Add(task);
    }

    private static async Task Guard(Func<Task> work)
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception e) { Log.Error("Background playback task failed", e); }
    }

    private async Task WaitIdleAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_pendingGate)
            {
                _pending.RemoveAll(t => t.IsCompleted);
                snapshot = [.. _pending];
            }
            if (snapshot.Length == 0) return;
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
    }
}
