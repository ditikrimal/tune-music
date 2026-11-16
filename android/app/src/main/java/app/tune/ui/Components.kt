package app.tune.ui

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.tune.data.Card
import app.tune.data.CardKind
import app.tune.data.DownloadState
import app.tune.data.Shelf
import app.tune.data.Thumbs
import app.tune.data.Track
import app.tune.data.formatDuration
import coil3.compose.AsyncImage
import kotlinx.coroutines.launch

@Composable
fun Art(url: String?, size: Dp, corner: Dp = 6.dp, modifier: Modifier = Modifier, circle: Boolean = false) {
    val px = with(LocalDensity.current) { size.roundToPx() }
    val shape = if (circle) CircleShape else RoundedCornerShape(corner)
    Box(modifier.size(size).clip(shape).background(Color(0xFF282828))) {
        AsyncImage(model = Thumbs.sized(url, (px * 1.2f).toInt().coerceAtLeast(120)), contentDescription = null,
            contentScale = ContentScale.Crop, modifier = Modifier.size(size))
    }
}

@Composable
fun IconBtn(icon: ImageVector, desc: String, tint: Color = LocalTune.current.subtle, size: Dp = 24.dp, box: Dp = 44.dp,
            modifier: Modifier = Modifier, onClick: () -> Unit) {
    Box(modifier.size(box).clip(CircleShape).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        Icon(icon, desc, tint = tint, modifier = Modifier.size(size))
    }
}

/** Heart that bounces in the accent colour when you like (not when it merely shows a liked song). */
@Composable
fun LikeButton(track: Track, size: Dp = 22.dp, box: Dp = 44.dp) {
    val app = LocalApp.current
    val version by app.graph.library.version.collectAsState()
    val liked = remember(track.videoId, version) { app.graph.library.isLiked(track.videoId) }
    val scale = remember { Animatable(1f) }
    val scope = rememberCoroutineScope()
    val c = LocalTune.current
    IconBtn(if (liked) Ico.Heart else Ico.HeartOutline, if (liked) "Remove from Liked songs" else "Save to Liked songs",
        tint = if (liked) c.accentText else c.subtle, size = size, box = box, modifier = Modifier.scale(scale.value)) {
        val becomesLiked = !liked
        app.toggleLike(track)
        if (becomesLiked) scope.launch {
            scale.animateTo(1.45f, tween(130))
            scale.animateTo(1f, spring(dampingRatio = Spring.DampingRatioMediumBouncy, stiffness = Spring.StiffnessMedium))
        }
    }
}

@Composable
fun DownloadBadge(state: DownloadState, size: Dp = 14.dp) {
    if (state == DownloadState.None) return
    val c = LocalTune.current
    val done = state == DownloadState.Done
    Box(Modifier.size(size).clip(CircleShape).background(if (done) c.accent else c.track), contentAlignment = Alignment.Center) {
        Icon(Ico.Download, null, tint = if (done) c.onAccent else c.subtle, modifier = Modifier.size(size * 0.66f))
    }
}

