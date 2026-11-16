package app.tune.data

import android.content.Context
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.util.concurrent.ConcurrentHashMap

enum class DownloadState { None, Queued, Downloading, Done }

/**
 * Saves songs for offline play, one at a time in 4 MB ranged chunks, into app storage. The list lives in the
 * library database, so unfinished downloads resume after a restart; while offline it waits.
 */
class Downloads(context: Context, private val lib: Library, private val streams: StreamResolver, private val http: OkHttpClient,
                private val online: StateFlow<Boolean>) {
    private val dir = File(context.filesDir, "downloads").apply { mkdirs() }
    private val files = ConcurrentHashMap<String, File>()
    private val queue = java.util.Collections.synchronizedList(mutableListOf<Track>())
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** videoId → progress 0..1 of the song downloading now; bumps whenever any state changes. */
    private val _progress = MutableStateFlow<Pair<String?, Float>>(null to 0f)
    val progress: StateFlow<Pair<String?, Float>> = _progress
    private val _version = MutableStateFlow(0)
    val version: StateFlow<Int> = _version

    fun start() = scope.launch {
        dir.listFiles { f -> f.name.endsWith(".part") }?.forEach { it.delete() }
        for (d in lib.downloads().reversed()) {
            val f = d.path?.let(::File)
            if (f != null && f.exists()) files[d.track.videoId] = f else queue += d.track
        }
        _version.value++
        work()
    }

    fun localFile(videoId: String): File? = files[videoId]

    fun stateOf(videoId: String): DownloadState = when {
        files.containsKey(videoId) -> DownloadState.Done
        _progress.value.first == videoId -> DownloadState.Downloading
        synchronized(queue) { queue.any { it.videoId == videoId } } -> DownloadState.Queued
        else -> DownloadState.None
    }

    val pendingCount get() = synchronized(queue) { queue.size }

    fun downloaded(): List<Track> = lib.downloads().filter { files.containsKey(it.track.videoId) }.map { it.track }

    fun downloadedBytes(): Long = lib.downloads().filter { files.containsKey(it.track.videoId) }.sumOf { it.bytes }

    fun enqueue(tracks: List<Track>): Int {
        val fresh = synchronized(queue) {
            tracks.distinctBy { it.videoId }.filter { t -> !files.containsKey(t.videoId) && queue.none { it.videoId == t.videoId } }
                .also { queue += it }
        }
        if (fresh.isNotEmpty()) {
            lib.addDownloads(fresh)
            _version.value++
            wake.trySend(Unit)
        }
        return fresh.size
    }

    fun remove(ids: List<String>) {
        for (id in ids) {
            synchronized(queue) { queue.removeAll { it.videoId == id } }
            files.remove(id)?.delete()
            lib.removeDownload(id)
        }
        _version.value++
    }

    private suspend fun work() {
        while (true) {
            val next = synchronized(queue) { queue.firstOrNull() }
            if (next == null) { wake.receive(); continue }
            if (!online.value) { online.first { it }; continue }
            _progress.value = next.videoId to 0f
            _version.value++
            try {
                download(next)
            } catch (e: Exception) {
                Log.w("Tune", "Download failed for ${next.videoId}", e)
                synchronized(queue) { queue.remove(next); queue.add(next) } // try the others first
                streams.invalidate(next.videoId)
                kotlinx.coroutines.delay(5_000)
            } finally {
                _progress.value = null to 0f
                _version.value++
            }
        }
    }

    private fun download(t: Track) {
        val s = streams.resolve(t.videoId)
        val part = File(dir, "${t.videoId}.part")
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
                    val n = r.body.byteStream().copyTo(out)
                    if (n == 0L) throw java.io.IOException("No data")
                    done += n
                    if (r.code == 200) { done = total ?: done }
                }
                if (total == null) break
                _progress.value = t.videoId to (done.toFloat() / total)
            }
        }
        val ext = if (s.url.contains("mime=audio%2Fwebm")) ".webm" else ".m4a"
        val file = File(dir, t.videoId + ext)
        part.renameTo(file)
        synchronized(queue) { queue.removeAll { it.videoId == t.videoId } }
        files[t.videoId] = file
        lib.setDownloaded(t.videoId, file.absolutePath, file.length())
    }

    private companion object { const val CHUNK = 4L * 1024 * 1024 }
}
