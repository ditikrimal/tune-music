using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tune.Core.Models;

namespace Tune.Core.Lyrics;

public sealed record LyricLine(TimeSpan At, string Text);

/// <summary>Lyrics for one song. <see cref="Lines"/> is set when they are time-synced.</summary>
public sealed record SongLyrics(string Text, string? Source, IReadOnlyList<LyricLine>? Lines);

/// <summary>LRC ("[mm:ss.xx] line") parsing and lookup.</summary>
public static partial class Lrc
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in (lrc ?? "").Split('\n'))
        {
            var stamps = new List<TimeSpan>();
            var rest = raw.TrimEnd('\r');
            Match m;
            while ((m = Stamp().Match(rest)).Success && m.Index == 0)
            {
                var ms = 0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    ms = int.Parse(f, CultureInfo.InvariantCulture) * (f.Length switch { 1 => 100, 2 => 10, _ => 1 });
                }
                stamps.Add(new TimeSpan(0, 0, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), ms));
                rest = rest[m.Length..];
            }
            foreach (var at in stamps) lines.Add(new LyricLine(at, rest.Trim()));
        }
        return lines.OrderBy(l => l.At).ToList();
    }

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].At <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}

/// <summary>Time-synced lyrics from lrclib.net (free, no account). Only title/artist/duration are sent.</summary>
public sealed partial class LrcLibClient(HttpClient http)
{
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|4k|hd|m/?v)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    public static string CleanTitle(string title) => Noise().Replace(title, "").Trim();

    public async Task<SongLyrics?> GetAsync(Track track, CancellationToken ct = default)
    {
        var artist = track.Artists.FirstOrDefault()?.Name ?? "";
        var title = CleanTitle(track.Title);
        var q = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (track.Duration is { } d) q += $"&duration={(int)d.TotalSeconds}";

        using (var r = await http.GetAsync($"https://lrclib.net/api/get?{q}", ct).ConfigureAwait(false))
        {
            if (r.IsSuccessStatusCode)
            {
                var hit = ParseResponse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (hit is not null) return hit;
            }
            else if (r.StatusCode != HttpStatusCode.NotFound) return null;
        }

        // durations on YouTube differ from the album cut: fall back to a search and take the best synced hit
        using var s = await http.GetAsync($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", ct).ConfigureAwait(false);
        if (!s.IsSuccessStatusCode) return null;
        if (JsonNode.Parse(await s.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is not JsonArray results) return null;
        var best = results
            .Where(n => n is not null)
            .OrderByDescending(n => !string.IsNullOrWhiteSpace((string?)n!["syncedLyrics"]))
            .ThenBy(n => track.Duration is { } td && n!["duration"] is JsonValue v && v.TryGetValue<double>(out var sec)
                ? Math.Abs(sec - td.TotalSeconds) : 999)
            .FirstOrDefault();
        return best is null ? null : ParseResponse(best.ToJsonString());
    }

    public static SongLyrics? ParseResponse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject o) return null;
        var synced = (string?)o["syncedLyrics"];
        var plain = (string?)o["plainLyrics"];
        var lines = string.IsNullOrWhiteSpace(synced) ? null : Lrc.Parse(synced);
        if (lines is { Count: 0 }) lines = null;
        var text = !string.IsNullOrWhiteSpace(plain) ? plain.Trim()
                 : lines is not null ? string.Join("\n", lines.Select(l => l.Text)) : null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new SongLyrics(text, lines is not null ? "Synced lyrics: LRCLIB" : "Lyrics: LRCLIB", lines);
    }
}

/// <summary>
/// Finds lyrics for a song: time-synced ones from LRCLIB when allowed (they can follow along),
/// otherwise YouTube Music's own. Results are remembered for the session.
/// </summary>
public sealed class LyricsService(Tune.Core.YtMusic.InnerTubeClient yt, LrcLibClient lrc, Func<bool> syncedAllowed)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<SongLyrics?>> _cache = new();

    /// <summary>Whether synced lyrics are currently allowed (a setting); part of the cache key so flipping it takes effect.</summary>
    public bool SyncedAllowed => syncedAllowed();

    public Task<SongLyrics?> GetAsync(Track track)
    {
        var allowed = syncedAllowed();
        return _cache.GetOrAdd(Key(track, allowed), _ => LoadAsync(track, allowed));
    }

    private static string Key(Track track, bool allowed) => track.VideoId + (allowed ? "|s" : "|p");

    private async Task<SongLyrics?> LoadAsync(Track track, bool allowed)
    {
        SongLyrics? synced = null;
        if (allowed)
        {
            try { synced = await lrc.GetAsync(track).ConfigureAwait(false); }
            catch (Exception) { /* offline or no match: fall back to YouTube Music */ }
            if (synced?.Lines is not null) return synced;
        }
        try
        {
            var next = await yt.PostAsync("next", new JsonObject { ["videoId"] = track.VideoId, ["isAudioOnly"] = true }).ConfigureAwait(false);
            if (Tune.Core.YtMusic.LyricsParser.LyricsBrowseId(next) is { } id)
            {
                var page = await yt.PostAsync("browse", new JsonObject { ["browseId"] = id }).ConfigureAwait(false);
                if (Tune.Core.YtMusic.LyricsParser.Parse(page) is { } plain) return plain;
            }
        }
        catch (Exception) { }
        if (synced is null) _cache.TryRemove(Key(track, allowed), out _); // let a later open retry
        return synced;
    }
}
