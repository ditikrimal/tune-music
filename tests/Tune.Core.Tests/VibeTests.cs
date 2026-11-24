using Tune.Core.Feed;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.YtMusic;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

public class VibeParserTests
{
    [Fact]
    public void Radio_page_has_chips_continuation_and_related()
    {
        var page = RadioParser.ParsePage(Fixture.Load("next_radio_chips.json"));
        Assert.True(page.Tracks.Count >= 40, $"tracks: {page.Tracks.Count}");
        Assert.Equal("kJQP7kiw5Fk", page.Tracks[0].VideoId);
        Assert.NotNull(page.Continuation);
        Assert.StartsWith("MPTRt", page.RelatedId);
        Assert.DoesNotContain(page.Chips, c => c.Name == "All");
        Assert.Contains(page.Chips, c => c is { Name: "Popular", Kind: VibeKind.Discovery });
        Assert.Contains(page.Chips, c => c is { Name: "Romance", Kind: VibeKind.Mood });
        Assert.Contains(page.Chips, c => c is { Name: "2010s", Kind: VibeKind.Era });
        Assert.Contains(page.Chips, c => c is { Name: "Reggaeton", Kind: VibeKind.Genre });
        Assert.All(page.Chips, c => Assert.StartsWith("RDAT", c.PlaylistId));
    }

    [Fact]
    public void Radio_continuation_has_more_songs_and_another_continuation()
    {
        var page = RadioParser.ParseContinuation(Fixture.Load("next_radio_continuation.json"));
        Assert.True(page.Tracks.Count >= 40, $"tracks: {page.Tracks.Count}");
        Assert.NotNull(page.Continuation);
    }

    [Fact]
    public void Related_has_songs_artists_and_playlists()
    {
        var r = BrowseParser.ParseRelated(Fixture.Load("related.json"));
        Assert.True(r.Songs.Count >= 10, $"songs: {r.Songs.Count}");
        Assert.True(r.Artists.Count >= 5, $"artists: {r.Artists.Count}");
        Assert.True(r.Playlists.Count >= 5, $"playlists: {r.Playlists.Count}");
        Assert.All(r.Songs, t => Assert.Equal(11, t.VideoId.Length));
    }

    [Theory]
    [InlineData("Downbeat", VibeKind.Mood)]
    [InlineData("Pump-up", VibeKind.Mood)]
    [InlineData("Deep cuts", VibeKind.Discovery)]
    [InlineData("2000s", VibeKind.Era)]
    [InlineData("Hardcore hip hop", VibeKind.Genre)]
    public void Chip_kinds(string name, VibeKind kind) => Assert.Equal(kind, RadioParser.KindOf(name));
}

