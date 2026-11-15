using System.Text.Json;
using System.Text.Json.Nodes;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.Core.YtMusic;

public sealed class YtMusicApi(InnerTubeClient client) : IYtMusic
{
    private const int HomeContinuations = 2;
    private const int PlaylistContinuations = 4;

    public async Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) =>
        SearchParser.Parse(await client.PostAsync("search", new JsonObject { ["query"] = query }, ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<Shelf>> GetHomeAsync(CancellationToken ct = default)
    {
        var page = BrowseParser.ParseHome(await client.PostAsync("browse", new JsonObject { ["browseId"] = "FEmusic_home" }, ct).ConfigureAwait(false));
        var shelves = new List<Shelf>(page.Shelves);
        var token = page.Continuation;
        for (var i = 0; i < HomeContinuations && token is not null; i++)
        {
            try
            {
                var next = BrowseParser.ParseHome(await client.PostAsync("browse", new JsonObject { ["continuation"] = token }, ct).ConfigureAwait(false));
                shelves.AddRange(next.Shelves);
                token = next.Continuation;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                Log.Warn("Home continuation failed", e);
                break;
            }
        }
        return shelves;
    }

    public async Task<IReadOnlyList<Track>> GetRadioAsync(string videoId, CancellationToken ct = default) =>
        RadioParser.Parse(await client.PostAsync("next", new JsonObject
        {
            ["videoId"] = videoId,
            ["playlistId"] = "RDAMVM" + videoId,
            ["isAudioOnly"] = true,
        }, ct).ConfigureAwait(false));

    public async Task<Collection> GetAlbumAsync(string browseId, CancellationToken ct = default) =>
        BrowseParser.ParseAlbum(await client.PostAsync("browse", new JsonObject { ["browseId"] = browseId }, ct).ConfigureAwait(false), browseId);

    public async Task<Collection> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId : "VL" + playlistId;
        var page = BrowseParser.ParsePlaylist(await client.PostAsync("browse", new JsonObject { ["browseId"] = browseId }, ct).ConfigureAwait(false));
        var tracks = new List<Track>(page.Collection.Tracks);
        var token = page.Continuation;
        for (var i = 0; i < PlaylistContinuations && token is not null; i++)
        {
            try
            {
                var more = BrowseParser.ParsePlaylistContinuation(await client.PostAsync("browse", new JsonObject { ["continuation"] = token }, ct).ConfigureAwait(false));
                tracks.AddRange(more.Tracks);
                token = more.Continuation;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                Log.Warn("Playlist continuation failed", e);
                break;
            }
        }
        return page.Collection with { Tracks = tracks };
    }

    public async Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default) =>
        BrowseParser.ParseArtist(await client.PostAsync("browse", new JsonObject { ["browseId"] = browseId }, ct).ConfigureAwait(false), browseId);
}
