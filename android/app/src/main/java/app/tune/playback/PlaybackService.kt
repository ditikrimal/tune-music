package app.tune.playback

import android.app.PendingIntent
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.util.Log
import androidx.annotation.OptIn
import androidx.core.net.toUri
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSpec
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.DefaultMediaNotificationProvider
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import app.tune.MainActivity
import app.tune.R
import app.tune.TuneApp
import app.tune.widget.TuneWidgets
import app.tune.data.Thumbs
import app.tune.data.Track
import app.tune.data.OfflineMix
import app.tune.data.OfflineStall
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Songs travel through the player as "tune://<videoId>"; the real link is found when ExoPlayer opens it. */
object TuneUri {
    const val SCHEME = "tune"
    fun of(videoId: String): Uri = "$SCHEME://$videoId".toUri()
}

/** [auto]: added by the radio, so the radio may re-steer it later (songs you add yourself are never touched). */
fun Track.toMediaItem(auto: Boolean = false): MediaItem = MediaItem.Builder()
    .setMediaId(videoId)
    .setUri(TuneUri.of(videoId))
    .setMediaMetadata(MediaMetadata.Builder()
        .setTitle(title)
        .setArtist(artistLine)
        .setAlbumTitle(album?.title)
        .setArtworkUri(Thumbs.sized(thumbnail, 544)?.toUri())
        .setDurationMs(durationSec?.let { it * 1000L })
        .setExtras(Bundle().apply { putString("track", toJson().toString()); if (auto) putBoolean("auto", true) })
        .build())
    .build()

fun MediaItem.track(): Track? = Track.fromJson(mediaMetadata.extras?.getString("track"))

/** Added by the radio (see [toMediaItem]). */
val MediaItem.isAuto: Boolean get() = mediaMetadata.extras?.getBoolean("auto") == true

/**
 * Plays music in the background: notification + lock screen + headset/Bluetooth controls (Media3 session),
 * records plays after 30 s, appends YouTube Music radio when the queue runs low, and recovers from expired links.
 */
@OptIn(UnstableApi::class)
class PlaybackService : MediaSessionService() {
    private lateinit var player: ExoPlayer
    private var session: MediaSession? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var recordJob: Job? = null
    private var playingId: String? = null
    private var playingTrack: Track? = null
    private var steerJob: Job? = null
    private var playingSince = 0L
    private var radioFor: String? = null
    private var retriedFor: String? = null
    private lateinit var cacheFactory: androidx.media3.datasource.cache.CacheDataSource.Factory
    private var lookahead: Job? = null
    private var offlineNoticeShown = false
    private var connectionFailures = 0
    /** Radio lists fetched this session, by seed: the offline mix prefers songs from the playing song's radio. */
    private val radioIds = java.util.concurrent.ConcurrentHashMap<String, List<String>>()

    private val app get() = application as TuneApp