public class SessionAndRankTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);
    private static string Id(string x) => T(x).VideoId;

    [Fact]
    public void Session_is_the_run_without_a_long_break_newest_first()
    {
        var plays = new[] { (T("a"), Now.AddMinutes(-5)), (T("b"), Now.AddMinutes(-9)), (T("old"), Now.AddHours(-2)) };
        var skips = new[] { (Id("s"), Now.AddMinutes(-7)) };
        var s = Session.Build(plays, skips, id => T(id.TrimEnd('_')), Now, current: T("now"));
        Assert.Equal([Id("now"), Id("a"), Id("s"), Id("b")], s.Songs.Select(x => x.Track.VideoId));
        Assert.True(s.Songs[0].Weight > s.Songs[1].Weight && s.Songs[1].Weight > s.Songs[3].Weight);
        Assert.True(s.Songs[2].Skipped);
    }

    [Fact]
    public void Session_without_current_song_may_end_a_while_ago_only_when_asked()
    {
        var plays = new[] { (T("a"), Now.AddHours(-3)), (T("b"), Now.AddHours(-3.2)) };
        Assert.True(Session.Build(plays, [], _ => null, Now).IsEmpty);
        Assert.Equal(2, Session.Build(plays, [], _ => null, Now, maxAge: TimeSpan.FromHours(12)).Played.Count());
    }

    [Fact]
    public void Mood_is_the_chip_most_songs_you_let_play_share()
    {
        var s = new Session([new(T("a"), 1, Now), new(T("b"), 0.65, Now), new(T("c"), 0.4, Now)]);
        var chips = new Dictionary<string, IReadOnlyList<VibeChip>>
        {
            [Id("a")] = [FakeYtMusic.Chip("Downbeat"), FakeYtMusic.Chip("Romance"), FakeYtMusic.Chip("Bengali")],
            [Id("b")] = [FakeYtMusic.Chip("Downbeat"), FakeYtMusic.Chip("Chill")],
            [Id("c")] = [FakeYtMusic.Chip("Party")],
        };
        Assert.Equal("Downbeat", s.Mood(chips.GetValueOrDefault));
        Assert.Null(s.Mood(chips.GetValueOrDefault, VibeKind.Genre)); // only one song is Bengali
    }

    [Fact]
    public void Songs_several_radios_agree_on_beat_one_radio_top_spot_and_skipped_neighbours_sink()
    {
        var ra = new[] { T("a") }.Concat(Enumerable.Range(0, 12).Select(i => T("p" + i, "P" + i))).Append(T("both", "B")).ToList();
        var rb = new[] { T("b") }.Concat(Enumerable.Range(0, 12).Select(i => T("q" + i, "Q" + i))).Append(T("both", "B")).ToList();
        var rs = new[] { T("p0", "P0") }.ToList(); // the skipped song's radio starts with p0
        var ranked = UpNext.Rank([new(ra, 1), new(rb, 1), new(rs, -1)], new HashSet<string> { Id("a"), Id("b") }, 30);
        Assert.Equal(Id("both"), ranked[0].VideoId);
        Assert.DoesNotContain(ranked, t => t.VideoId == Id("p0"));
    }

    [Fact]
    public void Same_artist_never_twice_within_three()
    {
        var ranked = new[] { T("x1", "X"), T("x2", "X"), T("x3", "X"), T("y1", "Y"), T("z1", "Z"), T("w1", "W") };
        var spaced = UpNext.Spaced(ranked, 6);
        Assert.Equal(["x1", "y1", "z1", "w1", "x2"], spaced.Take(5).Select(t => t.VideoId.TrimEnd('_')));
    }

    [Fact]
    public void Re_steering_keeps_the_list_the_next_song_and_songs_you_queued()
    {
        var q = new PlayQueue();
        q.StartList([T("l1"), T("l2")], 1);
        q.AppendRadio([T("r1"), T("r2"), T("r3")]);
        q.PlayNext(T("mine")); // right after l2
        q.Next(true); // mine
        q.ReplaceAuto([T("n1"), T("r2"), T("n2")], keep: 1);
        Assert.Equal(["l1", "l2", "mine", "r1", "n1", "r2", "n2"], q.Items.Select(t => t.VideoId.TrimEnd('_')));
        Assert.Equal(3, q.AutoUpcoming(keep: 1));
    }
}