@Composable
fun rememberDownloadState(videoId: String): DownloadState {
    val app = LocalApp.current
    val v by app.graph.downloads.version.collectAsState()
    return remember(videoId, v) { app.graph.downloads.stateOf(videoId) }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TrackRow(track: Track, number: Int? = null, isCurrent: Boolean = false, playlistId: Long? = null, playlistIndex: Int = -1, onClick: () -> Unit) {
    val app = LocalApp.current
    val c = LocalTune.current
    val dl = rememberDownloadState(track.videoId)
    val online by app.graph.online.collectAsState()
    val dim = !online && remember(track.videoId, dl) { !app.graph.isAvailableOffline(track.videoId) }
    val openMenu = { app.menuFor = track; app.menuPlaylistId = playlistId; app.menuPlaylistIndex = playlistIndex }
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp))
            .background(if (isCurrent) c.accentSoft else Color.Transparent)
            .combinedClickable(onClick = {
                if (dim) app.graph.toast("${track.title} isn't downloaded — it plays when you're back online") else onClick()
            }, onLongClick = openMenu)
            .alpha(if (dim) 0.4f else 1f)
            .padding(horizontal = 8.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (number != null) {
            Text(if (isCurrent) "♪" else number.toString(), style = Type.caption, color = if (isCurrent) c.accentText else c.subtle,
                modifier = Modifier.width(28.dp))
        }
        Art(track.thumbnail, 48.dp)
        Column(Modifier.weight(1f).padding(start = 12.dp)) {
            Text(track.title, style = Type.body, color = if (isCurrent) c.accentText else c.text, maxLines = 1, overflow = TextOverflow.Ellipsis,
                fontWeight = if (isCurrent) FontWeight.SemiBold else FontWeight.Normal)
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
                DownloadBadge(dl)
                if (dl != DownloadState.None) Spacer(Modifier.width(6.dp))
                Text(listOfNotNull(track.artistLine.ifEmpty { null }, formatDuration(track.durationSec).ifEmpty { null }).joinToString(" • "),
                    style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        IconBtn(Ico.More, "More options", onClick = openMenu)
    }
}

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) =
    Text(text, style = Type.section, color = LocalTune.current.text, modifier = modifier.padding(start = 16.dp, end = 16.dp, top = 24.dp, bottom = 12.dp))

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun CardItem(card: Card, width: Dp = 150.dp) {
    val app = LocalApp.current
    val c = LocalTune.current
    val round = card.kind == CardKind.Artist
    Column(Modifier.width(width).clip(RoundedCornerShape(12.dp))
        .combinedClickable(onClick = { app.openCard(card) }, onLongClick = { card.track?.let { app.menuFor = it; app.menuPlaylistId = null } })
        .padding(6.dp)) {
        Box {
            Art(card.thumbnail, width - 12.dp, corner = 10.dp, circle = round)
            card.track?.let { t ->
                val version by app.graph.library.version.collectAsState()
                if (remember(t.videoId, version) { app.graph.library.isLiked(t.videoId) })
                    Box(Modifier.align(Alignment.TopEnd).padding(6.dp).size(26.dp).clip(CircleShape).background(Color(0xB0000000)), contentAlignment = Alignment.Center) {
                        Icon(Ico.Heart, "Liked", tint = c.accentText, modifier = Modifier.size(15.dp))
                    }
            }
        }
        Text(card.title, style = Type.bodyStrong, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 8.dp))
        card.subtitle?.takeIf { it.isNotBlank() }?.let {
            Text(it, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

/** The first Home row: big tinted cards like the Windows "Recently played". */
@Composable
fun FeaturedCard(card: Card) {
    val app = LocalApp.current
    val c = LocalTune.current
    Column(Modifier.width(168.dp).clip(RoundedCornerShape(22.dp)).background(c.card).clickable { app.openCard(card) }.padding(12.dp),
        horizontalAlignment = Alignment.CenterHorizontally) {
        Text(card.title, style = Type.bodyStrong.copy(fontSize = 16.sp), color = c.text, maxLines = 2, overflow = TextOverflow.Ellipsis,
            modifier = Modifier.fillMaxWidth().height(44.dp))
        Text(card.subtitle ?: "", style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(10.dp))
        Art(card.thumbnail, 144.dp, corner = 14.dp)
    }
}

@Composable
fun ShelfRow(shelf: Shelf, featured: Boolean = false) {
    SectionTitle(shelf.title)
    LazyRow(contentPadding = PaddingValues(horizontal = 10.dp), horizontalArrangement = Arrangement.spacedBy(if (featured) 10.dp else 2.dp)) {
        items(shelf.items, key = { (it.videoId ?: it.browseId ?: it.title) + it.kind }) { if (featured) FeaturedCard(it) else CardItem(it) }
    }
}

// ---------------- song action sheet ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TrackMenuSheet() {
    val app = LocalApp.current
    val t = app.menuFor ?: return
    val c = LocalTune.current
    val dl = rememberDownloadState(t.videoId)
    val version by app.graph.library.version.collectAsState()
    val liked = remember(t.videoId, version) { app.graph.library.isLiked(t.videoId) }
    val close = { app.menuFor = null }
    ModalBottomSheet(onDismissRequest = close, containerColor = c.card) {
        Row(Modifier.padding(horizontal = 20.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Art(t.thumbnail, 52.dp)
            Column(Modifier.padding(start = 14.dp)) {
                Text(t.title, style = Type.bodyStrong, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(t.artistLine, style = Type.caption, color = c.subtle, maxLines = 1)
            }
        }
        Spacer(Modifier.height(8.dp))
        MenuRow(Ico.Radio, "Start radio") { app.player.playRadio(t); close() }
        MenuRow(Ico.Next, "Play next") { app.player.playNext(t); app.graph.toast("Will play next"); close() }
        MenuRow(Ico.Queue, "Add to queue") { app.player.addToQueue(t); app.graph.toast("Added to queue"); close() }
        MenuRow(Ico.PlaylistAdd, "Add to playlist") { app.addToPlaylist = t; close() }
        MenuRow(if (liked) Ico.Heart else Ico.HeartOutline, if (liked) "Remove from Liked songs" else "Save to Liked songs", accent = liked) { app.toggleLike(t); close() }
        when (dl) {
            DownloadState.None -> MenuRow(Ico.Download, "Download") { app.download(listOf(t)); close() }
            DownloadState.Done -> MenuRow(Ico.DownloadDone, "Remove download", accent = true) { app.removeDownloads(listOf(t)); close() }
            else -> MenuRow(Ico.Close, "Cancel download") { app.removeDownloads(listOf(t)); close() }
        }
        app.menuPlaylistId?.let { pid ->
            MenuRow(Ico.Delete, "Remove from this playlist") { app.graph.library.removeFromPlaylist(pid, app.menuPlaylistIndex); close() }
        }
        t.artists.filter { it.browseId != null }.take(2).forEach { a -> MenuRow(Ico.Person, "Go to ${a.name}") { close(); app.openArtist(a.browseId) } }
        t.album?.browseId?.let { id -> MenuRow(Ico.Album, "Go to album") { close(); app.nowPlayingOpen = false; app.nav.navigate("album/$id") } }
        Spacer(Modifier.height(24.dp))
    }
}

@Composable
fun MenuRow(icon: ImageVector, text: String, accent: Boolean = false, onClick: () -> Unit) {
    val c = LocalTune.current
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 24.dp, vertical = 14.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, tint = if (accent) c.accentText else c.subtle, modifier = Modifier.size(22.dp))
        Text(text, style = Type.body, color = c.text, modifier = Modifier.padding(start = 20.dp))
    }
}

// ---------------- dialogs ----------------

@Composable
fun NameDialog(title: String, suggested: String, ok: String = "Create", onLink: ((String) -> Unit)? = null, onDone: (String?) -> Unit) {
    val c = LocalTune.current
    var linkMode by remember { mutableStateOf(false) }
    var value by remember { mutableStateOf(TextFieldValue(suggested, TextRange(0, suggested.length))) }
    val focus = remember { FocusRequester() }
    val clipboard = androidx.compose.ui.platform.LocalClipboardManager.current
    LaunchedEffect(linkMode) { focus.requestFocus() }
    AlertDialog(
        onDismissRequest = { onDone(null) },
        containerColor = c.card,
        title = { Text(if (linkMode) "Import a playlist" else title, style = Type.title2, color = c.text) },
        text = {
            Column {
                OutlinedTextField(value, { value = it }, singleLine = true, modifier = Modifier.fillMaxWidth().focusRequester(focus),
                    placeholder = { if (linkMode) Text("https://music.youtube.com/playlist?list=\u2026", color = c.subtle, maxLines = 1) },
                    shape = RoundedCornerShape(22.dp),
                    colors = OutlinedTextFieldDefaults.colors(focusedBorderColor = c.accent, cursorColor = c.accent, unfocusedBorderColor = c.track))
                if (linkMode) Text("Paste a YouTube Music playlist link. Its songs become a new playlist.", style = Type.caption, color = c.subtle,
                    modifier = Modifier.padding(top = 8.dp, start = 4.dp))
            }
        },
        confirmButton = {
            TextButton(onClick = {
                val text = value.text.trim()
                if (text.isEmpty()) return@TextButton
                if (linkMode) onLink?.invoke(text) else onDone(text)
            }) { Text(if (linkMode) "Import" else ok, color = c.accentText, fontWeight = FontWeight.SemiBold) }
        },
        dismissButton = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                // quiet link icon: switch between naming a new playlist and importing one from a link
                if (onLink != null) IconBtn(Ico.Link, "Import from a YouTube Music link", tint = if (linkMode) c.accentText else c.subtle, size = 22.dp, box = 40.dp) {
                    linkMode = !linkMode
                    val pasted = clipboard.getText()?.text?.trim()?.takeIf { app.tune.data.PlaylistLinks.parse(it) != null }
                    val t = if (linkMode) pasted.orEmpty() else suggested
                    value = TextFieldValue(t, TextRange(0, t.length))
                }
                TextButton(onClick = { onDone(null) }) { Text("Cancel", color = c.subtle) }
            }
        },
    )
}

