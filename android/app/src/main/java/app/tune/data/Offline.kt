package app.tune.data

import android.content.Context
import android.util.Log
import androidx.annotation.OptIn
import androidx.core.net.toUri
import androidx.media3.common.util.UnstableApi
import androidx.media3.database.StandaloneDatabaseProvider
import androidx.media3.datasource.DataSpec
import androidx.media3.datasource.cache.CacheDataSource
import androidx.media3.datasource.cache.ContentMetadata
import androidx.media3.datasource.cache.LeastRecentlyUsedCacheEvictor
import androidx.media3.datasource.cache.SimpleCache
import java.io.File
import kotlin.math.exp

/**
 * Every song you stream is kept here (least recently played goes first) so it can play again without
 * internet — like Spotify's streaming cache. Keys are video ids, so a cached song is found before any lookup.
 */
@OptIn(UnstableApi::class)
class StreamCache(context: Context, maxBytes: Long = 1L shl 30) {
    val cache = SimpleCache(File(context.cacheDir, "media"), LeastRecentlyUsedCacheEvictor(maxBytes), StandaloneDatabaseProvider(context))

    fun isFullyCached(videoId: String): Boolean {
        val len = ContentMetadata.getContentLength(cache.getContentMetadata(videoId))
        return len > 0 && cache.isCached(videoId, 0, len)
    }

    fun cachedIds(): Set<String> = cache.keys.filter { isFullyCached(it) }.toSet()

    fun usedBytes(): Long = cache.cacheSpace

    /** Copies a fully cached song to [target] (turning it into a download without using data). */
    fun copyTo(videoId: String, target: File): Boolean {
        if (!isFullyCached(videoId)) return false
        return runCatching {
            val ds = CacheDataSource.Factory().setCache(cache).setFlags(CacheDataSource.FLAG_BLOCK_ON_CACHE).createDataSource() // no upstream: cache only
            ds.open(DataSpec.Builder().setUri("tune://$videoId".toUri()).setKey(videoId).build())
            target.outputStream().use { out ->
                val buf = ByteArray(1 shl 16)
                while (true) { val n = ds.read(buf, 0, buf.size); if (n < 0) break; out.write(buf, 0, n) }
            }
            ds.close()
            true
        }.getOrElse { Log.w("Tune", "Copy from cache failed for $videoId: $it"); target.delete(); false }
    }

    fun clear() { cache.keys.toList().forEach { cache.removeResource(it) } }
}

/**
 * Smart downloads (like YouTube Music's): keeps up to N songs ready offline without you choosing them —
 * mostly what you play often and recently plus liked songs, topped up with ~20% similar songs from radio.
 * Songs it picked are swapped out when your taste moves on; songs you downloaded yourself are never touched.
 */
class SmartDownloads(private val lib: Library, private val yt: YtMusic, private val downloads: Downloads) {
    val enabled: Boolean get() = lib.setting("smart.enabled") != "0"
    val target: Int get() = lib.setting("smart.count")?.toIntOrNull() ?: 100
    val lastRunMs: Long get() = lib.setting("smart.lastRun")?.toLongOrNull() ?: 0
    /** The songs the last run picked, whoever downloaded them (many are often your own downloads already). */
    val pickedIds: List<String> get() = lib.setting("smart.picks")?.split(',')?.filter { it.isNotBlank() }.orEmpty()

    /** Picks the set and queues/removes downloads to match it. Returns (added, removed). One at a time. */
    @Synchronized
    fun refresh(): Pair<Int, Int> {
        val now = System.currentTimeMillis()
        if (!enabled) {
            val old = downloads.smartIds()
            downloads.remove(old)
            return 0 to old.size
        }
        val picks = pick(now)
        val want = picks.map { it.videoId }.toSet()
        val stale = downloads.smartIds() - want
        downloads.remove(stale)
        val added = downloads.enqueue(picks, smart = true)
        lib.setSetting("smart.picks", want.joinToString(","))
        lib.setSetting("smart.lastRun", now.toString())
        Log.i("Tune", "Smart downloads: ${picks.size} picked, $added new, ${stale.size} swapped out")
        return added to stale.size
    }

