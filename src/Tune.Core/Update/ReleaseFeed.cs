using System.Text.Json;

namespace Tune.Core.Update;

/// <summary>A published Tune release and the file this app needs from it.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Title, string Notes, string PageUrl, string? AssetUrl, long AssetBytes);

/// <summary>
/// Reads Tune's newest GitHub release (public API, no account) and says whether it's newer than this app.
/// Friends' copies check this, so a new release reaches them without anyone sending a file.
/// </summary>
public static class ReleaseFeed
{
    public const string LatestUrl = "https://api.github.com/repos/ditikrimal/tune-music/releases/latest";

    /// <summary>Parses GitHub's "latest release" JSON; <paramref name="assetName"/> is e.g. Tune.exe or Tune.apk.</summary>
    public static ReleaseInfo? Parse(string json, string assetName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
            if (ParseVersion(tag) is not { } version) return null;
            string? url = null;
            long bytes = 0;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.GetProperty("browser_download_url").GetString();
                        bytes = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    }
            return new ReleaseInfo(version, tag,
                root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                url, bytes);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"v1.3.0" / "1.3" / "1.3.0-beta" -> 1.3.0 (a suffix is ignored).</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>True when <paramref name="release"/> is newer than <paramref name="current"/> (compares major.minor.patch).</summary>
    public static bool IsNewer(Version release, Version current) =>
        release > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>Asks GitHub; null when offline, rate-limited or there's no newer release.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(HttpClient http, Version current, string assetName, string? feedUrl = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl ?? LatestUrl);
        req.Headers.UserAgent.ParseAdd("Tune-Updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var release = Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), assetName);
        return release is not null && IsNewer(release.Version, current) ? release : null;
    }
}
