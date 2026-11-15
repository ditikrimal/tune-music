using System.Collections.Concurrent;
using System.Web;
using Tune.Core.Playback;

namespace Tune.Core.Streams;

public interface IStreamResolver
{
    /// <summary>Audio URL for the song, waiting out YouTube's start lock if needed.</summary>
    Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default);

    /// <summary>Looks the song up ahead of time (no waiting for the lock) so a later click starts instantly.</summary>
    Task PrepareAsync(string videoId, CancellationToken ct = default);

    /// <summary>True when the song has a valid, unlocked URL — playing it starts immediately.</summary>
    bool IsReady(string videoId);

    /// <summary>True when a lookup is cached or already running (ready or soon to be).</summary>
    bool IsKnown(string videoId);

    void Invalidate(string videoId);

    /// <summary>
    /// The song's link didn't play. Forgets it and tells the backend; true when the next lookup will use a
    /// different path (so retrying right away makes sense).
    /// </summary>
    bool ReportBroken(string videoId);
}

public sealed class StreamResolveException(string videoId, string reason) : Exception(reason)
{
    public string VideoId { get; } = videoId;
}

public sealed record CachedStream(string VideoId, Uri Url, DateTimeOffset ValidUntil, DateTimeOffset AvailableAt, string Source = "exe");

/// <summary>Keeps prepared links across restarts (they stay valid for hours).</summary>
public interface IStreamCacheStore
{
    IReadOnlyList<CachedStream> LoadStreams(DateTimeOffset now);
    void SaveStream(CachedStream stream);
}

/// <summary>
/// Turns a video id into a direct audio URL, caching it until shortly before it expires.
/// YouTube keeps a fresh URL locked (HTTP 403) for the length of the pre-roll ad the client skipped;
/// the lookup reports that moment as <c>available_at</c>: <see cref="ResolveAsync"/> waits for it,
/// <see cref="PrepareAsync"/> doesn't — preparing early is what makes clicks instant.
/// </summary>
public sealed class StreamResolver : IStreamResolver
{
    private static readonly TimeSpan AvailabilityMargin = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan Safety = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    private readonly IStreamBackend _backend;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IStreamCacheStore? _store;
    private readonly ConcurrentDictionary<string, CachedStream> _cache = new();

    /// <summary>One lookup shared by everyone asking for the same song; cancelled when the last waiter gives up.</summary>
    private sealed class InFlight
    {
        public required CancellationTokenSource Cts { get; init; }
        public Task<CachedStream> Run { get; set; } = null!;
        public int Waiters;
    }

    private readonly Dictionary<string, InFlight> _inFlight = [];
    private readonly Lock _gate = new();

    public StreamResolver(IStreamBackend backend, IClock clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null, IStreamCacheStore? store = null)
    {
        _backend = backend;
        _clock = clock;
        _delay = delay ?? Task.Delay;
        _store = store;
        if (store is not null)
            foreach (var s in store.LoadStreams(clock.Now))
                _cache[s.VideoId] = s;
    }

    /// <summary>yt-dlp.exe per lookup (kept for tests and as the fallback path).</summary>
    public StreamResolver(ToolManager tools, IProcessRunner runner, IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
        : this(new YtDlpExeBackend(tools, runner), clock, delay)
    {
    }

    public bool IsReady(string videoId) =>
        _cache.TryGetValue(videoId, out var c) && _clock.Now < c.ValidUntil && _clock.Now >= c.AvailableAt;

    public bool IsKnown(string videoId)
    {
        if (_cache.TryGetValue(videoId, out var c) && _clock.Now < c.ValidUntil) return true;
        lock (_gate) return _inFlight.ContainsKey(videoId);
    }

    public async Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default)
    {
        var s = await GetAsync(videoId, ct).ConfigureAwait(false);
        var wait = s.AvailableAt - _clock.Now;
        if (wait > TimeSpan.Zero) await _delay(wait + AvailabilityMargin, ct).ConfigureAwait(false);
        return s.Url;
    }

    public Task PrepareAsync(string videoId, CancellationToken ct = default) => GetAsync(videoId, ct);

    private async Task<CachedStream> GetAsync(string videoId, CancellationToken ct)
    {
        if (_cache.TryGetValue(videoId, out var hit) && _clock.Now < hit.ValidUntil) return hit;

        InFlight entry;
        lock (_gate)
        {
            // a finished entry whose cleanup hasn't run yet is stale (its result may be invalidated/expired)
            if (!_inFlight.TryGetValue(videoId, out entry!) || entry.Run.IsCompleted)
            {
                entry = new InFlight { Cts = new CancellationTokenSource() };
                var created = entry;
                _inFlight[videoId] = entry;
                entry.Run = LookupAsync(videoId, entry.Cts.Token);
                _ = entry.Run.ContinueWith(_ => Forget(videoId, created), TaskScheduler.Default);
            }
            entry.Waiters++;
        }

        try
        {
            return await entry.Run.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Leave(videoId, entry);
            throw;
        }
    }

    private async Task<CachedStream> LookupAsync(string videoId, CancellationToken ct)
    {
        var raw = await _backend.ExtractAsync(videoId, ct).ConfigureAwait(false);
        var validUntil = long.TryParse(HttpUtility.ParseQueryString(raw.Url.Query)["expire"], out var expire)
            ? DateTimeOffset.FromUnixTimeSeconds(expire) - Safety
            : _clock.Now + DefaultLifetime;
        var availableAt = raw.AvailableAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(raw.AvailableAtUnix) : DateTimeOffset.MinValue;
        var s = new CachedStream(videoId, raw.Url, validUntil, availableAt, raw.Source);
        _cache[videoId] = s;
        _store?.SaveStream(s);
        return s;
    }

    private void Leave(string videoId, InFlight entry)
    {
        lock (_gate)
        {
            if (--entry.Waiters > 0 || entry.Run.IsCompleted) return;
            Forget(videoId, entry);
        }
        entry.Cts.Cancel(); // nobody wants this song any more: stop the lookup (kills yt-dlp.exe)
    }

    private void Forget(string videoId, InFlight entry)
    {
        lock (_gate)
            if (_inFlight.TryGetValue(videoId, out var current) && current == entry) _inFlight.Remove(videoId);
    }

    public void Invalidate(string videoId) => _cache.TryRemove(videoId, out _);

    public bool ReportBroken(string videoId)
    {
        var source = _cache.TryRemove(videoId, out var c) ? c.Source : "exe";
        return _backend is IBrokenLinkSink sink && sink.LinkBroken(source);
    }
}