    fun pick(now: Long = System.currentTimeMillis()): List<Track> {
        val favourites = SmartPicker.rank(lib.playStats(now - 90 * DAY), lib.liked(), now)
            .take((target * 0.8).toInt().coerceAtLeast(1))
        val chosen = LinkedHashMap<String, Track>()
        favourites.forEach { chosen[it.videoId] = it }
        // discovery: songs like your top ones, so offline doesn't only mean repeats
        for (seed in favourites.take(5)) {
            if (chosen.size >= target) break
            val radio = runCatching { yt.radio(seed.videoId) }.getOrNull() ?: continue
            radio.drop(1).take(8).forEach { if (chosen.size < target) chosen.putIfAbsent(it.videoId, it) }
        }
        return chosen.values.toList()
    }

    private companion object { const val DAY = 86_400_000L }
}

/** Pure scoring, so it can be tested: how much you like a song from plays, recency and likes. */
object SmartPicker {
    fun rank(stats: List<Triple<Track, Int, Long>>, liked: List<Track>, now: Long): List<Track> {
        val score = HashMap<String, Double>()
        val tracks = HashMap<String, Track>()
        for ((t, plays, last) in stats) {
            val days = (now - last) / 86_400_000.0
            // plays count, but a song you played a lot months ago matters less than this week's
            score[t.videoId] = plays * (0.35 + exp(-days / 21.0))
            tracks[t.videoId] = t
        }
        for (t in liked) {
            score[t.videoId] = (score[t.videoId] ?: 0.0) + 2.5
            tracks.putIfAbsent(t.videoId, t)
        }
        return score.entries.sortedByDescending { it.value }.mapNotNull { tracks[it.key] }
    }
}

/**
 * What to play when the internet is gone and the queue has nothing playable left: songs saved on the phone,
 * closest to what was playing first — its radio, then the same artists, then what you play most — each tier
 * lightly shuffled so it doesn't feel like a fixed list.
 */
object OfflineMix {
    fun build(seed: Track?, available: List<Track>, radioIds: List<String>?, playCounts: Map<String, Int>,
              exclude: Set<String>, max: Int = 60, random: kotlin.random.Random = kotlin.random.Random): List<Track> {
        val pool = available.filter { it.videoId !in exclude && it.videoId != seed?.videoId }.distinctBy { it.videoId }
        val used = HashSet<String>()
        val out = ArrayList<Track>()
        fun take(list: List<Track>) { for (t in list) if (out.size < max && used.add(t.videoId)) out += t }

        val byId = pool.associateBy { it.videoId }
        take(radioIds.orEmpty().mapNotNull { byId[it] })                                     // 1. its radio, in order
        val artists = seed?.artists?.map { it.browseId ?: it.name }?.toSet().orEmpty()
        take(pool.filter { t -> t.artists.any { (it.browseId ?: it.name) in artists } }.shuffled(random)) // 2. same artists
        take(pool.filter { (playCounts[it.videoId] ?: 0) > 0 }.sortedByDescending { playCounts[it.videoId] }.take(30).shuffled(random)) // 3. your favourites
        take(pool.shuffled(random))                                                          // 4. everything else
        return out
    }
}

/** Offline: when to stop waiting for the current song and move on to one saved on the phone. */
object OfflineStall {
    /**
     * [state] is a Player.STATE_* value (1 idle, 2 buffering, 3 ready, 4 ended). [justArrived]: we just moved onto
     * this song, so if it can't play offline there's no point starting it at all.
     */
    fun isStuck(state: Int, failed: Boolean, currentPlayable: Boolean, justArrived: Boolean): Boolean = when {
        failed || state == 4 -> true                  // the song failed, or the queue ran out
        currentPlayable -> false                      // a saved song: it'll play
        justArrived -> true                           // not saved and nothing of it buffered yet
        else -> state == 2                            // not saved, and its buffer has run dry
    }
}
