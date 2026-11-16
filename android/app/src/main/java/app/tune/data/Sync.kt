package app.tune.data

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import okhttp3.FormBody
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject

/**
 * Account sync, the same design as the Windows app (Tune.Core/Sync): Google sign-in through Firebase Auth's REST
 * API, and the user's likes / playlists / history as Firestore documents under users/{uid}:
 *   likes/{videoId}            liked, track, updatedAt
 *   playlists/{uid}            name, tracks (JSON), deleted, updatedAt
 *   plays/{device}_{yyyyMMdd}  device, day, plays [[id, ms]…], tracks, updatedAt
 * Every write stamps serverAt (server clock); pulls page by (serverAt, name). Newer updatedAt wins per item.
 */
data class SyncConfig(val projectId: String, val apiKey: String, val webClientId: String?, val emulatorHost: String? = null) {
    val authBase get() = emulatorHost?.let { "http://$it:9099/identitytoolkit.googleapis.com" } ?: "https://identitytoolkit.googleapis.com"
    val tokenBase get() = emulatorHost?.let { "http://$it:9099/securetoken.googleapis.com" } ?: "https://securetoken.googleapis.com"
    val firestoreBase get() = emulatorHost?.let { "http://$it:8080/v1" } ?: "https://firestore.googleapis.com/v1"
    val documentsRoot get() = "projects/$projectId/databases/(default)/documents"

    companion object {
        fun of(projectId: String, apiKey: String, webClientId: String, emulator: String = ""): SyncConfig? =
            if (projectId.isBlank() || apiKey.isBlank()) null
            else SyncConfig(projectId, apiKey, webClientId.ifBlank { null }, emulator.ifBlank { null })
    }
}

class SyncException(message: String, val status: Int = 0) : Exception(message)

data class AuthSession(val uid: String, val email: String?, val name: String?, val photo: String?,
                       val idToken: String, val refreshToken: String, val expiresAt: Long)

private val JSON = "application/json".toMediaType()

private fun OkHttpClient.postJson(url: String, body: JSONObject, token: String? = null): Pair<Int, String> {
    val req = Request.Builder().url(url).post(body.toString().toRequestBody(JSON)).apply { token?.let { header("Authorization", "Bearer $it") } }.build()
    return newCall(req).execute().use { it.code to it.body.string() }
}

private fun errorMessage(text: String, code: Int): String = runCatching {
    val node = if (text.trimStart().startsWith("[")) JSONArray(text).getJSONObject(0) else JSONObject(text)
    node.getJSONObject("error").getString("message")
}.getOrNull() ?: "HTTP $code"

class FirebaseAuth(private val http: OkHttpClient, private val config: SyncConfig) {
    fun signInWithGoogle(googleIdToken: String): AuthSession {
        val body = JSONObject()
            .put("postBody", "id_token=${java.net.URLEncoder.encode(googleIdToken, "UTF-8")}&providerId=google.com")
            .put("requestUri", "http://localhost").put("returnIdpCredential", true).put("returnSecureToken", true)
        val (code, text) = http.postJson("${config.authBase}/v1/accounts:signInWithIdp?key=${config.apiKey}", body)
        if (code !in 200..299) throw SyncException(errorMessage(text, code), code)
        val o = JSONObject(text)
        return AuthSession(o.getString("localId"), o.optStringOrNull("email"), o.optStringOrNull("displayName") ?: o.optStringOrNull("fullName"),
            o.optStringOrNull("photoUrl"), o.getString("idToken"), o.getString("refreshToken"), expiry(o.optString("expiresIn")))
    }

    fun refresh(old: AuthSession): AuthSession {
        val req = Request.Builder().url("${config.tokenBase}/v1/token?key=${config.apiKey}")
            .post(FormBody.Builder().add("grant_type", "refresh_token").add("refresh_token", old.refreshToken).build()).build()
        val (code, text) = http.newCall(req).execute().use { it.code to it.body.string() }
        if (code !in 200..299) throw SyncException(errorMessage(text, code), code)
        val o = JSONObject(text)
        return old.copy(idToken = o.getString("id_token"), refreshToken = o.optStringOrNull("refresh_token") ?: old.refreshToken, expiresAt = expiry(o.optString("expires_in")))
    }

