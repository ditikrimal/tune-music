using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tune.Core.Models;

namespace Tune.Core.Sync;

/// <summary>
/// How songs travel in the account (Windows and Android write the same JSON, so a playlist made on one opens on
/// the other): {"v":id,"t":title,"a":[{"n":name,"b":browseId}],"al":{"t":title,"b":browseId},"d":seconds,"th":url}.
/// </summary>
public static class SyncFormat
{
    public static JsonObject ToJson(Track t)
    {
        var o = new JsonObject
        {
            ["v"] = t.VideoId,
            ["t"] = t.Title,
            ["a"] = new JsonArray(t.Artists.Select(a => (JsonNode)new JsonObject { ["n"] = a.Name, ["b"] = a.BrowseId }).ToArray()),
        };
        if (t.Album is { } al) o["al"] = new JsonObject { ["t"] = al.Title, ["b"] = al.BrowseId };
        if (t.Duration is { } d) o["d"] = (int)Math.Round(d.TotalSeconds);
        if (t.ThumbnailUrl is { } th) o["th"] = th;
        return o;
    }

    public static Track? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o || Str(o["v"]) is not { Length: > 0 } id) return null;
        var artists = (o["a"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(a => Str(a["n"]) is not null)
            .Select(a => new ArtistRef(Str(a["n"])!, Str(a["b"])))
            .ToList();
        var album = o["al"] is JsonObject al && Str(al["t"]) is { } alt ? new AlbumRef(alt, Str(al["b"])) : null;
        TimeSpan? duration = o["d"] is JsonValue dv && dv.TryGetValue<int>(out var sec) ? TimeSpan.FromSeconds(sec) : null;
        return new Track(id, Str(o["t"]) ?? "", artists, album, duration, Str(o["th"]));
    }

    public static string TracksJson(IEnumerable<Track> tracks) =>
        new JsonArray(tracks.Select(t => (JsonNode)ToJson(t)).ToArray()).ToJsonString();

    public static IReadOnlyList<Track> ParseTracks(string? json) =>
        Parse(json) is JsonArray a ? a.Select(FromJson).OfType<Track>().ToList() : [];

    /// <summary>A day of plays: [[videoId, atMs], …] plus the songs' details.</summary>
    public static string PlaysJson(IEnumerable<(Track Track, long At)> plays) =>
        new JsonArray(plays.Select(p => (JsonNode)new JsonArray(p.Track.VideoId, p.At)).ToArray()).ToJsonString();

    public static IReadOnlyList<(string VideoId, long At)> ParsePlays(string? json) =>
        Parse(json) is JsonArray a
            ? a.OfType<JsonArray>().Where(x => x.Count == 2 && Str(x[0]) is not null && x[1] is JsonValue)
                .Select(x => (Str(x[0])!, x[1]!.GetValue<long>())).ToList()
            : [];

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string Ms(long v) => v.ToString(CultureInfo.InvariantCulture);
}
