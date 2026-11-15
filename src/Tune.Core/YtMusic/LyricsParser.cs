using System.Text.Json.Nodes;
using Tune.Core.Lyrics;

namespace Tune.Core.YtMusic;

/// <summary>YouTube Music lyrics: the watch-next response links a lyrics page (MPLYt…), which holds plain text.</summary>
public static class LyricsParser
{
    public static string? LyricsBrowseId(JsonNode root) => FindLyricsId(root);

    private static string? FindLyricsId(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["browseEndpoint"]?["browseId"] is JsonValue v && v.TryGetValue<string>(out var id) && id.StartsWith("MPLYt", StringComparison.Ordinal))
                    return id;
                foreach (var (_, child) in o)
                    if (FindLyricsId(child) is { } found) return found;
                break;
            case JsonArray a:
                foreach (var child in a)
                    if (FindLyricsId(child) is { } found) return found;
                break;
        }
        return null;
    }

    public static SongLyrics? Parse(JsonNode root)
    {
        var shelf = root.Find("musicDescriptionShelfRenderer");
        var text = shelf.At("description").RunsText();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new SongLyrics(text.Trim(), shelf.At("footer").RunsText(), null);
    }
}
