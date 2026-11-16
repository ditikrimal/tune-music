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
import app.tune.data.Thumbs
import app.tune.data.Track
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Songs travel through the player as "tune://<videoId>"; the real link is found when ExoPlayer opens it. */
object TuneUri {
    const val SCHEME = "tune"
    fun of(videoId: String): Uri = "$SCHEME://$videoId".toUri()
}

fun Track.toMediaItem(): MediaItem = MediaItem.Builder()
    .setMediaId(videoId)
    .setUri(TuneUri.of(videoId))
    .setMediaMetadata(MediaMetadata.Builder()
        .setTitle(title)
        .setArtist(artistLine)
        .setAlbumTitle(album?.title)
        .setArtworkUri(Thumbs.sized(thumbnail, 544)?.toUri())
        .setDurationMs(durationSec?.let { it * 1000L })
        .setExtras(Bundle().apply { putString("track", toJson().toString()) })
        .build())
    .build()

fun MediaItem.track(): Track? = Track.fromJson(mediaMetadata.extras?.getString("track"))

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
    private var radioFor: String? = null
    private var retriedFor: String? = null

    private val app get() = application as TuneApp

    override fun onCreate() {
        super.onCreate()
        val graph = app.graph
        val http = OkHttpDataSource.Factory(graph.http).setUserAgent(app.graph.streamUserAgent)
        val resolving = ResolvingDataSource.Factory(DefaultDataSource.Factory(this, http)) { spec -> resolve(spec) }

        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(resolving))
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(), true)
            .setHandleAudioBecomingNoisy(true) // pause when headphones are unplugged
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
        player.addListener(listener)

        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        session = MediaSession.Builder(this, player).setSessionActivity(open).build()
        setMediaNotificationProvider(DefaultMediaNotificationProvider.Builder(this).build().apply { setSmallIcon(R.drawable.ic_notification) })
    }

    /** Runs on ExoPlayer's loader thread: turn tune://id into a downloaded file or a fresh stream link. */
    private fun resolve(spec: DataSpec): DataSpec {
        if (spec.uri.scheme != TuneUri.SCHEME) return spec
        val id = spec.uri.host ?: return spec
        app.graph.downloads.localFile(id)?.let { return spec.withUri(Uri.fromFile(it)) }
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

    private val listener = object : Player.Listener {
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            recordJob?.cancel()
            val track = mediaItem?.track() ?: return
            retriedFor = null
            recordJob = scope.launch {
                delay(30_000)
                withContext(Dispatchers.IO) { app.graph.library.recordPlay(track) }
            }
            topUpRadio()
            prepareUpcoming()
        }

        override fun onPlayerError(error: PlaybackException) {
            val item = player.currentMediaItem ?: return
            Log.w("Tune", "Playback error for ${item.mediaId}: ${error.errorCodeName}", error)
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

    /** When fewer than 3 songs are left, append YouTube Music radio for the last one (like the Windows app). */
    private fun topUpRadio() {
        if (player.repeatMode == Player.REPEAT_MODE_ALL) return
        val left = player.mediaItemCount - player.currentMediaItemIndex - 1
        if (left >= 3 || player.mediaItemCount == 0) return
        val seed = player.getMediaItemAt(player.mediaItemCount - 1).mediaId
        if (radioFor == seed) return
        radioFor = seed
        scope.launch {
            val tracks = withContext(Dispatchers.IO) { runCatching { app.graph.yt.radio(seed) }.getOrNull() } ?: run { radioFor = null; return@launch }
            val present = (0 until player.mediaItemCount).map { player.getMediaItemAt(it).mediaId }.toSet()
            player.addMediaItems(tracks.filter { it.videoId !in present }.map { it.toMediaItem() })
        }
    }

    /** Look the next two songs up now, so skipping starts instantly. */
    private fun prepareUpcoming() {
        val ids = (1..2).mapNotNull { k -> (player.currentMediaItemIndex + k).takeIf { it < player.mediaItemCount }?.let { player.getMediaItemAt(it).mediaId } }
        scope.launch(Dispatchers.IO) { ids.forEach { id -> if (app.graph.downloads.localFile(id) == null) runCatching { app.graph.streams.resolve(id) } } }
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo) = session

    override fun onTaskRemoved(rootIntent: Intent?) {
        // swiping Tune away keeps the music playing; with nothing playing, stop the service
        if (!player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        scope.cancel()
        session?.run { player.release(); release() }
        session = null
        super.onDestroy()
    }
}
