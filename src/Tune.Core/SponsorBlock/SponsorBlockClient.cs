using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tune.Core.Util;
using Tune.Core.YtMusic;

namespace Tune.Core.SponsorBlock;

public sealed record SkipSegment(double Start, double End);

public interface ISponsorBlock
{
    Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(string videoId, CancellationToken ct = default);
}

/// <summary>
/// Looks up non-music segments (category <c>music_offtopic</c>) on SponsorBlock.
/// Uses the hash-prefix endpoint, so the server only sees 4 hex chars shared by many videos — never the video id.
/// Any failure means "no segments".
/// </summary>
public sealed class SponsorBlockClient(HttpClient http, Func<bool> enabled, TimeSpan? timeout = null) : ISponsorBlock
{
    private const string Categories = """["music_offtopic"]""";
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(string videoId, CancellationToken ct = default)
    {
        if (!enabled()) return [];
        var prefix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(videoId)))[..4];
        var url = $"https://sponsor.ajay.app/api/skipSegments/{prefix}?categories={Uri.EscapeDataString(Categories)}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var resp = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            return (root as JsonArray ?? [])
                .Where(v => v.Str("videoID") == videoId)
                .SelectMany(v => v.Items("segments"))
                .Select(s => s.At("segment"))
                .OfType<JsonArray>()
                .Where(a => a.Count == 2)
                .Select(a => new SkipSegment(a[0]!.GetValue<double>(), a[1]!.GetValue<double>()))
                .Where(s => s.End > s.Start)
                .OrderBy(s => s.Start)
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or FormatException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Info($"SponsorBlock lookup skipped: {e.GetType().Name}");
            return [];
        }
    }
}
