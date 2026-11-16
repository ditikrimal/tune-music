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
class Library(context: Context) : SQLiteOpenHelper(context, "tune.db", null, 4) {
    private val _version = MutableStateFlow(0)
    val version: StateFlow<Int> = _version

    private fun changed() { _version.value++ }

    /** Ticks when the Home layout or pins arrive from another device (Home rebuilds). */
    val homeSynced = MutableStateFlow(0)

    /** Likes, playlists or history changed on this phone: account sync uploads soon after. */
    @Volatile var onLocalChange: (() -> Unit)? = null

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE tracks (video_id TEXT PRIMARY KEY, json TEXT NOT NULL)")
        db.execSQL("CREATE TABLE plays (id INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL, at INTEGER NOT NULL)")
        db.execSQL("CREATE INDEX plays_at ON plays(at)")
        db.execSQL("CREATE TABLE likes (seq INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL UNIQUE)")
        db.execSQL("CREATE TABLE playlists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)")
        db.execSQL("CREATE TABLE playlist_items (playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE, position INTEGER NOT NULL, video_id TEXT NOT NULL, PRIMARY KEY (playlist_id, position))")
        db.execSQL("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        db.execSQL("CREATE TABLE downloads (video_id TEXT PRIMARY KEY, path TEXT, bytes INTEGER NOT NULL DEFAULT 0, added_at INTEGER NOT NULL, smart INTEGER NOT NULL DEFAULT 0)")
        createSync(db, fresh = true)
        createSkips(db)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        // v2: smart downloads are picked (and swapped out) by Tune; smart = 0 means the user asked for it
        if (oldVersion < 2) db.execSQL("ALTER TABLE downloads ADD COLUMN smart INTEGER NOT NULL DEFAULT 0")
        // v3: account sync (same tables as the Windows app)
        if (oldVersion < 3) createSync(db, fresh = false)
        // v4: early skips (a "not now" signal for recommendations)
        if (oldVersion < 4) createSkips(db)
    }

    private fun createSkips(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE IF NOT EXISTS skips (id INTEGER PRIMARY KEY AUTOINCREMENT, video_id TEXT NOT NULL, at INTEGER NOT NULL)")
        db.execSQL("CREATE INDEX IF NOT EXISTS skips_at ON skips(at)")
    }

    // ---------- listening signals (recommendations) ----------

    fun notInterested(): Set<String> = setting(NOT_INTERESTED_KEY).orEmpty().split(',').filter { it.isNotBlank() }.toSet()

    /** "Not interested": the song stays out of Home, quick picks and radios. */
    fun setNotInterested(videoId: String, on: Boolean = true) {
        val ids = setting(NOT_INTERESTED_KEY).orEmpty().split(',').filter { it.isNotBlank() }.toMutableList()
        val had = ids.remove(videoId)
        if (on) ids.add(0, videoId)
        if (had != on) setSetting(NOT_INTERESTED_KEY, ids.take(1000).joinToString(","))
    }

    fun recordSkip(videoId: String, at: Long = System.currentTimeMillis()) {
        writableDatabase.insert("skips", null, ContentValues().apply { put("video_id", videoId); put("at", at) })
    }

    /** Every play since [sinceMs] with its time. */
    fun plays(sinceMs: Long): List<Pair<Track, Long>> = readableDatabase.rawQuery(
        "SELECT p.video_id, p.at, t.json FROM plays p JOIN tracks t ON t.video_id = p.video_id WHERE p.at >= ? ORDER BY p.at", arrayOf(sinceMs.toString())).use { c ->
        val cache = HashMap<String, Track?>()
        buildList { while (c.moveToNext()) cache.getOrPut(c.getString(0)) { Track.fromJson(c.getString(2)) }?.let { add(it to c.getLong(1)) } }
    }

    fun skips(sinceMs: Long): List<Pair<String, Long>> =
        readableDatabase.rawQuery("SELECT video_id, at FROM skips WHERE at >= ?", arrayOf(sinceMs.toString())).use { c ->
            buildList { while (c.moveToNext()) add(c.getString(0) to c.getLong(1)) }
        }

