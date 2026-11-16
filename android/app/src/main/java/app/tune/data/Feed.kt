package app.tune.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.max
import kotlin.math.pow

data class Feed(val shelves: List<Shelf>, val offline: Boolean)

/**
 * What you like, from your own listening (port of Tune.Core Taste): every play counts, recent ones more
 * (half-life 30 days "lately", 120 days "all-time"); a like is worth a few plays; early skips count against.
 */
class Taste(allPlays: List<Pair<Track, Long>>, liked: List<Track>, skips: List<Pair<String, Long>>, private val now: Long,
            notInterested: Set<String> = emptySet()) {
    private val plays = allPlays.filter { it.first.videoId !in notInterested }
    data class Entry(val track: Track, val lately: Double, val allTime: Double, val plays: Int, val lastPlayed: Long?, val skips: Int, val liked: Boolean)

    private class Acc(val t: Track) { var l = 0.0; var a = 0.0; var p = 0; var last: Long? = null; var s = 0; var like = false }

    private val songs = HashMap<String, Entry>()
    private val artists = HashMap<String, Double>()
    private val topArtist: Double
    val skippedIds: Set<String>

    init {
        val acc = LinkedHashMap<String, Acc>()
        for ((t, at) in plays) {
            val e = acc.getOrPut(t.videoId) { Acc(t) }
            val days = max(0.0, (now - at) / DAY.toDouble())
            e.l += decay(days, 30.0); e.a += decay(days, 120.0); e.p++
            if (e.last == null || at > e.last!!) e.last = at
        }
        for (t in liked) acc.getOrPut(t.videoId) { Acc(t) }.apply { l += 2.5; a += 2.5; like = true }
        for ((id, at) in skips) {
            val e = acc[id] ?: continue
            val days = max(0.0, (now - at) / DAY.toDouble())
            e.l -= 0.6 * decay(days, 30.0); e.a -= 0.6 * decay(days, 120.0); e.s++
        }
        skippedIds = skips.filter { now - it.second < 60 * DAY }.groupBy { it.first }
            .filter { (id, g) -> g.size >= 2 && acc[id].let { it == null || (it.p <= 1 && !it.like) } }.keys + notInterested
        for ((id, e) in acc) {
            songs[id] = Entry(e.t, e.l, e.a, e.p, e.last, e.s, e.like)
            for (a in e.t.artists) artists[artistKey(a)] = (artists[artistKey(a)] ?: 0.0) + max(0.0, e.l + e.a * 0.3)
        }
        topArtist = artists.values.maxOrNull()?.coerceAtLeast(1e-6) ?: 1.0
    }

    val isEmpty get() = songs.isEmpty()
    fun get(id: String) = songs[id]
    fun knows(id: String) = songs.containsKey(id)
    fun heardWithin(id: String, ms: Long) = songs[id]?.lastPlayed?.let { now - it < ms } == true
    fun artistAffinity(t: Track) = if (t.artists.isEmpty()) 0.0 else t.artists.maxOf { artists[artistKey(it)] ?: 0.0 } / topArtist
    fun lately() = songs.values.filter { it.lately > 0.05 }.sortedByDescending { it.lately }

    fun speedDial(max: Int, perArtist: Int = 3) =
        capPerArtist(songs.values.filter { (it.plays >= 2 || it.liked) && it.lately + it.allTime > 0.3 }
            .sortedByDescending { it.lately * 0.6 + it.allTime * 0.4 }.map { it.track }, perArtist).take(max)

    fun forgotten(max: Int) =
        capPerArtist(songs.values.filter { it.plays >= 3 && it.lastPlayed != null && now - it.lastPlayed > 21 * DAY && it.allTime > 0.5 }
            .sortedByDescending { it.allTime }.map { it.track }, 2).take(max)

    fun topArtists(max: Int): List<ArtistRef> =
        songs.values.flatMap { it.track.artists }.filter { it.browseId != null }.groupBy { artistKey(it) }
            .entries.sortedByDescending { artists[it.key] ?: 0.0 }.filter { (artists[it.key] ?: 0.0) > 0.3 }.map { it.value.first() }.take(max)

    /** Songs to start radios from, weighted by how much you like them lately (seeds vary day to day). */
    fun seeds(count: Int, random: kotlin.random.Random): List<Track> {
        val pool = lately().filter { it.track.videoId !in skippedIds }.take(20).toMutableList()
        val chosen = mutableListOf<Track>()
        while (chosen.size < count && pool.isNotEmpty()) {
            var roll = random.nextDouble() * pool.sumOf { it.lately }
            var i = 0
            while (i < pool.size - 1 && roll > pool[i].lately) { roll -= pool[i].lately; i++ }
            chosen += pool.removeAt(i).track
        }
        return chosen
    }

    companion object {
        const val DAY = 86_400_000L
        fun artistKey(a: ArtistRef) = a.browseId ?: a.name.lowercase()
        private fun decay(days: Double, halfLife: Double) = 0.5.pow(days / halfLife)

        fun capPerArtist(tracks: List<Track>, perArtist: Int): List<Track> {
            val counts = HashMap<String, Int>()
            return tracks.filter { t ->
                val key = t.artists.firstOrNull()?.let(::artistKey) ?: t.videoId
                val n = counts[key] ?: 0
                (n < perArtist).also { if (it) counts[key] = n + 1 }
            }
        }
    }
}

