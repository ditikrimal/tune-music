package app.tune.data

import android.content.Context
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.onTimeout
import kotlinx.coroutines.selects.select
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.util.concurrent.ConcurrentHashMap

enum class DownloadState { None, Queued, Downloading, Done }

/**
 * Saves songs for offline play, one at a time in 4 MB ranged chunks, into app storage. The list lives in the
 * library database, so unfinished downloads resume after a restart. Songs you download go on any connection;
 * smart downloads (picked by Tune) wait for Wi-Fi. A song already in the streaming cache is copied, not fetched.
 */
class Downloads(context: Context, private val lib: Library, private val streams: StreamResolver, private val http: OkHttpClient,
                private val online: StateFlow<Boolean>, private val unmetered: StateFlow<Boolean>,
                private val fromCache: (videoId: String, target: File) -> Boolean = { _, _ -> false }) {
    private data class Pending(val track: Track, val smart: Boolean)

    private val dir = File(context.filesDir, "downloads").apply { mkdirs() }
    private val files = ConcurrentHashMap<String, File>()
    private val queue = java.util.Collections.synchronizedList(mutableListOf<Pending>())
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private val failures = ConcurrentHashMap<String, Int>()

    /** A message for the user (e.g. a song that can't be downloaded). */
    var onNotice: (String) -> Unit = {}
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** videoId → progress 0..1 of the song downloading now. */
    private val _progress = MutableStateFlow<Pair<String?, Float>>(null to 0f)
    val progress: StateFlow<Pair<String?, Float>> = _progress
    private val _version = MutableStateFlow(0)
    val version: StateFlow<Int> = _version

    fun start() = scope.launch {
        dir.listFiles { f -> f.name.endsWith(".part") }?.forEach { it.delete() }
        for (d in lib.downloads().reversed()) {
            val f = d.path?.let(::File)
            if (f != null && f.exists()) files[d.track.videoId] = f else queue += Pending(d.track, d.smart)
        }
        _version.value++
        // a change of network may make smart downloads eligible
        launch { combine(online, unmetered) { a, b -> a to b }.drop(1).collect { wake.trySend(Unit) } }
        work()
    }

    fun localFile(videoId: String): File? = files[videoId]

    fun stateOf(videoId: String): DownloadState = when {
        files.containsKey(videoId) -> DownloadState.Done
        _progress.value.first == videoId -> DownloadState.Downloading
        synchronized(queue) { queue.any { it.track.videoId == videoId } } -> DownloadState.Queued
        else -> DownloadState.None
    }

    val pendingCount get() = synchronized(queue) { queue.size }

    fun downloaded(): List<Track> = lib.downloads().filter { files.containsKey(it.track.videoId) }.map { it.track }

    fun downloadedBytes(): Long = lib.downloads().filter { files.containsKey(it.track.videoId) }.sumOf { it.bytes }

    /** What's waiting, in order: (song, picked by smart downloads). The first one may be downloading now. */
    fun queued(): List<Pair<Track, Boolean>> = synchronized(queue) { queue.map { it.track to it.smart } }

    /** Smart songs only download on Wi-Fi: true when that's what they're waiting for. */
    val waitingForWifi: Boolean get() = !unmetered.value

    /** Video ids Tune picked itself (done or waiting). */
    fun smartIds(): Set<String> = lib.downloads().filter { it.smart }.map { it.track.videoId }.toSet()

    /** Queues songs. Asking for a song Tune already picked makes it yours (it won't be swapped out). */
    fun enqueue(tracks: List<Track>, smart: Boolean = false): Int {
        val fresh = synchronized(queue) {
            if (!smart) {
                val ids = tracks.map { it.videoId }.toSet()
                queue.replaceAll { if (it.track.videoId in ids) it.copy(smart = false) else it }
            }
            tracks.distinctBy { it.videoId }
                .filter { t -> !files.containsKey(t.videoId) && queue.none { it.track.videoId == t.videoId } }
                .also { list -> list.forEach { queue += Pending(it, smart) } }
        }
        lib.addDownloads(if (smart) fresh else tracks.distinctBy { it.videoId }, smart)
        _version.value++
        if (fresh.isNotEmpty()) wake.trySend(Unit)
        return fresh.size
    }

    fun remove(ids: kotlin.collections.Collection<String>) {
        for (id in ids) {
            synchronized(queue) { queue.removeAll { it.track.videoId == id } }
            files.remove(id)?.delete()
            lib.removeDownload(id)
        }
        _version.value++
    }

    private fun nextEligible(): Pending? = synchronized(queue) {
        queue.firstOrNull { !it.smart } ?: if (unmetered.value) queue.firstOrNull() else null
    }

    private suspend fun work() {
        while (true) {
            val next = if (online.value) nextEligible() else null
            if (next == null) { wake.receive(); continue }
            _progress.value = next.track.videoId to 0f
            _version.value++
            try {
                download(next.track)
            } catch (e: Exception) {
                Log.w("Tune", "Download failed for ${next.track.videoId}: $e")
                streams.invalidate(next.track.videoId)
                val n = failures.merge(next.track.videoId, 1, Int::plus)!!
                if (e is org.schabi.newpipe.extractor.exceptions.ContentNotAvailableException || n >= 5) {
                    // removed, private or blocked here: no point retrying
                    remove(listOf(next.track.videoId))
                    if (!next.smart) onNotice("Couldn't download ${next.track.title} — it isn't available")
                } else {
                    synchronized(queue) { if (queue.remove(next)) queue.add(next) } // try the others first
                    // back off: 5 s, 20 s, 80 s, ... unless something else wakes us
                    select<Unit> { wake.onReceive { }; onTimeout(5_000L shl (2 * (n - 1))) { } }
                }
            } finally {
                _progress.value = null to 0f
                _version.value++
            }
        }
    }

    private fun download(t: Track) {
        val part = File(dir, "${t.videoId}.part")
        if (fromCache(t.videoId, part)) {
            finish(t, part, sniffExtension(part))
            return
        }
        val s = streams.resolve(t.videoId)
        val total = Regex("[?&]clen=(\\d+)").find(s.url)?.groupValues?.get(1)?.toLong()
        var done = 0L
        part.outputStream().use { out ->
            while (total == null || done < total) {
                val end = done + CHUNK - 1
                val req = Request.Builder().url(s.url).header("User-Agent", s.userAgent).header("Range", "bytes=$done-${if (total != null) minOf(end, total - 1) else end}")
                    .apply { if (s.webClient) post(byteArrayOf(0x78, 0).toRequestBody()).header("Origin", "https://www.youtube.com").header("Referer", "https://www.youtube.com/") }
                    .build()
                http.newCall(req).execute().use { r ->
                    if (r.code == 416) return@use
                    if (!r.isSuccessful) throw java.io.IOException("HTTP ${r.code}")
                    // copy while reporting progress a few times a second (a song is often a single chunk)
                    val input = r.body.byteStream()
                    val buf = ByteArray(1 shl 16)
                    var n = 0L
                    var lastTick = 0L
                    while (true) {
                        val k = input.read(buf); if (k < 0) break
                        out.write(buf, 0, k); n += k
                        val now = System.currentTimeMillis()
                        if (total != null && now - lastTick > 150) { lastTick = now; _progress.value = t.videoId to ((done + n).toFloat() / total) }
                    }
                    if (n == 0L) throw java.io.IOException("No data")
                    done += n
                    if (r.code == 200) { done = total ?: done }
                }
                if (total == null) break
                _progress.value = t.videoId to (done.toFloat() / total)
            }
        }
        finish(t, part, if (s.url.contains("mime=audio%2Fwebm")) ".webm" else ".m4a")
    }

    private fun finish(t: Track, part: File, ext: String) {
        val file = File(dir, t.videoId + ext)
        part.renameTo(file)
        synchronized(queue) { queue.removeAll { it.track.videoId == t.videoId } }
        files[t.videoId] = file
        lib.setDownloaded(t.videoId, file.absolutePath, file.length())
    }

    /** WebM files start with the EBML magic number; everything else we get is MP4 audio. */
    private fun sniffExtension(f: File): String =
        f.inputStream().use { val b = ByteArray(4); it.read(b); if (b.contentEquals(byteArrayOf(0x1A, 0x45, 0xDF.toByte(), 0xA3.toByte()))) ".webm" else ".m4a" }

    private companion object { const val CHUNK = 4L * 1024 * 1024 }
}