    fun playlistUid(id: Long): String? =
        readableDatabase.rawQuery("SELECT uid FROM playlists WHERE id = ?", arrayOf(id.toString())).use { if (it.moveToFirst()) it.getString(0) else null }

    fun playlistIdByUid(uid: String): Long? =
        readableDatabase.rawQuery("SELECT id FROM playlists WHERE uid = ?", arrayOf(uid)).use { if (it.moveToFirst()) it.getLong(0) else null }

    /** Change tracking for account sync; playlists get an id that means the same on every device. */
    private fun createSync(db: SQLiteDatabase, fresh: Boolean) {
        db.execSQL("CREATE TABLE IF NOT EXISTS sync_items (kind TEXT NOT NULL, key TEXT NOT NULL, updated_at INTEGER NOT NULL, pushed INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (kind, key))")
        db.execSQL("ALTER TABLE playlists ADD COLUMN uid TEXT")
        if (!fresh) db.execSQL("UPDATE playlists SET uid = lower(hex(randomblob(16))) WHERE uid IS NULL")
        db.execSQL("ALTER TABLE plays ADD COLUMN remote INTEGER NOT NULL DEFAULT 0")
        db.execSQL("CREATE INDEX IF NOT EXISTS plays_video_at ON plays(video_id, at)")
    }

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
                touch(this, SyncKinds.PLAYS, syncDay(at))
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    /** Songs you played, each once at its latest play, newest first (History). */
    fun history(max: Int): List<Pair<Track, Long>> = readableDatabase.rawQuery("""
        SELECT t.json, MAX(p.at) AS last FROM plays p JOIN tracks t ON t.video_id = p.video_id
        GROUP BY p.video_id ORDER BY last DESC LIMIT $max""", null).use { c ->
        buildList { while (c.moveToNext()) Track.fromJson(c.getString(0))?.let { add(it to c.getLong(1)) } }
    }

    // by play time, not insert order: plays synced from another device arrive later than they happened
    fun recent(max: Int) = tracks("""
        SELECT t.json FROM (SELECT video_id, MAX(at) AS last, MAX(id) AS lid FROM plays GROUP BY video_id) p
        JOIN tracks t ON t.video_id = p.video_id ORDER BY p.last DESC, p.lid DESC LIMIT $max""")

    fun topTracks(sinceMs: Long, max: Int) = tracks("""
        SELECT t.json FROM plays p JOIN tracks t ON t.video_id = p.video_id WHERE p.at >= ?
        GROUP BY p.video_id ORDER BY COUNT(*) DESC, MAX(p.at) DESC, MAX(p.id) DESC LIMIT $max""", sinceMs.toString())

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
                touch(this, SyncKinds.LIKE, t.videoId)
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    fun liked() = tracks("SELECT t.json FROM likes l JOIN tracks t ON t.video_id = l.video_id ORDER BY l.seq DESC")

    // ---------- playlists ----------

    fun createPlaylist(name: String): Long {
        val uid = newUid()
        val id = writableDatabase.insert("playlists", null, ContentValues().apply { put("name", name); put("uid", uid) })
        touch(writableDatabase, SyncKinds.PLAYLIST, uid)
        changed()
        return id
    }

    fun renamePlaylist(id: Long, name: String) {
        writableDatabase.update("playlists", ContentValues().apply { put("name", name) }, "id = ?", arrayOf(id.toString()))
        touchPlaylist(writableDatabase, id); changed()
    }

