using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tune.Core.SponsorBlock;

namespace Tune.Core.Tests;

public class SponsorBlockTests
{
    private const string Vid = "oyr9_tu_F2U";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return respond(request, ct);
        }
    }

    private static Task<HttpResponseMessage> Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });

    private static string Prefix => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Vid)))[..4];

    private static readonly string TwoVideos = $$"""
        [{"videoID":"{{Vid}}","segments":[{"category":"music_offtopic","segment":[0.0,12.5]},{"category":"music_offtopic","segment":[200.25,248.0]}]},
         {"videoID":"zzzzzzzzzzz","segments":[{"category":"music_offtopic","segment":[1,2]}]}]
        """;

    [Fact]
    public async Task Uses_hash_prefix_not_raw_id()
    {
        var h = new Handler((_, _) => Json(TwoVideos));
        await new SponsorBlockClient(new HttpClient(h), () => true).GetSegmentsAsync(Vid);
        var uri = h.Requests.Single();
        Assert.DoesNotContain(Vid, uri.ToString());
        Assert.Equal("sponsor.ajay.app", uri.Host);
        Assert.Equal("/api/skipSegments/" + Prefix, uri.AbsolutePath);
        Assert.Contains("music_offtopic", Uri.UnescapeDataString(uri.Query));
    }

    [Fact]
    public async Task Filters_to_requested_video()
    {
        var h = new Handler((_, _) => Json(TwoVideos));
        var segs = await new SponsorBlockClient(new HttpClient(h), () => true).GetSegmentsAsync(Vid);
        Assert.Equal([new SkipSegment(0, 12.5), new SkipSegment(200.25, 248)], segs);
    }

    [Theory]
    [InlineData("404")]
    [InlineData("bad json")]
    [InlineData("timeout")]
    public async Task Returns_empty_on_404_timeout_or_bad_json(string mode)
    {
        var h = new Handler(async (req, ct) => mode switch
        {
            "404" => await Json("Not Found", HttpStatusCode.NotFound),
            "bad json" => await Json("{oops"),
            _ => await Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.OK), ct),
        });
        var client = new SponsorBlockClient(new HttpClient(h), () => true, timeout: TimeSpan.FromMilliseconds(200));
        Assert.Empty(await client.GetSegmentsAsync(Vid));
    }

    [Fact]
    public async Task Disabled_makes_no_request()
    {
        var h = new Handler((_, _) => Json(TwoVideos));
        Assert.Empty(await new SponsorBlockClient(new HttpClient(h), () => false).GetSegmentsAsync(Vid));
        Assert.Empty(h.Requests);
    }
}
