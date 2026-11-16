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
    Track? Track);

/// <summary>A Home row. <paramref name="IsList"/>: a grid of song rows (Quick picks) instead of big cards.</summary>
public sealed record Shelf(string Title, IReadOnlyList<Card> Items, bool IsLocal = false, bool IsList = false);

public sealed record Collection(string Title, string? Subtitle, string? ThumbnailUrl, IReadOnlyList<Track> Tracks);

public sealed record ArtistPage(string Name, string? ThumbnailUrl, IReadOnlyList<Track> TopSongs, IReadOnlyList<Shelf> Shelves);

public sealed record SearchResults(
    Card? Top,
    IReadOnlyList<Track> Songs,
    IReadOnlyList<Card> Albums,
    IReadOnlyList<Card> Artists,
    IReadOnlyList<Card> Playlists);