/** Home rows, in the user's order (Settings > Home). Same names and storage as the Windows app. */
enum class HomeSection(val label: String, val description: String) {
    SpeedDial("Speed dial", "Your pins and most-played songs"),
    QuickPicks("Quick picks", "New songs you'll like, mixed with favourites"),
    Because("Because you played", "More like songs you play a lot"),
    Forgotten("Forgotten favourites", "Old favourites you haven't played lately"),
    Artists("Your top artists", "The artists you play most"),
    Recent("Recently played", "What you just played"),
    Discovery("From YouTube Music", "Charts, moods and new releases"),
}

data class HomeSectionState(val section: HomeSection, val visible: Boolean)

object HomeLayout {
    const val KEY = "home.layout"
    val default = HomeSection.entries.map { HomeSectionState(it, it != HomeSection.Recent) }

    fun parse(text: String?): List<HomeSectionState> {
        val list = mutableListOf<HomeSectionState>()
        for (raw in text.orEmpty().split(',').map { it.trim() }.filter { it.isNotEmpty() }) {
            val s = HomeSection.entries.firstOrNull { it.name.equals(raw.trimStart('-'), ignoreCase = true) } ?: continue
            if (list.none { it.section == s }) list += HomeSectionState(s, !raw.startsWith('-'))
        }
        for (d in default.filter { d -> list.none { it.section == d.section } }) list.add(minOf(list.size, default.indexOf(d)), d)
        return list
    }

    fun format(layout: List<HomeSectionState>) = layout.joinToString(",") { (if (it.visible) "" else "-") + it.section.name.lowercase() }
    fun load(lib: Library) = parse(lib.setting(KEY))
    fun save(lib: Library, layout: List<HomeSectionState>) = lib.setSetting(KEY, format(layout))
}

/** Things pinned to the speed dial, first in the grid. Same JSON as the Windows app (setting speeddial.pins, synced). */
object SpeedDialPins {
    const val KEY = "speeddial.pins"
    const val MAX = 27

    fun pinKey(c: Card) = when (c.kind) {
        CardKind.Song -> "song:" + c.videoId
        CardKind.Artist -> "artist:" + c.browseId
        CardKind.Album -> "album:" + (c.browseId ?: c.playlistId)
        CardKind.Playlist -> "playlist:" + (c.playlistId ?: c.browseId)
    }

    fun load(lib: Library): List<Card> = runCatching {
        val a = JSONArray(lib.setting(KEY) ?: "[]")
        (0 until a.length()).mapNotNull { a.optJSONObject(it)?.let(::fromJson) }
    }.getOrDefault(emptyList())

    fun isPinned(lib: Library, c: Card) = load(lib).any { pinKey(it) == pinKey(c) }

    /** Pins (newest first) or unpins. Returns the new state. */
    fun toggle(lib: Library, c: Card): Boolean {
        val pins = load(lib).toMutableList()
        val was = pins.removeAll { pinKey(it) == pinKey(c) }
        if (!was) pins.add(0, c)
        save(lib, pins.take(MAX))
        return !was
    }

    fun save(lib: Library, pins: List<Card>) = lib.setSetting(KEY, JSONArray().apply { pins.forEach { put(toJson(it)) } }.toString())

    /** A pin for one of your own playlists (by uid, so it works on every device). */
    fun localPlaylistCard(uid: String, name: String, thumbnail: String?) =
        Card(CardKind.Playlist, name, "Your playlist", thumbnail, playlistId = "tune:$uid")

