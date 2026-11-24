using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tune.Core.Models;

namespace Tune.Core.Library;

public sealed record PlaylistInfo(long Id, string Name, int Count, string? ThumbnailUrl);

public sealed record PlayStat(Track Track, int Plays);

/// <summary>A song saved for offline play. <see cref="Path"/> is null while it is still waiting/downloading.</summary>
public sealed record DownloadInfo(Track Track, string? Path, string? ArtPath, long Bytes, DateTimeOffset AddedAt);

/// <summary>Everything the app remembers locally: play history, likes, playlists and settings.</summary>
public sealed partial class LibraryStore : IDisposable, Tune.Core.Streams.IStreamCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true };
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public LibraryStore(string dbPath)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS tracks (video_id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS plays (id INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS plays_at ON plays(at);
            CREATE TABLE IF NOT EXISTS likes (seq INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS playlists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS playlist_items (
                playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                position INTEGER NOT NULL, video_id TEXT NOT NULL, PRIMARY KEY (playlist_id, position));
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS downloads (video_id TEXT PRIMARY KEY, path TEXT, art_path TEXT,
                bytes INTEGER NOT NULL DEFAULT 0, added_at INTEGER NOT NULL);
            """);
        // v3: stream_cache gains "source" (worker/exe). Links saved before may be bound to a VPN route
        // (see ToolManager.PreparePrivatePython), so the old table is simply dropped — it's only a cache.
        if (Convert.ToInt64(Scalar("PRAGMA user_version"), System.Globalization.CultureInfo.InvariantCulture) < 3)
            Exec("DROP TABLE IF EXISTS stream_cache;");
        Exec("""
            CREATE TABLE IF NOT EXISTS stream_cache (video_id TEXT PRIMARY KEY, url TEXT NOT NULL,
                valid_until INTEGER NOT NULL, available_at INTEGER NOT NULL, source TEXT NOT NULL DEFAULT 'exe');
            PRAGMA user_version=3;
            """);
        // smart downloads: songs Tune picked itself (it may swap them out); 0 = the user asked for it
        if (Scalar("SELECT 1 FROM pragma_table_info('downloads') WHERE name = 'smart'") is null)
            Exec("ALTER TABLE downloads ADD COLUMN smart INTEGER NOT NULL DEFAULT 0;");
        MigrateSync();
        MigrateTaste();
        MigrateSignals();
    }

    public void Dispose() => _db.Dispose();

    // ---------- history ----------

    public void RecordPlay(Track t, DateTimeOffset at)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            SaveTrack(t);
            Exec("INSERT INTO plays (video_id, at) VALUES ($v, $at)", ("$v", t.VideoId), ("$at", at.ToUnixTimeMilliseconds()));
            Touch(SyncKinds.Plays, SyncDay(at.ToUnixTimeMilliseconds()));
            tx.Commit();
        }
    }

    public IReadOnlyList<Track> GetRecent(int max) =>
        QueryTracks("""
            SELECT t.json FROM (SELECT video_id, MAX(at) AS last, MAX(id) AS lid FROM plays GROUP BY video_id) p
            JOIN tracks t ON t.video_id = p.video_id ORDER BY p.last DESC, p.lid DESC LIMIT $n
            """, ("$n", max));

    public IReadOnlyList<PlayStat> GetTopTracks(DateTimeOffset since, int max)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT t.json, COUNT(*) AS n, MAX(p.at) AS last FROM plays p JOIN tracks t ON t.video_id = p.video_id
                WHERE p.at >= $since GROUP BY p.video_id ORDER BY n DESC, last DESC, MAX(p.id) DESC LIMIT $n
                """, ("$since", since.ToUnixTimeMilliseconds()), ("$n", max));
            using var r = cmd.ExecuteReader();
            var list = new List<PlayStat>();
            while (r.Read())
                if (Deserialize(r.GetString(0)) is { } t) list.Add(new PlayStat(t, r.GetInt32(1)));
            return list;
        }
    }

    public IReadOnlyList<(ArtistRef Artist, int Plays)> GetTopArtists(DateTimeOffset since, int max) =>
        GetTopTracks(since, int.MaxValue)
            .SelectMany(s => s.Track.Artists.Select(a => (Artist: a, s.Plays)))
            .GroupBy(x => x.Artist.BrowseId ?? x.Artist.Name)
            .Select(g => (g.First().Artist, Plays: g.Sum(x => x.Plays)))
            .OrderByDescending(x => x.Plays)
            .Take(max)
            .ToList();

    /// <summary>Per song since <paramref name="since"/>: how often and when last played (for smart downloads).</summary>
    public IReadOnlyList<(Track Track, int Plays, DateTimeOffset LastPlayed)> GetPlayStats(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT t.json, COUNT(*) AS n, MAX(p.at) FROM plays p JOIN tracks t ON t.video_id = p.video_id
                WHERE p.at >= $since GROUP BY p.video_id
                """, ("$since", since.ToUnixTimeMilliseconds()));
            using var r = cmd.ExecuteReader();
            var list = new List<(Track, int, DateTimeOffset)>();
            while (r.Read())
                if (Deserialize(r.GetString(0)) is { } t) list.Add((t, r.GetInt32(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))));
            return list;
        }
    }

    /// <summary>Remembers songs' details (e.g. songs kept in the streaming cache, so they can be listed offline).</summary>
    public void SaveTracks(IEnumerable<Track> tracks)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var t in tracks) SaveTrack(t);
            tx.Commit();
        }
    }

    /// <summary>The saved details of these songs (unknown ids are skipped), in the order given.</summary>
    public IReadOnlyList<Track> GetTracks(IEnumerable<string> videoIds)
    {
        var found = new Dictionary<string, Track>();
        lock (_gate)
        {
            foreach (var id in videoIds.Distinct())
            {
                using var cmd = Command("SELECT json FROM tracks WHERE video_id = $v", ("$v", id));
                if (cmd.ExecuteScalar() is string json && Deserialize(json) is { } t) found[id] = t;
            }
        }
        return videoIds.Distinct().Where(found.ContainsKey).Select(id => found[id]).ToList();
    }

    // ---------- likes ----------

    public bool IsLiked(string videoId) => Scalar("SELECT 1 FROM likes WHERE video_id = $v", ("$v", videoId)) is not null;

    public void SetLiked(Track t, bool liked)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Exec("DELETE FROM likes WHERE video_id = $v", ("$v", t.VideoId));
            if (liked)
            {
                SaveTrack(t);
                Exec("INSERT INTO likes (video_id) VALUES ($v)", ("$v", t.VideoId));
            }
            Touch(SyncKinds.Like, t.VideoId);
            tx.Commit();
        }
    }

    public IReadOnlyList<Track> GetLiked() =>
        QueryTracks("SELECT t.json FROM likes l JOIN tracks t ON t.video_id = l.video_id ORDER BY l.seq DESC");

    // ---------- downloads ----------

    /// <summary>Adds songs to the download list (already listed ones keep their place and file).</summary>
    public void AddDownloads(IEnumerable<Track> tracks, DateTimeOffset at, bool smart = false)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var ms = at.ToUnixTimeMilliseconds();
            foreach (var t in tracks)
            {
                SaveTrack(t);
                Exec("INSERT INTO downloads (video_id, added_at, smart) VALUES ($v, $at, $s) ON CONFLICT(video_id) DO NOTHING",
                    ("$v", t.VideoId), ("$at", ms--), ("$s", smart ? 1 : 0)); // newest-first list still shows a batch in the order given
            }
            tx.Commit();
        }
    }

    public void SetDownloaded(string videoId, string path, string? artPath, long bytes) =>
        Exec("UPDATE downloads SET path = $p, art_path = $a, bytes = $b WHERE video_id = $v",
            ("$v", videoId), ("$p", path), ("$a", (object?)artPath ?? DBNull.Value), ("$b", bytes));

    /// <summary>The user asked for these songs: smart downloads must never remove them.</summary>
    public void ClaimDownloads(IEnumerable<string> videoIds)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in videoIds) Exec("UPDATE downloads SET smart = 0 WHERE video_id = $v AND smart = 1", ("$v", id));
            tx.Commit();
        }
    }

    public IReadOnlySet<string> SmartDownloadIds()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT video_id FROM downloads WHERE smart = 1");
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public void RemoveDownload(string videoId) => Exec("DELETE FROM downloads WHERE video_id = $v", ("$v", videoId));

    /// <summary>Newest first.</summary>
    public IReadOnlyList<DownloadInfo> GetDownloads()
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT t.json, d.path, d.art_path, d.bytes, d.added_at FROM downloads d
                JOIN tracks t ON t.video_id = d.video_id ORDER BY d.added_at DESC
                """);
            using var r = cmd.ExecuteReader();
            var list = new List<DownloadInfo>();
            while (r.Read())
                if (Deserialize(r.GetString(0)) is { } t)
                    list.Add(new DownloadInfo(t, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                        r.GetInt64(3), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4))));
            return list;
        }
    }

    // ---------- playlists ----------

    public long CreatePlaylist(string name)
    {
        lock (_gate)
        {
            var uid = NewUid();
            Exec("INSERT INTO playlists (name, uid) VALUES ($n, $u)", ("$n", name), ("$u", uid));
            var id = (long)Scalar("SELECT last_insert_rowid()")!;
            Touch(SyncKinds.Playlist, uid);
            return id;
        }
    }

    public void RenamePlaylist(long id, string name)
    {
        lock (_gate)
        {
            Exec("UPDATE playlists SET name = $n WHERE id = $id", ("$n", name), ("$id", id));
            TouchPlaylist(id);
        }
    }

    public void DeletePlaylist(long id)
    {
        lock (_gate)
        {
            TouchPlaylist(id); // before the row goes: the sync record keeps its uid, so the delete reaches other devices
            Exec("DELETE FROM playlists WHERE id = $id", ("$id", id));
        }
    }

    public IReadOnlyList<PlaylistInfo> GetPlaylists()
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT p.id, p.name, COUNT(i.video_id),
                       (SELECT t.json FROM playlist_items f JOIN tracks t ON t.video_id = f.video_id
                        WHERE f.playlist_id = p.id ORDER BY f.position LIMIT 1)
                FROM playlists p LEFT JOIN playlist_items i ON i.playlist_id = p.id
                GROUP BY p.id ORDER BY p.id
                """);
            using var r = cmd.ExecuteReader();
            var list = new List<PlaylistInfo>();
            while (r.Read())
            {
                var first = r.IsDBNull(3) ? null : Deserialize(r.GetString(3));
                list.Add(new PlaylistInfo(r.GetInt64(0), r.GetString(1), r.GetInt32(2), first?.ThumbnailUrl));
            }
            return list;
        }
    }

    /// <summary>Appends many songs in one transaction (playlist import).</summary>
    public void AddManyToPlaylist(long id, IEnumerable<Track> tracks)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var next = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_items WHERE playlist_id = $id", ("$id", id)),
                System.Globalization.CultureInfo.InvariantCulture);
            foreach (var t in tracks.DistinctBy(t => t.VideoId))
            {
                SaveTrack(t);
                Exec("INSERT INTO playlist_items (playlist_id, position, video_id) VALUES ($id, $p, $v)", ("$id", id), ("$p", next++), ("$v", t.VideoId));
            }
            TouchPlaylist(id);
            tx.Commit();
        }
    }

    public void AddToPlaylist(long id, Track t)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            SaveTrack(t);
            Exec("""
                INSERT INTO playlist_items (playlist_id, position, video_id)
                VALUES ($id, (SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_items WHERE playlist_id = $id), $v)
                """, ("$id", id), ("$v", t.VideoId));
            TouchPlaylist(id);
            tx.Commit();
        }
    }

    public void RemoveFromPlaylist(long id, int position) =>
        Rewrite(id, ids => { if (position >= 0 && position < ids.Count) ids.RemoveAt(position); });

    public void MoveInPlaylist(long id, int from, int to) =>
        Rewrite(id, ids =>
        {
            if (from < 0 || from >= ids.Count || to < 0 || to >= ids.Count) return;
            var item = ids[from];
            ids.RemoveAt(from);
            ids.Insert(to, item);
        });

    public IReadOnlyList<Track> GetPlaylistTracks(long id) =>
        QueryTracks("""
            SELECT t.json FROM playlist_items i JOIN tracks t ON t.video_id = i.video_id
            WHERE i.playlist_id = $id ORDER BY i.position
            """, ("$id", id));

    private void Rewrite(long id, Action<List<string>> change)
    {
        lock (_gate)
        {
            var ids = new List<string>();
            using (var cmd = Command("SELECT video_id FROM playlist_items WHERE playlist_id = $id ORDER BY position", ("$id", id)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) ids.Add(r.GetString(0));
            change(ids);
            using var tx = _db.BeginTransaction();
            Exec("DELETE FROM playlist_items WHERE playlist_id = $id", ("$id", id));
            for (var i = 0; i < ids.Count; i++)
                Exec("INSERT INTO playlist_items (playlist_id, position, video_id) VALUES ($id, $p, $v)", ("$id", id), ("$p", i), ("$v", ids[i]));
            TouchPlaylist(id);
            tx.Commit();
        }
    }

    // ---------- prepared stream links (valid for hours; make clicks instant after a restart) ----------

    public void SaveStream(Tune.Core.Streams.CachedStream s) =>
        Exec("""
            INSERT INTO stream_cache (video_id, url, valid_until, available_at, source) VALUES ($v, $u, $until, $at, $src)
            ON CONFLICT(video_id) DO UPDATE SET url = excluded.url, valid_until = excluded.valid_until,
                available_at = excluded.available_at, source = excluded.source
            """, ("$v", s.VideoId), ("$u", s.Url.ToString()), ("$until", s.ValidUntil.ToUnixTimeMilliseconds()),
            ("$at", s.AvailableAt == DateTimeOffset.MinValue ? 0L : s.AvailableAt.ToUnixTimeMilliseconds()), ("$src", s.Source));

    public IReadOnlyList<Tune.Core.Streams.CachedStream> LoadStreams(DateTimeOffset now)
    {
        lock (_gate)
        {
            Exec("DELETE FROM stream_cache WHERE valid_until <= $now", ("$now", now.ToUnixTimeMilliseconds()));
            using var cmd = Command("SELECT video_id, url, valid_until, available_at, source FROM stream_cache");
            using var r = cmd.ExecuteReader();
            var list = new List<Tune.Core.Streams.CachedStream>();
            while (r.Read())
            {
                if (!Uri.TryCreate(r.GetString(1), UriKind.Absolute, out var url)) continue;
                var at = r.GetInt64(3);
                list.Add(new Tune.Core.Streams.CachedStream(r.GetString(0), url,
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                    at == 0 ? DateTimeOffset.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(at), r.GetString(4)));
            }
            return list;
        }
    }

    // ---------- settings ----------

    public string? GetSetting(string key) => Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) as string;

    public void SetSetting(string key, string value)
    {
        Exec("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
        if (SyncedSettings.Contains(key)) Touch(SyncKinds.Setting, key); // Home layout + pins follow the account
    }

    // ---------- helpers ----------

    private void SaveTrack(Track t) =>
        Exec("INSERT INTO tracks (video_id, json) VALUES ($v, $j) ON CONFLICT(video_id) DO UPDATE SET json = excluded.json",
            ("$v", t.VideoId), ("$j", JsonSerializer.Serialize(t, JsonOptions)));

    private static Track? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Track>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private IReadOnlyList<Track> QueryTracks(string sql, params (string, object)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<Track>();
            while (r.Read())
                if (Deserialize(r.GetString(0)) is { } t) list.Add(t);
            return list;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Exec(string sql, params (string, object)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql, params (string, object)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteScalar();
        }
    }
}
