package app.tune.data

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

data class PlaylistInfo(val id: Long, val name: String, val count: Int, val thumbnail: String?)

/**
 * Everything Tune remembers on the phone: play history, likes, playlists, settings, downloads.
 * Same tables as the Windows app's LibraryStore. [version] ticks on every change so screens refresh.
 */
class Library(context: Context) : SQLiteOpenHelper(context, "tune.db", null, 1) {
    private val _version = MutableStateFlow(0)
    val version: StateFlow<Int> = _version

    private fun changed() { _version.value++ }

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE tracks (video_id TEXT PRIMARY KEY, json TEXT NOT NULL)")
        db.execSQL("CREATE TABLE plays (id INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL, at INTEGER NOT NULL)")
        db.execSQL("CREATE INDEX plays_at ON plays(at)")
        db.execSQL("CREATE TABLE likes (seq INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL UNIQUE)")
        db.execSQL("CREATE TABLE playlists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)")
        db.execSQL("CREATE TABLE playlist_items (playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE, position INTEGER NOT NULL, video_id TEXT NOT NULL, PRIMARY KEY (playlist_id, position))")
        db.execSQL("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        db.execSQL("CREATE TABLE downloads (video_id TEXT PRIMARY KEY, path TEXT, bytes INTEGER NOT NULL DEFAULT 0, added_at INTEGER NOT NULL)")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit

    override fun onConfigure(db: SQLiteDatabase) {
        db.setForeignKeyConstraintsEnabled(true)
        db.enableWriteAheadLogging()
    }

    private fun saveTrack(db: SQLiteDatabase, t: Track) {
        db.insertWithOnConflict("tracks", null, ContentValues().apply {
            put("video_id", t.videoId); put("json", t.toJson().toString())
        }, SQLiteDatabase.CONFLICT_REPLACE)
    }

    private fun tracks(sql: String, vararg args: String): List<Track> = readableDatabase.rawQuery(sql, args).use { c ->
        buildList { while (c.moveToNext()) Track.fromJson(c.getString(0))?.let { add(it) } }
    }

    // ---------- history ----------

    fun recordPlay(t: Track, at: Long = System.currentTimeMillis()) {
        writableDatabase.run {
            beginTransaction()
            try {
                saveTrack(this, t)
                insert("plays", null, ContentValues().apply { put("video_id", t.videoId); put("at", at) })
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    fun recent(max: Int) = tracks("""
        SELECT t.json FROM (SELECT video_id, MAX(id) AS last FROM plays GROUP BY video_id) p
        JOIN tracks t ON t.video_id = p.video_id ORDER BY p.last DESC LIMIT $max""")

    fun topTracks(sinceMs: Long, max: Int) = tracks("""
        SELECT t.json FROM plays p JOIN tracks t ON t.video_id = p.video_id WHERE p.at >= ?
        GROUP BY p.video_id ORDER BY COUNT(*) DESC, MAX(p.id) DESC LIMIT $max""", sinceMs.toString())

    fun topArtists(sinceMs: Long, max: Int): List<ArtistRef> {
        val counts = readableDatabase.rawQuery(
            "SELECT t.json, COUNT(*) FROM plays p JOIN tracks t ON t.video_id = p.video_id WHERE p.at >= ? GROUP BY p.video_id",
            arrayOf(sinceMs.toString())).use { c ->
            buildList { while (c.moveToNext()) Track.fromJson(c.getString(0))?.let { add(it to c.getInt(1)) } }
        }
        return counts.flatMap { (t, n) -> t.artists.map { it to n } }
            .groupBy { it.first.browseId ?: it.first.name }
            .map { (_, g) -> g.first().first to g.sumOf { it.second } }
            .sortedByDescending { it.second }.take(max).map { it.first }
    }

    // ---------- likes ----------

    fun isLiked(videoId: String): Boolean =
        readableDatabase.rawQuery("SELECT 1 FROM likes WHERE video_id = ?", arrayOf(videoId)).use { it.moveToFirst() }

    fun setLiked(t: Track, liked: Boolean) {
        writableDatabase.run {
            beginTransaction()
            try {
                delete("likes", "video_id = ?", arrayOf(t.videoId))
                if (liked) {
                    saveTrack(this, t)
                    insert("likes", null, ContentValues().apply { put("video_id", t.videoId) })
                }
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    fun liked() = tracks("SELECT t.json FROM likes l JOIN tracks t ON t.video_id = l.video_id ORDER BY l.seq DESC")

    // ---------- playlists ----------

    fun createPlaylist(name: String): Long = writableDatabase.insert("playlists", null, ContentValues().apply { put("name", name) }).also { changed() }

    fun renamePlaylist(id: Long, name: String) {
        writableDatabase.update("playlists", ContentValues().apply { put("name", name) }, "id = ?", arrayOf(id.toString())); changed()
    }

    fun deletePlaylist(id: Long) { writableDatabase.delete("playlists", "id = ?", arrayOf(id.toString())); changed() }

    fun playlists(): List<PlaylistInfo> = readableDatabase.rawQuery("""
        SELECT p.id, p.name, COUNT(i.video_id),
          (SELECT t.json FROM playlist_items i2 JOIN tracks t ON t.video_id = i2.video_id WHERE i2.playlist_id = p.id ORDER BY i2.position LIMIT 1)
        FROM playlists p LEFT JOIN playlist_items i ON i.playlist_id = p.id GROUP BY p.id ORDER BY p.id""", null).use { c ->
        buildList { while (c.moveToNext()) add(PlaylistInfo(c.getLong(0), c.getString(1), c.getInt(2), Track.fromJson(c.getString(3))?.thumbnail)) }
    }

    fun playlistTracks(id: Long) = tracks("""
        SELECT t.json FROM playlist_items i JOIN tracks t ON t.video_id = i.video_id WHERE i.playlist_id = ? ORDER BY i.position""", id.toString())

    fun addToPlaylist(id: Long, t: Track) {
        writableDatabase.run {
            beginTransaction()
            try {
                saveTrack(this, t)
                val next = rawQuery("SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_items WHERE playlist_id = ?", arrayOf(id.toString()))
                    .use { it.moveToFirst(); it.getInt(0) }
                insert("playlist_items", null, ContentValues().apply { put("playlist_id", id); put("position", next); put("video_id", t.videoId) })
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    /** Appends many songs in one transaction (playlist import). */
    fun addManyToPlaylist(id: Long, list: List<Track>) {
        writableDatabase.run {
            beginTransaction()
            try {
                var next = rawQuery("SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_items WHERE playlist_id = ?", arrayOf(id.toString()))
                    .use { it.moveToFirst(); it.getInt(0) }
                for (t in list.distinctBy { it.videoId }) {
                    saveTrack(this, t)
                    insert("playlist_items", null, ContentValues().apply { put("playlist_id", id); put("position", next++); put("video_id", t.videoId) })
                }
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    fun removeFromPlaylist(id: Long, index: Int) = rewritePlaylist(id) { it.apply { if (index in indices) removeAt(index) } }

    fun moveInPlaylist(id: Long, from: Int, to: Int) = rewritePlaylist(id) { list ->
        list.apply { if (from in indices && to in indices) add(to, removeAt(from)) }
    }

    private fun rewritePlaylist(id: Long, edit: (MutableList<String>) -> MutableList<String>) {
        val ids = readableDatabase.rawQuery("SELECT video_id FROM playlist_items WHERE playlist_id = ? ORDER BY position", arrayOf(id.toString()))
            .use { c -> buildList { while (c.moveToNext()) add(c.getString(0)) }.toMutableList() }
        val updated = edit(ids)
        writableDatabase.run {
            beginTransaction()
            try {
                delete("playlist_items", "playlist_id = ?", arrayOf(id.toString()))
                updated.forEachIndexed { i, v -> insert("playlist_items", null, ContentValues().apply { put("playlist_id", id); put("position", i); put("video_id", v) }) }
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    // ---------- settings ----------

    fun setting(key: String): String? =
        readableDatabase.rawQuery("SELECT value FROM settings WHERE key = ?", arrayOf(key)).use { if (it.moveToFirst()) it.getString(0) else null }

    fun setSetting(key: String, value: String) {
        writableDatabase.insertWithOnConflict("settings", null, ContentValues().apply { put("key", key); put("value", value) }, SQLiteDatabase.CONFLICT_REPLACE)
        changed()
    }

    // ---------- downloads ----------

    data class Download(val track: Track, val path: String?, val bytes: Long)

    fun addDownloads(list: List<Track>) {
        writableDatabase.run {
            beginTransaction()
            try {
                var at = System.currentTimeMillis()
                for (t in list) {
                    saveTrack(this, t)
                    insertWithOnConflict("downloads", null, ContentValues().apply { put("video_id", t.videoId); put("added_at", at--) }, SQLiteDatabase.CONFLICT_IGNORE)
                }
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    fun setDownloaded(videoId: String, path: String, bytes: Long) {
        writableDatabase.update("downloads", ContentValues().apply { put("path", path); put("bytes", bytes) }, "video_id = ?", arrayOf(videoId)); changed()
    }

    fun removeDownload(videoId: String) { writableDatabase.delete("downloads", "video_id = ?", arrayOf(videoId)); changed() }

    fun downloads(): List<Download> = readableDatabase.rawQuery(
        "SELECT t.json, d.path, d.bytes FROM downloads d JOIN tracks t ON t.video_id = d.video_id ORDER BY d.added_at DESC", null).use { c ->
        buildList { while (c.moveToNext()) Track.fromJson(c.getString(0))?.let { add(Download(it, c.getString(1), c.getLong(2))) } }
    }
}
