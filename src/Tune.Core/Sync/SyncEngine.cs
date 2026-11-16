using Tune.Core.Library;
using Tune.Core.Models;

namespace Tune.Core.Sync;

public sealed record SyncResult(int Pulled, int Pushed, bool ChangedHere);

/// <summary>
/// One sync round: pull what other devices changed (newer change wins, per song / per playlist; history merges),
/// then push what changed here. Collections: likes/{videoId}, playlists/{uid}, plays/{device}_{yyyyMMdd}.
/// </summary>
public sealed class SyncEngine(LibraryStore lib, ISyncRemote remote, string deviceId)
{
    public const string Likes = "likes", Playlists = "playlists", Plays = "plays";
    private const int Page = 300;

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var pulled = 0;
        var changed = false;

        // ---- pull ----
        var playlistDocs = await PullAllAsync(Playlists, ct).ConfigureAwait(false);
        if (playlistDocs.Initial) changed |= MergeSameNamePlaylists(playlistDocs.Docs);
        foreach (var d in playlistDocs.Docs)
            changed |= lib.ApplyPlaylist(d.Id, d.Str("name") ?? "Playlist", SyncFormat.ParseTracks(d.Str("tracks")), d.Bool("deleted"), d.Long("updatedAt"));
        pulled += playlistDocs.Docs.Count;
        playlistDocs.Commit();

        var likeDocs = await PullAllAsync(Likes, ct).ConfigureAwait(false);
        foreach (var d in likeDocs.Docs)
        {
            var track = SyncFormat.ParseTracks($"[{d.Str("track")}]").FirstOrDefault();
            changed |= lib.ApplyLike(d.Id, track, d.Bool("liked"), d.Long("updatedAt"));
        }
        pulled += likeDocs.Docs.Count;
        likeDocs.Commit();

        var playDocs = await PullAllAsync(Plays, ct).ConfigureAwait(false);
        foreach (var d in playDocs.Docs.Where(d => d.Str("device") != deviceId))
        {
            var tracks = SyncFormat.ParseTracks(d.Str("tracks")).DistinctBy(t => t.VideoId).ToDictionary(t => t.VideoId);
            var plays = SyncFormat.ParsePlays(d.Str("plays"))
                .Where(p => tracks.ContainsKey(p.VideoId)).Select(p => (tracks[p.VideoId], p.At));
            changed |= lib.ApplyPlays(plays) > 0;
        }
        pulled += playDocs.Docs.Count;
        playDocs.Commit();

        // ---- push ----
        var pending = lib.PendingSync();
        var docs = new List<(SyncItem Item, RemoteDoc Doc)>();
        foreach (var item in pending)
            if (Build(item) is { } doc) docs.Add((item, doc));
        if (docs.Count > 0) await remote.PushAsync(docs.Select(x => x.Doc).ToList(), ct).ConfigureAwait(false);
        foreach (var item in pending) lib.MarkSynced(item);

        if (changed) lib.RaiseSyncApplied();
        return new SyncResult(pulled, docs.Count, changed);
    }

    private RemoteDoc? Build(SyncItem item)
    {
        switch (item.Kind)
        {
            case SyncKinds.Like:
                var (liked, track) = lib.LikeState(item.Key);
                var likeFields = new Dictionary<string, object?> { ["liked"] = liked, ["updatedAt"] = item.UpdatedAt };
                if (track is not null) likeFields["track"] = SyncFormat.ToJson(track).ToJsonString();
                else if (liked) return null;
                return new RemoteDoc(Likes, item.Key, likeFields);
            case SyncKinds.Playlist:
                var p = lib.PlaylistByUid(item.Key);
                return new RemoteDoc(Playlists, item.Key, p is null
                    ? new Dictionary<string, object?> { ["deleted"] = true, ["name"] = "", ["tracks"] = "[]", ["updatedAt"] = item.UpdatedAt }
                    : new Dictionary<string, object?>
                    {
                        ["deleted"] = false, ["name"] = p.Name, ["tracks"] = SyncFormat.TracksJson(p.Tracks), ["updatedAt"] = item.UpdatedAt,
                    });
            case SyncKinds.Plays:
                var plays = lib.LocalPlays(item.Key);
                if (plays.Count == 0) return null;
                return new RemoteDoc(Plays, $"{deviceId}_{item.Key}", new Dictionary<string, object?>
                {
                    ["device"] = deviceId,
                    ["day"] = item.Key,
                    ["plays"] = SyncFormat.PlaysJson(plays),
                    ["tracks"] = SyncFormat.TracksJson(plays.Select(x => x.Track).DistinctBy(t => t.VideoId)),
                    ["updatedAt"] = item.UpdatedAt,
                });
            default:
                return null;
        }
    }

    /// <summary>First sync here: "Songs to Listen" imported on both devices becomes one playlist, not two.</summary>
    private bool MergeSameNamePlaylists(IReadOnlyList<RemoteDoc> remoteDocs)
    {
        var remoteIds = remoteDocs.Select(d => d.Id).ToHashSet();
        var live = remoteDocs.Where(d => !d.Bool("deleted")).ToList();
        var merged = false;
        foreach (var local in lib.AllPlaylistsForSync().Where(p => !remoteIds.Contains(p.Uid)))
        {
            var match = live.FirstOrDefault(d => string.Equals(d.Str("name"), local.Name, StringComparison.OrdinalIgnoreCase)
                                                 && lib.PlaylistByUid(d.Id) is null);
            if (match is null) continue;
            merged |= lib.MergeIntoRemote(local.Uid, match.Id, SyncFormat.ParseTracks(match.Str("tracks")));
            live.Remove(match);
        }
        return merged;
    }

    private sealed record Pulled(IReadOnlyList<RemoteDoc> Docs, bool Initial, Action Commit);

    /// <summary>All pages since the saved cursor. The cursor is saved by Commit, after the docs are applied.</summary>
    private async Task<Pulled> PullAllAsync(string collection, CancellationToken ct)
    {
        var key = $"sync.cursor.{collection}";
        var start = lib.GetSetting(key) is { Length: > 0 } c ? c : null;
        var cursor = start;
        var all = new List<RemoteDoc>();
        while (true)
        {
            var page = await remote.PullAsync(collection, cursor, Page, ct).ConfigureAwait(false);
            all.AddRange(page.Docs);
            cursor = page.Cursor;
            if (page.Docs.Count < Page) break;
        }
        var end = cursor;
        return new Pulled(all, start is null, () => { if (end is not null) lib.SetSetting(key, end); });
    }

    public static void ResetCursors(LibraryStore lib)
    {
        foreach (var c in new[] { Likes, Playlists, Plays }) lib.SetSetting($"sync.cursor.{c}", "");
    }
}