@Composable
fun AddToPlaylistDialog() {
    val app = LocalApp.current
    val t = app.addToPlaylist ?: return
    val c = LocalTune.current
    var naming by remember { mutableStateOf(false) }
    val lists = remember { app.graph.library.playlists() }
    if (naming) {
        NameDialog("New playlist", "My playlist #${lists.size + 1}") { name ->
            if (name != null) {
                val id = app.graph.library.createPlaylist(name)
                app.graph.library.addToPlaylist(id, t)
                app.graph.toast("Added to $name")
            }
            app.addToPlaylist = null
        }
        return
    }
    AlertDialog(
        onDismissRequest = { app.addToPlaylist = null },
        containerColor = c.card,
        title = { Text("Add to playlist", style = Type.title2, color = c.text) },
        text = {
            Column {
                MenuRow(Ico.Add, "New playlist", accent = true) { naming = true }
                lists.forEach { p ->
                    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).clickable {
                        app.graph.library.addToPlaylist(p.id, t); app.graph.toast("Added to ${p.name}"); app.addToPlaylist = null
                    }.padding(horizontal = 8.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Art(p.thumbnail, 44.dp)
                        Column(Modifier.padding(start = 12.dp)) {
                            Text(p.name, style = Type.body, color = c.text)
                            Text("${p.count} songs", style = Type.caption, color = c.subtle)
                        }
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = { app.addToPlaylist = null }) { Text("Cancel", color = c.subtle) } },
    )
}

/**
 * Quick picks (like YouTube Music): song rows four to a column, swiped sideways a column at a time.
 * Tapping a song plays it with the rest of the picks queued; Play all starts from the top.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun QuickPicksRow(shelf: Shelf) {
    val app = LocalApp.current
    val c = LocalTune.current
    val tracks = remember(shelf) { shelf.items.mapNotNull { it.track } }
    val columns = remember(tracks) { tracks.chunked(4) }
    val ui by app.player.ui.collectAsState()
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 12.dp, top = 24.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(shelf.title, style = Type.section, color = c.text, modifier = Modifier.weight(1f))
        Text("Play all", style = Type.bodyStrong.copy(fontSize = 13.sp), color = c.text,
            modifier = Modifier.clip(RoundedCornerShape(16.dp)).background(c.card).clickable { app.playList(tracks, 0) }
                .padding(horizontal = 14.dp, vertical = 7.dp))
    }
    val state = androidx.compose.foundation.lazy.rememberLazyListState()
    val screen = androidx.compose.ui.platform.LocalConfiguration.current.screenWidthDp.dp
    LazyRow(state = state, contentPadding = PaddingValues(horizontal = 8.dp),
        flingBehavior = androidx.compose.foundation.gestures.snapping.rememberSnapFlingBehavior(state, androidx.compose.foundation.gestures.snapping.SnapPosition.Start)) {
        items(columns.size) { col ->
            Column(Modifier.width(screen - 48.dp)) {
                columns[col].forEach { t ->
                    val index = col * 4 + columns[col].indexOf(t)
                    val current = ui.track?.videoId == t.videoId
                    Row(Modifier.fillMaxWidth().height(64.dp).clip(RoundedCornerShape(10.dp))
                        .background(if (current) c.accentSoft else Color.Transparent)
                        .combinedClickable(onClick = { app.playList(tracks, index) }, onLongClick = { app.menuFor = t; app.menuPlaylistId = null })
                        .padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Art(t.thumbnail, 48.dp)
                        Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                            Text(t.title, style = Type.bodyStrong, color = if (current) c.accentText else c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(listOfNotNull(t.artistLine.ifEmpty { null }, t.album?.title).joinToString(" \u2022 "),
                                style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        IconBtn(Ico.More, "More options", size = 20.dp, box = 40.dp) { app.menuFor = t; app.menuPlaylistId = null }
                    }
                }
            }
        }
    }
}
