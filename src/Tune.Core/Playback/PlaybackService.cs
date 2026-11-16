using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Offline;
using Tune.Core.SponsorBlock;
using Tune.Core.Streams;
using Tune.Core.Util;

namespace Tune.Core.Playback;

/// <summary>
/// Orchestrates playback: resolves streams, drives the audio player, appends radio, records history,
/// skips SponsorBlock segments and recovers from failures. Events may fire on any thread.
/// </summary>
/// <remarks>
/// Every load gets a new <em>generation</em> with its own cancellation token: starting another song cancels
/// the previous resolve (killing its yt-dlp), and player events that arrive while nothing is loaded — late
/// ticks or the end of the song that was just replaced — are ignored.
/// </remarks>
public sealed class PlaybackService
{
    private static readonly TimeSpan RecordAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LookaheadAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReloadAfterPause = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ToolUpdateEvery = TimeSpan.FromHours(6);
    private const int MaxConsecutiveFailures = 3;

    private readonly IStreamResolver _resolver;
    private readonly IAudioPlayer _player;
    private readonly LibraryStore _lib;
    private readonly ISponsorBlock _sponsorBlock;
    private readonly IYtMusic _api;
    private readonly Func<CancellationToken, Task<bool>> _updateYtDlp;
    private readonly IClock _clock;
    private readonly IOfflineSource? _offline;
    private volatile bool _fromStream; // the current song plays from YouTube, not a downloaded file

    private readonly Lock _pendingGate = new();
    private readonly List<Task> _pending = [];

    private int _generation;
    private CancellationTokenSource _loadCts = new();
    private volatile bool _loaded;           // a file for the current generation is in the player
    private DateTimeOffset? _lastToolUpdate;
    private int _failures;
    private DateTimeOffset? _pausedAt;

    // radio is tied to the queue it was asked for: a new list/radio start bumps the epoch
    private int _queueEpoch;
    private (int Epoch, string Seed)? _radioKey;
    private Task? _radioTask;

    // per-track state
    private bool _recorded;
    private bool _lookaheadDone;
    private bool _reloadTried;
    private bool _altPathTried;
    private IReadOnlyList<SkipSegment> _segments = [];

    public PlaybackService(PlayQueue queue, IStreamResolver resolver, IAudioPlayer player, LibraryStore lib,
        ISponsorBlock sponsorBlock, IYtMusic api, Func<CancellationToken, Task<bool>> updateYtDlp, IClock clock,
        IOfflineSource? offline = null)
    {
        _offline = offline;
        if (offline is not null) offline.Network.Changed += OnNetworkChanged;
        Queue = queue;
        _resolver = resolver;
        _player = player;
        _lib = lib;
        _sponsorBlock = sponsorBlock;
        _api = api;
        _updateYtDlp = updateYtDlp;
        _clock = clock;

        _player.PositionChanged += OnPosition;
        _player.Ended += () =>
        {
            if (_loaded) Spawn(OnEndedAsync);
        };
        _player.Failed += why =>
        {
            var gen = _generation;
            if (_loaded && Current is { } t) Spawn(() => OnPlayerFailedAsync(t, why, gen));
        };
    }

    public PlayQueue Queue { get; }
    public IAudioPlayer Player => _player;
    public Track? Current => Queue.Current;
    public bool IsPlaying { get; private set; }

    /// <summary>True between choosing a song and its audio starting (yt-dlp + YouTube's start delay).</summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Sleep timer "end of song": when the current song finishes, move to the next one but don't start it.
    /// Clears itself once used.
    /// </summary>
    public bool PauseAfterCurrent { get; set; }

    /// <summary>Something the user should see (track list, play state, queue) changed.</summary>
    public event Action? StateChanged;

    /// <summary>A short message for a toast, e.g. a skipped song.</summary>
    public event Action<string>? Notice;

    /// <summary>Completes when all background work (radio, lookahead, failure handling) has settled.</summary>
    public Task Background => WaitIdleAsync();

