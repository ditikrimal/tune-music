package app.tune.data

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.max
import kotlin.math.pow

// Port of Tune.Core/Feed/Vibe.cs: keep the two in step.

/** What a radio chip changes: Discovery (Popular, Discover, Deep cuts), Mood (Chill, Downbeat, Pump-up…), Era (2010s…), Genre. */
enum class VibeKind { Discovery, Mood, Era, Genre }

/** One of YouTube Music's radio tuner chips for a song: a tag for the song's vibe, and the tuned radio behind it. */
data class VibeChip(val name: String, val playlistId: String, val params: String?, val kind: VibeKind) {
    fun toJson(): JSONObject = JSONObject().put("n", name).put("p", playlistId).put("q", params ?: JSONObject.NULL).put("k", kind.name)

    companion object {
        fun fromJson(o: JSONObject) = VibeChip(o.getString("n"), o.getString("p"), o.optStringOrNull("q"),
            VibeKind.entries.firstOrNull { it.name == o.optString("k") } ?: VibeKind.Genre)

        private val discovery = setOf("popular", "discover", "deep cuts", "familiar")
        // YouTube Music's moods & moments, as its chips and Moods page name them
        private val moods = setOf("chill", "downbeat", "upbeat", "romance", "party", "pump-up", "workout", "focus", "sleep", "feel good",
            "energize", "energy boosters", "relax", "commute", "sad", "heartbreak", "happy", "gaming", "dance", "mellow", "calm",
            "motivation", "hype", "love", "late night", "summer", "travel", "study", "rainy day", "moody", "dark", "emotional",
            "melancholy", "feel-good", "driving", "good vibes", "aggressive", "angry", "peaceful", "sentimental", "nostalgic", "romantic")
        private val era = Regex("""^\d{2,4}s$""")

        fun kindOf(name: String): VibeKind = name.lowercase().let {
            when {
                it in discovery -> VibeKind.Discovery
                it in moods -> VibeKind.Mood
                era.matches(it) || it == "new releases" -> VibeKind.Era
                else -> VibeKind.Genre
            }
        }
    }
}

/** A song's radio: songs, tuner chips, more songs ([continuation]) and its Related tab ([relatedId]). */
data class RadioPage(val tracks: List<Track>, val chips: List<VibeChip>, val continuation: String?, val relatedId: String? = null)

/** A song's Related tab: "You might also like" songs, similar artists, recommended playlists. */
data class RelatedPage(val songs: List<Track>, val artists: List<Card>, val playlists: List<Card>)

/** What YouTube Music says about a song (fetched at [at]). */
data class SongSignals(val videoId: String, val radio: List<Track>, val chips: List<VibeChip>, val relatedId: String?, val at: Long) {
    fun toJson(): JSONObject = JSONObject().put("v", videoId).put("at", at).put("r", relatedId ?: JSONObject.NULL)
        .put("radio", JSONArray().apply { radio.forEach { put(it.toJson()) } })
        .put("chips", JSONArray().apply { chips.forEach { put(it.toJson()) } })

    companion object {
        fun fromJson(s: String): SongSignals? = runCatching {
            val o = JSONObject(s)
            val r = o.getJSONArray("radio"); val c = o.getJSONArray("chips")
            SongSignals(o.getString("v"), (0 until r.length()).mapNotNull { Track.fromJson(r.getJSONObject(it)) },
                (0 until c.length()).map { VibeChip.fromJson(c.getJSONObject(it)) }, o.optStringOrNull("r"), o.getLong("at"))
        }.getOrNull()
    }
}

/**
 * YouTube Music's own knowledge of each song (radio, vibe chips, Related), fetched once and cached in the library
 * for a few days. Concurrent asks for the same song share one request. Blocking: call off the main thread.
 */
class Signals(private val lib: Library, private val api: YtMusic) {
    private val mem = ConcurrentHashMap<String, SongSignals>()
    private val tuned = ConcurrentHashMap<String, Pair<Long, List<Track>>>()
    private val related = ConcurrentHashMap<String, Pair<Long, RelatedPage>>()
    private val locks = ConcurrentHashMap<String, Any>()

