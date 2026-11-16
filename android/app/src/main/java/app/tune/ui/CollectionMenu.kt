package app.tune.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.tune.data.Card
import app.tune.data.CardKind
import app.tune.data.DownloadState
import app.tune.data.SpeedDialPins
import app.tune.data.Track
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** An album or playlist the options sheet is about (your own playlists by row id). */
data class CollectionTarget(val kind: CollectionKind, val id: String, val title: String, val subtitle: String?, val thumbnail: String?) {
    companion object {
        /** From a Home / search / speed dial card; null for songs and artists. */
        fun of(card: Card, lookupLocal: (String) -> Long?): CollectionTarget? = when (card.kind) {
            CardKind.Album -> card.browseId?.let { CollectionTarget(CollectionKind.Album, it, card.title, card.subtitle, card.thumbnail) }
            CardKind.Playlist -> {
                val p = card.playlistId ?: card.browseId
                when {
                    p == null -> null
                    p.startsWith("tune:") -> lookupLocal(p.removePrefix("tune:"))?.let { CollectionTarget(CollectionKind.Local, it.toString(), card.title, "Your playlist", card.thumbnail) }
                    else -> CollectionTarget(CollectionKind.YtPlaylist, p, card.title, card.subtitle, card.thumbnail)
                }
            }
            else -> null
        }
    }
}

/**
 * Options for a playlist or album, in the same sheet style as a song's: from a long-press anywhere (Library,
 * Home cards, speed dial) and from the ⋯ button on its own page.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CollectionMenuSheet() {
    val app = LocalApp.current
    val target = app.collectionMenuFor ?: return
    val c = LocalTune.current
    val scope = rememberCoroutineScope()
    val lib = app.graph.library
    val version by lib.version.collectAsState()
    val dv by app.graph.downloads.version.collectAsState()
    val close = { app.collectionMenuFor = null }
    val local = target.kind == CollectionKind.Local
    val localTracks = remember(target, version) { if (local) lib.playlistTracks(target.id.toLong()) else null }
    val states = remember(localTracks, dv) { localTracks?.map { app.graph.downloads.stateOf(it.videoId) } }
    val pinCard = remember(target) {
        when (target.kind) {
            CollectionKind.Local -> lib.playlistUid(target.id.toLong())?.let { SpeedDialPins.localPlaylistCard(it, target.title, target.thumbnail) }
            CollectionKind.Album -> Card(CardKind.Album, target.title, target.subtitle, target.thumbnail, browseId = target.id)
            CollectionKind.YtPlaylist -> Card(CardKind.Playlist, target.title, target.subtitle, target.thumbnail, playlistId = target.id)
            else -> null
        }
    }
    val pinned = remember(pinCard, version) { pinCard?.let { app.isPinned(it) } == true }
    val onItsPage = app.nav.currentBackStackEntry?.destination?.route?.let {
        it == "local/{id}" || it == "album/{id}" || it == "ytplaylist/{id}"
    } == true && app.nav.currentBackStackEntry?.arguments?.getString("id") == target.id

    /** The songs, loading an album / YouTube playlist first if needed. */
    fun withTracks(action: (List<Track>) -> Unit) {
        localTracks?.let { action(it); return }
        scope.launch {
            val loaded = withContext(Dispatchers.IO) {
                runCatching { if (target.kind == CollectionKind.Album) app.graph.yt.album(target.id).tracks else app.graph.yt.playlist(target.id).tracks }.getOrNull()
            }
            if (loaded.isNullOrEmpty()) app.graph.toast("Couldn't load this — check your connection") else action(loaded)
        }
    }

    ModalBottomSheet(onDismissRequest = close, containerColor = c.card) {
        Row(Modifier.padding(horizontal = 20.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Art(target.thumbnail ?: localTracks?.firstOrNull()?.thumbnail, 52.dp)
            Column(Modifier.padding(start = 14.dp)) {
                Text(target.title, style = Type.bodyStrong, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                (localTracks?.let { "Playlist · ${it.size} songs" } ?: target.subtitle)?.let {
                    Text(it, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
        Spacer(Modifier.height(8.dp))
        MenuRow(Ico.Play, "Play") { close(); withTracks { app.playList(it, 0) } }
        MenuRow(Ico.Shuffle, "Shuffle") { close(); withTracks { app.playList(it, 0, shuffle = true) } }
        if (!onItsPage) MenuRow(if (target.kind == CollectionKind.Album) Ico.Album else Ico.PlaylistAdd, "Open") {
            close()
            app.nav.navigate(when (target.kind) { CollectionKind.Local -> "local/${target.id}"; CollectionKind.Album -> "album/${target.id}"; else -> "ytplaylist/${target.id}" })
        }
        val anyDownloaded = states?.any { it != DownloadState.None } == true
        val allDownloaded = states?.isNotEmpty() == true && states.all { it != DownloadState.None }
        if (!allDownloaded) MenuRow(Ico.Download, if (anyDownloaded) "Download the new songs" else "Download") { close(); withTracks { app.download(it) } }
        if (anyDownloaded) MenuRow(Ico.DownloadDone, "Remove downloads", accent = true) {
            close(); localTracks?.let { t -> app.removeDownloads(t.filter { app.graph.downloads.stateOf(it.videoId) != DownloadState.None }) }
        }
        pinCard?.let { MenuRow(Ico.Pin, if (pinned) "Unpin from speed dial" else "Pin to speed dial", accent = pinned) { app.togglePin(it); close() } }
        if (local) {
            MenuRow(Ico.Edit, "Rename") { close(); app.renamePlaylistId = target.id.toLong() }
            MenuRow(Ico.Delete, "Delete playlist") {
                close()
                lib.deletePlaylist(target.id.toLong())
                app.graph.toast("Deleted ${target.title}")
                if (onItsPage) app.nav.popBackStack()
            }
        }
        Spacer(Modifier.height(24.dp))
    }
}

/** Rename one of your playlists (from its options sheet). */
@Composable
fun RenamePlaylistDialog() {
    val app = LocalApp.current
    val id = app.renamePlaylistId ?: return
    val current = remember(id) { app.graph.library.playlists().firstOrNull { it.id == id }?.name ?: "" }
    NameDialog("Rename playlist", current, ok = "Save") { name ->
        app.renamePlaylistId = null
        if (!name.isNullOrBlank() && name != current) app.graph.library.renamePlaylist(id, name.trim())
    }
}
