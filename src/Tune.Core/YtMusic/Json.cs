using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tune.Core.YtMusic;

/// <summary>Null-safe navigation over InnerTube JSON. Every miss returns null instead of throwing.</summary>
public static class Json
{
    public static JsonNode? At(this JsonNode? node, params object[] path)
    {
        var cur = node;
        foreach (var step in path)
        {
            cur = (cur, step) switch
            {
                (JsonObject o, string key) => o.TryGetPropertyValue(key, out var v) ? v : null,
                (JsonArray a, int i) => i >= 0 && i < a.Count ? a[i] : null,
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    public static string? Str(this JsonNode? node, params object[] path) =>
        node.At(path) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static IEnumerable<JsonNode> Items(this JsonNode? node, params object[] path) =>
        node.At(path) is JsonArray a ? a.Where(x => x is not null)!.Cast<JsonNode>() : [];

    public static string? RunsText(this JsonNode? textNode)
    {
        if (textNode.Str("simpleText") is { } simple) return simple;
        if (textNode.At("runs") is not JsonArray runs) return null;
        var text = string.Concat(runs.Select(r => r.Str("text")));
        return text.Length == 0 ? null : text;
    }

    public static string? BestThumbnail(this JsonNode? node)
    {
        if (node.At("thumbnails") is not JsonArray arr) return null;
        return arr
            .Where(t => t.Str("url") is not null)
            .OrderByDescending(t => t.At("width") is JsonValue w && w.TryGetValue<int>(out var i) ? i : 0)
            .Select(t => t.Str("url"))
            .FirstOrDefault();
    }

    /// <summary>Finds the first descendant object stored under <paramref name="key"/> (depth-first).</summary>
    public static JsonNode? Find(this JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.TryGetPropertyValue(key, out var hit) && hit is not null) return hit;
                foreach (var (_, child) in o)
                    if (Find(child, key) is { } found) return found;
                break;
            case JsonArray a:
                foreach (var child in a)
                    if (Find(child, key) is { } found) return found;
                break;
        }
        return null;
    }
}
