using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.SponsorBlock;
using Tune.Core.Streams;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

public sealed class FakePlayer : IAudioPlayer
{
    public List<(Uri Url, TimeSpan StartAt)> Loads { get; } = [];
    public List<TimeSpan> Seeks { get; } = [];
    public bool IsPaused { get; private set; } = true;
    public double Volume { get; set; } = 1;
    public TimeSpan Position { get; set; }
    public TimeSpan? Duration { get; set; } = TimeSpan.FromMinutes(4);
    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action? PositionChanged;

    public Task LoadAsync(Uri url, TimeSpan startAt)
    {
        Loads.Add((url, startAt));
        Position = startAt;
        return Task.CompletedTask;
    }

    public int Stops { get; private set; }
    public void Play() => IsPaused = false;
    public void Pause() => IsPaused = true;
    // like mpv: stopping unloads the file but leaves the pause flag as it was
    public void Stop() => Stops++;

    public void Seek(TimeSpan pos)
    {
        Seeks.Add(pos);
        Position = pos;
    }

    public void At(double seconds)
    {
        Position = TimeSpan.FromSeconds(seconds);
        PositionChanged?.Invoke();
    }

    public void End() => Ended?.Invoke();
    public void Fail(string why) => Failed?.Invoke(why);
}

public sealed class FakeResolver : IStreamResolver
{
    public List<string> Resolved { get; } = [];
    public List<string> Invalidated { get; } = [];
    public HashSet<string> Failing { get; } = [];
    public int FailuresBeforeSuccess { get; set; }

    /// <summary>Resolves that only finish once the test completes their gate (like a real 5 s yt-dlp run).</summary>
    public Dictionary<string, TaskCompletionSource> Gates { get; } = [];
    public Dictionary<string, CancellationToken> Tokens { get; } = [];

    public async Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default)
    {
        Resolved.Add(videoId);
        Tokens[videoId] = ct;
        var n = Resolved.Count;
        if (Gates.TryGetValue(videoId, out var gate)) await gate.Task.WaitAsync(ct);
        if (Failing.Contains(videoId) || FailuresBeforeSuccess-- > 0)
            throw new StreamResolveException(videoId, "ERROR: Video unavailable");
        return new Uri($"https://rr.googlevideo.com/{videoId}/{n}");
    }

    public void Invalidate(string videoId) => Invalidated.Add(videoId);
}

public sealed class FakeSponsorBlock : ISponsorBlock
{
    public Dictionary<string, IReadOnlyList<SkipSegment>> Segments { get; } = [];

    public Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(string videoId, CancellationToken ct = default) =>
        Task.FromResult(Segments.GetValueOrDefault(videoId) ?? (IReadOnlyList<SkipSegment>)[]);
}

