package app.tune.data

import org.json.JSONArray
import org.json.JSONObject

/**
 * Moods & genres for the Search page, kept for a week (same as Windows). Each tile's picture is the cover of the
 * first playlist on its page, fetched the first time the tile is shown and remembered with the list.
 */
class MoodsCatalog(private val library: Library, private val yt: YtMusic) {
    private val keepForMs = 7L * 86_400_000
    @Volatile private var sections: List<MoodSection>? = null
    private var at = 0L

    /** Blocking (call off the main thread). The cached tiles when fresh; on a failed refresh, the old ones. */
    @Synchronized
    fun get(): List<MoodSection> {
        if (sections == null) load()
        sections?.takeIf { it.isNotEmpty() && System.currentTimeMillis() - at < keepForMs }?.let { return it }
        val fresh = runCatching { yt.moods() }.getOrNull()?.takeIf { it.isNotEmpty() } ?: return sections.orEmpty()
        val covers = sections.orEmpty().flatMap { it.items }.filter { it.thumbnail != null }.associate { it.title to it.thumbnail }
        sections = fresh.map { s -> s.copy(items = s.items.map { it.copy(thumbnail = covers[it.title]) }) }
        at = System.currentTimeMillis()
        save()
        return sections.orEmpty()
    }

    /** Blocking: a tile's picture (the cover of its first playlist), or null. */
    fun cover(mood: MoodCategory): String? {
        mood.thumbnail?.let { return it }
        val url = runCatching { yt.mood(mood.params) }.getOrNull()?.flatMap { it.items }?.firstNotNullOfOrNull { it.thumbnail } ?: return null
        synchronized(this) {
            sections = sections?.map { s -> s.copy(items = s.items.map { if (it.params == mood.params) it.copy(thumbnail = url) else it }) }
            save()
        }
        return url
    }

    private fun load() {
        sections = runCatching {
            val o = JSONObject(library.setting(KEY) ?: return@runCatching emptyList())
            at = o.optLong("at")
            val arr = o.getJSONArray("sections")
            (0 until arr.length()).map { i ->
                val s = arr.getJSONObject(i)
                val items = s.getJSONArray("items")
                MoodSection(s.getString("title"), (0 until items.length()).map { j ->
                    val m = items.getJSONObject(j)
                    MoodCategory(m.getString("t"), m.getString("p"), m.getLong("c"), m.optString("img").takeIf { it.isNotEmpty() })
                })
            }
        }.getOrDefault(emptyList())
    }

    private fun save() {
        val arr = JSONArray()
        sections.orEmpty().forEach { s ->
            val items = JSONArray()
            s.items.forEach { items.put(JSONObject().put("t", it.title).put("p", it.params).put("c", it.color).put("img", it.thumbnail ?: "")) }
            arr.put(JSONObject().put("title", s.title).put("items", items))
        }
        library.setSetting(KEY, JSONObject().put("at", at).put("sections", arr).toString(), notify = false)
    }

    private companion object { const val KEY = "moods.cache" }
}
