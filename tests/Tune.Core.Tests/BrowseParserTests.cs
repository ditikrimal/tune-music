using System.Text.Json.Nodes;
using Tune.Core.Models;
using Tune.Core.YtMusic;

namespace Tune.Core.Tests;

public class BrowseParserTests
{
    [Fact]
    public void Home_first_page_has_titled_shelves_with_cards()
    {
        var page = BrowseParser.ParseHome(Fixture.Load("home.json"));
        Assert.True(page.Shelves.Count >= 2);
        Assert.All(page.Shelves, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.NotEmpty(s.Items);
        });
        Assert.NotNull(page.Continuation);
    }

    [Fact]
    public void Home_continuation_page_parses_more_shelves()
    {
        var page = BrowseParser.ParseHome(Fixture.Load("home_continuation.json"));
        Assert.NotEmpty(page.Shelves);
        Assert.All(page.Shelves, s => Assert.NotEmpty(s.Items));
    }

    [Fact]
    public void Home_playlist_cards_have_playlist_ids()
    {
        var page = BrowseParser.ParseHome(Fixture.Load("home.json"));
        var pl = page.Shelves.SelectMany(s => s.Items).First(c => c.Kind == CardKind.Playlist);
        Assert.Equal("VL" + pl.PlaylistId, pl.BrowseId);
        Assert.NotNull(pl.ThumbnailUrl);
    }

    [Fact]
    public void Home_unknown_shelf_renderer_is_skipped()
    {
        var text = Fixture.Text("home.json");
        var idx = text.IndexOf("\"musicCarouselShelfRenderer\"", StringComparison.Ordinal);
        var mutated = text[..idx] + "\"weirdNewShelfRenderer\"" + text[(idx + "\"musicCarouselShelfRenderer\"".Length)..];
        var original = BrowseParser.ParseHome(JsonNode.Parse(text)!);
        var page = BrowseParser.ParseHome(JsonNode.Parse(mutated)!);
        Assert.Equal(original.Shelves.Count - 1, page.Shelves.Count);
    }

    [Fact]
    public void Album_tracks_inherit_album_artist_thumbnail_and_have_durations()
    {
        var album = BrowseParser.ParseAlbum(Fixture.Load("album.json"), "MPREb_test");
        Assert.StartsWith("Tu Hain Toh Main Hoon", album.Title);
        Assert.Equal("Single • 2025", album.Subtitle);
        var t = Assert.Single(album.Tracks);
        Assert.Equal("CKJA9blyMUg", t.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(248), t.Duration);
        Assert.Equal(["Tanishk Bagchi", "Arijit Singh", "Afsana Khan"], t.Artists.Select(a => a.Name));
        Assert.Equal(album.ThumbnailUrl, t.ThumbnailUrl);
        Assert.Equal(new AlbumRef(album.Title, "MPREb_test"), t.Album);
    }

    [Fact]
    public void Playlist_has_tracks()
    {
        var pl = BrowseParser.ParsePlaylist(Fixture.Load("playlist.json"));
        Assert.Equal("Mellow Pop Classics", pl.Collection.Title);
        Assert.Equal(49, pl.Collection.Tracks.Count);
        var first = pl.Collection.Tracks[0];
        Assert.Equal("Thinking out Loud", first.Title);
        Assert.Equal("Ed Sheeran", first.ArtistLine);
        Assert.Equal(TimeSpan.FromSeconds(290), first.Duration);
        Assert.Null(pl.Continuation);
    }

    [Fact]
    public void Playlist_continuation_items_parse()
    {
        // shape of appended pages for playlists over 100 songs
        var cont = JsonNode.Parse("""
        {"onResponseReceivedActions":[{"appendContinuationItemsAction":{"continuationItems":[
          {"musicResponsiveListItemRenderer":{"flexColumns":[
             {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song X"}]}}},
             {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Artist Y"}]}}}],
           "playlistItemData":{"videoId":"abcdefghijk"},
           "fixedColumns":[{"musicResponsiveListItemFixedColumnRenderer":{"text":{"runs":[{"text":"3:05"}]}}}]}},
          {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"NEXT"}}}}]}}]}
        """)!;
        var page = BrowseParser.ParsePlaylistContinuation(cont);
        var t = Assert.Single(page.Tracks);
        Assert.Equal("Artist Y", t.ArtistLine);
        Assert.Equal(TimeSpan.FromSeconds(185), t.Duration);
        Assert.Equal("NEXT", page.Continuation);
    }

    [Fact]
    public void Artist_has_name_top_songs_and_shelves()
    {
        var a = BrowseParser.ParseArtist(Fixture.Load("artist.json"));
        Assert.Equal("Arijit Singh", a.Name);
        Assert.NotNull(a.ThumbnailUrl);
        Assert.Equal(5, a.TopSongs.Count);
        Assert.All(a.TopSongs, t => Assert.NotEmpty(t.Artists));
        Assert.Contains(a.Shelves, s => s.Title == "Albums" && s.Items.All(c => c.Kind == CardKind.Album));
        Assert.Contains(a.Shelves, s => s.Title == "Fans might also like" && s.Items.All(c => c.Kind == CardKind.Artist));
    }
}
