using Tune.Core.Models;

namespace Tune.Core.Playback;

public interface IYtMusic
{
    Task<SearchResults> SearchAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<Shelf>> GetHomeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetRadioAsync(string videoId, CancellationToken ct = default);
    Task<Collection> GetAlbumAsync(string browseId, CancellationToken ct = default);
    Task<Collection> GetPlaylistAsync(string playlistId, CancellationToken ct = default);
    Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default);
}
