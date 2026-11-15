using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tune.Core.Models;
using Tune.Core.Util;

namespace Tune.Core.YtMusic;

/// <summary>Shared parsing of the renderers YouTube Music reuses across search, browse and radio.</summary>
internal static partial class ItemParser
{
    private const string Sep = " • ";
    private static readonly HashSet<string> TypeLabels =
        ["Song", "Video", "Album", "Single", "EP", "Artist", "Playlist", "Episode", "Podcast", "Profile", "Station"];
    private static readonly HashSet<string> SkippedLabels = ["Episode", "Podcast", "Profile"];

    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    private static partial Regex DurationRx();

    public static string? PageType(JsonNode? endpoint) =>
        endpoint.Str("browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");

    public static TimeSpan? ParseDuration(string? text)
    {
        if (text is null || !DurationRx().IsMatch(text.Trim())) return null;
        var parts = text.Trim().Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var seconds = parts.Aggregate(0, (acc, p) => acc * 60 + p);
        return TimeSpan.FromSeconds(seconds);
    }

    public static IEnumerable<JsonNode> Runs(JsonNode? text) => text.Items("runs");

    /// <summary>Artist runs (linked to artist pages); falls back to the first plain text segment.</summary>
    public static List<ArtistRef> Artists(JsonNode? text, bool skipLeadingTypeLabel)
    {
        var linked = Runs(text)
            .Where(r => PageType(r.At("navigationEndpoint")) is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
            .Select(r => new ArtistRef(r.Str("text")!, r.Str("navigationEndpoint", "browseEndpoint", "browseId")))
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .ToList();
        if (linked.Count > 0) return linked;

        foreach (var segment in Segments(text, skipLeadingTypeLabel))
        {
            if (ParseDuration(segment) is not null || LooksLikeCount(segment) || IsYear(segment)) continue;
            return [new ArtistRef(segment, null)];
        }
        return [];
    }

    public static AlbumRef? Album(JsonNode? text) =>
        Runs(text)
            .Where(r => PageType(r.At("navigationEndpoint")) == "MUSIC_PAGE_TYPE_ALBUM")
            .Select(r => new AlbumRef(r.Str("text")!, r.Str("navigationEndpoint", "browseEndpoint", "browseId")))
            .FirstOrDefault();

    /// <summary>Text split on " • ", optionally dropping a leading "Song"/"Album"/… label.</summary>
    public static List<string> Segments(JsonNode? text, bool skipLeadingTypeLabel)
    {
        var all = (text.RunsText() ?? "").Split(Sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (skipLeadingTypeLabel && all.Count > 0 && TypeLabels.Contains(all[0])) all.RemoveAt(0);
        return all;
    }

    public static string? Label(JsonNode? text)
    {
        var first = (text.RunsText() ?? "").Split(Sep)[0].Trim();
        return TypeLabels.Contains(first) ? first : null;
    }

    private static bool LooksLikeCount(string s) =>
        s.EndsWith(" views", StringComparison.Ordinal) || s.EndsWith(" plays", StringComparison.Ordinal) ||
        s.EndsWith(" songs", StringComparison.Ordinal) || s.EndsWith(" subscribers", StringComparison.Ordinal) ||
        s.EndsWith(" monthly audience", StringComparison.Ordinal);

    private static bool IsYear(string s) => s.Length == 4 && s.All(char.IsAsciiDigit);

    private static JsonNode? Column(JsonNode r, int i) =>
        r.At("flexColumns", i, "musicResponsiveListItemFlexColumnRenderer", "text");

    private static JsonNode? FixedColumn(JsonNode r, int i) =>
        r.At("fixedColumns", i, "musicResponsiveListItemFixedColumnRenderer", "text");

    /// <summary>Parses a <c>musicResponsiveListItemRenderer</c> into a card (song card carries a Track).</summary>
    public static Card? ResponsiveItem(JsonNode r, IReadOnlyList<ArtistRef>? fallbackArtists = null, AlbumRef? albumContext = null, string? thumbnailContext = null)
    {
        var col0 = Column(r, 0);
        var col1 = Column(r, 1);
        var title = col0.RunsText();
        if (string.IsNullOrWhiteSpace(title)) return null;
        var thumb = r.At("thumbnail", "musicThumbnailRenderer", "thumbnail").BestThumbnail();
        var label = Label(col1);

        var videoId = r.Str("playlistItemData", "videoId")
                      ?? Runs(col0).Select(x => x.Str("navigationEndpoint", "watchEndpoint", "videoId")).FirstOrDefault(v => v is not null)
                      ?? r.Str("overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint", "watchEndpoint", "videoId");

        if (videoId is not null)
        {
            if (label is not null && SkippedLabels.Contains(label)) return null;
            var artists = Artists(col1, skipLeadingTypeLabel: true);
            // later flex columns (album track lists, playlists) may hold the artist / album
            for (var i = 2; artists.Count == 0 && i < 4; i++) artists = Artists(Column(r, i), false);
            if (artists.Count == 0 && fallbackArtists is { Count: > 0 }) artists = [.. fallbackArtists];
            var album = Album(col1) ?? Album(Column(r, 2)) ?? albumContext;
            var duration = Segments(col1, true).Select(ParseDuration).FirstOrDefault(d => d is not null)
                           ?? ParseDuration(FixedColumn(r, 0).RunsText());
            var track = new Track(videoId, title, artists, album, duration, thumb ?? thumbnailContext);
            return new Card(CardKind.Song, title, track.ArtistLine, track.ThumbnailUrl, videoId, null, null, track);
        }

        var endpoint = r.At("navigationEndpoint");
        var browseId = endpoint.Str("browseEndpoint", "browseId");
        if (browseId is null) return null;
        var subtitle = string.Join(Sep, Segments(col1, true).Where(s => !LooksLikeCount(s) || label == "Playlist"));
        return MakeBrowseCard(PageType(endpoint), title, subtitle, thumb, browseId);
    }

    /// <summary>Parses a <c>musicTwoRowItemRenderer</c> (home/artist carousels).</summary>
    public static Card? TwoRowItem(JsonNode r)
    {
        var title = r.At("title").RunsText();
        if (string.IsNullOrWhiteSpace(title)) return null;
        var thumb = r.At("thumbnailRenderer", "musicThumbnailRenderer", "thumbnail").BestThumbnail();
        var subtitleNode = r.At("subtitle");
        var subtitle = string.Join(Sep, Segments(subtitleNode, true));
        var endpoint = r.At("navigationEndpoint");

        if (endpoint.Str("watchEndpoint", "videoId") is { } videoId)
        {
            var artists = Artists(subtitleNode, skipLeadingTypeLabel: true);
            var track = new Track(videoId, title, artists, Album(subtitleNode), null, thumb);
            return new Card(CardKind.Song, title, track.ArtistLine, thumb, videoId, null, null, track);
        }

        var browseId = endpoint.Str("browseEndpoint", "browseId");
        return browseId is null ? null : MakeBrowseCard(PageType(endpoint), title, subtitle, thumb, browseId);
    }

    private static Card? MakeBrowseCard(string? pageType, string title, string? subtitle, string? thumb, string browseId) =>
        pageType switch
        {
            "MUSIC_PAGE_TYPE_ALBUM" or "MUSIC_PAGE_TYPE_AUDIOBOOK" =>
                new Card(CardKind.Album, title, subtitle, thumb, null, browseId, null, null),
            "MUSIC_PAGE_TYPE_ARTIST" =>
                new Card(CardKind.Artist, title, subtitle, thumb, null, browseId, null, null),
            "MUSIC_PAGE_TYPE_PLAYLIST" =>
                new Card(CardKind.Playlist, title, subtitle, thumb, null, browseId,
                    browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId[2..] : browseId, null),
            _ => null,
        };

    /// <summary>Runs <paramref name="parse"/> on each node, logging and skipping any that throw.</summary>
    public static List<T> SafeMap<T>(IEnumerable<JsonNode> nodes, Func<JsonNode, T?> parse) where T : class
    {
        var result = new List<T>();
        foreach (var n in nodes)
        {
            try
            {
                if (parse(n) is { } item) result.Add(item);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or ArgumentException or KeyNotFoundException)
            {
                Log.Warn("Skipped unparseable item", e);
            }
        }
        return result;
    }
}
