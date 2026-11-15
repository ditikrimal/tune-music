using System.Net;
using System.Text.Json.Nodes;
using Tune.Core.YtMusic;

namespace Tune.Core.Tests;

public sealed class CapturingHandler(Func<HttpRequestMessage, string>? respond = null) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond?.Invoke(request) ?? "{}") };
    }
}

public class InnerTubeClientTests
{
    private static (InnerTubeClient Client, CapturingHandler Handler) Make()
    {
        var h = new CapturingHandler();
        return (new InnerTubeClient(new HttpClient(h)), h);
    }

    [Fact]
    public async Task Post_targets_music_youtubei_endpoint()
    {
        var (c, h) = Make();
        await c.PostAsync("search", new JsonObject { ["query"] = "x" });
        var req = h.Requests.Single().Request;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://music.youtube.com/youtubei/v1/search?prettyPrint=false", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Post_includes_web_remix_context()
    {
        var (c, h) = Make();
        await c.PostAsync("search", new JsonObject { ["query"] = "hello" });
        var body = JsonNode.Parse(h.Requests.Single().Body)!;
        var client = body["context"]!["client"]!;
        Assert.Equal("WEB_REMIX", (string?)client["clientName"]);
        Assert.Equal("en", (string?)client["hl"]);
        Assert.Equal("NP", (string?)client["gl"]);
        Assert.False(string.IsNullOrEmpty((string?)client["clientVersion"]));
        Assert.Equal("hello", (string?)body["query"]);
    }

    [Fact]
    public async Task Post_sends_no_cookies()
    {
        var (c, h) = Make();
        await c.PostAsync("browse", new JsonObject());
        Assert.False(h.Requests.Single().Request.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task Visitor_id_from_response_is_sent_on_later_requests()
    {
        var h = new CapturingHandler(_ => """{"responseContext":{"visitorData":"VIS123"}}""");
        var c = new InnerTubeClient(new HttpClient(h));
        await c.PostAsync("browse", new JsonObject());
        await c.PostAsync("browse", new JsonObject());
        Assert.False(h.Requests[0].Request.Headers.Contains("X-Goog-Visitor-Id"));
        Assert.Equal("VIS123", h.Requests[1].Request.Headers.GetValues("X-Goog-Visitor-Id").Single());
    }

    [Fact]
    public void Json_At_returns_null_on_missing_path()
    {
        var n = JsonNode.Parse("""{"a":[{"b":1}]}""");
        Assert.Equal(1, (int)n.At("a", 0, "b")!);
        Assert.Null(n.At("a", 5, "b"));
        Assert.Null(n.At("x", "y"));
        Assert.Null(n.At("a", "b")); // wrong kind of step
    }

    [Fact]
    public void RunsText_joins_runs()
    {
        var n = JsonNode.Parse("""{"runs":[{"text":"A"},{"text":" • "},{"text":"B"}]}""");
        Assert.Equal("A • B", n.RunsText());
        Assert.Equal("S", JsonNode.Parse("""{"simpleText":"S"}""").RunsText());
        Assert.Null(((JsonNode?)null).RunsText());
    }

    [Fact]
    public void BestThumbnail_picks_largest()
    {
        var n = JsonNode.Parse("""{"thumbnails":[{"url":"s","width":60},{"url":"l","width":544},{"url":"m","width":120}]}""");
        Assert.Equal("l", n.BestThumbnail());
    }
}
