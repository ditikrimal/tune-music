using System.Globalization;
using Tune.Core.Models;

namespace Tune.Core.Library;

/// <summary>What account sync keeps in step across devices.</summary>
public static class SyncKinds
{
    public const string Like = "like";
    public const string Playlist = "playlist";
    /// <summary>Key = a UTC day (yyyyMMdd): this device's plays that day travel as one document.</summary>
    public const string Plays = "plays";
    /// <summary>A synced setting (Home layout, speed dial pins); key = the setting key.</summary>
    public const string Setting = "setting";
}

/// <summary>Something changed here at <paramref name="UpdatedAt"/> (ms) and has not reached the account yet.</summary>
public sealed record SyncItem(string Kind, string Key, long UpdatedAt);

public sealed record SyncPlaylist(string Uid, string Name, IReadOnlyList<Track> Tracks);

/// <summary>
/// Change tracking for account sync. Every like, playlist edit and play marks a row in sync_items with the time
/// of the change; the sync engine uploads unpushed rows and applies remote changes only when they are newer
/// (last change wins, per song / per playlist). History is append-only, so plays are simply merged.
/// </summary>
public sealed partial class LibraryStore
{
    /// <summary>Likes, playlists or history changed on this device (account sync uploads soon after).</summary>
    public event Action? LocalChanged;

    /// <summary>Sync brought in changes from another device: pages showing likes/playlists/history should reload.</summary>
    public event Action? SyncApplied;

