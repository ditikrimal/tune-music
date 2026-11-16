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

    /** [fresh] = pull-to-refresh: radios refetched, seeds picked among more favourites, Quick picks reshuffled. */
    suspend fun build(fresh: Boolean = false): Feed = withContext(Dispatchers.IO) {
        coroutineScope {
            val now = System.currentTimeMillis()
            val discovery = async { runCatching { api.home() }.getOrNull() }
            val shelves = mutableListOf<Shelf>()
            val recent = lib.recent(12)
            if (recent.size >= 3) shelves += Shelf("Recently played", recent.map(::songCard), isLocal = true)

            var seeds = if (fresh) weightedPick(lib.topTracks(now - 14 * DAY, 8), 3) else lib.topTracks(now - 14 * DAY, 3)
            if (seeds.isEmpty()) seeds = recent.take(3)
            val favourites = lib.topTracks(now - 30 * DAY, 12)
            val extraSeeds = favourites.filter { f -> seeds.none { it.videoId == f.videoId } }.take(3)
            val radios = seeds.map { s -> async { radio(s.videoId, now, fresh) } }
            val extraRadios = extraSeeds.map { s -> async { radio(s.videoId, now, fresh) } }
            val because = radios.awaitAll()
            val extra = extraRadios.awaitAll()
            val shown = seeds.map { it.videoId }.toMutableSet()
            seeds.forEachIndexed { i, seed ->
                val items = because[i]?.filter { shown.add(it.videoId) }?.take(12)?.map(::songCard).orEmpty()
                if (items.isNotEmpty()) shelves += Shelf("Because you played ${seed.title}", items, isLocal = true)
            }
            val picks = quickPicks(because, extra, favourites, shown, shuffle = fresh)
            if (picks.size >= 8)
                shelves.add(if (recent.size >= 3) 1 else 0, Shelf("Quick picks", picks.map(::songCard), isLocal = true, isList = true))

            val top = lib.topArtists(now - 30 * DAY, 8).filter { it.browseId != null }
            val pics = top.map { a -> async { picture(a.browseId!!, now) } }.awaitAll()
            val artists = top.mapIndexed { i, a -> Card(CardKind.Artist, a.name, "Artist", pics[i], browseId = a.browseId) }
            if (artists.size >= 3) shelves += Shelf("Your top artists", artists, isLocal = true)

            val d = discovery.await()
            Feed(shelves + d.orEmpty(), offline = d == null)
        }
    }

    /** Like YouTube Music's Quick picks: new songs close to what you play (several radios, round-robin, deeper than the
     *  "Because you played" rows so nothing repeats) with a familiar favourite every few songs. Same as the Windows app. */
    private fun quickPicks(because: List<List<Track>?>, extra: List<List<Track>?>, favourites: List<Track>, shown: MutableSet<String>,
                           shuffle: Boolean = false): List<Track> {
        val pools = (because.map { it.orEmpty().drop(13) } + extra.map { it.orEmpty().drop(1) }).filter { it.isNotEmpty() }
            .map { if (shuffle) it.shuffled() else it }
        val favs = if (shuffle) favourites.shuffled() else favourites
        val fresh = mutableListOf<Track>()
        var depth = 0
        while (fresh.size < 20 && pools.any { depth < it.size }) {
            for (p in pools) if (depth < p.size && fresh.size < 20 && shown.add(p[depth].videoId)) fresh += p[depth]
            depth++
        }
        val familiar = ArrayDeque(favs.filter { shown.add(it.videoId) })
        val picks = mutableListOf<Track>()
        for (t in fresh) {
            if (picks.size >= 20) break
            if (picks.size % 5 == 2 && familiar.isNotEmpty()) picks += familiar.removeFirst()
            if (picks.size < 20) picks += t
        }
        return picks
    }

    /** Picks [count] items, favouring the front of the ranked list. */
    private fun weightedPick(ranked: List<Track>, count: Int): List<Track> {
        val pool = ranked.toMutableList()
        val out = mutableListOf<Track>()
        while (out.size < count && pool.isNotEmpty()) {
            val weights = pool.indices.map { pool.size - it }
            var roll = kotlin.random.Random.nextInt(weights.sum())
            val i = weights.indexOfFirst { w -> (roll < w).also { roll -= w } }
            out += pool.removeAt(i)
        }
        return out
    }

    private fun radio(videoId: String, now: Long, bypassCache: Boolean = false): List<Track>? {
        if (!bypassCache) radioCache[videoId]?.takeIf { now - it.first < 6 * HOUR }?.let { return it.second }
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
