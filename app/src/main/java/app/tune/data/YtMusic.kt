package app.tune.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

// ======================= null-safe JSON navigation (port of Tune.Core Json.cs) =======================

fun Any?.at(vararg path: Any): Any? {
    var cur: Any? = this
    for (step in path) {
        cur = when {
            cur is JSONObject && step is String -> if (cur.has(step) && !cur.isNull(step)) cur.get(step) else null
            cur is JSONArray && step is Int -> if (step in 0 until cur.length()) cur.opt(step) else null
            else -> null
        }
        if (cur == null) return null
    }
    return cur
}

fun Any?.str(vararg path: Any): String? = at(*path) as? String

fun Any?.obj(vararg path: Any): JSONObject? = at(*path) as? JSONObject

fun Any?.items(vararg path: Any): List<JSONObject> {
    val a = (if (path.isEmpty()) this else at(*path)) as? JSONArray ?: return emptyList()
    return (0 until a.length()).mapNotNull { a.opt(it) as? JSONObject }
}

fun Any?.runsText(): String? {
    str("simpleText")?.let { return it }
    val runs = items("runs")
    if (runs.isEmpty()) return null
    return runs.joinToString("") { it.optString("text") }.ifEmpty { null }
}

fun Any?.bestThumbnail(): String? =
    items("thumbnails").filter { it.has("url") }.maxByOrNull { it.optInt("width") }?.optString("url")

/** First descendant value stored under [key] (depth-first). */
fun Any?.find(key: String): JSONObject? {
    when (this) {
        is JSONObject -> {
            if (has(key) && opt(key) is JSONObject) return getJSONObject(key)
            for (k in keys()) opt(k).find(key)?.let { return it }
        }
        is JSONArray -> for (i in 0 until length()) opt(i).find(key)?.let { return it }
    }
    return null
}

// ======================= InnerTube client =======================

/** Anonymous client for YouTube Music's internal API (no login). */
class InnerTube(private val http: OkHttpClient) {
    private val clientVersion = "1." + SimpleDateFormat("yyyyMMdd", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }
        .format(Date(System.currentTimeMillis() - 7L * 86_400_000)) + ".01.00"
    @Volatile private var visitorData: String? = null

    fun post(endpoint: String, body: JSONObject): JSONObject {
        val payload = JSONObject().put("context", JSONObject()
            .put("client", JSONObject().put("clientName", "WEB_REMIX").put("clientVersion", clientVersion).put("hl", "en").put("gl", "NP"))
            .put("user", JSONObject()))
        for (k in body.keys()) payload.put(k, body.get(k))
        val req = Request.Builder()
            .url("https://music.youtube.com/youtubei/v1/$endpoint?prettyPrint=false")
            .post(payload.toString().toRequestBody("application/json".toMediaType()))
            .header("User-Agent", UA)
            .header("Origin", "https://music.youtube.com")
            .header("Referer", "https://music.youtube.com/")
            .header("Accept-Language", "en")
            .apply { visitorData?.let { header("X-Goog-Visitor-Id", it) } }
            .build()
        http.newCall(req).execute().use { resp ->
            if (!resp.isSuccessful) throw java.io.IOException("InnerTube $endpoint: HTTP ${resp.code}")
            val json = JSONObject(resp.body.string())
            // continuations are only honoured when the anonymous visitor id is echoed back
            if (visitorData == null) visitorData = json.str("responseContext", "visitorData")
            return json
        }
    }

    companion object {
        const val UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36"
    }
}

// ======================= parsers (port of ItemParser / SearchParser / BrowseParser / RadioParser) =======================

object ItemParser {
    private const val SEP = " • "
    private val typeLabels = setOf("Song", "Video", "Album", "Single", "EP", "Artist", "Playlist", "Episode", "Podcast", "Profile", "Station")
    private val skipped = setOf("Episode", "Podcast", "Profile")
    private val durationRx = Regex("^\\d{1,2}(:\\d{2}){1,2}$")

    fun pageType(endpoint: Any?) = endpoint.str("browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType")

    fun parseDuration(text: String?): Int? {
        val t = text?.trim() ?: return null
        if (!durationRx.matches(t)) return null
        return t.split(':').fold(0) { acc, p -> acc * 60 + p.toInt() }
    }

