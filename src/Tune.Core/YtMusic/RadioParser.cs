using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tune.Core.Models;

namespace Tune.Core.YtMusic;

/// <summary>Parses the <c>next</c> endpoint (watch-next / radio queue).</summary>
public static partial class RadioParser
{
    public static IReadOnlyList<Track> Parse(JsonNode root) => Tracks(root.Find("playlistPanelRenderer"));

    /// <summary>The radio with its tuner chips, continuation and Related tab.</summary>
    public static RadioPage ParsePage(JsonNode root)
    {
        var panel = root.Find("playlistPanelRenderer");
        var related = FindAll(root, "tabRenderer")
            .Select(t => t.Str("endpoint", "browseEndpoint", "browseId"))
            .FirstOrDefault(id => id?.StartsWith("MPTRt", StringComparison.Ordinal) == true);
        return new RadioPage(Tracks(panel), Chips(root), Continuation(panel), related);
    }

    /// <summary>More songs of an endless radio.</summary>
    public static RadioPage ParseContinuation(JsonNode root)
    {
        var panel = root.At("continuationContents", "playlistPanelContinuation");
        return new RadioPage(Tracks(panel), [], Continuation(panel));
    }

    /// <summary>The radio tuner chips ("Popular", "Chill", "2010s", "Hip hop"…), without "All".</summary>
    public static IReadOnlyList<VibeChip> Chips(JsonNode root)
    {
        var list = new List<VibeChip>();
        foreach (var chip in FindAll(root, "chipCloudChipRenderer"))
        {
            var we = chip.At("navigationEndpoint", "queueUpdateCommand", "fetchContentsCommand", "watchEndpoint");
            var name = chip.At("text").RunsText();
            var playlist = we.Str("playlistId");
            if (we is null || string.IsNullOrWhiteSpace(name) || playlist is null || name == "All") continue;
            if (list.Any(c => c.Name == name)) continue;
            list.Add(new VibeChip(name, playlist, we.Str("params"), KindOf(name)));
        }
        return list;
    }

    private static readonly HashSet<string> DiscoveryChips = new(StringComparer.OrdinalIgnoreCase) { "Popular", "Discover", "Deep cuts", "Familiar" };

    // YouTube Music's moods & moments, as its chips and Moods page name them
    private static readonly HashSet<string> MoodChips = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chill", "Downbeat", "Upbeat", "Romance", "Party", "Pump-up", "Workout", "Focus", "Sleep", "Feel good", "Energize",
        "Energy boosters", "Relax", "Commute", "Sad", "Heartbreak", "Happy", "Gaming", "Dance", "Mellow", "Calm", "Motivation",
        "Hype", "Love", "Late night", "Summer", "Travel", "Study", "Rainy day", "Moody", "Dark", "Emotional", "Melancholy",
        "Feel-good", "Driving", "Good vibes", "Aggressive", "Angry", "Peaceful", "Sentimental", "Nostalgic", "Romantic",
    };

    public static VibeKind KindOf(string name) =>
        DiscoveryChips.Contains(name) ? VibeKind.Discovery
        : MoodChips.Contains(name) ? VibeKind.Mood
        : EraRegex().IsMatch(name) || name.Equals("New releases", StringComparison.OrdinalIgnoreCase) ? VibeKind.Era
        : VibeKind.Genre;

    [GeneratedRegex(@"^\d{2,4}s$")]
    private static partial Regex EraRegex();

    private static string? Continuation(JsonNode? panel) =>
        panel.Str("continuations", 0, "nextRadioContinuationData", "continuation")
        ?? panel.Str("continuations", 0, "nextContinuationData", "continuation");

    private static IReadOnlyList<Track> Tracks(JsonNode? panel)
    {
        var videos = panel.Items("contents")
            .Select(c => c.At("playlistPanelVideoRenderer")
                         ?? c.At("playlistPanelVideoWrapperRenderer", "primaryRenderer", "playlistPanelVideoRenderer"))
            .Where(v => v is not null)!;

        return ItemParser.SafeMap<Track>(videos!, v =>
        {
            var videoId = v.Str("videoId");
            var title = v.At("title").RunsText();
            if (videoId is null || string.IsNullOrWhiteSpace(title)) return null;
            var byline = v.At("longBylineText");
            return new Track(
                videoId,
                title,
                ItemParser.Artists(byline, skipLeadingTypeLabel: false),
                ItemParser.Album(byline),
                ItemParser.ParseDuration(v.At("lengthText").RunsText()),
                v.At("thumbnail").BestThumbnail());
        }).DistinctBy(t => t.VideoId).ToList();
    }

    internal static IEnumerable<JsonNode> FindAll(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, child) in o)
                {
                    if (k == key && child is not null) yield return child;
                    foreach (var hit in FindAll(child, key)) yield return hit;
                }
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var hit in FindAll(child, key)) yield return hit;
                break;
        }
    }
}
