package app.tune.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.navigation.NavHostController
import app.tune.Graph
import app.tune.data.Card
import app.tune.data.CardKind
import app.tune.data.Track
import app.tune.playback.PlayerConnection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** App-wide UI state and actions every screen can reach (via [LocalApp]). */
class AppState(val graph: Graph, val player: PlayerConnection, val nav: NavHostController) {
    /** Song whose action sheet is open. */
    var menuFor by mutableStateOf<Track?>(null)
    var menuPlaylistId by mutableStateOf<Long?>(null)
    var menuPlaylistIndex by mutableStateOf(-1)
    var addToPlaylist by mutableStateOf<Track?>(null)
    var nowPlayingOpen by mutableStateOf(false)
    var queueOpen by mutableStateOf(false)

    fun openCard(card: Card) {
        when (card.kind) {
            CardKind.Song -> card.track?.let { player.playRadio(it) }
            CardKind.Album -> card.browseId?.let { nav.navigate("album/$it") }
            CardKind.Playlist -> (card.playlistId ?: card.browseId)?.let { nav.navigate("ytplaylist/$it") }
            CardKind.Artist -> card.browseId?.let { nav.navigate("artist/$it") }
        }
    }

    fun openArtist(browseId: String?) { if (browseId != null) { nowPlayingOpen = false; nav.navigate("artist/$browseId") } }

    fun toggleLike(t: Track) {
        val now = !graph.library.isLiked(t.videoId)
        graph.library.setLiked(t, now)
        graph.toast(if (now) "Saved to Liked songs" else "Removed from Liked songs")
    }

    fun download(tracks: List<Track>) {
        val n = graph.downloads.enqueue(tracks)
        graph.toast(when (n) { 0 -> "Already downloaded"; 1 -> "Downloading ${tracks.first().title}"; else -> "Downloading $n songs" })
    }

    fun removeDownloads(tracks: List<Track>) {
        graph.downloads.remove(tracks.map { it.videoId })
        graph.toast(if (tracks.size == 1) "Removed download" else "Removed ${tracks.size} downloads")
    }

    /** Offline and not downloaded: the song can't play right now. */
    fun unavailable(t: Track) = !graph.online.value && !graph.isAvailableOffline(t.videoId)

    /** Plays a list, but offline it skips to the downloaded songs (same rules as the Windows app). */
    fun playList(tracks: List<Track>, index: Int, shuffle: Boolean = false) {
        if (tracks.isEmpty()) return
        if (!graph.online.value) {
            val local = tracks.filter { graph.isAvailableOffline(it.videoId) }
            if (local.isEmpty()) { graph.toast("You're offline and none of these songs are downloaded"); return }
            val start = tracks.getOrNull(index)?.let { local.indexOf(it) }?.takeIf { it >= 0 } ?: 0
            player.playList(local, if (shuffle) local.indices.random() else start, shuffle)
            return
        }
        player.playList(tracks, if (shuffle) tracks.indices.random() else index, shuffle)
    }
}

val LocalApp = staticCompositionLocalOf<AppState> { error("no app state") }

/** Loads something once per screen instance and survives configuration changes / back navigation. */
class Loader<T> : ViewModel() {
    private val _state = MutableStateFlow<Result<T>?>(null)
    val state: StateFlow<Result<T>?> = _state
    private var started = false

    fun load(force: Boolean = false, block: suspend () -> T) {
        if (started && !force) return
        started = true
        if (force) _state.value = null
        viewModelScope.launch { _state.value = withContext(Dispatchers.IO) { runCatching { block() } } }
    }
}

@Composable
fun greeting(): String = when (java.time.LocalTime.now().hour) {
    in 0..4 -> "Good evening"
    in 5..11 -> "Good morning"
    in 12..17 -> "Good afternoon"
    else -> "Good evening"
}