    public Task PlayListAsync(IReadOnlyList<Track> list, int index)
    {
        if (IsOffline && index >= 0 && index < list.Count && !IsLocal(list[index]))
        {
            // offline: start from the next downloaded song instead; nothing downloaded here -> say so
            var local = Enumerable.Range(1, list.Count - 1).Select(k => (index + k) % list.Count).Where(i => IsLocal(list[i])).ToList();
            if (local.Count == 0)
            {
                Notice?.Invoke(list.Count == 1
                    ? $"You're offline — download {list[index].Title} to play it without internet"
                    : "You're offline and none of these songs are downloaded");
                return Task.CompletedTask;
            }
            Notice?.Invoke("You're offline — playing the downloaded songs");
            index = local[0];
        }
        Queue.StartList(list, index);
        Interlocked.Increment(ref _queueEpoch);
        return PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true);
    }

    public Task PlayRadioAsync(Track seed)
    {
        if (IsOffline) return PlayListAsync([seed], 0); // no radio without internet
        Queue.StartRadio(seed);
        Interlocked.Increment(ref _queueEpoch);
        var play = PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true);
        Spawn(EnsureRadioAsync);
        return play;
    }

    public Task NextAsync() => AdvanceAsync(userInitiated: true);

    public Task PreviousAsync()
    {
        if (_loaded && _player.Position > RestartThreshold)
        {
            _player.Seek(TimeSpan.Zero);
            return Task.CompletedTask;
        }
        Queue.Previous();
        return PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true);
    }

    public void TogglePause()
    {
        if (Current is null || IsLoading) return;

        if (!_loaded)
        {
            // stopped (end of queue, failures, "play next" into an empty queue, failed resume): start it again
            var from = _resumePosition ?? TimeSpan.Zero;
            _resumePosition = null;
            Spawn(() => PlayCurrentAsync(from, resetTrackState: from == TimeSpan.Zero));
            return;
        }

        if (_player.IsPaused)
        {
            if (_pausedAt is { } at && _clock.Now - at > ReloadAfterPause)
            {
                // the stream URL has probably expired while paused (e.g. laptop slept overnight)
                Spawn(() => ResumeAsync(_player.Position));
                return;
            }
            _player.Play();
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

    public void Seek(TimeSpan pos)
    {
        if (_loaded) _player.Seek(pos);
    }

    /// <summary>Call after changing <see cref="Queue"/> directly (shuffle, repeat, reorder…).</summary>
    public void NotifyQueueChanged()
    {
        if (Queue.NeedsRadio) Spawn(EnsureRadioAsync);
        StateChanged?.Invoke();
    }

    // ---------- core ----------

    /// <summary>Starts a new generation: cancels whatever was loading and silences the old song immediately.</summary>
    private (int Gen, CancellationToken Ct) BeginLoad()
    {
        var gen = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _loadCts, cts).Cancel();
        _loaded = false;
        _player.Stop();
        IsLoading = true;
        IsPlaying = false;
        return (gen, cts.Token);
    }

    private async Task PlayCurrentAsync(TimeSpan startAt, bool resetTrackState)
    {
        var track = Queue.Current;
        if (track is null)
        {
            Stop();
            return;
        }

        var (gen, ct) = BeginLoad();
        if (resetTrackState)
        {
            _recorded = false;
            _lookaheadDone = false;
            _reloadTried = false;
            _altPathTried = false;
            _segments = [];
        }
        StateChanged?.Invoke();

        Uri url;
        try
        {
            if (_offline?.LocalPath(track.VideoId) is { } file)
            {
                url = new Uri(file);
                _fromStream = false;
            }
            else if (IsOffline)
            {
                // not downloaded and no internet: move on to something that can play
                if (gen == _generation) await AdvanceAsync(userInitiated: true).ConfigureAwait(false);
                return;
            }
            else
            {
                url = await _resolver.ResolveAsync(track.VideoId, ct).ConfigureAwait(false);
                _fromStream = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // another song was chosen meanwhile
        }
        catch (Exception e)
        {
            if (gen == _generation) await OnTrackFailedAsync(track, e.Message, gen).ConfigureAwait(false);
            return;
        }
        if (gen != _generation) return;

        if (resetTrackState && !IsOffline) Spawn(() => LoadSegmentsAsync(track, gen));
        await StartAudioAsync(url, startAt, gen).ConfigureAwait(false);
    }

    private async Task StartAudioAsync(Uri url, TimeSpan startAt, int gen)
    {
        await _player.LoadAsync(url, startAt).ConfigureAwait(false);
        if (gen != _generation) return;
        _player.Play();
        _loaded = true;
        _pausedAt = null;
        IsLoading = false;
        IsPlaying = true;
        StateChanged?.Invoke();
        PrepareUpcoming();
    }

    private const int PrepareAhead = 2;

    /// <summary>Looks up the next songs now, so Next / auto-advance start instantly.</summary>
    private void PrepareUpcoming()
    {
        if (IsOffline) return;
        var items = Queue.Items;
        for (int i = Queue.CurrentIndex + 1, n = 0; i < items.Count && n < PrepareAhead; i++, n++)
        {
            var id = items[i].VideoId;
            if (_offline?.LocalPath(id) is null && !_resolver.IsKnown(id)) Spawn(() => _resolver.PrepareAsync(id));
        }
    }

    /// <summary>Resume after a long pause: fresh URL, same position. Without network it stays paused.</summary>
    private async Task ResumeAsync(TimeSpan position)
    {
        var track = Queue.Current;
        if (track is null) return;
        if (!_fromStream || IsLocal(track))
        {
            await PlayCurrentAsync(position, resetTrackState: false).ConfigureAwait(false);
            return;
        }
        _resolver.Invalidate(track.VideoId);
        var (gen, ct) = BeginLoad();
        StateChanged?.Invoke();
        try
        {
            var url = await _resolver.ResolveAsync(track.VideoId, ct).ConfigureAwait(false);
            if (gen != _generation) return;
            await StartAudioAsync(url, position, gen).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (gen != _generation) return;
            Log.Warn($"Resume failed for {track.VideoId}", e);
            // keep the song and position; the next press of Play tries again
            IsLoading = false;
            IsPlaying = false;
            _resumePosition = position;
            StateChanged?.Invoke();
            Notice?.Invoke("Couldn't resume — check your internet connection, then press play again.");
        }
    }

    private TimeSpan? _resumePosition;

    private async Task AdvanceAsync(bool userInitiated)
    {
        if (IsOffline)
        {
            await AdvanceOfflineAsync(userInitiated).ConfigureAwait(false);
            return;
        }
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

    /// <summary>Next downloaded song in the queue; if the queue has none, the downloads themselves.</summary>
    private async Task AdvanceOfflineAsync(bool userInitiated)
    {
        var tries = Queue.Items.Count;
        for (var k = 0; k < tries; k++)
        {
            var next = Queue.Next(userInitiated || k > 0);
            if (next is null) break;
            if (IsLocal(next))
            {
                await PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true).ConfigureAwait(false);
                return;
            }
        }
        var downloads = _offline!.DownloadedTracks();
        if (downloads.Count == 0)
        {
            Stop();
            Notice?.Invoke("You're offline — download songs to keep listening without internet");
            return;
        }
        Notice?.Invoke("You're offline — playing your downloads");
        Queue.StartList(downloads, Queue.Shuffle ? Random.Shared.Next(downloads.Count) : 0);
        Interlocked.Increment(ref _queueEpoch);
        await PlayCurrentAsync(TimeSpan.Zero, resetTrackState: true).ConfigureAwait(false);
    }

    /// <summary>
    /// The connection dropped while a downloaded song was streaming from YouTube: switch to the file at the
    /// same spot, so it keeps going.
    /// </summary>
    private void OnNetworkChanged(bool online)
    {
        if (online || !_loaded || !_fromStream || Current is not { } t || !IsLocal(t)) return;
        var gen = _generation;
        var at = _player.Position;
        var paused = _player.IsPaused;
        Spawn(async () =>
        {
            if (gen != _generation) return;
            await PlayCurrentAsync(at, resetTrackState: false).ConfigureAwait(false);
            if (paused && _loaded) TogglePause();
        });
    }

    private bool IsOffline => _offline is { Network.IsOnline: false };

    private bool IsLocal(Track t) => _offline?.LocalPath(t.VideoId) is not null;

    /// <summary>A failure while streaming: is it just the internet gone? Then continue with downloads.</summary>
    private async Task<bool> WentOfflineAsync() =>
        _offline is not null && !await _offline.Network.CheckNowAsync().ConfigureAwait(false);

    private async Task OnEndedAsync()
    {
        RecordPlay();
        if (PauseAfterCurrent)
        {
            PauseAfterCurrent = false;
            Queue.Next(userInitiated: false); // play resumes with the next song (or this one, if it was the last)
            Stop();
            Notice?.Invoke("Sleep timer: paused");
            return;
        }
        await AdvanceAsync(userInitiated: false).ConfigureAwait(false);
    }

    /// <summary>The stream broke while playing (URL expired, connection dropped): first reload where it was.</summary>
    private async Task OnPlayerFailedAsync(Track track, string reason, int gen)
    {
        if (gen != _generation) return;
        var position = _player.Position;
        if (_fromStream && IsLocal(track))
        {
            // the stream broke but the song is downloaded: carry on from the file
            await PlayCurrentAsync(position, resetTrackState: false).ConfigureAwait(false);
            return;
        }
        if (await WentOfflineAsync().ConfigureAwait(false))
        {
            if (gen == _generation) await AdvanceAsync(userInitiated: true).ConfigureAwait(false);
            return;
        }
        if (!_reloadTried && position > TimeSpan.FromSeconds(1))
        {
            _reloadTried = true;
            Log.Warn($"Stream broke at {position} for {track.VideoId} ({reason}); reloading");
            _resolver.Invalidate(track.VideoId);
            await PlayCurrentAsync(position, resetTrackState: false).ConfigureAwait(false);
            return;
        }
        await OnTrackFailedAsync(track, reason, gen).ConfigureAwait(false);
    }

    private async Task OnTrackFailedAsync(Track track, string reason, int gen)
    {
        if (gen != _generation) return;
        Log.Warn($"Playback failed for {track.VideoId}: {reason}");
        if (await WentOfflineAsync().ConfigureAwait(false))
        {
            if (gen == _generation) await AdvanceAsync(userInitiated: true).ConfigureAwait(false);
            return;
        }

        // the link came from the fast path and a different path exists: retry this song at once
        if (!_altPathTried && _resolver.ReportBroken(track.VideoId))
        {
            _altPathTried = true;
            await PlayCurrentAsync(TimeSpan.Zero, resetTrackState: false).ConfigureAwait(false);
            return;
        }

        var now = _clock.Now;
        if (_lastToolUpdate is not { } last || now - last >= ToolUpdateEvery)
        {
            // most failures are YouTube changes that a newer yt-dlp already handles
            _lastToolUpdate = now;
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

    /// <summary>Appends YouTube radio for the queue's last song; concurrent callers share one request.</summary>
    private Task EnsureRadioAsync()
    {
        if (IsOffline) return Task.CompletedTask;
        var seed = Queue.RadioSeedId;
        if (seed is null) return Task.CompletedTask;
        var key = (Volatile.Read(ref _queueEpoch), seed);
        lock (_pendingGate)
        {
            if (_radioKey == key && _radioTask is not null) return _radioTask;
            _radioKey = key;
            return _radioTask = FetchRadioAsync(key);
        }
    }

    private async Task FetchRadioAsync((int Epoch, string Seed) key)
    {
        try
        {
            var tracks = await _api.GetRadioAsync(key.Seed).ConfigureAwait(false);
            if (key.Epoch != Volatile.Read(ref _queueEpoch)) return; // the user started something else meanwhile
            Queue.AppendRadio(tracks);
            StateChanged?.Invoke();
            if (_loaded) PrepareUpcoming();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_pendingGate)
                if (_radioKey == key) _radioKey = null; // allow a later retry
            Log.Warn($"Radio for {key.Seed} unavailable", e);
        }
    }

    private async Task LoadSegmentsAsync(Track track, int gen)
    {
        var segments = await _sponsorBlock.GetSegmentsAsync(track.VideoId).ConfigureAwait(false);
        if (gen == _generation) _segments = segments;
    }

    private void OnPosition()
    {
        if (!_loaded) return; // ticks from a song that is being replaced
        var pos = _player.Position;
        if (pos >= HealthyAfter) _failures = 0;
        if (!_recorded && pos >= RecordAfter) RecordPlay();

        if (!_lookaheadDone && pos >= LookaheadAfter)
        {
            _lookaheadDone = true;
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
        if (_recorded || !_loaded || Current is not { } t) return;
        _recorded = true;
        _lib.RecordPlay(t, _clock.Now);
    }

    private void Stop()
    {
        Interlocked.Increment(ref _generation);
        _loaded = false;
        _player.Stop();
        IsLoading = false;
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
        catch (OperationCanceledException) { }
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