    /** Whatever is known about the song, however old (no network). */
    fun cached(videoId: String): SongSignals? = mem[videoId] ?: lib.signals(videoId)?.also { mem[videoId] = it }

    /** The song's radio and chips: cached if fresh, else from YouTube Music (old data if that fails). */
    fun get(videoId: String, fresh: Boolean = false): SongSignals? {
        val now = System.currentTimeMillis()
        if (!fresh) cached(videoId)?.takeIf { now - it.at < FRESH }?.let { return it }
        synchronized(locks.getOrPut(videoId) { Any() }) {
            if (!fresh) mem[videoId]?.takeIf { now - it.at < FRESH }?.let { return it }
            return try {
                val page = api.radioPage(videoId)
                if (page.tracks.isEmpty()) cached(videoId)
                else SongSignals(videoId, page.tracks, page.chips, page.relatedId, now).also { mem[videoId] = it; lib.saveSignals(it) }
            } catch (e: Exception) {
                Log.w("Tune", "Signals for $videoId unavailable: $e")
                cached(videoId)
            } finally {
                locks.remove(videoId)
            }
        }
    }

    /** The song's radio tuned with one of its chips (e.g. only the "Chill" side of it). */
    fun tuned(videoId: String, chip: VibeChip): List<Track> {
        val key = "$videoId|${chip.name}"
        val hit = tuned[key]
        if (hit != null && System.currentTimeMillis() - hit.first < 6 * HOUR) return hit.second
        return runCatching { api.radioPage(videoId, chip).tracks }.onSuccess { tuned[key] = System.currentTimeMillis() to it }
            .getOrElse { Log.w("Tune", "Tuned radio ${chip.name} for $videoId unavailable: $it"); hit?.second.orEmpty() }
    }

    /** The song's "You might also like", similar artists and playlists. */
    fun related(videoId: String): RelatedPage? {
        val hit = related[videoId]
        if (hit != null && System.currentTimeMillis() - hit.first < 12 * HOUR) return hit.second
        val rid = get(videoId)?.relatedId ?: return null
        return runCatching { api.related(rid) }.onSuccess { related[videoId] = System.currentTimeMillis() to it }
            .getOrElse { Log.w("Tune", "Related for $videoId unavailable: $it"); hit?.second }
    }

    private companion object {
        const val HOUR = 3_600_000L
        const val FRESH = 72 * HOUR
    }
}

/** One song of a listening session: [weight] > 0 for songs you let play (newest weighs most), < 0 for songs you skipped. */
data class SessionSong(val track: Track, val weight: Double, val at: Long) {
    val skipped get() = weight < 0
}

/**
 * What you're listening to right now: the run of plays and skips without a long break, newest first. Songs you let play
 * pull recommendations towards them (the latest most), skipped songs push away. [mood] is a vibe chip most of them share.
 */
class Session(val songs: List<SessionSong>) {
    val played get() = songs.filter { !it.skipped }
    val skippedSongs get() = songs.filter { it.skipped }
    val isEmpty get() = played.isEmpty()

    /** The chip most songs you let play share: at least two have it and it carries close to half the weight. */
    fun mood(chips: (String) -> List<VibeChip>?, kind: VibeKind = VibeKind.Mood): String? {
        val total = played.sumOf { it.weight }
        if (total <= 0) return null
        val score = HashMap<String, Pair<Double, Int>>()
        for (s in songs) for (c in chips(s.track.videoId).orEmpty().filter { it.kind == kind }) {
            val e = score[c.name.lowercase()] ?: (0.0 to 0)
            score[c.name.lowercase()] = (e.first + if (s.skipped) s.weight * 0.5 else s.weight) to (e.second + if (s.skipped) 0 else 1)
        }
        val best = score.filter { it.value.second >= 2 && it.value.first >= 0.45 * total }
            .maxByOrNull { it.value.first * specificity(it.key) }?.key ?: return null
        // the chip's own spelling
        return songs.firstNotNullOfOrNull { s -> chips(s.track.videoId)?.firstOrNull { it.name.lowercase() == best }?.name }
    }

