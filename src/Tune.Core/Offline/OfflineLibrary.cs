using Tune.Core.Library;
using Tune.Core.Models;

namespace Tune.Core.Offline;

/// <summary>What plays without internet: your downloads, then songs kept in the streaming cache.</summary>
public sealed class OfflineLibrary(DownloadManager downloads, AudioCache cache, LibraryStore lib) : IOfflineSource
{
    public IConnectivity Network => downloads.Network;

    public string? LocalPath(string videoId) => downloads.LocalPath(videoId) ?? cache.LocalPath(videoId);

    public IReadOnlyList<Track> PlayableTracks()
    {
        var saved = downloads.DownloadedTracks();
        var have = saved.Select(t => t.VideoId).ToHashSet();
        var cached = lib.GetTracks(cache.Ids().Where(id => !have.Contains(id)));
        return [.. saved, .. cached];
    }

    public void KeepForOffline(IReadOnlyList<Track> tracks) => cache.Want(tracks);
}
