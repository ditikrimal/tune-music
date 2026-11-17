namespace Tune.Core.Models;

public sealed record ArtistRef(string Name, string? BrowseId);

public sealed record AlbumRef(string Title, string? BrowseId);

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
/// <paramref name="IsSpeedDial"/>: pages of 3×3 square tiles.</summary>
public sealed record Shelf(string Title, IReadOnlyList<Card> Items, bool IsLocal = false, bool IsList = false, bool IsSpeedDial = false);

public sealed record Collection(string Title, string? Subtitle, string? ThumbnailUrl, IReadOnlyList<Track> Tracks);

public sealed record ArtistPage(string Name, string? ThumbnailUrl, IReadOnlyList<Track> TopSongs, IReadOnlyList<Shelf> Shelves);

/// <summary>A Moods &amp; genres tile ("Chill", "Hip-hop"…): <paramref name="Params"/> opens its page; <paramref name="Color"/> is
/// YouTube Music's ARGB stripe colour; <paramref name="ThumbnailUrl"/> is the cover of the page's first playlist (filled in later).</summary>
public sealed record MoodCategory(string Title, string Params, uint Color, string? ThumbnailUrl = null);

/// <summary>"Moods &amp; moments" or "Genres".</summary>
public sealed record MoodSection(string Title, IReadOnlyList<MoodCategory> Items);

public sealed record SearchResults(
    Card? Top,
    IReadOnlyList<Track> Songs,
    IReadOnlyList<Card> Albums,
    IReadOnlyList<Card> Artists,
    IReadOnlyList<Card> Playlists);
