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

    private static string Id(string x) => T(x).VideoId;
    private static List<string> Ids(Shelf s) => s.Items.Select(c => c.VideoId!).ToList();

    private void Play(string id, int times = 1, double daysAgo = 0, string artist = "Artist", string? artistId = null)
    {
        for (var i = 0; i < times; i++) _lib.RecordPlay(T(id, artist, artistId), Now.AddDays(-daysAgo).AddMinutes(-i));
    }

    /// <summary>Four favourites, each with a long radio of its own songs (artist = the seed's letter).</summary>
    private void FourFavouritesWithRadios()
    {
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            Play(id, 3, daysAgo: 3, artist: "Fav" + id);
            _api.Radios[Id(id)] = [T(id, "Fav" + id), .. Enumerable.Range(0, 25).Select(i => T(id + "r" + i, id + "-artist" + (i % 8)))];
        }
    }

    [Fact]
    public async Task Empty_history_returns_only_discovery()
    {
        var feed = await Builder().BuildAsync();
        Assert.Equal(_api.Home, feed.Shelves);
        Assert.False(feed.Offline);
    }

    [Fact]
    public async Task Default_layout_puts_speed_dial_first_then_quick_picks()
    {
        FourFavouritesWithRadios();
        var feed = await Builder().BuildAsync();
        Assert.Equal(["Speed dial", "Quick picks"], feed.Shelves.Take(2).Select(s => s.Title));
        Assert.True(feed.Shelves[0].IsSpeedDial);
        Assert.True(feed.Shelves[1].IsList);
        Assert.DoesNotContain(feed.Shelves, s => s.Title == "Recently played"); // hidden by default
        Assert.Equal("Trending", feed.Shelves[^1].Title);                        // YouTube Music's rows last
    }

    [Fact]
    public async Task Speed_dial_is_your_most_played_songs_with_at_most_three_per_artist()
    {
        foreach (var (id, n) in new[] { ("a", 9), ("b", 8), ("c", 7), ("d", 6), ("e", 5) }) Play(id, n, artist: "Same");
        Play("f", 4, artist: "Other");
        Play("once", 1, artist: "Other2"); // one play isn't "frequent"
        var dial = (await Builder().BuildAsync()).Shelves.Single(s => s.IsSpeedDial);
        Assert.Equal([Id("a"), Id("b"), Id("c"), Id("f")], Ids(dial));
    }

    [Fact]
    public async Task Pins_come_first_and_your_playlists_follow_renames_and_deletes()
    {
        Play("a", 5);
        var p = _lib.CreatePlaylist("Road trip");
        _lib.AddToPlaylist(p, T("x"));
        var gone = _lib.CreatePlaylist("Old");
        SpeedDialPins.Toggle(_lib, SpeedDialPins.LocalPlaylistCard(_lib.PlaylistUid(gone)!, "Old", null));
        SpeedDialPins.Toggle(_lib, SpeedDialPins.LocalPlaylistCard(_lib.PlaylistUid(p)!, "Road trip", null));
        SpeedDialPins.Toggle(_lib, new Card(CardKind.Album, "An album", "Artist", null, null, "MPRE1", null, null));
        _lib.RenamePlaylist(p, "Road trip 2026");
        _lib.DeletePlaylist(gone);

        var dial = (await Builder().BuildAsync()).Shelves.Single(s => s.IsSpeedDial).Items;
        Assert.Equal(["An album", "Road trip 2026", "Title a"], dial.Select(c => c.Title));
        Assert.True(SpeedDialPins.IsPinned(_lib, dial[0]));
        Assert.False(SpeedDialPins.Toggle(_lib, dial[0])); // unpin
        Assert.Equal(["Road trip 2026", "Title a"], (await Builder().BuildAsync()).Shelves.Single(s => s.IsSpeedDial).Items.Select(c => c.Title));
    }

    [Fact]
    public async Task Quick_picks_are_new_songs_mixed_with_favourites_and_varied()
    {
        FourFavouritesWithRadios();
        var feed = await Builder().BuildAsync();
        var picks = feed.Shelves.Single(s => s.Title == "Quick picks");
        Assert.Equal(20, picks.Items.Count);
        Assert.Equal(picks.Items.Count, picks.Items.Select(c => c.VideoId).Distinct().Count());
        Assert.All(picks.Items.GroupBy(c => c.Track!.Artists[0].Name), g => Assert.True(g.Count() <= 2, g.Key)); // ≤ 2 per artist
        Assert.True(picks.Items.Select(c => c.VideoId![..1]).Distinct().Count() >= 3);                        // from several radios
        var favourites = new[] { "a", "b", "c", "d" }.Select(Id).ToHashSet();
        Assert.Contains(picks.Items, c => favourites.Contains(c.VideoId!));                                     // familiar ones mixed in
        var because = feed.Shelves.Where(s => s.Title.StartsWith("Because")).SelectMany(s => s.Items).Select(c => c.VideoId).ToHashSet();
        Assert.DoesNotContain(picks.Items, c => because.Contains(c.VideoId));
    }

    [Fact]
    public async Task Quick_picks_rank_songs_several_radios_agree_on_and_leave_out_skips_and_just_heard()
    {
        FourFavouritesWithRadios();
        foreach (var id in new[] { "a", "b", "c", "d" })
            _api.Radios[Id(id)] = [T(id), T("shared", "Shared"), .. _api.Radios[Id(id)].Skip(1)];
        _api.Radios[Id("a")] = [T("a"), T("skipme", "S"), T("heard", "H"), .. _api.Radios[Id("a")].Skip(1)];
        _lib.RecordSkip(Id("skipme"), Now.AddDays(-1));
        _lib.RecordSkip(Id("skipme"), Now.AddDays(-2));
        Play("heard", 1, daysAgo: 0.1, artist: "H");

        var picks = (await Builder().BuildAsync()).Shelves.Single(s => s.Title == "Quick picks").Items.Select(c => c.VideoId).ToList();
        Assert.Equal(Id("shared"), picks[0]);
        Assert.DoesNotContain(Id("skipme"), picks);
        Assert.DoesNotContain(Id("heard"), picks);
    }

    [Fact]
    public async Task Picks_stay_the_same_all_day_and_change_on_refresh()
    {
        FourFavouritesWithRadios();
        var builder = new FeedBuilder(_lib, _api, _clock) { Random = new Random(7) };
        var first = Picks(await builder.BuildAsync());
        Assert.Equal(first, Picks(await builder.BuildAsync()));
        var refreshed = Picks(await builder.BuildAsync(fresh: true));
        Assert.Equal(2, _api.RadioCalls[Id("a")]); // the 6 h radio cache is skipped on refresh
        Assert.NotEqual(first, refreshed);

        static List<string?> Picks(FeedResult f) => f.Shelves.Single(s => s.Title == "Quick picks").Items.Select(c => c.VideoId).ToList();
    }

    [Fact]
    public async Task Forgotten_favourites_are_old_loves_not_played_lately()
    {
        foreach (var id in new[] { "o1", "o2", "o3" }) Play(id, 6, daysAgo: 40, artist: "Old" + id);
        Play("new", 6, daysAgo: 1);
        var row = (await Builder().BuildAsync()).Shelves.Single(s => s.Title == "Forgotten favourites");
        Assert.Equal(new[] { "o1", "o2", "o3" }.Select(Id).Order(), Ids(row).Order());
    }

    [Fact]
    public async Task Layout_hides_and_reorders_sections()
    {
        FourFavouritesWithRadios();
        HomeLayout.Save(_lib, HomeLayout.Parse("quickpicks,-speeddial,recent,-because,-forgotten,-artists,-discovery"));
        var feed = await Builder().BuildAsync();
        Assert.Equal(["Quick picks", "Recently played"], feed.Shelves.Select(s => s.Title));
    }

    [Fact]
    public void Layout_round_trips_and_adds_sections_it_did_not_know()
    {
        var parsed = HomeLayout.Parse("quickpicks,-speeddial,bogus");
        Assert.Equal(HomeSection.QuickPicks, parsed[0].Section);
        Assert.False(parsed.Single(s => s.Section == HomeSection.SpeedDial).Visible);
        Assert.Equal(HomeLayout.Default.Count, parsed.Count); // every section present
        Assert.Equal(parsed, HomeLayout.Parse(HomeLayout.Format(parsed)));
        Assert.Equal(HomeLayout.Default, HomeLayout.Parse(null));
    }

    [Fact]
    public async Task Top_artists_get_pictures_from_their_artist_pages_cached()
    {
        foreach (var (id, n) in new[] { ("a", 1), ("b", 2), ("c", 3) })
        {
            Play(id, 2, artist: "Artist" + n, artistId: "UC" + n);
            _api.ArtistPictures["UC" + n] = $"https://lh3.googleusercontent.com/artist{n}=w120-h120";
        }
        var builder = Builder();
        var shelf = (await builder.BuildAsync()).Shelves.Single(s => s.Title == "Your top artists");
        Assert.All(shelf.Items, c => Assert.StartsWith("https://lh3.googleusercontent.com/artist", c.ThumbnailUrl));
        await builder.BuildAsync();
        Assert.Equal(3, _api.ArtistCalls); // pictures cached, not refetched
    }

    [Fact]
    public async Task Radio_cached_within_6h_refetched_after()
    {
        Play("a", 2);
        _api.Radios[Id("a")] = [T("x")];
        var builder = Builder();
        await builder.BuildAsync();
        _clock.Now = Now.AddHours(5);
        await builder.BuildAsync();
        Assert.Equal(1, _api.RadioCalls[Id("a")]);
        _clock.Now = Now.AddHours(7);
        await builder.BuildAsync();
        Assert.Equal(2, _api.RadioCalls[Id("a")]);
    }

    [Fact]
    public async Task Discovery_failure_still_returns_your_shelves()
    {
        Play("a", 3);
        _api.HomeError = new HttpRequestException("offline");
        var feed = await Builder().BuildAsync();
        Assert.True(feed.Offline);
        Assert.Contains(feed.Shelves, s => s.IsSpeedDial);
    }

    [Fact]
    public async Task One_radio_failure_skips_only_that_row()
    {
        Play("a", 3); Play("b", 2);
        _api.FailingRadios.Add(Id("a"));
        _api.Radios[Id("b")] = [T("b"), T("z", "Z")];
        var feed = await Builder().BuildAsync();
        Assert.Equal(["Because you played Title b"], feed.Shelves.Where(s => s.Title.StartsWith("Because")).Select(s => s.Title));
    }

    [Fact]
    public void Taste_weighs_recent_plays_more_and_skips_against()
    {
        Play("old", 4, daysAgo: 90);
        Play("recent", 3, daysAgo: 1);
        Play("skipped", 3, daysAgo: 1);
        for (var i = 0; i < 4; i++) _lib.RecordSkip(Id("skipped"), Now.AddHours(-i));
        var taste = Builder().LoadTaste();
        Assert.Equal(Id("recent"), taste.Lately()[0].Track.VideoId);
        Assert.True(taste.Get(Id("skipped"))!.Lately < taste.Get(Id("recent"))!.Lately);
        Assert.True(taste.HeardWithin(Id("recent"), TimeSpan.FromDays(2)));
    }

    [Fact]
    public void Skips_are_recorded_and_read_back()
    {
        _lib.RecordSkip(Id("a"), Now);
        Assert.Equal([(Id("a"), Now)], _lib.GetSkips(Now.AddDays(-1)));
    }
}