    private fun toJson(c: Card) = JSONObject().put("k", c.kind.name).put("t", c.title).put("s", c.subtitle ?: JSONObject.NULL)
        .put("th", c.thumbnail ?: JSONObject.NULL).put("v", c.videoId ?: JSONObject.NULL).put("b", c.browseId ?: JSONObject.NULL)
        .put("p", c.playlistId ?: JSONObject.NULL).apply { c.track?.let { put("tr", it.toJson()) } }

    private fun fromJson(o: JSONObject): Card? {
        val kind = CardKind.entries.firstOrNull { it.name == o.optString("k") } ?: return null
        return Card(kind, o.optString("t"), o.optStringOrNull("s"), o.optStringOrNull("th"), o.optStringOrNull("v"), o.optStringOrNull("b"),
            o.optStringOrNull("p"), o.optJSONObject("tr")?.let(Track::fromJson))
    }
}

/** Home in the user's layout; everything personal ranked by [Taste] (port of Tune.Core FeedBuilder). */
class FeedBuilder(private val lib: Library, private val api: YtMusic) {
    private val radioCache = ConcurrentHashMap<String, Pair<Long, List<Track>>>()
    private val pictures = ConcurrentHashMap<String, Pair<Long, String?>>()

    fun taste(now: Long = System.currentTimeMillis()) = Taste(lib.plays(now - 365 * DAY), lib.liked(), lib.skips(now - 90 * DAY), now, lib.notInterested())

    /** [fresh] = pull-to-refresh: radios refetched, other seeds, quick picks reshuffled. */
    suspend fun build(fresh: Boolean = false): Feed = withContext(Dispatchers.IO) {
        coroutineScope {
            val now = System.currentTimeMillis()
            val layout = HomeLayout.load(lib).filter { it.visible }.map { it.section }
            val discovery = async { if (HomeSection.Discovery in layout) runCatching { api.home() }.getOrNull() else emptyList() }
            val taste = taste(now)
            val day = java.time.LocalDate.now()
            val random = if (fresh) kotlin.random.Random.Default else kotlin.random.Random(day.dayOfYear * 7919 + day.year)

            var seeds = taste.seeds(6, random)
            if (seeds.isEmpty()) seeds = lib.recent(6)
            val radios = seeds.map { s -> async { radio(s.videoId, now, fresh) } }.awaitAll()
            val rows = HashMap<HomeSection, List<Shelf>>()
            val shown = HashSet<String>()

            if (HomeSection.SpeedDial in layout) speedDial(taste).takeIf { it.isNotEmpty() }?.let {
                rows[HomeSection.SpeedDial] = listOf(Shelf("Speed dial", it, isLocal = true, isSpeedDial = true))
            }
            if (HomeSection.QuickPicks in layout) {
                val picks = quickPicks(taste, radios, shown, random, jitter = fresh)
                if (picks.size >= 8) {
                    rows[HomeSection.QuickPicks] = listOf(Shelf("Quick picks", picks.map(::songCard), isLocal = true, isList = true))
                    picks.forEach { shown += it.videoId }
                }
            }
            if (HomeSection.Because in layout) {
                val because = mutableListOf<Shelf>()
                for (i in seeds.indices) {
                    if (because.size >= 3) break
                    val radio = radios[i] ?: continue
                    val items = Taste.capPerArtist(radio.drop(1).filter { t ->
                        t.videoId !in taste.skippedIds && t.videoId !in shown && seeds.none { it.videoId == t.videoId } }, 3).take(12)
                    items.forEach { shown += it.videoId }
                    if (items.isNotEmpty()) because += Shelf("Because you played ${seeds[i].title}", items.map(::songCard), isLocal = true)
                }
                rows[HomeSection.Because] = because
            }
            if (HomeSection.Forgotten in layout) taste.forgotten(12).takeIf { it.size >= 3 }?.let {
                rows[HomeSection.Forgotten] = listOf(Shelf("Forgotten favourites", it.map(::songCard), isLocal = true))
            }
            if (HomeSection.Artists in layout) {
                val top = taste.topArtists(8)
                val pics = top.map { a -> async { picture(a.browseId!!, now) } }.awaitAll()
                val artists = top.mapIndexed { i, a -> Card(CardKind.Artist, a.name, "Artist", pics[i], browseId = a.browseId) }
                if (artists.size >= 3) rows[HomeSection.Artists] = listOf(Shelf("Your top artists", artists, isLocal = true))
            }
            if (HomeSection.Recent in layout) lib.recent(12).takeIf { it.size >= 3 }?.let {
                rows[HomeSection.Recent] = listOf(Shelf("Recently played", it.map(::songCard), isLocal = true))
            }
            val d = discovery.await()
            rows[HomeSection.Discovery] = d.orEmpty()
            Feed(layout.flatMap { rows[it].orEmpty() }, offline = d == null)
        }
    }