    private fun runs(text: Any?) = text.items("runs")

    fun artists(text: Any?, skipLeadingTypeLabel: Boolean): List<ArtistRef> {
        val linked = runs(text).filter { pageType(it.obj("navigationEndpoint")) in setOf("MUSIC_PAGE_TYPE_ARTIST", "MUSIC_PAGE_TYPE_USER_CHANNEL") }
            .mapNotNull { r -> r.optString("text").takeIf { it.isNotBlank() }?.let { ArtistRef(it, r.str("navigationEndpoint", "browseEndpoint", "browseId")) } }
        if (linked.isNotEmpty()) return linked
        for (seg in segments(text, skipLeadingTypeLabel)) {
            if (parseDuration(seg) != null || looksLikeCount(seg) || isYear(seg)) continue
            return listOf(ArtistRef(seg, null))
        }
        return emptyList()
    }

    fun album(text: Any?): AlbumRef? = runs(text).firstOrNull { pageType(it.obj("navigationEndpoint")) == "MUSIC_PAGE_TYPE_ALBUM" }
        ?.let { AlbumRef(it.optString("text"), it.str("navigationEndpoint", "browseEndpoint", "browseId")) }

    fun segments(text: Any?, skipLeadingTypeLabel: Boolean): List<String> {
        val all = (text.runsText() ?: "").split(SEP).map { it.trim() }.filter { it.isNotEmpty() }.toMutableList()
        if (skipLeadingTypeLabel && all.isNotEmpty() && all[0] in typeLabels) all.removeAt(0)
        return all
    }

    private fun label(text: Any?): String? = (text.runsText() ?: "").split(SEP)[0].trim().takeIf { it in typeLabels }
    private fun looksLikeCount(s: String) = listOf(" views", " plays", " songs", " subscribers", " monthly audience").any { s.endsWith(it) }
    private fun isYear(s: String) = s.length == 4 && s.all { it.isDigit() }
    private fun column(r: JSONObject, i: Int) = r.at("flexColumns", i, "musicResponsiveListItemFlexColumnRenderer", "text")
    private fun fixedColumn(r: JSONObject, i: Int) = r.at("fixedColumns", i, "musicResponsiveListItemFixedColumnRenderer", "text")

    fun responsiveItem(r: JSONObject, fallbackArtists: List<ArtistRef>? = null, albumContext: AlbumRef? = null, thumbContext: String? = null): Card? {
        val col0 = column(r, 0); val col1 = column(r, 1)
        val title = col0.runsText()?.takeIf { it.isNotBlank() } ?: return null
        val thumb = r.at("thumbnail", "musicThumbnailRenderer", "thumbnail").bestThumbnail()
        val label = label(col1)
        val videoId = r.str("playlistItemData", "videoId")
            ?: runs(col0).firstNotNullOfOrNull { it.str("navigationEndpoint", "watchEndpoint", "videoId") }
            ?: r.str("overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint", "watchEndpoint", "videoId")
        if (videoId != null) {
            if (label != null && label in skipped) return null
            var artists = artists(col1, true)
            var i = 2
            while (artists.isEmpty() && i < 4) { artists = artists(column(r, i), false); i++ }
            if (artists.isEmpty() && !fallbackArtists.isNullOrEmpty()) artists = fallbackArtists
            val album = album(col1) ?: album(column(r, 2)) ?: albumContext
            val duration = segments(col1, true).firstNotNullOfOrNull { parseDuration(it) } ?: parseDuration(fixedColumn(r, 0).runsText())
            val track = Track(videoId, title, artists, album, duration, thumb ?: thumbContext)
            return Card(CardKind.Song, title, track.artistLine, track.thumbnail, videoId, track = track)
        }
        val endpoint = r.obj("navigationEndpoint")
        val browseId = endpoint.str("browseEndpoint", "browseId") ?: return null
        val subtitle = segments(col1, true).filter { !looksLikeCount(it) || label == "Playlist" }.joinToString(SEP)
        return browseCard(pageType(endpoint), title, subtitle, thumb, browseId)
    }

