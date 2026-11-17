package app.tune.widget

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.SizeF
import android.widget.RemoteViews
import androidx.core.content.ContextCompat
import androidx.core.graphics.ColorUtils
import androidx.media3.common.C
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import app.tune.MainActivity
import app.tune.R
import app.tune.TuneApp
import app.tune.data.Thumbs
import app.tune.data.Track
import app.tune.playback.PlaybackService
import app.tune.playback.track
import coil3.SingletonImageLoader
import coil3.request.ImageRequest
import coil3.request.allowHardware
import coil3.toBitmap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/** Home-screen widgets. All four share [TuneWidgets]; each is its own entry in the widget picker. */
open class TuneWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = TuneWidgets.render(context)

    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, id: Int, options: Bundle) =
        TuneWidgets.render(context)

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        val action = intent.action ?: return
        if (action !in TuneWidgets.ACTIONS) return
        val pending = goAsync()
        TuneWidgets.control(context.applicationContext, action, intent.getIntExtra(TuneWidgets.EXTRA_INDEX, -1)) { pending.finish() }
    }
}

/** 2×2: full-bleed cover with the controls over it. */
class TuneSquareWidget : TuneWidget()
/** 2×3: cover on top, title and controls below. */
class TuneTallWidget : TuneWidget()
/** 1×1: a stack of the queue's covers — swipe to browse, tap one to play it (tap the playing one to pause). */
class TuneCoverWidget : TuneWidget()

object TuneWidgets {
    private const val PLAY = "app.tune.widget.PLAY"
    private const val PREV = "app.tune.widget.PREV"
    private const val NEXT = "app.tune.widget.NEXT"
    private const val LIKE = "app.tune.widget.LIKE"
    private const val COVER = "app.tune.widget.COVER"
    const val EXTRA_INDEX = "index"
    val ACTIONS = setOf(PLAY, PREV, NEXT, LIKE, COVER)

    /** A song in the queue and its position in the player (for "play this one"). */
    data class QueuedSong(val index: Int, val track: Track)

    /** What the widgets show; [PlaybackService] keeps it current, otherwise it's what Tune would continue with. */
    data class Shown(val track: Track?, val playing: Boolean, val upcoming: List<QueuedSong>)

    private val main = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    @Volatile var shown: Shown? = null
        private set
    private var artUrl: String? = null
    private var art: Bitmap? = null
    private var artJob: Job? = null

    /** Called by the player whenever something changes; re-draws only when the song, queue or play state did. */
    fun update(context: Context, player: Player) {
        val s = Shown(player.currentMediaItem?.track(), player.playWhenReady, upcoming(player))
        if (s == shown) return
        val queueChanged = s.upcoming != shown?.upcoming
        shown = s
        render(context, queueChanged)
    }

    /** The current song and the next ones in play order (shuffle included). */
    private fun upcoming(player: Player): List<QueuedSong> {
        val timeline = player.currentTimeline
        if (timeline.isEmpty) return emptyList()
        val out = mutableListOf<QueuedSong>()
        var i = player.currentMediaItemIndex
        while (i != C.INDEX_UNSET && out.size < 12) {
            player.getMediaItemAt(i).track()?.let { out += QueuedSong(i, it) }
            i = timeline.getNextWindowIndex(i, Player.REPEAT_MODE_OFF, player.shuffleModeEnabled)
        }
        return out
    }

    /** The player is going away: keep the song, show it paused. */
    fun stopped(context: Context) {
        shown = shown?.copy(playing = false) ?: return
        render(context)
    }

    private val kinds = listOf(TuneWidget::class.java, TuneSquareWidget::class.java, TuneTallWidget::class.java, TuneCoverWidget::class.java)