    private fun expiry(seconds: String?) = System.currentTimeMillis() + (seconds?.toLongOrNull() ?: 3600) * 1000
}

data class RemoteDoc(val collection: String, val id: String, val fields: Map<String, Any?>) {
    fun str(k: String) = fields[k] as? String
    fun long(k: String) = fields[k] as? Long ?: 0L
    fun bool(k: String) = fields[k] == true
}

class RemotePage(val docs: List<RemoteDoc>, val cursor: String?)

interface SyncRemote {
    fun push(docs: List<RemoteDoc>)
    fun pull(collection: String, cursor: String?, limit: Int): RemotePage
}

class FirestoreRemote(private val http: OkHttpClient, private val config: SyncConfig, uid: String,
                      private val token: (force: Boolean) -> String) : SyncRemote {
    private val userPath = "${config.documentsRoot}/users/$uid"

    override fun push(docs: List<RemoteDoc>) {
        docs.chunked(400).forEach { chunk ->
            val writes = JSONArray()
            chunk.forEach { d ->
                writes.put(JSONObject()
                    .put("update", JSONObject().put("name", "$userPath/${d.collection}/${d.id}").put("fields", encode(d.fields)))
                    .put("updateTransforms", JSONArray().put(JSONObject().put("fieldPath", "serverAt").put("setToServerValue", "REQUEST_TIME"))))
            }
            send("${config.firestoreBase}/${config.documentsRoot}:commit", JSONObject().put("writes", writes))
        }
    }

    override fun pull(collection: String, cursor: String?, limit: Int): RemotePage {
        val query = JSONObject()
            .put("from", JSONArray().put(JSONObject().put("collectionId", collection)))
            .put("orderBy", JSONArray()
                .put(JSONObject().put("field", JSONObject().put("fieldPath", "serverAt")).put("direction", "ASCENDING"))
                .put(JSONObject().put("field", JSONObject().put("fieldPath", "__name__")).put("direction", "ASCENDING")))
            .put("limit", limit)
        cursor?.split('|', limit = 2)?.takeIf { it.size == 2 }?.let { (ts, name) ->
            query.put("startAt", JSONObject().put("values", JSONArray()
                .put(JSONObject().put("timestampValue", ts)).put(JSONObject().put("referenceValue", name))).put("before", false))
        }
        val reply = JSONArray(send("${config.firestoreBase}/$userPath:runQuery", JSONObject().put("structuredQuery", query)))
        val docs = ArrayList<RemoteDoc>()
        var next = cursor
        for (i in 0 until reply.length()) {
            val doc = reply.getJSONObject(i).optJSONObject("document") ?: continue
            val name = doc.getString("name")
            val fields = doc.optJSONObject("fields") ?: JSONObject()
            docs += RemoteDoc(collection, name.substringAfterLast('/'), decode(fields))
            fields.optJSONObject("serverAt")?.optStringOrNull("timestampValue")?.let { next = "$it|$name" }
        }
        return RemotePage(docs, next)
    }

    private fun send(url: String, body: JSONObject): String {
        var (code, text) = http.postJson(url, body, token(false))
        if (code == 401) http.postJson(url, body, token(true)).let { code = it.first; text = it.second } // expired early: refresh once
        if (code !in 200..299) throw SyncException(errorMessage(text, code), code)
        return text
    }

    private fun encode(fields: Map<String, Any?>) = JSONObject().apply {
        fields.forEach { (k, v) ->
            put(k, when (v) {
                is String -> JSONObject().put("stringValue", v)
                is Long, is Int -> JSONObject().put("integerValue", v.toString())
                is Boolean -> JSONObject().put("booleanValue", v)
                else -> JSONObject().put("nullValue", JSONObject.NULL)
            })
        }
    }

    private fun decode(fields: JSONObject): Map<String, Any?> = buildMap {
        for (k in fields.keys()) {
            val v = fields.optJSONObject(k) ?: continue
            put(k, when {
                v.has("stringValue") -> v.getString("stringValue")
                v.has("integerValue") -> v.getString("integerValue").toLongOrNull()
                v.has("booleanValue") -> v.getBoolean("booleanValue")
                v.has("timestampValue") -> v.getString("timestampValue")
                else -> null
            })
        }
    }
}

