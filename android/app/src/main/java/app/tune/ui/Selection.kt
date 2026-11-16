package app.tune.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import app.tune.data.DownloadState
import app.tune.data.Track

/** Which songs on a page are chosen (by position, so a song twice in a playlist works). */
@Stable
class SongSelection {
    var active by mutableStateOf(false)
    var chosen by mutableStateOf(setOf<Int>())

    fun toggle(i: Int) {
        chosen = if (i in chosen) chosen - i else chosen + i
        active = true
    }

    fun close() { active = false; chosen = emptySet() }
    fun tracks(all: List<Track>) = chosen.sorted().mapNotNull { all.getOrNull(it) }
}

data class SelectionAction(val icon: ImageVector, val label: String, val onClick: () -> Unit)

/** Pinned at the top while choosing songs: how many, and what to do with them. */
@Composable
fun SelectionBar(selection: SongSelection, total: Int, actions: List<SelectionAction>) {
    val c = LocalTune.current
    Row(Modifier.fillMaxWidth().background(c.base).padding(horizontal = 8.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        IconBtn(Ico.Close, "Done", tint = c.text) { selection.close() }
        Text(if (selection.chosen.isEmpty()) "Select songs" else "${selection.chosen.size} selected", style = Type.bodyStrong, color = c.text,
            modifier = Modifier.padding(start = 4.dp, end = 8.dp))
        Text(if (selection.chosen.size == total) "None" else "All", style = Type.bodyStrong, color = c.accentText,
            modifier = Modifier.clip(RoundedCornerShape(14.dp)).clickable {
                selection.chosen = if (selection.chosen.size == total) emptySet() else (0 until total).toSet()
            }.padding(horizontal = 10.dp, vertical = 6.dp))
        Row(Modifier.weight(1f).horizontalScroll(rememberScrollState()), horizontalArrangement = androidx.compose.foundation.layout.Arrangement.End) {
            if (selection.chosen.isNotEmpty()) actions.forEach { a -> IconBtn(a.icon, a.label, tint = c.text, size = 22.dp, box = 42.dp, onClick = a.onClick) }
        }
    }
}

/** The usual things to do with chosen songs; [remove] for a playlist / Liked songs page. */
fun selectionActions(app: AppState, songs: () -> List<Track>, done: () -> Unit, removeLabel: String? = null, remove: (() -> Unit)? = null): List<SelectionAction> {
    val list = mutableListOf(
        SelectionAction(Ico.Play, "Play") { app.playList(songs(), 0); done() },
        SelectionAction(Ico.Next, "Play next") {
            songs().asReversed().forEach { app.player.playNext(it) }
            app.graph.toast("${songs().size} songs will play next"); done()
        },
        SelectionAction(Ico.PlaylistAdd, "Add to playlist") { app.addToPlaylist = songs(); done() },
        SelectionAction(Ico.Download, "Download") { app.download(songs()); done() },
        SelectionAction(Ico.DownloadDone, "Remove downloads") {
            val downloaded = songs().filter { app.graph.downloads.stateOf(it.videoId) != DownloadState.None }
            if (downloaded.isNotEmpty()) app.removeDownloads(downloaded) else app.graph.toast("None of these are downloaded")
            done()
        },
    )
    if (remove != null && removeLabel != null) list += SelectionAction(Ico.Delete, removeLabel) { remove(); done() }
    list += SelectionAction(Ico.Block, "Not interested") {
        songs().forEach { app.graph.library.setNotInterested(it.videoId) }
        app.graph.toast("You won't get these songs in recommendations"); app.homeChanged++; done()
    }
    return list
}