    companion object {
        const val GAP = 45 * 60_000L
        private const val FADE = 0.65
        private const val SKIP_WEIGHT = 0.7
        // YouTube tags many songs "Romance", "Party", "Upbeat" or "Chill"; a shared "Pump-up" or "Downbeat" says much more
        private val broad = setOf("romance", "party", "upbeat", "chill", "feel good", "love", "dance")
        private fun specificity(chip: String) = if (chip in broad) 0.75 else 1.25

        /** [current] leads the session; with nothing playing, the latest session may have ended up to [maxAge] ago. */
        fun build(plays: List<Pair<Track, Long>>, skips: List<Pair<String, Long>>, track: (String) -> Track?, now: Long,
                  current: Track? = null, maxAge: Long = GAP): Session {
            data class Ev(val id: String, val track: Track?, val at: Long, val skip: Boolean)
            val events = (plays.map { Ev(it.first.videoId, it.first, it.second, false) } + skips.map { Ev(it.first, null, it.second, true) })
                .sortedByDescending { it.at }
            val songs = mutableListOf<SessionSong>()
            val seen = HashSet<String>()
            var played = 0; var skipped = 0
            if (current != null) { songs += SessionSong(current, 1.0, now); seen += current.videoId; played = 1 }
            var last = now
            var first = true
            for (e in events) {
                val gap = last - e.at
                if (if (first && current == null) gap > maxAge else gap > GAP) break
                first = false
                last = e.at
                if (!seen.add(e.id)) continue
                if (e.skip) {
                    if (skipped >= 4) continue
                    val t = e.track ?: track(e.id) ?: continue
                    songs += SessionSong(t, -SKIP_WEIGHT * FADE.pow(skipped + played * 0.5), e.at)
                    skipped++
                } else if (played < 6) {
                    songs += SessionSong(e.track!!, FADE.pow(played), e.at)
                    played++
                }
                if (played >= 6 && skipped >= 4) break
            }
            return Session(songs)
        }
    }
}

/**
 * Ranks candidates the way YouTube's "Up next" feels: songs several of your recent songs' radios agree on come first,
 * the vibe's tuned radio adds to them, skipped songs' neighbours sink, loved artists rise, artists don't repeat back to back.
 */
object UpNext {
    /** A list of suggestions with how much it counts (negative: steer away). */
    data class Source(val tracks: List<Track>, val weight: Double, val take: Int = Int.MAX_VALUE)

    private const val RANK_FADE = 0.08

    fun rank(sources: List<Source>, exclude: Set<String>, count: Int, taste: Taste? = null, avoidArtists: Set<String> = emptySet(),
             random: kotlin.random.Random? = null, excludeTitles: Set<String> = emptySet()): List<Track> {
        data class S(val track: Track, var score: Double, var votes: Int)
        val scores = LinkedHashMap<String, S>()
        for (src in sources) for ((rank, t) in src.tracks.withIndex()) {
            if (rank >= src.take) break
            if (t.videoId in exclude) continue
            val e = scores.getOrPut(t.videoId) { S(t, 0.0, 0) }
            e.score += src.weight / (1 + RANK_FADE * rank)
            if (src.weight > 0) e.votes++
        }
        val ranked = scores.values.filter { it.votes > 0 }.map { e ->
            var s = e.score
            if (taste != null) s *= 1 + 0.35 * taste.artistAffinity(e.track)
            if (e.track.artists.any { Taste.artistKey(it) in avoidArtists }) s *= 0.45
            if (random != null) s *= 0.75 + 0.5 * random.nextDouble()
            e.track to s
        }.filter { it.second > 0 }.sortedByDescending { it.second }.map { it.first }
            // one version of a song: no live take, remix or cover of something you just heard or that's already here
            .filter { titleKey(it.title) !in excludeTitles }.distinctBy { titleKey(it.title) }
        return spaced(ranked, count)
    }

