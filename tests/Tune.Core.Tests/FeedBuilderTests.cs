using Tune.Core.Feed;
using Tune.Core.Library;
using Tune.Core.Models;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

public class FeedBuilderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private readonly LibraryStore _lib = new(":memory:");
    private readonly FakeYtMusic _api = new();
    private readonly FakeClock _clock = new(Now);

    public void Dispose() => _lib.Dispose();

    private FeedBuilder Builder() => new(_lib, _api, _clock);

    private static List<string> Ids(Shelf s) => s.Items.Select(c => c.VideoId!).ToList();

    [Fact]
    public async Task Empty_history_returns_only_discovery()
    {
        var feed = await Builder().BuildAsync();
        Assert.Equal(_api.Home, feed.Shelves);
        Assert.False(feed.Offline);
    }

    [Fact]
    public async Task Recently_played_needs_three_songs()
    {
        _lib.RecordPlay(T("a"), Now.AddHours(-1));
        _lib.RecordPlay(T("b"), Now.AddHours(-1));
        Assert.DoesNotContain((await Builder().BuildAsync()).Shelves, s => s.Title == "Recently played");
        _lib.RecordPlay(T("c"), Now);
        var shelf = (await Builder().BuildAsync()).Shelves[0];
        Assert.Equal("Recently played", shelf.Title);
        Assert.True(shelf.IsLocal);
        Assert.Equal([T("c").VideoId, T("b").VideoId, T("a").VideoId], Ids(shelf));
    }

    [Fact]
    public async Task Because_you_played_excludes_seed_and_dupes()
    {
        _lib.RecordPlay(T("a"), Now); _lib.RecordPlay(T("a"), Now);
        _lib.RecordPlay(T("b"), Now);
        _api.Radios[T("a").VideoId] = [T("a"), T("x"), T("y")];
        _api.Radios[T("b").VideoId] = [T("b"), T("x"), T("z")];
        var feed = await Builder().BuildAsync();
        var rows = feed.Shelves.Where(s => s.Title.StartsWith("Because you played")).ToList();
        Assert.Equal("Because you played Title a", rows[0].Title);
        Assert.Equal([T("x").VideoId, T("y").VideoId], Ids(rows[0]));
        Assert.Equal([T("z").VideoId], Ids(rows[1]));
        Assert.All(rows, r => Assert.True(r.IsLocal));
    }

    [Fact]
    public async Task Because_rows_capped_at_twelve_items()
    {
        _lib.RecordPlay(T("a"), Now);
        _api.Radios[T("a").VideoId] = Enumerable.Range(0, 30).Select(i => T("r" + i)).ToList();
        var row = (await Builder().BuildAsync()).Shelves.Single(s => s.Title.StartsWith("Because"));
        Assert.Equal(12, row.Items.Count);
    }

    [Fact]
    public async Task Max_three_because_rows()
    {
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            _lib.RecordPlay(T(id), Now);
            _api.Radios[T(id).VideoId] = [T(id + "1")];
        }
        var feed = await Builder().BuildAsync();
        Assert.Equal(3, feed.Shelves.Count(s => s.Title.StartsWith("Because")));
    }

    [Fact]
    public async Task Seeds_fall_back_to_recent_when_nothing_in_last_14_days()
    {
        _lib.RecordPlay(T("old"), Now.AddDays(-40));
        _api.Radios[T("old").VideoId] = [T("q")];
        var feed = await Builder().BuildAsync();
        Assert.Contains(feed.Shelves, s => s.Title == "Because you played Title old");
    }

    [Fact]
    public async Task Top_artists_shelf_needs_three_artists_with_ids()
    {
        _lib.RecordPlay(T("a", "A1", "UC1"), Now);
        _lib.RecordPlay(T("b", "A2", "UC2"), Now);
        _lib.RecordPlay(T("c", "A3", null), Now);
        Assert.DoesNotContain((await Builder().BuildAsync()).Shelves, s => s.Title == "Your top artists");
        _lib.RecordPlay(T("d", "A4", "UC4"), Now);
        var shelf = (await Builder().BuildAsync()).Shelves.Single(s => s.Title == "Your top artists");
        Assert.All(shelf.Items, c => Assert.Equal(CardKind.Artist, c.Kind));
        Assert.Equal(3, shelf.Items.Count);
    }

    [Fact]
    public async Task Radio_cached_within_6h_refetched_after()
    {
        _lib.RecordPlay(T("a"), Now);
        _api.Radios[T("a").VideoId] = [T("x")];
        var builder = Builder();
        await builder.BuildAsync();
        _clock.Now = Now.AddHours(5);
        await builder.BuildAsync();
        Assert.Equal(1, _api.RadioCalls[T("a").VideoId]);
        _clock.Now = Now.AddHours(7);
        await builder.BuildAsync();
        Assert.Equal(2, _api.RadioCalls[T("a").VideoId]);
    }

    [Fact]
    public async Task Discovery_failure_still_returns_local_shelves()
    {
        foreach (var id in new[] { "a", "b", "c" }) _lib.RecordPlay(T(id), Now);
        _api.HomeError = new HttpRequestException("offline");
        var feed = await Builder().BuildAsync();
        Assert.True(feed.Offline);
        Assert.Contains(feed.Shelves, s => s.Title == "Recently played");
    }

    [Fact]
    public async Task One_radio_failure_skips_only_that_row()
    {
        _lib.RecordPlay(T("a"), Now); _lib.RecordPlay(T("a"), Now);
        _lib.RecordPlay(T("b"), Now);
        _api.FailingRadios.Add(T("a").VideoId);
        _api.Radios[T("b").VideoId] = [T("z")];
        var feed = await Builder().BuildAsync();
        Assert.Equal(["Because you played Title b"], feed.Shelves.Where(s => s.Title.StartsWith("Because")).Select(s => s.Title));
    }
}
