using Tune.Core.Models;
using Tune.Core.YtMusic;

namespace Tune.Core.Tests;

public class SearchParserTests
{
    private static readonly SearchResults Arijit = SearchParser.Parse(Fixture.Load("search_arijit.json"));

    [Fact]
    public void Search_returns_songs_with_ids_titles_artists()
    {
        Assert.True(Arijit.Songs.Count >= 3, $"songs: {Arijit.Songs.Count}");
        Assert.All(Arijit.Songs, s =>
        {
            Assert.Equal(11, s.VideoId.Length);
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.NotEmpty(s.Artists);
        });
        Assert.Contains(Arijit.Songs, s => s.Duration is not null);
    }

    [Fact]
    public void Search_top_result_songs_inherit_top_artist()
    {
        // top-result card songs only carry "Song • 4:08" in their subtitle
        var first = Arijit.Songs[0];
        Assert.Equal("oyr9_tu_F2U", first.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(248), first.Duration);
        Assert.Equal("Arijit Singh", first.ArtistLine);
    }

    [Fact]
    public void Search_song_with_artist_runs_lists_all_artists()
    {
        var kasturi = Arijit.Songs.Single(s => s.VideoId == "OBhN_FwzFEg");
        Assert.Equal(["Arijit Singh", "Prasad S", "Kunaal Vermaa"], kasturi.Artists.Select(a => a.Name));
        Assert.All(kasturi.Artists, a => Assert.StartsWith("UC", a.BrowseId));
    }

    [Fact]
    public void Search_returns_artist_card_with_browseId_starting_UC()
    {
        Assert.Contains(Arijit.Artists, a => a.Kind == CardKind.Artist && a.BrowseId!.StartsWith("UC"));
    }

    [Fact]
    public void Search_returns_albums_and_playlists()
    {
        Assert.Contains(Arijit.Albums, a => a.BrowseId!.StartsWith("MPRE") && a.Subtitle!.Contains("Arijit Singh"));
        var pl = Assert.Single(Arijit.Playlists, p => p.Title == "Spotlight: Arijit Singh");
        Assert.False(pl.PlaylistId!.StartsWith("VL"));
        Assert.Equal("VL" + pl.PlaylistId, pl.BrowseId);
    }

    [Fact]
    public void Search_top_result_present()
    {
        Assert.NotNull(Arijit.Top);
        Assert.Equal(CardKind.Artist, Arijit.Top!.Kind);
        Assert.Equal("Arijit Singh", Arijit.Top.Title);
    }

    [Fact]
    public void Search_skips_podcast_episodes_and_user_profiles()
    {
        Assert.DoesNotContain(Arijit.Songs, s => s.Title == "Yeh Fitoor Mera");
        Assert.DoesNotContain(Arijit.Artists, a => a.Title == "arijit singh shots");
    }

    [Fact]
    public void Search_handles_devanagari()
    {
        var r = SearchParser.Parse(Fixture.Load("search_sajjan.json"));
        Assert.NotEmpty(r.Songs);
    }

    [Fact]
    public void Unknown_renderer_is_skipped()
    {
        var r = SearchParser.Parse(Fixture.Load("search_arijit_mutated.json"));
        Assert.True(r.Songs.Count >= 2);
        Assert.DoesNotContain(r.Songs, s => s.VideoId == "oyr9_tu_F2U"); // its renderer was renamed
    }

    [Fact]
    public void Missing_thumbnail_still_parses()
    {
        var r = SearchParser.Parse(Fixture.Load("search_arijit_mutated.json"));
        Assert.Contains(r.Songs, s => s.ThumbnailUrl is null);
        Assert.Contains(r.Songs, s => s.ThumbnailUrl is not null);
    }

    [Fact]
    public void Empty_or_garbage_json_gives_empty_results()
    {
        var r = SearchParser.Parse(System.Text.Json.Nodes.JsonNode.Parse("""{"contents":{"x":1}}""")!);
        Assert.Empty(r.Songs);
        Assert.Null(r.Top);
    }
}