    fun deletePlaylist(id: Long) {
        touchPlaylist(writableDatabase, id) // before the row goes: the sync record keeps its uid, so the delete reaches other devices
        writableDatabase.delete("playlists", "id = ?", arrayOf(id.toString())); changed()
    }

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
                touchPlaylist(this, id)
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
                touchPlaylist(this, id)
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
                touchPlaylist(this, id)
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
        if (key in SYNCED_SETTINGS) touch(writableDatabase, SyncKinds.SETTING, key) // Home layout + pins follow the account
        changed()
    }

    /** A Home-layout / pins change from another device. Newer wins. */
    fun applySetting(key: String, value: String, updatedAt: Long): Boolean {
        if (key !in SYNCED_SETTINGS || updatedAt <= syncUpdatedAt(SyncKinds.SETTING, key)) return false
        writableDatabase.run {
            beginTransaction()
            try {
                insertWithOnConflict("settings", null, ContentValues().apply { put("key", key); put("value", value) }, SQLiteDatabase.CONFLICT_REPLACE)
                applied(this, SyncKinds.SETTING, key, updatedAt)
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        homeSynced.value++
        return true
    }

    // ---------- downloads ----------

    data class Download(val track: Track, val path: String?, val bytes: Long, val smart: Boolean = false)

    /** Adds songs to the download list. A song the user asks for stops being "smart" (Tune won't remove it). */
    fun addDownloads(list: List<Track>, smart: Boolean = false) {
        writableDatabase.run {
            beginTransaction()
            try {
                var at = System.currentTimeMillis()
                for (t in list) {
                    saveTrack(this, t)
                    insertWithOnConflict("downloads", null, ContentValues().apply {
                        put("video_id", t.videoId); put("added_at", at--); put("smart", if (smart) 1 else 0)
                    }, SQLiteDatabase.CONFLICT_IGNORE)
                    if (!smart) update("downloads", ContentValues().apply { put("smart", 0) }, "video_id = ?", arrayOf(t.videoId))
                }
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        changed()
    }

    /** Play count and last play per song since [sinceMs] (smart downloads, offline mix). */
    fun playStats(sinceMs: Long): List<Triple<Track, Int, Long>> = readableDatabase.rawQuery("""
        SELECT t.json, COUNT(*), MAX(p.at) FROM plays p JOIN tracks t ON t.video_id = p.video_id
        WHERE p.at >= ? GROUP BY p.video_id""", arrayOf(sinceMs.toString())).use { c ->
        buildList { while (c.moveToNext()) Track.fromJson(c.getString(0))?.let { add(Triple(it, c.getInt(1), c.getLong(2))) } }
    }

    fun setDownloaded(videoId: String, path: String, bytes: Long) {
        writableDatabase.update("downloads", ContentValues().apply { put("path", path); put("bytes", bytes) }, "video_id = ?", arrayOf(videoId)); changed()
    }

    fun removeDownload(videoId: String) { writableDatabase.delete("downloads", "video_id = ?", arrayOf(videoId)); changed() }

    fun downloads(): List<Download> = readableDatabase.rawQuery(
        "SELECT t.json, d.path, d.bytes, d.smart FROM downloads d JOIN tracks t ON t.video_id = d.video_id ORDER BY d.added_at DESC", null).use { c ->
        buildList { while (c.moveToNext()) Track.fromJson(c.getString(0))?.let { add(Download(it, c.getString(1), c.getLong(2), c.getInt(3) == 1)) } }
    }

    // ---------- account sync (same rules as the Windows app's LibraryStore.Sync) ----------
    // Every like, playlist edit and play marks sync_items with the time of the change. The sync engine uploads
    // unpushed rows and applies remote changes only when newer (last change wins per song / per playlist).

    private fun newUid() = java.util.UUID.randomUUID().toString().replace("-", "")

    // (no "ON CONFLICT … DO UPDATE": Android 9 ships SQLite 3.22, which doesn't have it)
    private fun setSyncRow(db: SQLiteDatabase, kind: String, key: String, updatedAt: Long, pushed: Boolean) {
        val values = ContentValues().apply { put("kind", kind); put("key", key); put("updated_at", updatedAt); put("pushed", if (pushed) 1 else 0) }
        db.insertWithOnConflict("sync_items", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    private fun touch(db: SQLiteDatabase, kind: String, key: String) {
        val old = db.rawQuery("SELECT updated_at FROM sync_items WHERE kind = ? AND key = ?", arrayOf(kind, key)).use { if (it.moveToFirst()) it.getLong(0) else 0 }
        setSyncRow(db, kind, key, maxOf(System.currentTimeMillis(), old + 1), pushed = false)
        onLocalChange?.invoke()
    }

    private fun touchPlaylist(db: SQLiteDatabase, id: Long) {
        db.rawQuery("SELECT uid FROM playlists WHERE id = ?", arrayOf(id.toString())).use { if (it.moveToFirst()) it.getString(0) else null }
            ?.let { touch(db, SyncKinds.PLAYLIST, it) }
    }

    fun pendingSync(): List<SyncItem> = readableDatabase.rawQuery("SELECT kind, key, updated_at FROM sync_items WHERE pushed = 0 ORDER BY updated_at", null).use { c ->
        buildList { while (c.moveToNext()) add(SyncItem(c.getString(0), c.getString(1), c.getLong(2))) }
    }

    /** Uploaded — unless it changed again meanwhile (then it stays pending). */
    fun markSynced(item: SyncItem) {
        writableDatabase.execSQL("UPDATE sync_items SET pushed = 1 WHERE kind = ? AND key = ? AND updated_at = ?", arrayOf<Any>(item.kind, item.key, item.updatedAt))
    }

    fun syncUpdatedAt(kind: String, key: String): Long =
        readableDatabase.rawQuery("SELECT updated_at FROM sync_items WHERE kind = ? AND key = ?", arrayOf(kind, key)).use { if (it.moveToFirst()) it.getLong(0) else 0 }

    /** Signing in: everything on the phone is uploaded; untouched items get an old date so account changes win. */
    fun markAllForSync() {
        writableDatabase.run {
            beginTransaction()
            try {
                execSQL("UPDATE sync_items SET pushed = 0")
                execSQL("INSERT OR IGNORE INTO sync_items (kind, key, updated_at) SELECT 'like', video_id, 1 FROM likes")
                execSQL("INSERT OR IGNORE INTO sync_items (kind, key, updated_at) SELECT 'playlist', uid, 1 FROM playlists")
                execSQL("INSERT OR IGNORE INTO sync_items (kind, key, updated_at) SELECT 'setting', key, 1 FROM settings WHERE key IN ('home.layout', 'speeddial.pins', 'taste.notInterested')")
                execSQL("INSERT OR IGNORE INTO sync_items (kind, key, updated_at) SELECT 'plays', strftime('%Y%m%d', at / 1000, 'unixepoch'), 1 FROM plays WHERE remote = 0 GROUP BY 2")
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
    }

    fun track(videoId: String): Track? = tracks("SELECT json FROM tracks WHERE video_id = ?", videoId).firstOrNull()

    /** Null when the playlist was deleted. */
    fun playlistByUid(uid: String): SyncPlaylist? {
        val row = readableDatabase.rawQuery("SELECT id, name FROM playlists WHERE uid = ?", arrayOf(uid)).use { if (it.moveToFirst()) it.getLong(0) to it.getString(1) else null }
            ?: return null
        return SyncPlaylist(uid, row.second, playlistTracks(row.first))
    }

    fun allPlaylistsForSync(): List<SyncPlaylist> =
        readableDatabase.rawQuery("SELECT uid FROM playlists ORDER BY id", null).use { c -> buildList { while (c.moveToNext()) add(c.getString(0)) } }
            .mapNotNull { playlistByUid(it) }

    /** This phone's own plays on one UTC day. */
    fun localPlays(day: String): List<Pair<Track, Long>> = readableDatabase.rawQuery("""
        SELECT t.json, p.at FROM plays p JOIN tracks t ON t.video_id = p.video_id
        WHERE p.remote = 0 AND strftime('%Y%m%d', p.at / 1000, 'unixepoch') = ? ORDER BY p.at""", arrayOf(day)).use { c ->
        buildList { while (c.moveToNext()) Track.fromJson(c.getString(0))?.let { add(it to c.getLong(1)) } }
    }

    private fun applied(db: SQLiteDatabase, kind: String, key: String, updatedAt: Long) = setSyncRow(db, kind, key, updatedAt, pushed = true)

    /** A like/unlike from the account, applied only if newer. True when the phone's likes changed. */
    fun applyLike(videoId: String, track: Track?, liked: Boolean, updatedAt: Long): Boolean {
        if (updatedAt <= syncUpdatedAt(SyncKinds.LIKE, videoId)) return false
        if (liked && track == null) return false
        val was = isLiked(videoId)
        writableDatabase.run {
            beginTransaction()
            try {
                if (!liked) delete("likes", "video_id = ?", arrayOf(videoId))
                else if (!was) { saveTrack(this, track!!); insert("likes", null, ContentValues().apply { put("video_id", videoId) }) }
                applied(this, SyncKinds.LIKE, videoId, updatedAt)
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        return was != liked
    }

    /** A whole playlist from the account (or its deletion), applied only if newer. */
    fun applyPlaylist(uid: String, name: String, list: List<Track>, deleted: Boolean, updatedAt: Long): Boolean {
        if (updatedAt <= syncUpdatedAt(SyncKinds.PLAYLIST, uid)) return false
        writableDatabase.run {
            beginTransaction()
            try {
                var id = rawQuery("SELECT id FROM playlists WHERE uid = ?", arrayOf(uid)).use { if (it.moveToFirst()) it.getLong(0) else null }
                if (deleted) id?.let { delete("playlists", "id = ?", arrayOf(it.toString())) }
                else {
                    if (id == null) id = insert("playlists", null, ContentValues().apply { put("name", name); put("uid", uid) })
                    else update("playlists", ContentValues().apply { put("name", name) }, "id = ?", arrayOf(id.toString()))
                    replaceItems(this, id!!, list)
                }
                applied(this, SyncKinds.PLAYLIST, uid, updatedAt)
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        return true
    }

    /** First sync here: a phone playlist with the same name as one in the account becomes that one, songs merged. */
    fun mergeIntoRemote(localUid: String, remoteUid: String, remoteTracks: List<Track>): Boolean {
        val id = readableDatabase.rawQuery("SELECT id FROM playlists WHERE uid = ?", arrayOf(localUid)).use { if (it.moveToFirst()) it.getLong(0) else null } ?: return false
        val merged = (remoteTracks + playlistTracks(id)).distinctBy { it.videoId }
        writableDatabase.run {
            beginTransaction()
            try {
                update("playlists", ContentValues().apply { put("uid", remoteUid) }, "id = ?", arrayOf(id.toString()))
                delete("sync_items", "kind = 'playlist' AND key = ?", arrayOf(localUid))
                replaceItems(this, id, merged)
                touch(this, SyncKinds.PLAYLIST, remoteUid)
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        return true
    }

    private fun replaceItems(db: SQLiteDatabase, id: Long, list: List<Track>) {
        db.delete("playlist_items", "playlist_id = ?", arrayOf(id.toString()))
        list.distinctBy { it.videoId }.forEachIndexed { i, t ->
            saveTrack(db, t)
            db.insert("playlist_items", null, ContentValues().apply { put("playlist_id", id); put("position", i); put("video_id", t.videoId) })
        }
    }

    /** Plays from another device; ones already here are skipped. Returns how many were new. */
    fun applyPlays(plays: List<Pair<Track, Long>>): Int {
        var added = 0
        writableDatabase.run {
            beginTransaction()
            try {
                for ((t, at) in plays) {
                    val exists = rawQuery("SELECT 1 FROM plays WHERE video_id = ? AND at = ?", arrayOf(t.videoId, at.toString())).use { it.moveToFirst() }
                    if (exists) continue
                    saveTrack(this, t)
                    insert("plays", null, ContentValues().apply { put("video_id", t.videoId); put("at", at); put("remote", 1) })
                    added++
                }
                setTransactionSuccessful()
            } finally { endTransaction() }
        }
        return added
    }

    /** Sync brought changes from another device: screens reload. */
    fun syncApplied() = changed()
}

object SyncKinds {
    const val LIKE = "like"
    const val PLAYLIST = "playlist"
    const val PLAYS = "plays"
    const val SETTING = "setting"
}

/** Settings that follow the account to every device. */
val SYNCED_SETTINGS = setOf("home.layout", "speeddial.pins", NOT_INTERESTED_KEY)

/** Songs you said you're not interested in (comma-separated ids, newest first; synced). */
const val NOT_INTERESTED_KEY = "taste.notInterested"

data class SyncItem(val kind: String, val key: String, val updatedAt: Long)

data class SyncPlaylist(val uid: String, val name: String, val tracks: List<Track>)

fun syncDay(ms: Long): String = java.text.SimpleDateFormat("yyyyMMdd", java.util.Locale.US)
    .apply { timeZone = java.util.TimeZone.getTimeZone("UTC") }.format(java.util.Date(ms))
