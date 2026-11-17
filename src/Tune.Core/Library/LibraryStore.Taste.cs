using Tune.Core.Models;

namespace Tune.Core.Library;

/// <summary>Listening signals for recommendations: every play (with its time) and early skips.</summary>
public sealed partial class LibraryStore
{
    /// <summary>Settings that follow the account to every device (Home layout, speed dial pins).</summary>
    public static readonly IReadOnlySet<string> SyncedSettings = new HashSet<string> { "home.layout", "speeddial.pins", NotInterestedKey, ArtistsKey, AlbumsKey };

    /// <summary>Songs you said you're not interested in (comma-separated ids, newest first; synced).</summary>
    public const string NotInterestedKey = "taste.notInterested";

    public IReadOnlySet<string> GetNotInterested() =>
        (GetSetting(NotInterestedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>"Not interested": the song stays out of Home, quick picks and radios. Returns false if it already was.</summary>
    public bool SetNotInterested(string videoId, bool notInterested = true)
    {
        var ids = (GetSetting(NotInterestedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var had = ids.Remove(videoId);
        if (notInterested) ids.Insert(0, videoId);
        if (had == notInterested) return false;
        SetSetting(NotInterestedKey, string.Join(',', ids.Take(1000)));
        return true;
    }

    private void MigrateTaste() =>
        Exec("""
            CREATE TABLE IF NOT EXISTS skips (id INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS skips_at ON skips(at);
            """);

    /// <summary>The song was skipped early (before it counted as a play): a "not now / not for me" signal.</summary>
    public void RecordSkip(string videoId, DateTimeOffset at) =>
        Exec("INSERT INTO skips (video_id, at) VALUES ($v, $at)", ("$v", videoId), ("$at", at.ToUnixTimeMilliseconds()));

    /// <summary>Every play since <paramref name="since"/> with its time (one entry per play).</summary>
    public IReadOnlyList<(Track Track, DateTimeOffset At)> GetPlays(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT p.video_id, p.at, t.json FROM plays p JOIN tracks t ON t.video_id = p.video_id WHERE p.at >= $since ORDER BY p.at
                """, ("$since", since.ToUnixTimeMilliseconds()));
            using var r = cmd.ExecuteReader();
            var tracks = new Dictionary<string, Track?>();
            var list = new List<(Track, DateTimeOffset)>();
            while (r.Read())
            {
                var id = r.GetString(0);
                if (!tracks.TryGetValue(id, out var t)) tracks[id] = t = Deserialize(r.GetString(2));
                if (t is not null) list.Add((t, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1))));
            }
            return list;
        }
    }

    public IReadOnlyList<(string VideoId, DateTimeOffset At)> GetSkips(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT video_id, at FROM skips WHERE at >= $since", ("$since", since.ToUnixTimeMilliseconds()));
            using var r = cmd.ExecuteReader();
            var list = new List<(string, DateTimeOffset)>();
            while (r.Read()) list.Add((r.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1))));
            return list;
        }
    }

    /// <summary>The device-independent id of a playlist (for pins), and back.</summary>
    public string? PlaylistUid(long id) => Scalar("SELECT uid FROM playlists WHERE id = $id", ("$id", id)) as string;

    public long? PlaylistIdByUid(string uid) => Scalar("SELECT id FROM playlists WHERE uid = $u", ("$u", uid)) as long?;

    /// <summary>A Home-layout / pins change from another device. Newer wins.</summary>
    public bool ApplySetting(string key, string value, long updatedAt)
    {
        lock (_gate)
        {
            if (!SyncedSettings.Contains(key) || updatedAt <= SyncUpdatedAt(SyncKinds.Setting, key)) return false;
            using var tx = _db.BeginTransaction();
            Exec("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
            Applied(SyncKinds.Setting, key, updatedAt);
            tx.Commit();
        }
        if (key is ArtistsKey or AlbumsKey) RaiseSavedChanged();
        return true;
    }
}