    fun twoRowItem(r: JSONObject): Card? {
        val title = r.at("title").runsText()?.takeIf { it.isNotBlank() } ?: return null
        val thumb = r.at("thumbnailRenderer", "musicThumbnailRenderer", "thumbnail").bestThumbnail()
        val sub = r.at("subtitle")
        val endpoint = r.obj("navigationEndpoint")
        endpoint.str("watchEndpoint", "videoId")?.let { videoId ->
            val track = Track(videoId, title, artists(sub, true), album(sub), null, thumb)
            return Card(CardKind.Song, title, track.artistLine, thumb, videoId, track = track)
        }
        val browseId = endpoint.str("browseEndpoint", "browseId") ?: return null
        return browseCard(pageType(endpoint), title, segments(sub, true).joinToString(SEP), thumb, browseId)
    }

    private fun browseCard(pageType: String?, title: String, subtitle: String?, thumb: String?, browseId: String): Card? = when (pageType) {
        "MUSIC_PAGE_TYPE_ALBUM", "MUSIC_PAGE_TYPE_AUDIOBOOK" -> Card(CardKind.Album, title, subtitle, thumb, browseId = browseId)
        "MUSIC_PAGE_TYPE_ARTIST" -> Card(CardKind.Artist, title, subtitle, thumb, browseId = browseId)
        "MUSIC_PAGE_TYPE_PLAYLIST" -> Card(CardKind.Playlist, title, subtitle, thumb, browseId = browseId, playlistId = browseId.removePrefix("VL"))
        else -> null
    }

    fun <T> safeMap(nodes: List<JSONObject>, parse: (JSONObject) -> T?): List<T> = nodes.mapNotNull { runCatching { parse(it) }.getOrNull() }
}

private fun listItems(contents: List<JSONObject>) = contents.mapNotNull { it.obj("musicResponsiveListItemRenderer") }

object Parsers {
    fun search(root: JSONObject): SearchResults {
        var top: Card? = null
        val cards = mutableListOf<Card>()
        val sections = root.at("contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents")
        for (section in sections.items()) {
            val cardShelf = section.obj("musicCardShelfRenderer")
            if (cardShelf != null) {
                if (top == null) top = topResult(cardShelf)
                val fallback = top?.takeIf { it.kind == CardKind.Artist }?.let { listOf(ArtistRef(it.title, it.browseId)) }
                cards += ItemParser.safeMap(listItems(cardShelf.items("contents"))) { ItemParser.responsiveItem(it, fallback) }
                continue
            }
            val shelf = section.obj("itemSectionRenderer") ?: section.obj("musicShelfRenderer")
            cards += ItemParser.safeMap(listItems(shelf.items("contents"))) { ItemParser.responsiveItem(it) }
        }
        return SearchResults(
            top,
            cards.mapNotNull { it.track }.distinctBy { it.videoId },
            cards.filter { it.kind == CardKind.Album },
            cards.filter { it.kind == CardKind.Artist },
            cards.filter { it.kind == CardKind.Playlist },
        )
    }

    private fun topResult(cardShelf: JSONObject): Card? {
        val titleRun = cardShelf.obj("title", "runs", 0)
        val title = titleRun.str("text") ?: return null
        val endpoint = titleRun.obj("navigationEndpoint") ?: cardShelf.obj("onTap")
        val thumb = cardShelf.at("thumbnail", "musicThumbnailRenderer", "thumbnail").bestThumbnail()
        val sub = cardShelf.at("subtitle")
        endpoint.str("watchEndpoint", "videoId")?.let { videoId ->
            val track = Track(videoId, title, ItemParser.artists(sub, true), ItemParser.album(sub),
                ItemParser.segments(sub, true).firstNotNullOfOrNull { ItemParser.parseDuration(it) }, thumb)
            return Card(CardKind.Song, title, track.artistLine, thumb, videoId, track = track)
        }
        val browseId = endpoint.str("browseEndpoint", "browseId") ?: return null
        val kind = when (ItemParser.pageType(endpoint)) {
            "MUSIC_PAGE_TYPE_ARTIST" -> CardKind.Artist
            "MUSIC_PAGE_TYPE_ALBUM" -> CardKind.Album
            "MUSIC_PAGE_TYPE_PLAYLIST" -> CardKind.Playlist
            else -> return null
        }
        return Card(kind, title, sub.runsText(), thumb, browseId = browseId,
            playlistId = if (kind == CardKind.Playlist) browseId.removePrefix("VL") else null)
    }

    data class HomePage(val shelves: List<Shelf>, val continuation: String?)

