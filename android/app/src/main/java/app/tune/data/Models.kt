package app.tune.data

import org.json.JSONArray
import org.json.JSONObject

data class ArtistRef(val name: String, val browseId: String?)

data class AlbumRef(val title: String, val browseId: String?)

data class Track(
    val videoId: String,
    val title: String,
    val artists: List<ArtistRef>,
    val album: AlbumRef?,
    val durationSec: Int?,
    val thumbnail: String?,
) {
    val artistLine: String get() = artists.joinToString(", ") { it.name }

    fun toJson(): JSONObject = JSONObject().apply {
        put("v", videoId)
        put("t", title)
        put("a", JSONArray().apply { artists.forEach { put(JSONObject().put("n", it.name).put("b", it.browseId ?: JSONObject.NULL)) } })
        album?.let { put("al", JSONObject().put("t", it.title).put("b", it.browseId ?: JSONObject.NULL)) }
        durationSec?.let { put("d", it) }
        thumbnail?.let { put("th", it) }
    }

    companion object {
        fun fromJson(o: JSONObject): Track? = runCatching {
            val arr = o.optJSONArray("a") ?: JSONArray()
            Track(
                videoId = o.getString("v"),
                title = o.getString("t"),
                artists = (0 until arr.length()).map { arr.getJSONObject(it).let { a -> ArtistRef(a.getString("n"), a.optStringOrNull("b")) } },
                album = o.optJSONObject("al")?.let { AlbumRef(it.getString("t"), it.optStringOrNull("b")) },
                durationSec = if (o.has("d")) o.getInt("d") else null,
                thumbnail = o.optStringOrNull("th"),
            )
        }.getOrNull()

        fun fromJson(s: String?): Track? = s?.let { runCatching { fromJson(JSONObject(it)) }.getOrNull() }
    }
}

internal fun JSONObject.optStringOrNull(key: String): String? = if (isNull(key) || !has(key)) null else optString(key)

enum class CardKind { Song, Album, Playlist, Artist }

data class Card(
    val kind: CardKind,
    val title: String,
    val subtitle: String?,
    val thumbnail: String?,
    val videoId: String? = null,
    val browseId: String? = null,
    val playlistId: String? = null,
    val track: Track? = null,
)

/** A Home row; [isList] = a grid of song rows (Quick picks) instead of big cards. */
data class Shelf(val title: String, val items: List<Card>, val isLocal: Boolean = false, val isList: Boolean = false)

data class Collection(val title: String, val subtitle: String?, val thumbnail: String?, val tracks: List<Track>)

data class ArtistPage(val name: String, val thumbnail: String?, val topSongs: List<Track>, val shelves: List<Shelf>)

data class SearchResults(
    val top: Card?,
    val songs: List<Track>,
    val albums: List<Card>,
    val artists: List<Card>,
    val playlists: List<Card>,
)

/** Google image servers resize on request; ytimg has fixed sizes. Same rules as the Windows app. */
object Thumbs {
    private val googleSize = Regex("=w\\d+-h\\d+")
    private val ytimgSize = Regex("/(default|mqdefault|hqdefault|sddefault)\\.jpg")

    fun sized(url: String?, px: Int): String? {
        if (url == null) return null
        if (googleSize.containsMatchIn(url)) return googleSize.replace(url, "=w$px-h$px")
        if (ytimgSize.containsMatchIn(url))
            return ytimgSize.replace(url, if (px <= 120) "/mqdefault.jpg" else if (px <= 200) "/hqdefault.jpg" else "/maxresdefault.jpg")
        return url
    }
}

fun formatDuration(sec: Int?): String {
    if (sec == null || sec < 0) return ""
    val h = sec / 3600; val m = (sec % 3600) / 60; val s = sec % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%d:%02d".format(m, s)
}

/** Finds the playlist id in a pasted music.youtube.com / youtube.com link or a bare id. */
object PlaylistLinks {
    private val listParam = Regex("[?&]list=([A-Za-z0-9_-]{10,})")
    private val bareId = Regex("^(VL)?(PL|OLAK5uy_|RD|LL|FL|UU)[A-Za-z0-9_-]{8,}$")

    fun parse(text: String?): String? {
        val t = text?.trim().orEmpty()
        listParam.find(t)?.let { return it.groupValues[1] }
        return if (bareId.matches(t)) t.removePrefix("VL") else null
    }
}
