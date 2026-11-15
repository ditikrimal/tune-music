using System.Text.Json.Nodes;
using Tune.Core.Models;
using Tune.Core.Util;

namespace Tune.Core.YtMusic;

public sealed record HomePage(IReadOnlyList<Shelf> Shelves, string? Continuation);

public sealed record PlaylistPage(Collection Collection, string? Continuation);

public sealed record TrackPage(IReadOnlyList<Track> Tracks, string? Continuation);

public static class BrowseParser
{
    // ---------- home ----------

    public static HomePage ParseHome(JsonNode root)
    {
        var list = root.At("continuationContents", "sectionListContinuation")
                   ?? root.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer");
        var shelves = list.Items("contents")
            .Select(s => s.At("musicCarouselShelfRenderer"))
            .Where(s => s is not null)
            .Select(s => CarouselShelf(s!))
            .Where(s => s is { Items.Count: > 0 })
            .ToList();
        return new HomePage(shelves!, list.Str("continuations", 0, "nextContinuationData", "continuation"));
    }

    private static Shelf? CarouselShelf(JsonNode shelf)
    {
        var title = shelf.At("header", "musicCarouselShelfBasicHeaderRenderer", "title").RunsText();
        if (string.IsNullOrWhiteSpace(title)) return null;
        var items = ItemParser.SafeMap(shelf.Items("contents"), c =>
            c.At("musicTwoRowItemRenderer") is { } two ? ItemParser.TwoRowItem(two)
            : c.At("musicResponsiveListItemRenderer") is { } list ? ItemParser.ResponsiveItem(list)
            : null);
        return new Shelf(title, items);
    }

    // ---------- album / playlist ----------

    private static JsonNode? Header(JsonNode root) =>
        root.At("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "musicResponsiveHeaderRenderer")
        ?? root.Find("musicResponsiveHeaderRenderer");

    private static JsonNode? TrackShelf(JsonNode root)
    {
        var secondary = root.At("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents");
        return secondary.Items().Select(s => s.At("musicShelfRenderer") ?? s.At("musicPlaylistShelfRenderer")).FirstOrDefault(s => s is not null);
    }

    public static Collection ParseAlbum(JsonNode root, string? browseId = null)
    {
        var header = Header(root);
        var title = header.At("title").RunsText() ?? "Album";
        var thumb = header.At("thumbnail", "musicThumbnailRenderer", "thumbnail").BestThumbnail();
        var albumArtists = ItemParser.Artists(header.At("straplineTextOne"), skipLeadingTypeLabel: false);
        var albumRef = new AlbumRef(title, browseId);
        var tracks = ItemParser.SafeMap(ListItems(TrackShelf(root).Items("contents")),
                r => ItemParser.ResponsiveItem(r, albumArtists, albumRef, thumb))
            .Where(c => c.Track is not null)
            .Select(c => c.Track! with { Album = albumRef, ThumbnailUrl = thumb ?? c.Track.ThumbnailUrl })
            .ToList();
        return new Collection(title, header.At("subtitle").RunsText(), thumb, tracks);
    }

    public static PlaylistPage ParsePlaylist(JsonNode root)
    {
        var header = Header(root);
        var shelf = TrackShelf(root);
        var page = Tracks(shelf.Items("contents"));
        var subtitle = string.Join(" • ", new[] { header.At("subtitle").RunsText(), header.At("secondSubtitle").RunsText() }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var collection = new Collection(
            header.At("title").RunsText() ?? "Playlist",
            subtitle.Length == 0 ? null : subtitle,
            header.At("thumbnail", "musicThumbnailRenderer", "thumbnail").BestThumbnail(),
            page.Tracks);
        return new PlaylistPage(collection, page.Continuation ?? shelf.Str("continuations", 0, "nextContinuationData", "continuation"));
    }

    public static TrackPage ParsePlaylistContinuation(JsonNode root)
    {
        var items = root.Items("onResponseReceivedActions")
            .SelectMany(a => a.Items("appendContinuationItemsAction", "continuationItems"))
            .Concat(root.Items("continuationContents", "musicPlaylistShelfContinuation", "contents"));
        var page = Tracks(items);
        return page with { Continuation = page.Continuation ?? root.Str("continuationContents", "musicPlaylistShelfContinuation", "continuations", 0, "nextContinuationData", "continuation") };
    }

    private static TrackPage Tracks(IEnumerable<JsonNode> contents)
    {
        var list = contents.ToList();
        var tracks = ItemParser.SafeMap(ListItems(list), r => ItemParser.ResponsiveItem(r))
            .Where(c => c.Track is not null)
            .Select(c => c.Track!)
            .ToList();
        var token = list.Select(c => c.Str("continuationItemRenderer", "continuationEndpoint", "continuationCommand", "token"))
            .LastOrDefault(t => t is not null);
        return new TrackPage(tracks, token);
    }

    private static IEnumerable<JsonNode> ListItems(IEnumerable<JsonNode> contents) =>
        contents.Select(c => c.At("musicResponsiveListItemRenderer")).Where(r => r is not null)!;

    // ---------- artist ----------

    public static ArtistPage ParseArtist(JsonNode root, string? browseId = null)
    {
        var header = root.At("header", "musicImmersiveHeaderRenderer") ?? root.At("header", "musicVisualHeaderRenderer")
                     ?? root.Find("musicImmersiveHeaderRenderer");
        var name = header.At("title").RunsText() ?? "Artist";
        IReadOnlyList<ArtistRef> self = [new ArtistRef(name, browseId)];
        var sections = root.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");

        var topSongs = new List<Track>();
        var shelves = new List<Shelf>();
        foreach (var section in sections.Items())
        {
            try
            {
                if (section.At("musicShelfRenderer") is { } songs && topSongs.Count == 0)
                {
                    topSongs.AddRange(ItemParser.SafeMap(ListItems(songs.Items("contents")), r => ItemParser.ResponsiveItem(r, self))
                        .Where(c => c.Track is not null).Select(c => c.Track!));
                }
                else if (section.At("musicCarouselShelfRenderer") is { } carousel && CarouselShelf(carousel) is { Items.Count: > 0 } shelf)
                {
                    shelves.Add(shelf);
                }
            }
            catch (InvalidOperationException e)
            {
                Log.Warn("Skipped artist section", e);
            }
        }

        var thumb = header.At("thumbnail", "musicThumbnailRenderer", "thumbnail").BestThumbnail();
        return new ArtistPage(name, thumb, topSongs, shelves);
    }
}