    override fun onCreate() {
        super.onCreate()
        val graph = app.graph
        // audio servers answer in well under a second and send steadily while we read: short timeouts notice a dead
        // connection (e.g. Wi-Fi with no internet) in seconds instead of the 30 s an open stream would otherwise hang
        val streamClient = graph.http.newBuilder()
            .connectTimeout(6, java.util.concurrent.TimeUnit.SECONDS)
            .readTimeout(10, java.util.concurrent.TimeUnit.SECONDS)
            .build()
        val http = OkHttpDataSource.Factory(streamClient).setUserAgent(app.graph.streamUserAgent)
        // network path for a song: find a fresh link, then stream it; the cache above keeps what was read
        val resolving = ResolvingDataSource.Factory(http) { spec -> resolve(spec) }
        cacheFactory = androidx.media3.datasource.cache.CacheDataSource.Factory()
            .setCache(graph.streamCache.cache)
            .setUpstreamDataSourceFactory(resolving)
            .setFlags(androidx.media3.datasource.cache.CacheDataSource.FLAG_IGNORE_CACHE_ON_ERROR)
        val sources = TuneDataSource.Factory({ graph.downloads.localFile(it) }, androidx.media3.datasource.FileDataSource.Factory(),
            cacheFactory, DefaultDataSource.Factory(this, http))

        // buffer the whole song (audio is small): riding through a dead zone doesn't cut the current song
        val loadControl = androidx.media3.exoplayer.DefaultLoadControl.Builder()
            .setBufferDurationsMs(30_000, 600_000, 1_500, 3_000)
            .build()

        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(sources))
            .setLoadControl(loadControl)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(), true)
            .setHandleAudioBecomingNoisy(true) // pause when headphones are unplugged
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
        player.addListener(listener)
        // Wi-Fi that's connected but has no internet still counts as "online", and ExoPlayer keeps retrying for about
        // a minute before it gives up. Its failed attempts arrive here as they happen: after two connection failures
        // in a row, a song that has run out of audio moves on to one saved on the phone.
        player.addAnalyticsListener(object : androidx.media3.exoplayer.analytics.AnalyticsListener {
            override fun onLoadError(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime,
                                     loadEventInfo: androidx.media3.exoplayer.source.LoadEventInfo,
                                     mediaLoadData: androidx.media3.exoplayer.source.MediaLoadData,
                                     error: java.io.IOException, wasCanceled: Boolean) {
                Log.i("Tune", "Load error: ${generateSequence(error as Throwable) { it.cause }.last()}")
                if (isConnectionFailure(error) && ++connectionFailures >= 2) scheduleOfflineCheck()
            }
            override fun onLoadCompleted(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime,
                                         loadEventInfo: androidx.media3.exoplayer.source.LoadEventInfo,
                                         mediaLoadData: androidx.media3.exoplayer.source.MediaLoadData) {
                connectionFailures = 0
            }
        })

        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        // Media3 gives apps it can't verify (home-screen widgets, other launchers) only a reduced command set,
        // so their play/pause/skip were silently dropped. Everyone gets the normal playback commands.
        session = MediaSession.Builder(this, player).setSessionActivity(open)
            .setCallback(object : MediaSession.Callback {
                override fun onConnect(session: MediaSession, controller: MediaSession.ControllerInfo) =
                    MediaSession.ConnectionResult.AcceptedResultBuilder(session).build()
            })
            .build()
        restoreSession()
        // a chip picked in the queue: re-steer everything coming up
        scope.launch {
            var first = true
            graph.vibe.tuning.collect {
                if (first) { first = false; return@collect }
                steerSoon(keep = 0, retune = true) // everything coming up follows the chip, like YouTube Music
            }
        }
        setMediaNotificationProvider(DefaultMediaNotificationProvider.Builder(this).build().apply { setSmallIcon(R.drawable.ic_notification) })
        scope.launch {
            graph.online.collect { online ->
                if (online) offlineNoticeShown = false
                else scheduleOfflineCheck()
            }
        }
    }

    /** Runs on a loader thread, only when the song isn't downloaded or cached: turn tune://id into a fresh stream link. */
    private fun resolve(spec: DataSpec): DataSpec {
        if (spec.uri.scheme != TuneUri.SCHEME) return spec
        val id = spec.uri.host ?: return spec
        val s = app.graph.streams.resolve(id)
        var out = spec.withUri(s.url.toUri()).withAdditionalHeaders(mapOf("User-Agent" to s.userAgent))
        if (s.webClient) {
            // web-client links want what the YouTube web player sends
            out = out.buildUpon().setHttpMethod(DataSpec.HTTP_METHOD_POST).setHttpBody(byteArrayOf(0x78, 0))
                .setHttpRequestHeaders(mapOf("User-Agent" to s.userAgent, "Origin" to "https://www.youtube.com", "Referer" to "https://www.youtube.com/"))
                .build()
        }
        return out
    }

    // ---------- remember the queue, so Tune reopens where you left off ----------

    private fun saveSession(now: Boolean = false) {
        val items = (0 until player.mediaItemCount).mapNotNull { player.getMediaItemAt(it).track()?.toJson() }
        if (items.isEmpty()) return
        val json = org.json.JSONObject()
            .put("queue", org.json.JSONArray(items.takeLast(300)))
            .put("index", player.currentMediaItemIndex - (items.size - items.takeLast(300).size))
            .put("position", player.currentPosition)
            .put("shuffle", player.shuffleModeEnabled).put("repeat", player.repeatMode)
        if (now) app.graph.library.setSetting("session", json.toString())
        else scope.launch(Dispatchers.IO) { app.graph.library.setSetting("session", json.toString()) }
    }

    private fun restoreSession() {
        val json = app.graph.library.setting("session")?.let { runCatching { org.json.JSONObject(it) }.getOrNull() } ?: return
        val arr = json.optJSONArray("queue") ?: return
        val tracks = (0 until arr.length()).mapNotNull { Track.fromJson(arr.optJSONObject(it)) }
        if (tracks.isEmpty()) return
        player.shuffleModeEnabled = json.optBoolean("shuffle")
        player.repeatMode = json.optInt("repeat")
        player.setMediaItems(tracks.map { it.toMediaItem() }, json.optInt("index").coerceIn(0, tracks.size - 1), json.optLong("position"))
        player.prepare() // paused: press play to continue
    }

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) = TuneWidgets.update(this@PlaybackService, player)
        override fun onIsPlayingChanged(isPlaying: Boolean) { if (!isPlaying) saveSession() }
        override fun onTimelineChanged(timeline: androidx.media3.common.Timeline, reason: Int) { saveSession() }

        override fun onPlaybackStateChanged(state: Int) {
            if (app.graph.online.value) return
            when (state) {
                // the queue ran out while offline: carry on with the offline mix
                Player.STATE_ENDED -> scheduleOfflineCheck()
                // a streamed song's buffer ran dry: give it a moment (it may be reading from the cache), then move on
                // instead of waiting ~30 s for ExoPlayer to give up on the dead connection
                Player.STATE_BUFFERING -> scheduleOfflineCheck(delayMs = 2_000)
            }
        }

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            saveSession()
            connectionFailures = 0
            if (!app.graph.online.value && mediaItem != null && !app.graph.isAvailableOffline(mediaItem.mediaId)) {
                scheduleOfflineCheck(justArrived = true) // don't even try a song that can't play offline
                return
            }
            // a new list/radio from the UI starts untuned
            if (reason == Player.MEDIA_ITEM_TRANSITION_REASON_PLAYLIST_CHANGED) app.graph.vibe.tuning.value = null
            // you moved on from a song early (it played a bit, never counted as a play): a skip, for recommendations
            val left = playingTrack
            val skipped = left != null && recordJob?.isActive == true && reason == Player.MEDIA_ITEM_TRANSITION_REASON_SEEK &&
                android.os.SystemClock.elapsedRealtime() - playingSince >= 3_000
            if (skipped) scope.launch(Dispatchers.IO) { app.graph.library.recordSkip(left!!) }
            playingId = mediaItem?.mediaId
            playingTrack = mediaItem?.track()
            playingSince = android.os.SystemClock.elapsedRealtime()
            recordJob?.cancel()
            val track = mediaItem?.track() ?: return
            retriedFor = null
            recordJob = scope.launch {
                delay(30_000)
                withContext(Dispatchers.IO) { app.graph.library.recordPlay(track) }
                steerSoon(keep = 1) // it counts as a play: the radio follows it
            }
            if (skipped) steerSoon(keep = 1) // steer away from what was just skipped
            else updateChips(track)
            topUpRadio()
            prepareUpcoming()
        }

        override fun onPlayerError(error: PlaybackException) {
            val item = player.currentMediaItem ?: return
            Log.w("Tune", "Playback error for ${item.mediaId}: ${error.errorCodeName}", error)
            if (!app.graph.online.value || isConnectionError(error)) {
                // the connection went (even if Android still calls the network "validated"): carry on with what's
                // on the phone instead of retrying a link that can't load
                scheduleOfflineCheck()
                return
            }
            if (retriedFor != item.mediaId) {
                // most failures are an expired/blocked link: get a fresh one and carry on where we were
                retriedFor = item.mediaId
                app.graph.streams.invalidate(item.mediaId)
                val at = player.currentPosition
                player.prepare(); player.seekTo(at); player.play()
            } else if (player.hasNextMediaItem()) {
                app.graph.toast("Couldn't play ${item.mediaMetadata.title} — skipped")
                player.seekToNextMediaItem(); player.prepare(); player.play()
            } else {
                app.graph.toast("Couldn't play ${item.mediaMetadata.title}")
            }
        }
    }

    // ---------- the radio follows what you listen to ----------

    /** Radio songs after the next [keep] (what re-steering may replace); [all]: every song after them (a chip was picked). */
    private fun autoAfter(keep: Int, all: Boolean = false): List<Int> {
        if (player.repeatMode == Player.REPEAT_MODE_ALL) return emptyList()
        return (player.currentMediaItemIndex + 1 + keep until player.mediaItemCount).filter { all || player.getMediaItemAt(it).isAuto }
    }

    /** Re-ranks the radio songs coming up from the session so far (plays pull, skips push away). Never inside a player callback. */
    private fun steerSoon(keep: Int, retune: Boolean = false) {
        if (!app.graph.online.value) return
        steerJob?.cancel()
        steerJob = scope.launch {
            val current = player.currentMediaItem?.track() ?: return@launch
            if (!retune && autoAfter(keep).isEmpty()) { updateChips(current); return@launch }
            val fixed = (0..minOf(player.currentMediaItemIndex + keep, player.mediaItemCount - 1)).map { player.getMediaItemAt(it).mediaId }
            val result = runCatching { app.graph.vibe.upNext(current, fixed, 25) }.getOrNull() ?: return@launch
            // something newer happened meanwhile (another song, another queue): that one steers
            if (player.currentMediaItem?.mediaId != current.videoId) return@launch
            app.graph.vibe.vibe.value = result.vibe
            app.graph.vibe.chips.value = result.chips
            if (result.tracks.size < 5) return@launch
            val old = autoAfter(keep, all = retune)
            for (i in old.asReversed()) player.removeMediaItem(i)
            val present = (0 until player.mediaItemCount).map { player.getMediaItemAt(it).mediaId }.toSet()
            val notInterested = app.graph.library.notInterested()
            player.addMediaItems(result.tracks.filter { it.videoId !in present && it.videoId !in notInterested }.map { it.toMediaItem(auto = true) })
            prepareUpcoming()
        }
    }

    /** The playing song's tuner chips for the queue. */
    private fun updateChips(track: Track) {
        if (!app.graph.online.value) return
        scope.launch {
            val chips = withContext(Dispatchers.IO) { app.graph.vibe.signals.get(track.videoId)?.chips }.orEmpty()
            if (player.currentMediaItem?.mediaId == track.videoId) app.graph.vibe.chips.value = chips
        }
    }

    /** When fewer than 3 songs are left, append YouTube Music radio for the last one (offline: the offline mix). */
    private fun topUpRadio() {
        if (player.repeatMode == Player.REPEAT_MODE_ALL) return
        val left = player.mediaItemCount - player.currentMediaItemIndex - 1
        if (left >= 3 || player.mediaItemCount == 0) return
        if (!app.graph.online.value) {
            // after the callback (see scheduleOfflineCheck), and only if we're still on the last song by then
            if (left == 0) scope.launch { if (!player.hasNextMediaItem() && !app.graph.online.value) appendOfflineMix() }
            return
        }
        val seed = player.getMediaItemAt(player.mediaItemCount - 1).mediaId
        if (radioFor == seed) return
        radioFor = seed
        scope.launch {
            // the vibe engine follows the session; plain radio of the last song if it has nothing
            val current = player.currentMediaItem?.track()
            val queued = (0 until player.mediaItemCount).map { player.getMediaItemAt(it).mediaId }
            val steered = current?.let { runCatching { app.graph.vibe.upNext(it, queued, 25) }.getOrNull() }
            steered?.let { app.graph.vibe.vibe.value = it.vibe; app.graph.vibe.chips.value = it.chips }
            val tracks = steered?.tracks?.takeIf { it.size >= 5 }
                ?: withContext(Dispatchers.IO) { runCatching { app.graph.yt.radio(seed) }.getOrNull() } ?: run { radioFor = null; return@launch }
            radioIds[seed] = tracks.map { it.videoId }
            val present = (0 until player.mediaItemCount).map { player.getMediaItemAt(it).mediaId }.toSet()
            val notInterested = app.graph.library.notInterested() // radios leave out songs you said no to
            player.addMediaItems(tracks.filter { it.videoId !in present && it.videoId !in notInterested }.map { it.toMediaItem(auto = true) })
        }
    }

    /**
     * Save the next 3 songs into the streaming cache now (a few MB), so the queue keeps going through a dead zone,
     * and skipping starts instantly. Restarts whenever the song changes.
     */
    private fun prepareUpcoming() {
        lookahead?.cancel()
        if (!app.graph.online.value || app.graph.library.setting("lookahead.enabled") == "0") return
        val ids = (1..3).mapNotNull { k -> (player.currentMediaItemIndex + k).takeIf { it < player.mediaItemCount }?.let { player.getMediaItemAt(it).mediaId } }
        lookahead = scope.launch(Dispatchers.IO) {
            for (id in ids) {
                if (!kotlinx.coroutines.currentCoroutineContext().isActive) break
                if (app.graph.isAvailableOffline(id)) continue
                val spec = DataSpec.Builder().setUri(TuneUri.of(id)).setKey(id).build()
                val writer = androidx.media3.datasource.cache.CacheWriter(cacheFactory.createDataSource(), spec, null, null)
                val cancel = kotlinx.coroutines.currentCoroutineContext()[Job]!!.invokeOnCompletion { writer.cancel() }
                runCatching { writer.cache() }.onFailure { if (it !is java.io.InterruptedIOException) Log.w("Tune", "Lookahead failed for $id: $it") }
                cancel.dispose()
            }
        }
    }

    // ---------- offline: keep the music going with what's on the phone ----------

    private var offlineCheck: Job? = null

    /**
     * Runs [keepPlayingOffline] soon, on the main thread but never inside a player callback: changing the queue while
     * ExoPlayer is still delivering an event makes Media3's session see an old (shorter) queue with the new position
     * and crash (IllegalStateException in PlayerInfo). A newer request replaces a pending one; each re-checks the
     * player as it is by then, so two triggers for the same moment (song changed + error) only skip once.
     */
    private fun scheduleOfflineCheck(delayMs: Long = 0, justArrived: Boolean = false) {
        offlineCheck?.cancel()
        offlineCheck = scope.launch {
            if (delayMs > 0) delay(delayMs)
            runCatching { keepPlayingOffline(justArrived) }.onFailure { Log.w("Tune", "Offline continuation failed", it) }
        }
    }

    /** A failure to reach the server at all (not an expired link): retrying a fresh link won't help. */
    private fun isConnectionError(error: PlaybackException): Boolean =
        error.errorCode == PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED ||
            error.errorCode == PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT ||
            isConnectionFailure(error)

    private fun isConnectionFailure(error: Throwable): Boolean =
        generateSequence(error) { it.cause }.take(12).any {
            it is java.net.UnknownHostException || it is java.net.SocketException || it is java.net.SocketTimeoutException
        }

    /**
     * The internet is gone. If the current song can't go on ([OfflineStall]), move to the next song in the queue that
     * can; if none is left, continue with an offline mix close to what was playing. Never replaces the queue.
     * A song that's still playing from its buffer is left to finish.
     */
    private fun keepPlayingOffline(justArrived: Boolean) {
        val graph = app.graph
        if (player.mediaItemCount == 0) return
        val current = player.currentMediaItem
        val playable = current != null && graph.isAvailableOffline(current.mediaId)
        if (!OfflineStall.isStuck(player.playbackState, player.playerError != null, playable, justArrived)) return
        val resume = player.playWhenReady // keep paused if the user paused
        val cur = player.currentMediaItemIndex
        val next = (cur + 1 until player.mediaItemCount).firstOrNull { graph.isAvailableOffline(player.getMediaItemAt(it).mediaId) }
        if (next != null) {
            notifyOffline("You're offline — skipping songs that aren't saved")
            player.seekTo(next, 0); player.prepare(); if (resume) player.play()
            return
        }
        if (appendOfflineMix()) {
            notifyOffline("You're offline — continuing with songs saved on your phone")
            player.seekTo(cur + 1, 0); player.prepare(); if (resume) player.play()
        } else {
            notifyOffline("You're offline and nothing is saved yet — Smart downloads will fix that on Wi-Fi")
            player.pause()
        }
    }

    /** Appends an offline mix seeded by the current song; false when nothing playable offline is known. */
    private fun appendOfflineMix(): Boolean {
        val graph = app.graph
        val seed = player.currentMediaItem?.track()
        val queued = (0 until player.mediaItemCount).map { player.getMediaItemAt(it).mediaId }.toSet()
        val counts = graph.library.playStats(System.currentTimeMillis() - 90L * 86_400_000).associate { it.first.videoId to it.second }
        val available = graph.offlineTracks()
        val radio = seed?.let { radioIds[it.videoId] }
        // prefer songs not already in the queue; if every saved song is in it, repeating some beats stopping
        val mix = OfflineMix.build(seed, available, radio, counts, queued)
            .ifEmpty { OfflineMix.build(seed, available, radio, counts, emptySet()) }
        if (mix.isEmpty()) return false
        // drop the unplayable rest of the queue after the current song, then add the mix
        val cur = player.currentMediaItemIndex
        if (cur + 1 < player.mediaItemCount) player.removeMediaItems(cur + 1, player.mediaItemCount)
        player.addMediaItems(mix.map { it.toMediaItem() })
        return true
    }

    private fun notifyOffline(message: String) {
        if (offlineNoticeShown) return
        offlineNoticeShown = true
        app.graph.toast(message)
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo) = session

    override fun onTaskRemoved(rootIntent: Intent?) {
        // swiping Tune away keeps the music playing; with nothing playing, stop the service
        if (!player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        saveSession(now = true)
        TuneWidgets.stopped(this)
        scope.cancel()
        session?.run { player.release(); release() }
        session = null
        super.onDestroy()
    }
}
