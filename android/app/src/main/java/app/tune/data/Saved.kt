package app.tune.data

import org.json.JSONArray
import org.json.JSONObject

/** Followed artists (synced setting; same JSON as the Windows app). */
const val ARTISTS_KEY = "library.artists"

/** Saved albums (synced setting; same JSON as the Windows app). */
const val ALBUMS_KEY = "library.albums"

data class SavedArtist(val browseId: String, val name: String, val thumbnail: String?, val addedAt: Long)

data class SavedAlbum(val browseId: String, val title: String, val artist: String?, val thumbnail: String?, val year: String?, val addedAt: Long)

/**
 * Followed artists and saved albums: two small JSON lists in synced settings, so they follow the account to every
 * device (newest change of a whole list wins). Keys match the Windows app (camelCase: browseId, thumbnailUrl…).
 */
fun Library.followedArtists(): List<SavedArtist> = readList(ARTISTS_KEY) {
    SavedArtist(it.getString("browseId"), it.optString("name"), it.optStr("thumbnailUrl"), it.optLong("addedAt"))
}

fun Library.savedAlbums(): List<SavedAlbum> = readList(ALBUMS_KEY) {
    SavedAlbum(it.getString("browseId"), it.optString("title"), it.optStr("artist"), it.optStr("thumbnailUrl"), it.optStr("year"), it.optLong("addedAt"))
}

fun Library.isFollowing(browseId: String?) = browseId != null && followedArtists().any { it.browseId == browseId }

fun Library.isAlbumSaved(browseId: String?) = browseId != null && savedAlbums().any { it.browseId == browseId }

fun Library.setFollowing(browseId: String, name: String, thumbnail: String?, follow: Boolean) {
    val list = followedArtists().filter { it.browseId != browseId }.toMutableList()
    if (follow) list.add(0, SavedArtist(browseId, name, thumbnail, System.currentTimeMillis()))
    setSetting(ARTISTS_KEY, JSONArray(list.map {
        JSONObject().put("browseId", it.browseId).put("name", it.name).put("thumbnailUrl", it.thumbnail ?: JSONObject.NULL).put("addedAt", it.addedAt)
    }).toString())
}

fun Library.setAlbumSaved(browseId: String, title: String, artist: String?, thumbnail: String?, year: String?, save: Boolean) {
    val list = savedAlbums().filter { it.browseId != browseId }.toMutableList()
    if (save) list.add(0, SavedAlbum(browseId, title, artist, thumbnail, year, System.currentTimeMillis()))
    setSetting(ALBUMS_KEY, JSONArray(list.map {
        JSONObject().put("browseId", it.browseId).put("title", it.title).put("artist", it.artist ?: JSONObject.NULL)
            .put("thumbnailUrl", it.thumbnail ?: JSONObject.NULL).put("year", it.year ?: JSONObject.NULL).put("addedAt", it.addedAt)
    }).toString())
}

private fun <T> Library.readList(key: String, parse: (JSONObject) -> T): List<T> {
    val json = setting(key) ?: return emptyList()
    return runCatching {
        val arr = JSONArray(json)
        (0 until arr.length()).mapNotNull { i -> runCatching { parse(arr.getJSONObject(i)) }.getOrNull() }
    }.getOrDefault(emptyList())
}

private fun JSONObject.optStr(key: String): String? = if (isNull(key)) null else optString(key).ifEmpty { null }
