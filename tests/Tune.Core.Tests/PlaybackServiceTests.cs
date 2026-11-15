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

    public void Play() => IsPaused = false;
    public void Pause() => IsPaused = true;
    public void Stop() { IsPaused = true; }

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

    public Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default)
    {
        Resolved.Add(videoId);
        if (Failing.Contains(videoId) || FailuresBeforeSuccess-- > 0)
            return Task.FromException<Uri>(new StreamResolveException(videoId, "ERROR: Video unavailable"));
        return Task.FromResult(new Uri($"https://rr.googlevideo.com/{videoId}/{Resolved.Count}"));
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
    }

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