public sealed class VibePlaybackTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);
    private readonly LibraryStore _lib = new(":memory:");
    private readonly FakeYtMusic _api = new();
    private readonly FakeClock _clock = new(Now);
    private readonly FakePlayer _player = new();
    private readonly PlaybackService _svc;

    public VibePlaybackTests()
    {
        var vibe = new VibeEngine(_lib, new Signals(_lib, _api, _clock), _clock);
        _svc = new PlaybackService(new PlayQueue(), new FakeResolver(), _player, _lib, new FakeSponsorBlock(), _api,
            _ => Task.FromResult(true), _clock, null, vibe);
    }

    public void Dispose() => _lib.Dispose();

    private static string Id(string x) => T(x).VideoId;
    private List<string> Upcoming() => _svc.Queue.Items.Skip(_svc.Queue.CurrentIndex + 1).Select(t => t.VideoId.TrimEnd('_')).ToList();

    [Fact]
    public async Task Radio_follows_the_song_you_let_play()
    {
        _api.Radios[Id("a")] = [T("a"), T("b", "B"), .. Enumerable.Range(0, 8).Select(i => T("a" + i, "A" + i))];
        _api.Radios[Id("b")] = [T("b", "B"), .. Enumerable.Range(0, 8).Select(i => T("c" + i, "C" + i))];
        await _svc.PlayRadioAsync(T("a"));
        await _svc.Background;
        Assert.Equal("b", Upcoming()[0]);
        Assert.DoesNotContain(Upcoming(), id => id.StartsWith('c'));

        await _svc.NextAsync(); // b
        _clock.Now = Now.AddMinutes(1);
        _player.At(31);         // b counts as a play: the radio re-steers towards it
        await _svc.Background;
        Assert.Contains(Upcoming().Skip(1), id => id.StartsWith('c'));
    }

    [Fact]
    public async Task Skipping_steers_away_from_that_song_and_its_artist()
    {
        _api.Radios[Id("a")] = [T("a"), .. Enumerable.Range(1, 4).Select(i => T("x" + i, "X")), .. Enumerable.Range(1, 8).Select(i => T("y" + i, "Y" + i))];
        _api.Radios[Id("x1")] = [T("x1", "X"), T("x2", "X"), T("x3", "X"), T("x4", "X")];
        await _svc.PlayRadioAsync(T("a"));
        _player.At(31); // a counts
        await _svc.Background;
        Assert.Equal("x1", Upcoming()[0]);

        await _svc.NextAsync();  // x1
        _clock.Now = Now.AddMinutes(4);
        _player.At(5);
        await _svc.NextAsync();  // skipped x1 after 5 s
        await _svc.Background;
        var after = Upcoming().Skip(1).ToList(); // the next song was kept
        Assert.DoesNotContain(after.Take(4), id => id.StartsWith('x'));
    }

    [Fact]
    public async Task Picking_a_chip_retunes_what_comes_up()
    {
        _api.Radios[Id("a")] = [T("a"), .. Enumerable.Range(0, 8).Select(i => T("a" + i, "A" + i))];
        _api.Chips[Id("a")] = [FakeYtMusic.Chip("Chill", "a")];
        _api.Tuned[Id("a") + "|Chill"] = [T("a"), .. Enumerable.Range(0, 8).Select(i => T("chill" + i, "C" + i))];
        await _svc.PlayRadioAsync(T("a"));
        await _svc.Background;
        Assert.Contains(_svc.CurrentChips, c => c.Name == "Chill");

        _svc.Tune("Chill");
        await _svc.Background;
        Assert.Equal("Chill", _svc.Tuning);
        Assert.StartsWith("chill", Upcoming()[0]);
    }

    [Fact]
    public async Task A_chip_retunes_a_playlist_too_but_keeps_songs_you_queued()
    {
        _api.Radios[Id("a")] = [T("a"), .. Enumerable.Range(0, 8).Select(i => T("a" + i, "A" + i))];
        _api.Chips[Id("a")] = [FakeYtMusic.Chip("Chill", "a")];
        _api.Tuned[Id("a") + "|Chill"] = [T("a"), .. Enumerable.Range(0, 8).Select(i => T("chill" + i, "C" + i))];
        await _svc.PlayListAsync([T("a"), T("l1"), T("l2")], 0);
        _svc.Queue.PlayNext(T("mine"));
        await _svc.Background;

        _svc.Tune("Chill");
        await _svc.Background;
        Assert.Equal("mine", Upcoming()[0]);
        Assert.DoesNotContain("l1", Upcoming());
        Assert.StartsWith("chill", Upcoming()[1]);
    }
}