object SyncFormat {
    fun tracksJson(list: List<Track>) = JSONArray().apply { list.forEach { put(it.toJson()) } }.toString()

    fun parseTracks(json: String?): List<Track> = runCatching {
        val a = JSONArray(json ?: return emptyList())
        (0 until a.length()).mapNotNull { a.optJSONObject(it)?.let(Track::fromJson) }
    }.getOrDefault(emptyList())

    fun playsJson(plays: List<Pair<Track, Long>>) = JSONArray().apply { plays.forEach { (t, at) -> put(JSONArray().put(t.videoId).put(at)) } }.toString()

    fun parsePlays(json: String?): List<Pair<String, Long>> = runCatching {
        val a = JSONArray(json ?: return emptyList())
        (0 until a.length()).mapNotNull { i -> a.optJSONArray(i)?.takeIf { it.length() == 2 }?.let { it.getString(0) to it.getLong(1) } }
    }.getOrDefault(emptyList())
}

data class SyncResult(val pulled: Int, val pushed: Int, val changedHere: Boolean)

/** One round: pull other devices' changes (newer wins; history merges), then push this phone's. */
class SyncEngine(private val lib: Library, private val remote: SyncRemote, private val deviceId: String) {
    fun sync(): SyncResult {
        var pulled = 0
        var changed = false

        val playlists = pullAll(PLAYLISTS)
        if (playlists.initial) changed = mergeSameName(playlists.docs) or changed
        for (d in playlists.docs)
            changed = lib.applyPlaylist(d.id, d.str("name") ?: "Playlist", SyncFormat.parseTracks(d.str("tracks")), d.bool("deleted"), d.long("updatedAt")) or changed
        pulled += playlists.docs.size; playlists.commit()

        val likes = pullAll(LIKES)
        for (d in likes.docs) {
            val track = d.str("track")?.let { runCatching { Track.fromJson(JSONObject(it)) }.getOrNull() }
            changed = lib.applyLike(d.id, track, d.bool("liked"), d.long("updatedAt")) or changed
        }
        pulled += likes.docs.size; likes.commit()

        val plays = pullAll(PLAYS)
        for (d in plays.docs) {
            if (d.str("device") == deviceId) continue
            val tracks = SyncFormat.parseTracks(d.str("tracks")).associateBy { it.videoId }
            val list = SyncFormat.parsePlays(d.str("plays")).mapNotNull { (id, at) -> tracks[id]?.let { it to at } }
            changed = (lib.applyPlays(list) > 0) or changed
        }
        pulled += plays.docs.size; plays.commit()

        val settings = pullAll(SETTINGS)
        for (d in settings.docs) d.str("value")?.let { changed = lib.applySetting(d.id, it, d.long("updatedAt")) or changed }
        pulled += settings.docs.size; settings.commit()

        val pending = lib.pendingSync()
        val docs = pending.mapNotNull { build(it) }
        if (docs.isNotEmpty()) remote.push(docs)
        pending.forEach { lib.markSynced(it) }

        if (changed) lib.syncApplied()
        return SyncResult(pulled, docs.size, changed)
    }