    fun render(context: Context, queueChanged: Boolean = false) {
        val manager = AppWidgetManager.getInstance(context)
        val all = kinds.associateWith { manager.getAppWidgetIds(ComponentName(context, it)) }
        if (all.values.all { it.isEmpty() }) return
        val s = shown ?: (lastSession(context) ?: Shown(null, false, emptyList())).also { shown = it }
        val url = Thumbs.sized(s.track?.thumbnail, 400)
        if (url != artUrl) loadArt(context, url)
        for ((kind, ids) in all) for (id in ids) {
            val views = when (kind) {
                TuneSquareWidget::class.java -> square(context, s)
                TuneTallWidget::class.java -> tall(context, s)
                TuneCoverWidget::class.java -> cover(context, id)
                else -> bar(context, s, manager.getAppWidgetOptions(id))
            }
            manager.updateAppWidget(id, views)
        }
        all[TuneCoverWidget::class.java]?.takeIf { it.isNotEmpty() }?.let {
            if (queueChanged || coversShown != s) { coversShown = s; @Suppress("DEPRECATION") manager.notifyAppWidgetViewDataChanged(it, R.id.widget_stack) }
        }
    }
    private var coversShown: Shown? = null

    /** The queue Tune reopens with, so the widgets aren't empty before anything has played. */
    private fun lastSession(context: Context): Shown? = runCatching {
        val json = org.json.JSONObject(app(context).graph.library.setting("session") ?: return null)
        val queue = json.getJSONArray("queue")
        val at = json.optInt("index").coerceIn(0, queue.length() - 1)
        val songs = (at until minOf(queue.length(), at + 12)).mapNotNull { i -> Track.fromJson(queue.getJSONObject(i))?.let { QueuedSong(i, it) } }
        Shown(songs.firstOrNull()?.track, false, songs)
    }.getOrNull()

    private fun loadArt(context: Context, url: String?) {
        artUrl = url
        art = null
        artJob?.cancel()
        if (url == null) return
        artJob = main.launch {
            val bitmap = loadBitmap(context, url, 400) ?: return@launch
            if (artUrl != url) return@launch
            art = bitmap
            render(context)
        }
    }

    suspend fun loadBitmap(context: Context, url: String, px: Int): Bitmap? = runCatching {
        val request = ImageRequest.Builder(context).data(url).size(px).allowHardware(false).build()
        SingletonImageLoader.get(context).execute(request).image?.toBitmap()
    }.getOrNull()

    // ---------- layouts ----------