    private void MigrateSync()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS sync_items (kind TEXT NOT NULL, key TEXT NOT NULL, updated_at INTEGER NOT NULL,
                pushed INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (kind, key));
            """);
        // playlists need an id that means the same thing on every device
        if (Scalar("SELECT 1 FROM pragma_table_info('playlists') WHERE name = 'uid'") is null)
            Exec("ALTER TABLE playlists ADD COLUMN uid TEXT;");
        Exec("UPDATE playlists SET uid = lower(hex(randomblob(16))) WHERE uid IS NULL;");
        // plays that came from another device are not uploaded again
        if (Scalar("SELECT 1 FROM pragma_table_info('plays') WHERE name = 'remote'") is null)
            Exec("ALTER TABLE plays ADD COLUMN remote INTEGER NOT NULL DEFAULT 0;");
        Exec("CREATE INDEX IF NOT EXISTS plays_video_at ON plays(video_id, at);");
    }

    private static string NewUid() => Guid.NewGuid().ToString("N");

    public static string SyncDay(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private void Touch(string kind, string key)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Exec("""
            INSERT INTO sync_items (kind, key, updated_at, pushed) VALUES ($k, $key, $now, 0)
            ON CONFLICT(kind, key) DO UPDATE SET updated_at = MAX($now, updated_at + 1), pushed = 0
            """, ("$k", kind), ("$key", key), ("$now", now));
        LocalChanged?.Invoke();
    }

    private void TouchPlaylist(long id)
    {
        if (Scalar("SELECT uid FROM playlists WHERE id = $id", ("$id", id)) is string uid) Touch(SyncKinds.Playlist, uid);
    }

    // ---------- used by the sync engine ----------

    public IReadOnlyList<SyncItem> PendingSync()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT kind, key, updated_at FROM sync_items WHERE pushed = 0 ORDER BY updated_at");
            using var r = cmd.ExecuteReader();
            var list = new List<SyncItem>();
            while (r.Read()) list.Add(new SyncItem(r.GetString(0), r.GetString(1), r.GetInt64(2)));
            return list;
        }
    }

    /// <summary>Uploaded — unless it changed again meanwhile (then it stays pending).</summary>
    public void MarkSynced(SyncItem item) =>
        Exec("UPDATE sync_items SET pushed = 1 WHERE kind = $k AND key = $key AND updated_at = $u",
            ("$k", item.Kind), ("$key", item.Key), ("$u", item.UpdatedAt));

    public long SyncUpdatedAt(string kind, string key) =>
        Scalar("SELECT updated_at FROM sync_items WHERE kind = $k AND key = $key", ("$k", kind), ("$key", key)) is long v ? v : 0;

    /// <summary>
    /// Signing in: everything already on this device is uploaded. Items never changed since tracking began get
    /// a very old date, so a change already in the account (e.g. an unlike on the phone) wins over them.
    /// </summary>
    public void MarkAllForSync()
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Exec("UPDATE sync_items SET pushed = 0");
            Exec("INSERT OR IGNORE INTO sync_items (kind, key, updated_at) SELECT 'like', video_id, 1 FROM likes");
            Exec("INSERT OR IGNORE INTO sync_items (kind, key, updated_at) SELECT 'playlist', uid, 1 FROM playlists");
            Exec("INSERT OR IGNORE INTO sync_items (kind, key, updated_at) SELECT 'setting', key, 1 FROM settings WHERE key IN ('home.layout', 'speeddial.pins')");
            Exec("""
                INSERT OR IGNORE INTO sync_items (kind, key, updated_at)
                SELECT 'plays', strftime('%Y%m%d', at / 1000, 'unixepoch'), 1 FROM plays WHERE remote = 0 GROUP BY 2
                """);
            tx.Commit();
        }
    }

    public (bool Liked, Track? Track) LikeState(string videoId)
    {
        var liked = IsLiked(videoId);
        return (liked, GetTracks([videoId]).FirstOrDefault());
    }

    /// <summary>Null when the playlist was deleted.</summary>
    public SyncPlaylist? PlaylistByUid(string uid)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT id, name FROM playlists WHERE uid = $u", ("$u", uid));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var (id, name) = (r.GetInt64(0), r.GetString(1));
            r.Close();
            return new SyncPlaylist(uid, name, GetPlaylistTracks(id));
        }
    }

    public IReadOnlyList<SyncPlaylist> AllPlaylistsForSync()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT uid FROM playlists ORDER BY id");
            using var r = cmd.ExecuteReader();
            var uids = new List<string>();
            while (r.Read()) uids.Add(r.GetString(0));
            r.Close();
            return uids.Select(PlaylistByUid).OfType<SyncPlaylist>().ToList();
        }
    }

    /// <summary>This device's own plays on one UTC day (plays from other devices are theirs to upload).</summary>
    public IReadOnlyList<(Track Track, long At)> LocalPlays(string day)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT t.json, p.at FROM plays p JOIN tracks t ON t.video_id = p.video_id
                WHERE p.remote = 0 AND strftime('%Y%m%d', p.at / 1000, 'unixepoch') = $d ORDER BY p.at
                """, ("$d", day));
            using var r = cmd.ExecuteReader();
            var list = new List<(Track, long)>();
            while (r.Read())
                if (Deserialize(r.GetString(0)) is { } t) list.Add((t, r.GetInt64(1)));
            return list;
        }
    }

    private void Applied(string kind, string key, long updatedAt) =>
        Exec("""
            INSERT INTO sync_items (kind, key, updated_at, pushed) VALUES ($k, $key, $u, 1)
            ON CONFLICT(kind, key) DO UPDATE SET updated_at = $u, pushed = 1
            """, ("$k", kind), ("$key", key), ("$u", updatedAt));

    /// <summary>A like/unlike from the account. Applied only if newer than what this device has.</summary>
    public bool ApplyLike(string videoId, Track? track, bool liked, long updatedAt)
    {
        lock (_gate)
        {
            if (updatedAt <= SyncUpdatedAt(SyncKinds.Like, videoId)) return false;
            if (liked && track is null) return false;
            using var tx = _db.BeginTransaction();
            var was = IsLiked(videoId);
            if (!liked) Exec("DELETE FROM likes WHERE video_id = $v", ("$v", videoId));
            else if (!was)
            {
                SaveTrack(track!);
                Exec("INSERT INTO likes (video_id) VALUES ($v)", ("$v", videoId));
            }
            Applied(SyncKinds.Like, videoId, updatedAt);
            tx.Commit();
            return was != liked;
        }
    }

    /// <summary>A playlist from the account (whole playlist: name + songs), or its deletion. Newer wins.</summary>
    public bool ApplyPlaylist(string uid, string name, IReadOnlyList<Track> tracks, bool deleted, long updatedAt)
    {
        lock (_gate)
        {
            if (updatedAt <= SyncUpdatedAt(SyncKinds.Playlist, uid)) return false;
            using var tx = _db.BeginTransaction();
            var id = Scalar("SELECT id FROM playlists WHERE uid = $u", ("$u", uid)) as long?;
            if (deleted)
            {
                if (id is { } gone) Exec("DELETE FROM playlists WHERE id = $id", ("$id", gone));
            }
            else
            {
                if (id is null)
                {
                    Exec("INSERT INTO playlists (name, uid) VALUES ($n, $u)", ("$n", name), ("$u", uid));
                    id = (long)Scalar("SELECT last_insert_rowid()")!;
                }
                else Exec("UPDATE playlists SET name = $n WHERE id = $id", ("$n", name), ("$id", id));
                ReplaceItems(id.Value, tracks);
            }
            Applied(SyncKinds.Playlist, uid, updatedAt);
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// First sync on this device: a local playlist with the same name as one in the account (e.g. the same YouTube
    /// playlist imported on both) becomes that playlist — its songs merged in — instead of a duplicate.
    /// </summary>
    public bool MergeIntoRemote(string localUid, string remoteUid, IReadOnlyList<Track> remoteTracks)
    {
        lock (_gate)
        {
            if (Scalar("SELECT id FROM playlists WHERE uid = $u", ("$u", localUid)) is not long id) return false;
            var merged = remoteTracks.Concat(GetPlaylistTracks(id)).DistinctBy(t => t.VideoId).ToList();
            using var tx = _db.BeginTransaction();
            Exec("UPDATE playlists SET uid = $r WHERE id = $id", ("$r", remoteUid), ("$id", id));
            Exec("DELETE FROM sync_items WHERE kind = 'playlist' AND key = $u", ("$u", localUid));
            ReplaceItems(id, merged);
            Touch(SyncKinds.Playlist, remoteUid);
            tx.Commit();
            return true;
        }
    }

    private void ReplaceItems(long id, IReadOnlyList<Track> tracks)
    {
        Exec("DELETE FROM playlist_items WHERE playlist_id = $id", ("$id", id));
        var i = 0;
        foreach (var t in tracks.DistinctBy(t => t.VideoId))
        {
            SaveTrack(t);
            Exec("INSERT INTO playlist_items (playlist_id, position, video_id) VALUES ($id, $p, $v)", ("$id", id), ("$p", i++), ("$v", t.VideoId));
        }
    }

    /// <summary>Plays from another device; ones already here are skipped. Returns how many were new.</summary>
    public int ApplyPlays(IEnumerable<(Track Track, long At)> plays)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var added = 0;
            foreach (var (t, at) in plays)
            {
                if (Scalar("SELECT 1 FROM plays WHERE video_id = $v AND at = $at", ("$v", t.VideoId), ("$at", at)) is not null) continue;
                SaveTrack(t);
                Exec("INSERT INTO plays (video_id, at, remote) VALUES ($v, $at, 1)", ("$v", t.VideoId), ("$at", at));
                added++;
            }
            tx.Commit();
            return added;
        }
    }

    public void RaiseSyncApplied() => SyncApplied?.Invoke();
}