    private fun build(item: SyncItem): RemoteDoc? = when (item.kind) {
        SyncKinds.LIKE -> {
            val liked = lib.isLiked(item.key)
            val track = lib.track(item.key)
            if (liked && track == null) null
            else RemoteDoc(LIKES, item.key, buildMap {
                put("liked", liked); put("updatedAt", item.updatedAt)
                track?.let { put("track", it.toJson().toString()) }
            })
        }
        SyncKinds.PLAYLIST -> {
            val p = lib.playlistByUid(item.key)
            RemoteDoc(PLAYLISTS, item.key,
                if (p == null) mapOf("deleted" to true, "name" to "", "tracks" to "[]", "updatedAt" to item.updatedAt)
                else mapOf("deleted" to false, "name" to p.name, "tracks" to SyncFormat.tracksJson(p.tracks), "updatedAt" to item.updatedAt))
        }
        SyncKinds.PLAYS -> {
            val plays = lib.localPlays(item.key)
            if (plays.isEmpty()) null
            else RemoteDoc(PLAYS, "${deviceId}_${item.key}", mapOf(
                "device" to deviceId, "day" to item.key, "plays" to SyncFormat.playsJson(plays),
                "tracks" to SyncFormat.tracksJson(plays.map { it.first }.distinctBy { it.videoId }), "updatedAt" to item.updatedAt))
        }
        SyncKinds.SETTING -> lib.setting(item.key)?.let { RemoteDoc(SETTINGS, item.key, mapOf("value" to it, "updatedAt" to item.updatedAt)) }
        else -> null
    }

    /** First sync here: "Songs to Listen" imported on both devices becomes one playlist, not two. */
    private fun mergeSameName(remoteDocs: List<RemoteDoc>): Boolean {
        val remoteIds = remoteDocs.map { it.id }.toSet()
        val live = remoteDocs.filter { !it.bool("deleted") }.toMutableList()
        var merged = false
        for (local in lib.allPlaylistsForSync().filter { it.uid !in remoteIds }) {
            val match = live.firstOrNull { it.str("name").equals(local.name, ignoreCase = true) && lib.playlistByUid(it.id) == null } ?: continue
            merged = lib.mergeIntoRemote(local.uid, match.id, SyncFormat.parseTracks(match.str("tracks"))) or merged
            live.remove(match)
        }
        return merged
    }

    private class Pulled(val docs: List<RemoteDoc>, val initial: Boolean, val commit: () -> Unit)

    private fun pullAll(collection: String): Pulled {
        val key = "sync.cursor.$collection"
        val start = lib.setting(key)?.ifBlank { null }
        var cursor = start
        val all = ArrayList<RemoteDoc>()
        while (true) {
            val page = remote.pull(collection, cursor, PAGE)
            all += page.docs
            cursor = page.cursor
            if (page.docs.size < PAGE) break
        }
        val end = cursor
        return Pulled(all, start == null, { if (end != null) lib.setSetting(key, end) })
    }

    companion object {
        const val LIKES = "likes"; const val PLAYLISTS = "playlists"; const val PLAYS = "plays"; const val SETTINGS = "settings"
        private const val PAGE = 300
        fun resetCursors(lib: Library) = listOf(LIKES, PLAYLISTS, PLAYS, SETTINGS).forEach { lib.setSetting("sync.cursor.$it", "") }
    }
}

/**
 * The signed-in account and the background sync: soon after a change on the phone, when Tune opens, and every
 * few minutes while it runs. Signing out keeps everything on the phone.
 */