    /** Keeps the order but never the same main artist within [window] songs (those wait a little). */
    fun spaced(ranked: List<Track>, count: Int, window: Int = 3): List<Track> {
        fun key(t: Track) = t.artists.firstOrNull()?.let(Taste::artistKey) ?: t.videoId
        val picks = mutableListOf<Track>()
        val waiting = mutableListOf<Track>()
        fun fits(t: Track) = picks.takeLast(window).none { key(it) == key(t) }
        for (t in ranked) {
            if (picks.size >= count) break
            val w = waiting.indexOfFirst(::fits)
            if (w >= 0) { picks += waiting.removeAt(w); if (picks.size >= count) break }
            if (fits(t)) picks += t else waiting += t
        }
        for (t in waiting) { if (picks.size >= count) break; picks += t }
        return picks
    }

    /** "Believer (Live at Pinkpop) - Remastered" and "Believer" are the same song. */
    fun titleKey(title: String): String {
        var t = title.lowercase()
        for ((open, close) in listOf('(' to ')', '[' to ']', '{' to '}')) {
            while (true) {
                val i = t.indexOf(open); if (i < 0) break
                val j = t.indexOf(close, i)
                t = if (j < 0) t.substring(0, i) else t.substring(0, i) + t.substring(j + 1)
            }
        }
        for (cut in listOf(" - ", " | ", " feat.", " ft.", " ft ", " featuring ", " x ")) {
            val k = t.indexOf(cut); if (k > 0) t = t.substring(0, k)
        }
        return t.filter { it.isLetterOrDigit() }.ifEmpty { title }
    }
}

/** What the engine chose: the songs, the vibe it followed (a chip name, or null) and the current song's chips. */
data class VibeResult(val tracks: List<Track>, val vibe: String?, val chips: List<VibeChip>)

/**
 * Tune's answer to YouTube's recommendations, built from what YouTube Music says about each song: the radio keeps
 * following what you're listening to (and away from what you skip), in the vibe your recent songs share or the one you
 * pick with a chip. Shared by the playback service (queue) and Home; [chips]/[vibe]/[tuning] drive the queue's chip row.
 */
class VibeEngine(private val lib: Library, val signals: Signals) {
    /** A chip the user picked for the radio ("Popular", "Chill"…); null = follow the vibe. The service re-steers on change. */
    val tuning = MutableStateFlow<String?>(null)
    /** The current song's chips and the vibe the radio follows now (for the queue). */
    val chips = MutableStateFlow<List<VibeChip>>(emptyList())
    val vibe = MutableStateFlow<String?>(null)

    fun taste(now: Long = System.currentTimeMillis()) =
        Taste(lib.plays(now - 365 * DAY), lib.liked(), lib.skips(now - 90 * DAY), now, lib.notInterested())

    fun session(current: Track?, maxAge: Long = Session.GAP): Session {
        val now = System.currentTimeMillis()
        val since = now - 12 * HOUR
        return Session.build(lib.plays(since), lib.skips(since), { lib.track(it) ?: signals.cached(it)?.radio?.firstOrNull() }, now, current, maxAge)
    }

    /** The next songs after [current], leaving out [exclude] (the queue so far). Blocking network: call on IO. */
    suspend fun upNext(current: Track, exclude: kotlin.collections.Collection<String>, count: Int): VibeResult = withContext(Dispatchers.IO) {
        val chipsNow = signals.get(current.videoId)?.chips.orEmpty()
        val (tracks, v) = rank(session(current), exclude.toHashSet(), count, tuning.value, null)
        VibeResult(tracks, v, chipsNow)
    }

