package app.tune.playback

import android.content.ComponentName
import android.content.Context
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import app.tune.data.Track
import com.google.common.util.concurrent.MoreExecutors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

data class PlayerUi(
    val track: Track? = null,
    val isPlaying: Boolean = false,
    val isLoading: Boolean = false,
    val durationMs: Long = 0,
    val queue: List<Track> = emptyList(),
    val index: Int = -1,
    val shuffle: Boolean = false,
    val repeat: Int = Player.REPEAT_MODE_OFF,
)

/** The UI's handle on the background player (a Media3 controller connected to [PlaybackService]). */
class PlayerConnection(context: Context) {
    private var controller: MediaController? = null
    private val pending = mutableListOf<(MediaController) -> Unit>()
    private val _ui = MutableStateFlow(PlayerUi())
    val ui: StateFlow<PlayerUi> = _ui

    init {
        val token = SessionToken(context, ComponentName(context, PlaybackService::class.java))
        val future = MediaController.Builder(context, token).buildAsync()
        future.addListener({
            val c = future.get()
            controller = c
            c.addListener(object : Player.Listener {
                override fun onEvents(player: Player, events: Player.Events) = refresh()
            })
            refresh()
            pending.forEach { it(c) }
            pending.clear()
        }, MoreExecutors.directExecutor())
    }

    private fun run(action: (MediaController) -> Unit) {
        controller?.let(action) ?: pending.add(action)
    }

    private fun refresh() {
        val c = controller ?: return
        val items = (0 until c.mediaItemCount).mapNotNull { c.getMediaItemAt(it).track() }
        _ui.value = PlayerUi(
            track = c.currentMediaItem?.track(),
            isPlaying = c.isPlaying || (c.playWhenReady && c.playbackState == Player.STATE_BUFFERING),
            isLoading = c.playbackState == Player.STATE_BUFFERING,
            durationMs = c.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: (c.currentMediaItem?.track()?.durationSec?.times(1000L) ?: 0),
            queue = items,
            index = c.currentMediaItemIndex,
            shuffle = c.shuffleModeEnabled,
            repeat = c.repeatMode,
        )
    }

    val positionMs: Long get() = controller?.currentPosition ?: 0

    fun playList(tracks: List<Track>, index: Int, shuffle: Boolean = false) = run { c ->
        c.shuffleModeEnabled = shuffle
        c.setMediaItems(tracks.map { it.toMediaItem() }, index.coerceIn(0, (tracks.size - 1).coerceAtLeast(0)), 0)
        c.prepare(); c.play()
    }

    /** One song; the service appends radio for it right away (fewer than 3 songs left). */
    fun playRadio(track: Track) = playList(listOf(track), 0)

    fun playNext(track: Track) = run { c ->
        if (c.mediaItemCount == 0) { c.setMediaItem(track.toMediaItem()); c.prepare(); c.play() }
        else c.addMediaItem(c.currentMediaItemIndex + 1, track.toMediaItem())
    }

    fun addToQueue(track: Track) = run { c ->
        if (c.mediaItemCount == 0) { c.setMediaItem(track.toMediaItem()); c.prepare(); c.play() } else c.addMediaItem(track.toMediaItem())
    }

    fun toggle() = run { c ->
        if (c.isPlaying) c.pause() else {
            if (c.playbackState == Player.STATE_IDLE) c.prepare()
            if (c.playbackState == Player.STATE_ENDED) c.seekTo(0)
            c.play()
        }
    }

    fun next() = run { it.seekToNextMediaItem() }
    fun previous() = run { it.seekToPrevious() }
    fun seek(ms: Long) = run { it.seekTo(ms) }
    fun skipTo(index: Int) = run { it.seekTo(index, 0); it.play() }
    fun remove(index: Int) = run { if (index != it.currentMediaItemIndex) it.removeMediaItem(index) }
    fun move(from: Int, to: Int) = run { it.moveMediaItem(from, to) }
    fun clearUpcoming() = run { c -> if (c.currentMediaItemIndex + 1 < c.mediaItemCount) c.removeMediaItems(c.currentMediaItemIndex + 1, c.mediaItemCount) }
    fun toggleShuffle() = run { it.shuffleModeEnabled = !it.shuffleModeEnabled }
    fun cycleRepeat() = run {
        it.repeatMode = when (it.repeatMode) {
            Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
            Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
            else -> Player.REPEAT_MODE_OFF
        }
    }

    fun pause() = run { it.pause() }
    fun setVolume(v: Float) = run { it.volume = v }
}
