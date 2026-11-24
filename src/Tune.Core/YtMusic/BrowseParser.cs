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
            .Select(s => CarouselShelf(s!) is { } sh ? sh with { More = null } : null)
            .Where(s => s is { Items.Count: > 0 })
            .ToList();
        return new HomePage(shelves!, list.Str("continuations", 0, "nextContinuationData", "continuation"));
    }

    // ---------- a song's Related tab ----------

    /// <summary>"You might also like" (songs), "Similar artists" and "Recommended playlists" of a song.</summary>
    public static RelatedPage ParseRelated(JsonNode root)
    {
        var shelves = RadioParser.FindAll(root, "musicCarouselShelfRenderer").Select(CarouselShelf).OfType<Shelf>().ToList();
        var songs = shelves.SelectMany(s => s.Items).Select(c => c.Track).OfType<Track>().DistinctBy(t => t.VideoId).ToList();
        var artists = shelves.SelectMany(s => s.Items).Where(c => c.Kind == CardKind.Artist).ToList();
        var playlists = shelves.SelectMany(s => s.Items).Where(c => c.Kind == CardKind.Playlist).ToList();
        return new RelatedPage(songs, artists, playlists);
    }

    // ---------- moods & genres ----------

    public static IReadOnlyList<MoodSection> ParseMoods(JsonNode root) =>
        root.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer")
            .Items("contents")
            .Select(s => s.At("gridRenderer"))
            .Where(g => g is not null)
            .Select(g => new MoodSection(
                g.At("header", "gridHeaderRenderer", "title").RunsText() ?? "",
                g.Items("items").Select(i => i.At("musicNavigationButtonRenderer")).Where(b => b is not null).Select(b =>
                {
                    var title = b.At("buttonText").RunsText();
                    var p = b.Str("clickCommand", "browseEndpoint", "params");
                    var color = b.At("solid", "leftStripeColor") is JsonValue v && v.TryGetValue<long>(out var c) ? (uint)c : 0xFF808080u;
                    return title is null || p is null ? null : new MoodCategory(title, p, color);
                }).OfType<MoodCategory>().ToList()))
            .Where(s => s.Title.Length > 0 && s.Items.Count > 0)
            .ToList();

    private static Shelf? CarouselShelf(JsonNode shelf)
    {
        var title = shelf.At("header", "musicCarouselShelfBasicHeaderRenderer", "title").RunsText();
        if (string.IsNullOrWhiteSpace(title)) return null;
        var contents = shelf.Items("contents").ToList();
        var items = ItemParser.SafeMap(contents, c =>
            c.At("musicTwoRowItemRenderer") is { } two ? ItemParser.TwoRowItem(two)
            : c.At("musicResponsiveListItemRenderer") is { } list ? ItemParser.ResponsiveItem(list)
            : null);
        // rows of songs (YouTube Music's "Quick picks", "Trending songs"...) are shown as a song grid
        var listRows = contents.Count(c => c.At("musicResponsiveListItemRenderer") is not null);
        var isList = listRows > contents.Count / 2 && items.Count(i => i.Track is not null) > items.Count / 2;
        return new Shelf(title, items, IsList: isList, More: MoreLink(shelf));
    }

    /// <summary>"See all" for a carousel: its More button, else its title's link.</summary>
    private static BrowseLink? MoreLink(JsonNode shelf)
    {
        var header = shelf.At("header", "musicCarouselShelfBasicHeaderRenderer");
        var endpoint = header.At("moreContentButton", "buttonRenderer", "navigationEndpoint", "browseEndpoint")
                       ?? header.At("title", "runs", 0, "navigationEndpoint", "browseEndpoint");
        var id = endpoint.Str("browseId");
        return id is null ? null : new BrowseLink(id, endpoint.Str("params"));
    }

    // ---------- "See all" grid (an artist's albums, singles…) ----------

    public static GridPage ParseGrid(JsonNode root)
    {
        var title = root.At("header", "musicHeaderRenderer", "title").RunsText() ?? "";
        var grid = root.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents")
            .Items().Select(s => s.At("gridRenderer")).FirstOrDefault(g => g is not null);
        var items = ItemParser.SafeMap(grid.Items("items"), i =>
            i.At("musicTwoRowItemRenderer") is { } two ? ItemParser.TwoRowItem(two)
            : i.At("musicResponsiveListItemRenderer") is { } list ? ItemParser.ResponsiveItem(list)
            : null);
        return new GridPage(title, items);
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
        // "Album • 2023" / "Single • 2021" / "EP • 2019"
        var parts = (header.At("subtitle").RunsText() ?? "").Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var year = parts.FirstOrDefault(p => p.Length == 4 && p.All(char.IsDigit));
        var kind = parts.FirstOrDefault(p => p != year);
        return new Collection(title, header.At("subtitle").RunsText(), thumb, tracks, albumArtists, kind, year);
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
        var subs = header.At("subscriptionButton", "subscribeButtonRenderer", "subscriberCountText").RunsText();
        var monthly = header.At("monthlyListenerCount").RunsText();
        var about = header.At("description").RunsText()
                    ?? sections.Items().Select(s => s.At("musicDescriptionShelfRenderer", "description").RunsText()).FirstOrDefault(d => d is not null);
        // "Top songs" links to a playlist of all the artist's songs
        var all = sections.Items().Select(s => s.At("musicShelfRenderer")).Where(s => s is not null)
            .Select(s => s.Str("title", "runs", 0, "navigationEndpoint", "browseEndpoint", "browseId") ?? s.Str("bottomEndpoint", "browseEndpoint", "browseId"))
            .FirstOrDefault(id => id is not null);
        if (all is not null && all.StartsWith("VL", StringComparison.Ordinal)) all = all[2..];
        return new ArtistPage(name, thumb, topSongs, shelves, browseId, subs, monthly, about, all);
    }
}