    /** Pins first (your own playlists kept current: name, art, deleted), then the songs you come back to. */
    fun speedDial(taste: Taste): List<Card> {
        val playlists by lazy { lib.playlists() }
        val cards = mutableListOf<Card>()
        for (pin in SpeedDialPins.load(lib)) {
            val p = pin.playlistId
            if (p != null && p.startsWith("tune:")) {
                val id = lib.playlistIdByUid(p.removePrefix("tune:")) ?: continue
                val info = playlists.firstOrNull { it.id == id } ?: continue
                cards += pin.copy(title = info.name, thumbnail = info.thumbnail ?: pin.thumbnail, pinned = true)
            } else cards += pin.copy(pinned = true)
        }
        val pinnedSongs = cards.filter { it.kind == CardKind.Song }.mapNotNull { it.videoId }.toSet() + taste.skippedIds
        val songs = taste.speedDial(27).toMutableList()
        // little history yet: fill a full page with what you played lately and your likes
        if (songs.size < 27) songs += (lib.recent(60) + lib.liked()).filter { t -> songs.none { it.videoId == t.videoId } }.distinctBy { it.videoId }.take(27 - songs.size)
        cards += songs.filter { it.videoId !in pinnedSongs }.map(::songCard)
        return cards.take(27)
    }

    /** Same rules as the Windows app: agreed-on radio picks, artist affinity, nothing just heard or skipped,
     *  ≤ 2 per artist, a favourite you haven't heard today every third song. */
    private fun quickPicks(taste: Taste, radios: List<List<Track>?>, shown: Set<String>, random: kotlin.random.Random, jitter: Boolean): List<Track> {
        val candidates = LinkedHashMap<String, Pair<Track, Double>>()
        for (radio in radios) {
            radio ?: continue
            for (rank in 1 until radio.size) {
                val t = radio[rank]
                if (t.videoId in shown || t.videoId in taste.skippedIds) continue
                val c = candidates[t.videoId]
                candidates[t.videoId] = t to ((c?.second ?: 0.0) + 1.0 / (1 + 0.12 * rank))
            }
        }
        val ranked = candidates.values.map { (t, base) ->
            var s = base * (1 + 0.8 * taste.artistAffinity(t))
            if (taste.heardWithin(t.videoId, 36 * HOUR)) s *= 0.15
            if (jitter) s *= 0.6 + 0.8 * random.nextDouble()
            t to s
        }.sortedByDescending { it.second }.map { it.first }
        val fresh = ArrayDeque(Taste.capPerArtist(ranked.filter { !taste.knows(it.videoId) }, 2))
        val familiarPool = taste.lately().filter { it.track.videoId !in shown && !taste.heardWithin(it.track.videoId, 20 * HOUR) }
            .take(25).map { it.track to it.lately * (0.5 + random.nextDouble()) }.sortedByDescending { it.second }.map { it.first }
        val familiar = ArrayDeque(Taste.capPerArtist(familiarPool, 1).take(6))
        ranked.filter { t -> taste.knows(t.videoId) && !taste.heardWithin(t.videoId, 20 * HOUR) && familiar.none { it.videoId == t.videoId } }
            .take(2).forEach { familiar.addLast(it) }

        val picks = mutableListOf<Track>()
        val perArtist = HashMap<String, Int>()
        while (picks.size < 20 && (fresh.isNotEmpty() || familiar.isNotEmpty())) {
            val next = if ((picks.size % 3 == 1 && familiar.isNotEmpty()) || fresh.isEmpty()) familiar.removeFirst() else fresh.removeFirst()
            if (picks.any { it.videoId == next.videoId }) continue
            val key = next.artists.firstOrNull()?.let(Taste::artistKey) ?: next.videoId
            if ((perArtist[key] ?: 0) >= 2) continue
            perArtist[key] = (perArtist[key] ?: 0) + 1
            picks += next
        }
        return picks
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
