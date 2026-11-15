using System.Text.Json.Nodes;
using Tune.Core.Models;

namespace Tune.Core.YtMusic;

/// <summary>Parses the <c>next</c> endpoint (watch-next / radio queue).</summary>
public static class RadioParser
{
    public static IReadOnlyList<Track> Parse(JsonNode root)
    {
        var panel = root.Find("playlistPanelRenderer");
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
}
