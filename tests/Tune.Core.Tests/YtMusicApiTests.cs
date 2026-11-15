using System.Text.Json.Nodes;
using Tune.Core.YtMusic;

namespace Tune.Core.Tests;

public class YtMusicApiTests
{
    private static (YtMusicApi Api, CapturingHandler Handler) Make(Func<JsonNode, string> respond)
    {
        var handler = new CapturingHandler(req =>
            respond(JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!));
        return (new YtMusicApi(new InnerTubeClient(new HttpClient(handler))), handler);
    }

    private static JsonNode Body(CapturingHandler h, int i) => JsonNode.Parse(h.Requests[i].Body)!;

    [Fact]
    public async Task GetPlaylist_adds_VL_prefix_once()
    {
        var (api, h) = Make(_ => Fixture.Text("playlist.json"));
        await api.GetPlaylistAsync("PLx");
        await api.GetPlaylistAsync("VLPLy");
        Assert.Equal("VLPLx", (string?)Body(h, 0)["browseId"]);
        Assert.Equal("VLPLy", (string?)Body(h, 1)["browseId"]);
    }

    [Fact]
    public async Task GetRadio_posts_RDAMVM_playlist()
    {
        var (api, h) = Make(_ => Fixture.Text("next_radio.json"));
        var tracks = await api.GetRadioAsync("oyr9_tu_F2U");
        var body = Body(h, 0);
        Assert.EndsWith("/next?prettyPrint=false", h.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal("oyr9_tu_F2U", (string?)body["videoId"]);
        Assert.Equal("RDAMVMoyr9_tu_F2U", (string?)body["playlistId"]);
        Assert.True(tracks.Count >= 10);
    }

    [Fact]
    public async Task GetHome_follows_two_continuations_with_visitor_id()
    {
        var (api, h) = Make(body => body["continuation"] is null ? Fixture.Text("home.json") : Fixture.Text("home_continuation.json"));
        var shelves = await api.GetHomeAsync();
        Assert.Equal(3, h.Requests.Count); // first page + 2 continuations
        Assert.Equal("FEmusic_home", (string?)Body(h, 0)["browseId"]);
        Assert.NotNull((string?)Body(h, 1)["continuation"]);
        Assert.True(h.Requests[1].Request.Headers.Contains("X-Goog-Visitor-Id"));
        Assert.True(shelves.Count >= 4);
    }

    [Fact]
    public async Task GetHome_survives_failing_continuation()
    {
        var calls = 0;
        var (api, _) = Make(_ => ++calls == 1 ? Fixture.Text("home.json") : "not json");
        var shelves = await api.GetHomeAsync();
        Assert.NotEmpty(shelves);
    }

    [Fact]
    public async Task GetAlbum_passes_browseId_into_tracks()
    {
        var (api, _) = Make(_ => Fixture.Text("album.json"));
        var album = await api.GetAlbumAsync("MPREb_abc");
        Assert.Equal("MPREb_abc", album.Tracks[0].Album!.BrowseId);
    }

    [Fact]
    public async Task Search_posts_query()
    {
        var (api, h) = Make(_ => Fixture.Text("search_arijit.json"));
        var r = await api.SearchAsync("Arijit Singh");
        Assert.Equal("Arijit Singh", (string?)Body(h, 0)["query"]);
        Assert.NotEmpty(r.Songs);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_home_has_shelves()
    {
        var api = new YtMusicApi(new InnerTubeClient(new HttpClient()));
        Assert.NotEmpty(await api.GetHomeAsync());
    }
}
