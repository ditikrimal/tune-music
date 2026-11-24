namespace Tune.Core.Models;

public sealed record ArtistRef(string Name, string? BrowseId);

public sealed record AlbumRef(string Title, string? BrowseId);

/// <summary>A YouTube Music page to open: a browse id plus optional params (an artist's "Albums → More" list…).</summary>
public sealed record BrowseLink(string BrowseId, string? Params = null);

public sealed record Track(
    string VideoId,
    string Title,
    IReadOnlyList<ArtistRef> Artists,
    AlbumRef? Album,
    TimeSpan? Duration,
    string? ThumbnailUrl)
{
    public string ArtistLine => string.Join(", ", Artists.Select(a => a.Name));
}

public enum CardKind { Song, Album, Playlist, Artist }

public sealed record Card(
    CardKind Kind,
    string Title,
    string? Subtitle,
    string? ThumbnailUrl,
    string? VideoId,
    string? BrowseId,
    string? PlaylistId,
    Track? Track,
    bool Pinned = false);

/// <summary>A Home row. <paramref name="IsList"/>: a grid of song rows (Quick picks) instead of big cards;
/// <paramref name="IsSpeedDial"/>: pages of 3×3 square tiles. <paramref name="More"/>: where "See all" goes.</summary>
public sealed record Shelf(string Title, IReadOnlyList<Card> Items, bool IsLocal = false, bool IsList = false, bool IsSpeedDial = false,
    BrowseLink? More = null);

/// <summary>A list of songs. Albums also carry their <paramref name="Artists"/>, <paramref name="Kind"/> ("Album", "Single", "EP")
/// and <paramref name="Year"/>.</summary>
public sealed record Collection(string Title, string? Subtitle, string? ThumbnailUrl, IReadOnlyList<Track> Tracks,
    IReadOnlyList<ArtistRef>? Artists = null, string? Kind = null, string? Year = null);

/// <summary>An artist's page. <paramref name="AllSongs"/>: the playlist behind "Top songs → See all";
/// <paramref name="Subscribers"/> / <paramref name="MonthlyListeners"/> as YouTube Music writes them ("6.59M").</summary>
public sealed record ArtistPage(string Name, string? ThumbnailUrl, IReadOnlyList<Track> TopSongs, IReadOnlyList<Shelf> Shelves,
    string? BrowseId = null, string? Subscribers = null, string? MonthlyListeners = null, string? About = null, string? AllSongs = null);

/// <summary>A "See all" page: a titled grid of albums / singles / playlists.</summary>
public sealed record GridPage(string Title, IReadOnlyList<Card> Items);

/// <summary>A Moods &amp; genres tile ("Chill", "Hip-hop"…): <paramref name="Params"/> opens its page; <paramref name="Color"/> is
/// YouTube Music's ARGB stripe colour; <paramref name="ThumbnailUrl"/> is the cover of the page's first playlist (filled in later).</summary>
public sealed record MoodCategory(string Title, string Params, uint Color, string? ThumbnailUrl = null);

/// <summary>"Moods &amp; moments" or "Genres".</summary>
public sealed record MoodSection(string Title, IReadOnlyList<MoodCategory> Items);

/// <summary>What a radio chip changes: <see cref="Discovery"/> (Popular, Discover, Deep cuts), <see cref="Mood"/> (Chill,
/// Downbeat, Pump-up…), <see cref="Era"/> (2010s…) or <see cref="Genre"/> (Hip hop, Bengali…).</summary>
public enum VibeKind { Discovery, Mood, Era, Genre }

/// <summary>One of YouTube Music's radio tuner chips for a song: a tag for the song's vibe, and the tuned radio behind it.</summary>
public sealed record VibeChip(string Name, string PlaylistId, string? Params, VibeKind Kind);

/// <summary>A song's radio as YouTube Music gives it: the songs, the tuner chips, more songs (<paramref name="Continuation"/>)
/// and the song's Related tab (<paramref name="RelatedId"/>).</summary>
public sealed record RadioPage(IReadOnlyList<Track> Tracks, IReadOnlyList<VibeChip> Chips, string? Continuation, string? RelatedId = null);

/// <summary>A song's Related tab: "You might also like" songs, similar artists, recommended playlists.</summary>
public sealed record RelatedPage(IReadOnlyList<Track> Songs, IReadOnlyList<Card> Artists, IReadOnlyList<Card> Playlists);

public sealed record SearchResults(
    Card? Top,
    IReadOnlyList<Track> Songs,
    IReadOnlyList<Card> Albums,
    IReadOnlyList<Card> Artists,
    IReadOnlyList<Card> Playlists);
