package app.tune.widget

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.view.View
import android.widget.RemoteViews
import android.widget.RemoteViewsService
import app.tune.R
import app.tune.data.Thumbs
import kotlinx.coroutines.runBlocking

/** Feeds the 1×1 cover stack: the playing song first, then what's next in the queue. */
class CoverStackService : RemoteViewsService() {
    override fun onGetViewFactory(intent: Intent): RemoteViewsFactory = Covers(applicationContext)

    private class Covers(private val context: Context) : RemoteViewsFactory {
        private var songs = emptyList<TuneWidgets.QueuedSong>()
        private var playing = false
        /** Covers already fetched, by link (the stack asks for the same ones again on every change). */
        private val cache = object : LinkedHashMap<String, Bitmap>(16, 0.75f, true) {
            override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, Bitmap>) = size > 16
        }

        override fun onCreate() = onDataSetChanged()

        override fun onDataSetChanged() {
            if (TuneWidgets.shown == null) TuneWidgets.render(context) // fills it from the saved queue
            val s = TuneWidgets.shown
            songs = s?.upcoming.orEmpty()
            playing = s?.playing == true
        }

        override fun getCount() = songs.size

        override fun getViewAt(position: Int): RemoteViews {
            val v = RemoteViews(context.packageName, R.layout.widget_cover_item)
            val song = songs.getOrNull(position) ?: return v
            val url = Thumbs.sized(song.track.thumbnail, 240)
            val bitmap = url?.let { cache[it] ?: runBlocking { TuneWidgets.loadBitmap(context, it, 240) }?.also { b -> cache[url] = b } }
            if (bitmap != null) v.setImageViewBitmap(R.id.widget_cover_art, bitmap) else v.setImageViewResource(R.id.widget_cover_art, R.drawable.widget_art_empty)
            // the playing song shows a play badge while paused; the others show what tapping does (play this one)
            val current = position == 0
            v.setViewVisibility(R.id.widget_cover_badge, if (current && playing) View.GONE else View.VISIBLE)
            v.setContentDescription(R.id.widget_cover_art, song.track.title)
            v.setOnClickFillInIntent(R.id.widget_cover_root, Intent().putExtra(TuneWidgets.EXTRA_INDEX, song.index))
            return v
        }

        override fun getLoadingView(): RemoteViews? = null
        override fun getViewTypeCount() = 1
        override fun getItemId(position: Int) = songs.getOrNull(position)?.index?.toLong() ?: position.toLong()
        override fun hasStableIds() = true
        override fun onDestroy() = cache.clear()
    }
}
