using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Tune.Core.YtMusic;

/// <summary>Anonymous client for YouTube Music's internal "InnerTube" API (no cookies, no login).</summary>
public sealed class InnerTubeClient(HttpClient http)
{
    private const string BaseUrl = "https://music.youtube.com/youtubei/v1/";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    // ytmusicapi convention: a WEB_REMIX version dated about a week ago is always accepted.
    private static readonly string ClientVersion =
        "1." + DateTime.UtcNow.AddDays(-7).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".01.00";

    public async Task<JsonNode> PostAsync(string endpoint, JsonObject body, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["context"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["clientName"] = "WEB_REMIX",
                    ["clientVersion"] = ClientVersion,
                    ["hl"] = "en",
                    ["gl"] = "NP",
                },
                ["user"] = new JsonObject(),
            },
        };
        foreach (var (key, value) in body)
            payload[key] = value?.DeepClone();

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpoint + "?prettyPrint=false")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        req.Headers.Referrer = new Uri("https://music.youtube.com/");
        req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(text) ?? new JsonObject();
    }
}
