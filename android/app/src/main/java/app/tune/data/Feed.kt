package app.tune.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import java.util.concurrent.ConcurrentHashMap

data class Feed(val shelves: List<Shelf>, val offline: Boolean)

/** Home: local rows from play history first, then YouTube Music's anonymous discovery rows (port of FeedBuilder). */
class FeedBuilder(private val lib: Library, private val api: YtMusic) {
    private val radioCache = ConcurrentHashMap<String, Pair<Long, List<Track>>>()
    private val pictures = ConcurrentHashMap<String, Pair<Long, String?>>()

    suspend fun build(): Feed = withContext(Dispatchers.IO) {
        coroutineScope {
            val now = System.currentTimeMillis()
            val discovery = async { runCatching { api.home() }.getOrNull() }
            val shelves = mutableListOf<Shelf>()
            val recent = lib.recent(12)
            if (recent.size >= 3) shelves += Shelf("Recently played", recent.map(::songCard), isLocal = true)

            var seeds = lib.topTracks(now - 14 * DAY, 3)
            if (seeds.isEmpty()) seeds = recent.take(3)
            val radios = seeds.map { s -> async { radio(s.videoId, now) } }.awaitAll()
            val shown = seeds.map { it.videoId }.toMutableSet()
            seeds.forEachIndexed { i, seed ->
                val items = radios[i]?.filter { shown.add(it.videoId) }?.take(12)?.map(::songCard).orEmpty()
                if (items.isNotEmpty()) shelves += Shelf("Because you played ${seed.title}", items, isLocal = true)
            }

            val top = lib.topArtists(now - 30 * DAY, 8).filter { it.browseId != null }
            val pics = top.map { a -> async { picture(a.browseId!!, now) } }.awaitAll()
            val artists = top.mapIndexed { i, a -> Card(CardKind.Artist, a.name, "Artist", pics[i], browseId = a.browseId) }
            if (artists.size >= 3) shelves += Shelf("Your top artists", artists, isLocal = true)

            val d = discovery.await()
            Feed(shelves + d.orEmpty(), offline = d == null)
        }
    }

    private fun radio(videoId: String, now: Long): List<Track>? {
        radioCache[videoId]?.takeIf { now - it.first < 6 * HOUR }?.let { return it.second }
        return runCatching { api.radio(videoId) }.getOrNull()?.also { radioCache[videoId] = now to it }
    }

    private fun picture(browseId: String, now: Long): String? {
        pictures[browseId]?.takeIf { now - it.first < DAY }?.let { return it.second }
        return runCatching { api.artist(browseId).thumbnail }.getOrNull().also { pictures[browseId] = now to it }
    }

    private fun songCard(t: Track) = Card(CardKind.Song, t.title, t.artistLine, t.thumbnail, t.videoId, track = t)

    private companion object {
        const val HOUR = 3_600_000L
        const val DAY = 24 * HOUR
    }
}