    private fun bar(context: Context, s: Shown, options: Bundle): RemoteViews {
        if (Build.VERSION.SDK_INT >= 31)
            return RemoteViews(mapOf(SizeF(200f, 40f) to bind(context, s, R.layout.widget_small, like = false),
                SizeF(200f, 110f) to bind(context, s, R.layout.widget_large, like = true)))
        val large = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT) >= 110
        return bind(context, s, if (large) R.layout.widget_large else R.layout.widget_small, like = large)
    }

    private fun square(context: Context, s: Shown) = bind(context, s, R.layout.widget_square, like = true)
    private fun tall(context: Context, s: Shown) = bind(context, s, R.layout.widget_tall, like = true)

    /** Title, art and controls: every layout uses the same view ids. */
    private fun bind(context: Context, s: Shown, layout: Int, like: Boolean): RemoteViews {
        val v = RemoteViews(context.packageName, layout)
        val accent = accent(context)
        val t = s.track
        v.setTextViewText(R.id.widget_title, t?.title ?: "Tune")
        v.setTextViewText(R.id.widget_artist, t?.artistLine ?: "Tap to open")
        val bitmap = art
        if (bitmap != null) v.setImageViewBitmap(R.id.widget_art, bitmap) else v.setImageViewResource(R.id.widget_art, R.drawable.widget_art_empty)

        v.setInt(R.id.widget_play_bg, "setColorFilter", accent)
        v.setImageViewResource(R.id.widget_play_icon, if (s.playing) R.drawable.widget_pause else R.drawable.widget_play)
        v.setInt(R.id.widget_play_icon, "setColorFilter", onAccent(accent))

        v.setOnClickPendingIntent(R.id.widget_root, openApp(context))
        v.setOnClickPendingIntent(R.id.widget_play, action(context, PLAY))
        v.setOnClickPendingIntent(R.id.widget_prev, action(context, PREV))
        v.setOnClickPendingIntent(R.id.widget_next, action(context, NEXT))
        if (like) {
            val liked = t != null && app(context).graph.library.isLiked(t.videoId)
            v.setImageViewResource(R.id.widget_like, if (liked) R.drawable.widget_heart else R.drawable.widget_heart_outline)
            v.setInt(R.id.widget_like, "setColorFilter", if (liked) accent else 0xFFF2F2F2.toInt())
            v.setOnClickPendingIntent(R.id.widget_like, action(context, LIKE))
        }
        return v
    }

    private fun cover(context: Context, widgetId: Int): RemoteViews {
        val v = RemoteViews(context.packageName, R.layout.widget_cover)
        val adapter = Intent(context, CoverStackService::class.java)
            .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId)
        adapter.data = android.net.Uri.parse(adapter.toUri(Intent.URI_INTENT_SCHEME))
        @Suppress("DEPRECATION") v.setRemoteAdapter(R.id.widget_stack, adapter)
        v.setEmptyView(R.id.widget_stack, R.id.widget_stack_empty)
        v.setOnClickPendingIntent(R.id.widget_stack_empty, openApp(context))
        // each cover fills in its queue position (see CoverStackService)
        v.setPendingIntentTemplate(R.id.widget_stack, PendingIntent.getBroadcast(context, 7,
            Intent(context, TuneWidget::class.java).setAction(COVER),
            PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)))
        v.setDisplayedChild(R.id.widget_stack, 0) // the playing song on top
        return v
    }

    // ---------- buttons ----------

    /** Runs a button press on the player (starting it if Tune isn't running), then lets the broadcast finish. */
    fun control(context: Context, action: String, index: Int, done: () -> Unit) {
        if (action == LIKE) {
            val t = shown?.track
            if (t != null) app(context).graph.library.let { it.setLiked(t, !it.isLiked(t.videoId)) }
            render(context)
            done()
            return
        }
        val future = MediaController.Builder(context, SessionToken(context, ComponentName(context, PlaybackService::class.java))).buildAsync()
        future.addListener({
            val c = runCatching { future.get() }.getOrNull() ?: return@addListener done()
            when (action) {
                PLAY -> toggle(c)
                NEXT -> c.seekToNextMediaItem()
                PREV -> c.seekToPrevious()
                COVER -> if (index < 0 || index == c.currentMediaItemIndex || index >= c.mediaItemCount) toggle(c)
                         else { c.seekTo(index, 0); c.prepare(); c.play() }
            }
            // give the command a moment to reach the player before letting go of it
            Handler(Looper.getMainLooper()).postDelayed({ c.release(); done() }, 800)
        }, ContextCompat.getMainExecutor(context))
    }

    private fun toggle(c: MediaController) {
        if (c.playWhenReady) c.pause() else {
            if (c.playbackState == Player.STATE_IDLE) c.prepare()
            if (c.playbackState == Player.STATE_ENDED) c.seekTo(0)
            c.play()
        }
    }

    private fun action(context: Context, action: String): PendingIntent =
        PendingIntent.getBroadcast(context, action.hashCode(), Intent(context, TuneWidget::class.java).setAction(action),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun openApp(context: Context): PendingIntent =
        PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    fun accent(context: Context): Int =
        runCatching { android.graphics.Color.parseColor(app(context).graph.library.setting("theme.accent")) }.getOrDefault(0xFF9D7BFF.toInt())

    private fun onAccent(accent: Int) = if (ColorUtils.calculateLuminance(accent) > 0.45) 0xFF000000.toInt() else 0xFFFFFFFF.toInt()

    private fun app(context: Context) = context.applicationContext as TuneApp
}
