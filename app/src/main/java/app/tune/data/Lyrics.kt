package app.tune.data

import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

data class LyricLine(val atMs: Long, val text: String)

/** Lyrics for one song; [lines] is set when they're time-synced. */
data class SongLyrics(val text: String, val source: String?, val lines: List<LyricLine>?)

object Lrc {
    private val stamp = Regex("^\\[(\\d{1,3}):(\\d{2})(?:[.:](\\d{1,3}))?]")

    fun parse(lrc: String?): List<LyricLine> {
        val out = mutableListOf<LyricLine>()
        for (raw in (lrc ?: "").split('\n')) {
            var rest = raw.trimEnd('\r')
            val stamps = mutableListOf<Long>()
            while (true) {
                val m = stamp.find(rest) ?: break
                val (min, sec, frac) = m.destructured
                val ms = if (frac.isEmpty()) 0 else frac.toInt() * when (frac.length) { 1 -> 100; 2 -> 10; else -> 1 }
                stamps += min.toLong() * 60_000 + sec.toLong() * 1000 + ms
                rest = rest.substring(m.value.length)
            }
            stamps.forEach { out += LyricLine(it, rest.trim()) }
        }
        return out.sortedBy { it.atMs }
    }

    /** Index of the line being sung at [positionMs], or -1 before the first. */
    fun indexAt(lines: List<LyricLine>, positionMs: Long): Int {
        var lo = 0; var hi = lines.size - 1; var found = -1
        while (lo <= hi) {
            val mid = (lo + hi) / 2
            if (lines[mid].atMs <= positionMs) { found = mid; lo = mid + 1 } else hi = mid - 1
        }
        return found
    }
}

/**
 * Synced lyrics from lrclib.net when allowed (only title/artist/duration are sent), otherwise
 * YouTube Music's own plain lyrics. Remembered for the session.
 */
class LyricsService(private val http: OkHttpClient, private val yt: YtMusic, private val syncedAllowed: () -> Boolean) {
    private val cache = ConcurrentHashMap<String, SongLyrics>()
    private val noise = Regex("\\s*[(\\[][^)\\]]*\\b(official|video|audio|lyrics?|visuali[sz]er|4k|hd|m/?v)\\b[^)\\]]*[)\\]]", RegexOption.IGNORE_CASE)

    fun get(track: Track): SongLyrics? {
        val allowed = syncedAllowed()
        val key = track.videoId + if (allowed) "|s" else "|p"
        cache[key]?.let { return it }
        var synced: SongLyrics? = null
        if (allowed) {
            synced = runCatching { lrclib(track) }.getOrNull()
            if (synced?.lines != null) return synced.also { cache[key] = it }
        }
        val plain = runCatching { yt.ytLyrics(track.videoId) }.getOrNull()
        val result = plain?.let { SongLyrics(it.first, it.second, null) } ?: synced
        if (result != null) cache[key] = result
        return result
    }

    private fun lrclib(track: Track): SongLyrics? {
        val artist = track.artists.firstOrNull()?.name ?: ""
        val title = noise.replace(track.title, "").trim()
        val get = "https://lrclib.net/api/get".toHttpUrl().newBuilder()
            .addQueryParameter("track_name", title).addQueryParameter("artist_name", artist)
            .apply { track.durationSec?.let { addQueryParameter("duration", it.toString()) } }.build()
        http.newCall(Request.Builder().url(get).header("User-Agent", "Tune/1.0").build()).execute().use { r ->
            if (r.isSuccessful) parse(JSONObject(r.body.string()))?.let { return it }
            else if (r.code != 404) return null
        }
        // YouTube durations differ from the album cut: search and take the best synced hit
        val search = "https://lrclib.net/api/search".toHttpUrl().newBuilder()
            .addQueryParameter("track_name", title).addQueryParameter("artist_name", artist).build()
        http.newCall(Request.Builder().url(search).header("User-Agent", "Tune/1.0").build()).execute().use { r ->
            if (!r.isSuccessful) return null
            val arr = JSONArray(r.body.string())
            val best = (0 until arr.length()).map { arr.getJSONObject(it) }.sortedWith(
                compareByDescending<JSONObject> { !it.optString("syncedLyrics").isNullOrBlank() && !it.isNull("syncedLyrics") }
                    .thenBy { o -> track.durationSec?.let { kotlin.math.abs(o.optDouble("duration", 9e9) - it) } ?: 999.0 }
            ).firstOrNull() ?: return null
            return parse(best)
        }
    }

    private fun parse(o: JSONObject): SongLyrics? {
        val synced = if (o.isNull("syncedLyrics")) null else o.optString("syncedLyrics")
        val plain = if (o.isNull("plainLyrics")) null else o.optString("plainLyrics")
        val lines = synced?.takeIf { it.isNotBlank() }?.let { Lrc.parse(it) }?.takeIf { it.isNotEmpty() }
        val text = plain?.takeIf { it.isNotBlank() }?.trim() ?: lines?.joinToString("\n") { it.text } ?: return null
        return SongLyrics(text, if (lines != null) "Synced lyrics: LRCLIB" else "Lyrics: LRCLIB", lines)
    }
}