    fun home(root: JSONObject): HomePage {
        val list = root.obj("continuationContents", "sectionListContinuation")
            ?: root.obj("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer")
        val shelves = list.items("contents").mapNotNull { it.obj("musicCarouselShelfRenderer") }.mapNotNull { carousel(it) }.filter { it.items.isNotEmpty() }
        return HomePage(shelves, list.str("continuations", 0, "nextContinuationData", "continuation"))
    }

    private fun carousel(shelf: JSONObject): Shelf? {
        val title = shelf.at("header", "musicCarouselShelfBasicHeaderRenderer", "title").runsText()?.takeIf { it.isNotBlank() } ?: return null
        val items = ItemParser.safeMap(shelf.items("contents")) { c ->
            c.obj("musicTwoRowItemRenderer")?.let { ItemParser.twoRowItem(it) }
                ?: c.obj("musicResponsiveListItemRenderer")?.let { ItemParser.responsiveItem(it) }
        }
        return Shelf(title, items)
    }

    private fun header(root: JSONObject) =
        root.obj("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "musicResponsiveHeaderRenderer")
            ?: root.find("musicResponsiveHeaderRenderer")

    private fun trackShelf(root: JSONObject) =
        root.at("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents").items()
            .firstNotNullOfOrNull { it.obj("musicShelfRenderer") ?: it.obj("musicPlaylistShelfRenderer") }

    fun album(root: JSONObject, browseId: String?): Collection {
        val h = header(root)
        val title = h.at("title").runsText() ?: "Album"
        val thumb = h.at("thumbnail", "musicThumbnailRenderer", "thumbnail").bestThumbnail()
        val albumArtists = ItemParser.artists(h.at("straplineTextOne"), false)
        val ref = AlbumRef(title, browseId)
        val tracks = ItemParser.safeMap(listItems(trackShelf(root).items("contents"))) { ItemParser.responsiveItem(it, albumArtists, ref, thumb) }
            .mapNotNull { it.track?.copy(album = ref, thumbnail = thumb ?: it.track.thumbnail) }
        return Collection(title, h.at("subtitle").runsText(), thumb, tracks)
    }

    data class TrackPage(val tracks: List<Track>, val continuation: String?)

    fun playlist(root: JSONObject): Pair<Collection, String?> {
        val h = header(root)
        val shelf = trackShelf(root)
        val page = tracks(shelf.items("contents"))
        val subtitle = listOfNotNull(h.at("subtitle").runsText(), h.at("secondSubtitle").runsText()).filter { it.isNotBlank() }.joinToString(" • ")
        val c = Collection(h.at("title").runsText() ?: "Playlist", subtitle.ifEmpty { null },
            h.at("thumbnail", "musicThumbnailRenderer", "thumbnail").bestThumbnail(), page.tracks)
        return c to (page.continuation ?: shelf.str("continuations", 0, "nextContinuationData", "continuation"))
    }

    fun playlistContinuation(root: JSONObject): TrackPage {
        val items = root.items("onResponseReceivedActions").flatMap { it.items("appendContinuationItemsAction", "continuationItems") } +
            root.items("continuationContents", "musicPlaylistShelfContinuation", "contents")
        val page = tracks(items)
        return page.copy(continuation = page.continuation
            ?: root.str("continuationContents", "musicPlaylistShelfContinuation", "continuations", 0, "nextContinuationData", "continuation"))
    }

    private fun tracks(contents: List<JSONObject>): TrackPage = TrackPage(
        ItemParser.safeMap(listItems(contents)) { ItemParser.responsiveItem(it) }.mapNotNull { it.track },
        contents.mapNotNull { it.str("continuationItemRenderer", "continuationEndpoint", "continuationCommand", "token") }.lastOrNull(),
    )

    fun artist(root: JSONObject, browseId: String?): ArtistPage {
        val h = root.obj("header", "musicImmersiveHeaderRenderer") ?: root.obj("header", "musicVisualHeaderRenderer") ?: root.find("musicImmersiveHeaderRenderer")
        val name = h.at("title").runsText() ?: "Artist"
        val self = listOf(ArtistRef(name, browseId))
        val topSongs = mutableListOf<Track>()
        val shelves = mutableListOf<Shelf>()
        for (section in root.at("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents").items()) {
            val songs = section.obj("musicShelfRenderer")
            if (songs != null && topSongs.isEmpty()) {
                topSongs += ItemParser.safeMap(listItems(songs.items("contents"))) { ItemParser.responsiveItem(it, self) }.mapNotNull { it.track }
            } else section.obj("musicCarouselShelfRenderer")?.let { carousel(it) }?.takeIf { it.items.isNotEmpty() }?.let { shelves += it }
        }
        return ArtistPage(name, h.at("thumbnail", "musicThumbnailRenderer", "thumbnail").bestThumbnail(), topSongs, shelves)
    }

    fun radio(root: JSONObject): List<Track> {
        val panel = root.find("playlistPanelRenderer")
        val videos = panel.items("contents").mapNotNull {
            it.obj("playlistPanelVideoRenderer") ?: it.obj("playlistPanelVideoWrapperRenderer", "primaryRenderer", "playlistPanelVideoRenderer")
        }
        return ItemParser.safeMap(videos) { v ->
            val id = v.str("videoId") ?: return@safeMap null
            val title = v.at("title").runsText()?.takeIf { it.isNotBlank() } ?: return@safeMap null
            val byline = v.at("longBylineText")
            Track(id, title, ItemParser.artists(byline, false), ItemParser.album(byline),
                ItemParser.parseDuration(v.at("lengthText").runsText()), v.at("thumbnail").bestThumbnail())
        }.distinctBy { it.videoId }
    }

    fun lyricsBrowseId(node: Any?): String? {
        when (node) {
            is JSONObject -> {
                node.str("browseEndpoint", "browseId")?.takeIf { it.startsWith("MPLYt") }?.let { return it }
                for (k in node.keys()) lyricsBrowseId(node.opt(k))?.let { return it }
            }
            is JSONArray -> for (i in 0 until node.length()) lyricsBrowseId(node.opt(i))?.let { return it }
        }
        return null
    }

    fun plainLyrics(root: JSONObject): Pair<String, String?>? {
        val shelf = root.find("musicDescriptionShelfRenderer")
        val text = shelf.at("description").runsText()?.trim()?.takeIf { it.isNotBlank() } ?: return null
        return text to shelf.at("footer").runsText()
    }
}

// ======================= API =======================

class YtMusic(private val client: InnerTube) {
    fun search(query: String) = Parsers.search(client.post("search", JSONObject().put("query", query)))