    /** Ranks songs for a session (also Home's "Keep the vibe going"). */
    suspend fun rank(session: Session, exclude: HashSet<String>, count: Int, tuning: String?, random: kotlin.random.Random?): Pair<List<Track>, String?> =
        withContext(Dispatchers.IO) {
            if (session.isEmpty) return@withContext emptyList<Track>() to null
            val played = session.played.take(5)
            val skipped = session.skippedSongs.take(3)
            val byId = coroutineScope {
                (played + skipped).map { s -> async { s.track.videoId to signals.get(s.track.videoId) } }.awaitAll()
            }.mapNotNull { (id, s) -> s?.let { id to it } }.toMap()

            val taste = taste()
            session.songs.forEach { exclude += it.track.videoId }
            exclude += taste.skippedIds
            taste.lately().filter { taste.heardWithin(it.track.videoId, 3 * HOUR) }.forEach { exclude += it.track.videoId }
            exclude += lib.notInterested()

            val v = tuning ?: session.mood({ byId[it]?.chips }) ?: session.mood({ byId[it]?.chips }, VibeKind.Genre)
            val chosen = tuning != null
            val sources = mutableListOf<UpNext.Source>()
            for (s in played) byId[s.track.videoId]?.let { sources += UpNext.Source(it.radio, s.weight * if (chosen) 0.4 else 1.0) }
            if (v != null) for (s in played) {
                val chip = byId[s.track.videoId]?.chips?.firstOrNull { it.name.equals(v, ignoreCase = true) } ?: continue
                sources += UpNext.Source(signals.tuned(s.track.videoId, chip), if (chosen) 2.6 else 1.3 * max(0.5, s.weight))
                break
            }
            if (!chosen && played.isNotEmpty()) signals.related(played[0].track.videoId)?.let { sources += UpNext.Source(it.songs, 0.6) }
            val avoid = HashSet<String>()
            for (s in skipped) {
                byId[s.track.videoId]?.let { sources += UpNext.Source(it.radio, s.weight * 0.8, take = 15) }
                val a = s.track.artists.firstOrNull()?.let(Taste::artistKey) ?: continue
                if (played.none { p -> p.track.artists.any { Taste.artistKey(it) == a } }) avoid += a
            }
            val titles = played.map { UpNext.titleKey(it.track.title) }.toSet()
            UpNext.rank(sources, exclude, count, taste, avoid, random, titles) to v
        }

    /**
     * "Your chill mix", "Your pump-up mix"…: your songs grouped by the moods YouTube Music tags them with, each mixed with
     * new songs from that mood's tuned radios. Moods with at least three of your songs, strongest first.
     */
    suspend fun mixes(taste: Taste, shown: Set<String>, rows: Int, perRow: Int, random: kotlin.random.Random): List<Pair<String, List<Track>>> =
        withContext(Dispatchers.IO) {
            val yours = taste.lately().filter { it.track.videoId !in taste.skippedIds }.take(30)
            coroutineScope {
                yours.filter { signals.cached(it.track.videoId) == null }.take(8).map { async { signals.get(it.track.videoId) } }.awaitAll()
            }
            val moods = LinkedHashMap<String, MutableList<Taste.Entry>>()
            for (e in yours) for (c in signals.cached(e.track.videoId)?.chips.orEmpty().filter { it.kind == VibeKind.Mood })
                moods.getOrPut(c.name) { mutableListOf() } += e
            val result = mutableListOf<Pair<String, List<Track>>>()
            val used = HashSet(shown)
            for ((mood, songs) in moods.filter { it.value.size >= 3 }.entries.sortedByDescending { m -> m.value.sumOf { it.lately } }.take(rows)) {
                val sources = songs.take(2).mapIndexedNotNull { i, e ->
                    val chip = signals.cached(e.track.videoId)?.chips?.firstOrNull { it.name.equals(mood, ignoreCase = true) } ?: return@mapIndexedNotNull null
                    UpNext.Source(signals.tuned(e.track.videoId, chip), 1.0 / (i + 1))
                }
                val fresh = ArrayDeque(UpNext.rank(sources, used + yours.map { it.track.videoId }, perRow, taste, random = random))
                val own = ArrayDeque(songs.map { it.track }.filter { it.videoId !in used }.shuffled(random).take(perRow / 3 + 1))
                val list = mutableListOf<Track>()
                // like YouTube's mixes: mostly new songs with one of yours every third place
                while (list.size < perRow && (fresh.isNotEmpty() || own.isNotEmpty()))
                    list += if ((list.size % 3 == 0 && own.isNotEmpty()) || fresh.isEmpty()) own.removeFirst() else fresh.removeFirst()
                if (list.size < 5) continue
                list.forEach { used += it.videoId }
                result += mood to list
            }
            result
        }

    private companion object {
        const val HOUR = 3_600_000L
        const val DAY = 24 * HOUR
    }
}
