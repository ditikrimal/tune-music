using System.Text.Json.Nodes;
using Tune.Core.Models;

namespace Tune.Core.YtMusic;

public static class SearchParser
{
    public static SearchResults Parse(JsonNode root)
    {
        Card? top = null;
        var cards = new List<Card>();

        var sections = root.At("contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content",
            "sectionListRenderer", "contents");

        foreach (var section in sections.Items())
        {
            if (section.At("musicCardShelfRenderer") is { } cardShelf)
            {
                top ??= TopResult(cardShelf);
                IReadOnlyList<ArtistRef>? fallback = top is { Kind: CardKind.Artist } ? [new ArtistRef(top.Title, top.BrowseId)] : null;
                cards.AddRange(ItemParser.SafeMap(ListItems(cardShelf), r => ItemParser.ResponsiveItem(r, fallback)));
                continue;
            }

            var shelf = section.At("itemSectionRenderer") ?? section.At("musicShelfRenderer");
            cards.AddRange(ItemParser.SafeMap(ListItems(shelf), r => ItemParser.ResponsiveItem(r)));
        }

        return new SearchResults(
            top,
            cards.Where(c => c.Track is not null).Select(c => c.Track!).DistinctBy(t => t.VideoId).ToList(),
            cards.Where(c => c.Kind == CardKind.Album).ToList(),
            cards.Where(c => c.Kind == CardKind.Artist).ToList(),
            cards.Where(c => c.Kind == CardKind.Playlist).ToList());
    }

    private static IEnumerable<JsonNode> ListItems(JsonNode? shelf) =>
        shelf.Items("contents")
            .Select(c => c.At("musicResponsiveListItemRenderer"))
            .Where(r => r is not null)!;

    private static Card? TopResult(JsonNode cardShelf)
    {
        var titleRun = cardShelf.At("title", "runs", 0);
        var title = titleRun.Str("text");
        if (title is null) return null;
        var endpoint = titleRun.At("navigationEndpoint") ?? cardShelf.At("onTap");
        var thumb = cardShelf.At("thumbnail", "musicThumbnailRenderer", "thumbnail").BestThumbnail();
        var subtitle = cardShelf.At("subtitle").RunsText();

        if (endpoint.Str("watchEndpoint", "videoId") is { } videoId)
        {
            var sub = cardShelf.At("subtitle");
            var track = new Track(videoId, title, ItemParser.Artists(sub, true), ItemParser.Album(sub),
                ItemParser.Segments(sub, true).Select(ItemParser.ParseDuration).FirstOrDefault(d => d is not null), thumb);
            return new Card(CardKind.Song, title, track.ArtistLine, thumb, videoId, null, null, track);
        }

        var browseId = endpoint.Str("browseEndpoint", "browseId");
        var kind = ItemParser.PageType(endpoint) switch
        {
            "MUSIC_PAGE_TYPE_ARTIST" => CardKind.Artist,
            "MUSIC_PAGE_TYPE_ALBUM" => CardKind.Album,
            "MUSIC_PAGE_TYPE_PLAYLIST" => CardKind.Playlist,
            _ => (CardKind?)null,
        };
        if (kind is null || browseId is null) return null;
        var playlistId = kind == CardKind.Playlist && browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId[2..] : null;
        return new Card(kind.Value, title, subtitle, thumb, null, browseId, playlistId, null);
    }
}