    fun home(): List<Shelf> {
        var page = Parsers.home(client.post("browse", JSONObject().put("browseId", "FEmusic_home")))
        val shelves = page.shelves.toMutableList()
        repeat(2) {
            val token = page.continuation ?: return shelves
            page = runCatching { Parsers.home(client.post("browse", JSONObject().put("continuation", token))) }.getOrElse { return shelves }
            shelves += page.shelves
        }
        return shelves
    }

    fun radio(videoId: String) = Parsers.radio(client.post("next", JSONObject()
        .put("videoId", videoId).put("playlistId", "RDAMVM$videoId").put("isAudioOnly", true)))

    fun album(browseId: String) = Parsers.album(client.post("browse", JSONObject().put("browseId", browseId)), browseId)

    fun playlist(playlistId: String): Collection {
        val browseId = if (playlistId.startsWith("VL")) playlistId else "VL$playlistId"
        val (c, first) = Parsers.playlist(client.post("browse", JSONObject().put("browseId", browseId)))
        val tracks = c.tracks.toMutableList()
        var token = first
        repeat(4) {
            val t = token ?: return c.copy(tracks = tracks)
            val more = runCatching { Parsers.playlistContinuation(client.post("browse", JSONObject().put("continuation", t))) }.getOrNull()
                ?: return c.copy(tracks = tracks)
            tracks += more.tracks
            token = more.continuation
        }
        return c.copy(tracks = tracks)
    }

    fun artist(browseId: String) = Parsers.artist(client.post("browse", JSONObject().put("browseId", browseId)), browseId)

    fun ytLyrics(videoId: String): Pair<String, String?>? {
        val next = client.post("next", JSONObject().put("videoId", videoId).put("isAudioOnly", true))
        val id = Parsers.lyricsBrowseId(next) ?: return null
        return Parsers.plainLyrics(client.post("browse", JSONObject().put("browseId", id)))
    }
}
