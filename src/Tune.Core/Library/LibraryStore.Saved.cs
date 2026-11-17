using System.Text.Json;
using Tune.Core.Models;

namespace Tune.Core.Library;

/// <summary>An artist you follow.</summary>
public sealed record SavedArtist(string BrowseId, string Name, string? ThumbnailUrl, long AddedAt);

/// <summary>An album (or single / EP) saved to your library.</summary>
public sealed record SavedAlbum(string BrowseId, string Title, string? Artist, string? ThumbnailUrl, string? Year, long AddedAt);

/// <summary>
/// Followed artists and saved albums. Kept as two small JSON lists in synced settings, so they follow the account to
/// every device like the Home layout does (newest change of the whole list wins — fine for one person's library).
/// </summary>
public sealed partial class LibraryStore
{
    public const string ArtistsKey = "library.artists";
    public const string AlbumsKey = "library.albums";

    private static readonly JsonSerializerOptions SavedJson = new(JsonSerializerDefaults.Web);

    /// <summary>Raised when the followed artists or saved albums change (here or from another device).</summary>
    public event Action? SavedChanged;

    public IReadOnlyList<SavedArtist> GetFollowedArtists() => ReadList<SavedArtist>(ArtistsKey);

    public IReadOnlyList<SavedAlbum> GetSavedAlbums() => ReadList<SavedAlbum>(AlbumsKey);

    public bool IsFollowing(string? browseId) => browseId is not null && GetFollowedArtists().Any(a => a.BrowseId == browseId);

    public bool IsAlbumSaved(string? browseId) => browseId is not null && GetSavedAlbums().Any(a => a.BrowseId == browseId);

    /// <summary>Follows or unfollows an artist (newest first).</summary>
    public void SetFollowing(string browseId, string name, string? thumbnailUrl, bool follow)
    {
        var list = GetFollowedArtists().Where(a => a.BrowseId != browseId).ToList();
        if (follow) list.Insert(0, new SavedArtist(browseId, name, thumbnailUrl, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        WriteList(ArtistsKey, list);
    }

    /// <summary>Saves or removes an album (newest first).</summary>
    public void SetAlbumSaved(string browseId, string title, string? artist, string? thumbnailUrl, string? year, bool save)
    {
        var list = GetSavedAlbums().Where(a => a.BrowseId != browseId).ToList();
        if (save) list.Insert(0, new SavedAlbum(browseId, title, artist, thumbnailUrl, year, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        WriteList(AlbumsKey, list);
    }

    private IReadOnlyList<T> ReadList<T>(string key)
    {
        var json = GetSetting(key);
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json, SavedJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private void WriteList<T>(string key, IReadOnlyList<T> list)
    {
        SetSetting(key, JsonSerializer.Serialize(list, SavedJson));
        SavedChanged?.Invoke();
    }

    /// <summary>Called after a sync wrote settings from another device.</summary>
    internal void RaiseSavedChanged() => SavedChanged?.Invoke();
}