public class PlaybackServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 20, 0, 0, TimeSpan.Zero);
    private readonly LibraryStore _lib = new(":memory:");
    private readonly FakePlayer _player = new();
    private readonly FakeResolver _resolver = new();
    private readonly FakeSponsorBlock _sb = new();
    private readonly FakeYtMusic _api = new();
    private readonly FakeClock _clock = new(Now);
    private readonly List<string> _notices = [];
    private int _updates;
    private readonly PlaybackService _svc;

    public PlaybackServiceTests()
    {
        _svc = new PlaybackService(new PlayQueue(), _resolver, _player, _lib, _sb, _api,
            _ => { _updates++; return Task.FromResult(true); }, _clock);
        _svc.Notice += _notices.Add;
        _svc.StateChanged += () => _states.Add((_svc.Current?.VideoId, _svc.IsPlaying, _svc.IsLoading));
    }

    private readonly List<(string? Id, bool Playing, bool Loading)> _states = [];

    public void Dispose() => _lib.Dispose();

    private static Track[] List(params string[] ids) => ids.Select(i => T(i)).ToArray();
    private static string Id(string s) => T(s).VideoId;

    [Fact]
    public async Task Play_resolves_loads_and_plays()
    {
        await _svc.PlayListAsync(List("a", "b"), 0);
        Assert.Equal([Id("a")], _resolver.Resolved);
        Assert.Equal(TimeSpan.Zero, _player.Loads.Single().StartAt);
        Assert.False(_player.IsPaused);
        Assert.True(_svc.IsPlaying);
        Assert.Equal(Id("a"), _svc.Current!.VideoId);
    }

    [Fact]
    public async Task Previous_restarts_if_position_over_3s()
    {
        await _svc.PlayListAsync(List("a", "b"), 1);
        _player.At(10);
        await _svc.PreviousAsync();
        Assert.Equal([TimeSpan.Zero], _player.Seeks);
        Assert.Equal(Id("b"), _svc.Current!.VideoId);
        _player.At(1);
        await _svc.PreviousAsync();
        Assert.Equal(Id("a"), _svc.Current!.VideoId);
    }

    [Fact]
    public async Task Ended_advances_and_fetches_radio_when_needed()
    {
        _api.Radios[Id("b")] = List("b", "x", "y");
        await _svc.PlayListAsync(List("a", "b"), 0);
        _player.End();
        await _svc.Background;
        Assert.Equal(Id("b"), _svc.Current!.VideoId);
        _player.End();
        await _svc.Background;
        Assert.Equal(Id("x"), _svc.Current!.VideoId);
        Assert.Equal(1, _api.RadioCalls[Id("b")]);
    }

    [Fact]
    public async Task Radio_play_fetches_radio_right_away()
    {
        _api.Radios[Id("a")] = List("a", "x");
        await _svc.PlayRadioAsync(T("a"));
        await _svc.Background;
        Assert.Equal([Id("a"), Id("x")], _svc.Queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task End_of_everything_stops_without_error()
    {
        await _svc.PlayListAsync(List("a"), 0); // radio for a returns nothing
        _player.End();
        await _svc.Background;
        Assert.False(_svc.IsPlaying);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task Records_play_after_30s_or_end_once()
    {
        await _svc.PlayListAsync(List("a", "b"), 0);
        _player.At(29);
        Assert.Empty(_lib.GetRecent(10));
        _player.At(30);
        _player.At(31);
        _player.End();
        await _svc.Background;
        Assert.Equal(1, _lib.GetTopTracks(Now.AddDays(-1), 5).Single(s => s.Track.VideoId == Id("a")).Plays);

        _player.At(5);  // b: short song that ends early
        _player.End();
        await _svc.Background;
        Assert.Contains(_lib.GetRecent(10), t => t.VideoId == Id("b"));
    }

    [Fact]
    public async Task Lookahead_resolves_next_after_10s_only_once()
    {
        await _svc.PlayListAsync(List("a", "b"), 0);
        _player.At(9);
        Assert.Equal([Id("a")], _resolver.Resolved);
        _player.At(10);
        _player.At(11);
        await _svc.Background;
        Assert.Equal([Id("a"), Id("b")], _resolver.Resolved);
    }

    [Fact]
    public async Task Sponsor_segment_skipped_when_position_enters_it()
    {
        _sb.Segments[Id("a")] = [new SkipSegment(4, 20)];
        await _svc.PlayListAsync(List("a"), 0);
        await _svc.Background;
        _player.At(3);
        Assert.Empty(_player.Seeks);
        _player.At(5);
        Assert.Equal([TimeSpan.FromSeconds(20)], _player.Seeks);
    }

    [Fact]
    public async Task Failed_track_updates_tools_once_then_skips_with_notice()
    {
        _resolver.FailuresBeforeSuccess = 1; // first attempt fails, retry after update works
        await _svc.PlayListAsync(List("a", "b"), 0);
        await _svc.Background;
        Assert.Equal(1, _updates);
        Assert.Equal(Id("a"), _svc.Current!.VideoId);
        Assert.Contains(Id("a"), _resolver.Invalidated);
        Assert.Empty(_notices);

        _resolver.Failing.Add(Id("b"));
        _player.End();
        await _svc.Background;
        Assert.Equal(1, _updates); // only once per session
        Assert.Equal("Couldn't play Title b — skipped", Assert.Single(_notices));
    }

    [Fact]
    public async Task Player_failure_mid_song_counts_as_failure()
    {
        await _svc.PlayListAsync(List("a", "b"), 0);
        _player.Fail("http 403");
        await _svc.Background;
        Assert.Equal(1, _updates);
        Assert.Equal(2, _player.Loads.Count); // retried a
    }

    [Fact]
    public async Task Three_consecutive_failures_stop_with_notice()
    {
        foreach (var id in new[] { "a", "b", "c", "d" }) _resolver.Failing.Add(Id(id));
        await _svc.PlayListAsync(List("a", "b", "c", "d"), 0);
        await _svc.Background;
        Assert.False(_svc.IsPlaying);
        Assert.DoesNotContain(Id("d"), _resolver.Resolved);
        Assert.StartsWith("Couldn't play 3 songs in a row", _notices[^1]);
    }

    [Fact]
    public async Task Resume_after_url_expiry_reresolves_and_seeks_to_saved_position()
    {
        await _svc.PlayListAsync(List("a"), 0);
        _player.At(95);
        _svc.TogglePause();
        Assert.True(_player.IsPaused);
        _clock.Now = Now.AddHours(7);
        _svc.TogglePause();
        await _svc.Background;
        Assert.Contains(Id("a"), _resolver.Invalidated);
        Assert.Equal(TimeSpan.FromSeconds(95), _player.Loads[^1].StartAt);
        Assert.False(_player.IsPaused);
    }

    [Fact]
    public async Task Short_pause_resumes_without_reload()
    {
        await _svc.PlayListAsync(List("a"), 0);
        _svc.TogglePause();
        _clock.Now = Now.AddMinutes(5);
        _svc.TogglePause();
        await _svc.Background;
        Assert.Single(_player.Loads);
        Assert.False(_player.IsPaused);
    }

    // ---------- final-review fix pass ----------

    [Fact]
    public async Task Replaying_same_seed_fetches_radio_again()
    {
        _api.Radios[Id("a")] = List("a", "x");
        await _svc.PlayRadioAsync(T("a"));
        await _svc.Background;
        await _svc.PlayRadioAsync(T("a"));
        await _svc.Background;
        Assert.Equal([Id("a"), Id("x")], _svc.Queue.Items.Select(t => t.VideoId));
        _player.End();
        await _svc.Background;
        Assert.Equal(Id("x"), _svc.Current!.VideoId);
    }

    [Fact]
    public async Task Late_radio_for_a_replaced_queue_is_dropped()
    {
        _api.RadioGates[Id("a")] = new TaskCompletionSource();
        _api.Radios[Id("a")] = List("a", "x");
        _api.Radios[Id("b")] = List("b", "y");
        await _svc.PlayRadioAsync(T("a"));
        await _svc.PlayRadioAsync(T("b"));
        _api.RadioGates[Id("a")].SetResult();
        await _svc.Background;
        Assert.Equal([Id("b"), Id("y")], _svc.Queue.Items.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Next_while_radio_is_in_flight_waits_for_it()
    {
        _api.RadioGates[Id("a")] = new TaskCompletionSource();
        _api.Radios[Id("a")] = List("a", "x");
        await _svc.PlayRadioAsync(T("a"));
        var next = _svc.NextAsync();
        _api.RadioGates[Id("a")].SetResult();
        await next;
        await _svc.Background;
        Assert.Equal(Id("x"), _svc.Current!.VideoId);
        Assert.True(_svc.IsPlaying);
    }

    [Fact]
    public async Task Switching_stops_the_old_song_at_once_and_shows_loading()
    {
        await _svc.PlayListAsync(List("a"), 0);
        _resolver.Gates[Id("b")] = new TaskCompletionSource();
        var stopsBefore = _player.Stops;
        var switching = _svc.PlayListAsync(List("b"), 0);
        Assert.Equal(stopsBefore + 1, _player.Stops);        // old audio cut immediately
        Assert.True(_svc.IsLoading);
        Assert.Contains((Id("b"), false, true), _states);     // UI shows the new song as loading
        _resolver.Gates[Id("b")].SetResult();
        await switching;
        Assert.False(_svc.IsLoading);
        Assert.True(_svc.IsPlaying);
    }

    [Fact]
    public async Task Old_song_events_while_loading_are_ignored()
    {
        await _svc.PlayListAsync(List("a", "z"), 0);
        _resolver.Gates[Id("b")] = new TaskCompletionSource();
        var switching = _svc.PlayListAsync(List("b", "c"), 0);
        _player.At(45);      // late position tick from the old song
        _player.End();       // the old song's end arriving late
        await _svc.Background;
        Assert.Equal(Id("b"), _svc.Current!.VideoId);
        Assert.Empty(_lib.GetRecent(10));                     // b not recorded as played
        _resolver.Gates[Id("b")].SetResult();
        await switching;
        Assert.Equal(Id("b"), _svc.Current!.VideoId);
    }

    [Fact]
    public async Task Superseded_resolve_is_cancelled()
    {
        _resolver.Gates[Id("a")] = new TaskCompletionSource();
        var first = _svc.PlayListAsync(List("a"), 0);
        await _svc.PlayListAsync(List("b"), 0);
        Assert.True(_resolver.Tokens[Id("a")].IsCancellationRequested);
        await first;
        await _svc.Background;
        Assert.Equal(Id("b"), _svc.Current!.VideoId);
        Assert.True(_svc.IsPlaying);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task Play_after_end_of_queue_restarts_current_song()
    {
        await _svc.PlayListAsync(List("a"), 0);
        _player.End();
        await _svc.Background;
        Assert.False(_svc.IsPlaying);
        _svc.TogglePause();
        await _svc.Background;
        Assert.True(_svc.IsPlaying);
        Assert.Equal(2, _player.Loads.Count);
        Assert.False(_player.IsPaused);
    }

    [Fact]
    public async Task Play_next_into_empty_queue_then_play_starts_it()
    {
        _svc.Queue.PlayNext(T("a"));
        _svc.TogglePause();
        await _svc.Background;
        Assert.True(_svc.IsPlaying);
        Assert.Single(_player.Loads);
    }

    [Fact]
    public async Task Failure_mid_song_reloads_at_position_without_counting()
    {
        await _svc.PlayListAsync(List("a", "b"), 0);
        _player.At(100);
        _player.Fail("http 403");                             // e.g. the URL expired while the laptop slept
        await _svc.Background;
        Assert.Equal(Id("a"), _svc.Current!.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(100), _player.Loads[^1].StartAt);
        Assert.Contains(Id("a"), _resolver.Invalidated);
        Assert.Equal(0, _updates);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task Resume_with_no_network_stays_paused_at_position()
    {
        await _svc.PlayListAsync(List("a", "b"), 0);
        _player.At(95);
        _svc.TogglePause();
        _clock.Now = Now.AddHours(7);
        _resolver.Failing.Add(Id("a"));
        _svc.TogglePause();
        await _svc.Background;
        Assert.Equal(Id("a"), _svc.Current!.VideoId);        // not skipped
        Assert.False(_svc.IsPlaying);
        Assert.Single(_notices);
        _resolver.Failing.Remove(Id("a"));
        _svc.TogglePause();                                   // network back: try again
        await _svc.Background;
        Assert.True(_svc.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(95), _player.Loads[^1].StartAt);
    }

    [Fact]
    public async Task Tools_update_on_failure_again_after_6_hours()
    {
        _resolver.Failing.Add(Id("a"));
        await _svc.PlayListAsync(List("a"), 0);
        await _svc.Background;
        Assert.Equal(1, _updates);
        _clock.Now = Now.AddHours(5);
        await _svc.PlayListAsync(List("a"), 0);
        await _svc.Background;
        Assert.Equal(1, _updates);
        _clock.Now = Now.AddHours(7);
        await _svc.PlayListAsync(List("a"), 0);
        await _svc.Background;
        Assert.Equal(2, _updates);
    }

    [Fact]
    public async Task Offline_resolve_failure_gives_notice_not_exception()
    {
        _resolver.Failing.Add(Id("a"));
        await _svc.PlayListAsync(List("a"), 0); // must not throw
        await _svc.Background;
        Assert.NotEmpty(_notices);
        Assert.False(_svc.IsPlaying);
    }
}