class AccountSync(private val lib: Library, private val http: OkHttpClient, val config: SyncConfig) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val auth = FirebaseAuth(http, config)
    private val mutex = Mutex()
    @Volatile private var session: AuthSession? = null
    private var debounce: Job? = null
    private var loop: Job? = null

    val deviceId: String = lib.setting("sync.device")?.ifBlank { null }
        ?: java.util.UUID.randomUUID().toString().replace("-", "").take(12).also { lib.setSetting("sync.device", it) }

    data class State(val signedIn: Boolean = false, val email: String? = null, val name: String? = null, val photo: String? = null,
                     val syncing: Boolean = false, val lastSynced: Long = 0, val error: String? = null)

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state

    init {
        val uid = get("account.uid"); val refresh = get("account.refresh")
        if (uid != null && refresh != null) session = AuthSession(uid, get("account.email"), get("account.name"), get("account.photo"), "", refresh, 0)
        publish()
        lib.onLocalChange = {
            if (session != null) {
                debounce?.cancel()
                debounce = scope.launch { delay(4_000); syncNow(skipIfBusy = true) }
            }
        }
    }

    /** Sync now and every 5 minutes while Tune runs (call once at startup). */
    fun start() {
        loop?.cancel()
        loop = scope.launch {
            delay(2_000)
            while (isActive) { if (session != null) syncNow(skipIfBusy = true); delay(5 * 60_000L) }
        }
    }

    /** Tune came to the front: sync unless it just did (other devices' changes show up without waiting). */
    fun syncSoon() {
        if (session == null) return
        if (System.currentTimeMillis() - (get("sync.last")?.toLongOrNull() ?: 0) < 20_000) return
        scope.launch { syncNow(skipIfBusy = true) }
    }

    suspend fun signInWithGoogle(googleIdToken: String) = withContext(Dispatchers.IO) {
        val s = auth.signInWithGoogle(googleIdToken)
        if (get("sync.lastUid") != s.uid) { SyncEngine.resetCursors(lib); lib.setSetting("sync.lastUid", s.uid) }
        lib.markAllForSync()
        session = s
        save(s)
        publish(error = null)
        scope.launch { syncNow() }
    }

    fun signOut() {
        session = null
        listOf("account.uid", "account.email", "account.name", "account.photo", "account.refresh").forEach { lib.setSetting(it, "") }
        publish(error = null)
    }

    /** One round now; with [skipIfBusy] false it waits for a running one, then runs. Never throws. */
    suspend fun syncNow(skipIfBusy: Boolean = false): SyncResult? = withContext(Dispatchers.IO) {
        if (session == null) return@withContext null
        if (skipIfBusy && mutex.isLocked) return@withContext null
        mutex.withLock {
            val s = session ?: return@withLock null
            publish(syncing = true)
            try {
                val remote = FirestoreRemote(http, config, s.uid) { force -> token(force) }
                val r = SyncEngine(lib, remote, deviceId).sync()
                lib.setSetting("sync.last", System.currentTimeMillis().toString())
                if (r.pulled + r.pushed > 0) Log.i("Tune", "Sync: ${r.pulled} pulled, ${r.pushed} pushed")
                publish(error = null)
                r
            } catch (e: Exception) {
                Log.w("Tune", "Sync failed: $e")
                val msg = if (e is java.io.IOException) "No connection" else e.message ?: e.toString()
                if (e is SyncException && e.message in setOf("TOKEN_EXPIRED", "USER_NOT_FOUND", "USER_DISABLED", "INVALID_REFRESH_TOKEN")) signOut()
                publish(error = msg)
                null
            }
        }
    }

    @Synchronized
    private fun token(force: Boolean): String {
        var s = session ?: throw SyncException("Signed out")
        if (force || s.expiresAt - System.currentTimeMillis() < 120_000) {
            s = auth.refresh(s)
            if (session != null) { session = s; save(s) }
        }
        return s.idToken
    }

    private fun publish(syncing: Boolean = false, error: String? = _state.value.error) {
        val s = session
        _state.value = State(s != null, s?.email, s?.name, s?.photo, syncing, get("sync.last")?.toLongOrNull() ?: 0, if (s == null) null else error)
    }

    private fun save(s: AuthSession) {
        lib.setSetting("account.uid", s.uid); lib.setSetting("account.email", s.email ?: ""); lib.setSetting("account.name", s.name ?: "")
        lib.setSetting("account.photo", s.photo ?: ""); lib.setSetting("account.refresh", s.refreshToken)
    }

    private fun get(key: String) = lib.setting(key)?.ifBlank { null }
}
