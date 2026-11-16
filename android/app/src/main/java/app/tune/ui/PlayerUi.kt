package app.tune.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.media3.common.Player
import app.tune.data.DownloadState
import app.tune.data.Lrc
import app.tune.data.SongLyrics
import app.tune.data.Track
import app.tune.data.formatDuration
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.first
import kotlin.math.roundToInt

/** Current position, ticking while something plays (only while this is on screen). */
@Composable
fun rememberPosition(): Long {
    val app = LocalApp.current
    val ui by app.player.ui.collectAsState()
    val pos by produceState(app.player.positionMs, ui.track?.videoId, ui.isPlaying) {
        while (true) { value = app.player.positionMs; delay(if (ui.isPlaying) 250 else 1000) }
    }
    return pos
}

// ============================== mini player ==============================

@Composable
fun MiniPlayer(modifier: Modifier = Modifier) {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    val t = ui.track ?: return
    val pos = rememberPosition()
    Column(modifier.padding(horizontal = 8.dp).shadow(12.dp, RoundedCornerShape(16.dp)).clip(RoundedCornerShape(16.dp))
        .background(Color(0xF2242426)).clickable { app.nowPlayingOpen = true }
        .pointerInput(Unit) { detectVerticalDragGestures { _, dy -> if (dy < -12) app.nowPlayingOpen = true } }) {
        Row(Modifier.padding(start = 8.dp, top = 8.dp, bottom = 6.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Art(t.thumbnail, 44.dp, corner = 8.dp)
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text(t.title, style = Type.bodyStrong.copy(fontSize = 14.sp), color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(t.artistLine, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            LikeButton(t, size = 22.dp, box = 40.dp)
            IconBtn(if (ui.isPlaying) Ico.Pause else Ico.Play, "Play/Pause", tint = c.text, size = 28.dp, box = 44.dp) { app.player.toggle() }
        }
        // thin accent progress line
        Box(Modifier.fillMaxWidth().padding(horizontal = 10.dp).height(2.dp).clip(RoundedCornerShape(1.dp)).background(c.track)) {
            Box(Modifier.fillMaxHeight().fillMaxWidth(if (ui.durationMs > 0) (pos.toFloat() / ui.durationMs).coerceIn(0f, 1f) else 0f).background(c.accent))
        }
        Spacer(Modifier.height(4.dp))
    }
}

// ============================== now playing ==============================

private enum class NpPane { Art, Lyrics, Queue }

@Composable
fun NowPlaying() {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    val t = ui.track
    var pane by remember { mutableStateOf(if (app.queueOpen) NpPane.Queue else NpPane.Art) }
    BackHandler { if (pane != NpPane.Art) pane = NpPane.Art else app.nowPlayingOpen = false }
    LaunchedEffect(app.queueOpen) { if (app.queueOpen) { pane = NpPane.Queue; app.queueOpen = false } }

    // solid base first: the gradient alone is see-through and the page behind showed at the top
    Box(Modifier.fillMaxSize().background(c.base).background(Brush.verticalGradient(listOf(c.accent.copy(alpha = 0.32f), c.base, c.base)))
        .pointerInput(Unit) { detectVerticalDragGestures { _, dy -> if (dy > 40 && pane == NpPane.Art) app.nowPlayingOpen = false } }) {
        Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(horizontal = 20.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconBtn(Ico.ChevronDown, "Close", tint = c.text, size = 30.dp) { app.nowPlayingOpen = false }
                Text("NOW PLAYING", style = Type.label, color = c.subtle, modifier = Modifier.weight(1f), textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                IconBtn(Ico.More, "More options", tint = c.text) { t?.let { app.menuFor = it; app.menuPlaylistId = null } }
            }
            if (t == null) { Message("Play a song to see it here."); return@Column }

            Box(Modifier.weight(1f).fillMaxWidth().padding(vertical = 12.dp), contentAlignment = Alignment.Center) {
                when (pane) {
                    NpPane.Art -> Art(t.thumbnail, 340.dp, corner = 18.dp, modifier = Modifier.shadow(24.dp, RoundedCornerShape(18.dp)))
                    NpPane.Lyrics -> LyricsPane(t)
                    NpPane.Queue -> QueuePane()
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(t.title, style = Type.title2, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(t.artistLine, style = Type.body, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.clickable(enabled = t.artists.any { it.browseId != null }) { app.openArtist(t.artists.firstOrNull { it.browseId != null }?.browseId) })
                }
                val dl = rememberDownloadState(t.videoId)
                IconBtn(if (dl == DownloadState.Done) Ico.DownloadDone else Ico.Download, "Download",
                    tint = if (dl != DownloadState.None) c.accentText else c.subtle) {
                    if (dl == DownloadState.None) app.download(listOf(t)) else app.graph.toast("Downloaded — remove it from the ⋯ menu")
                }
                LikeButton(t, size = 26.dp, box = 48.dp)
            }
            SeekBar(ui.durationMs)
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                IconBtn(Ico.Shuffle, "Shuffle", tint = if (ui.shuffle) c.accentText else c.subtle) { app.player.toggleShuffle() }
                IconBtn(Ico.Prev, "Previous", tint = c.text, size = 34.dp, box = 56.dp) { app.player.previous() }
                Box(Modifier.size(72.dp).shadow(16.dp, CircleShape, ambientColor = c.accent, spotColor = c.accent).clip(CircleShape).background(c.accent)
                    .clickable { app.player.toggle() }, contentAlignment = Alignment.Center) {
                    Icon(if (ui.isPlaying) Ico.Pause else Ico.Play, "Play/Pause", tint = c.onAccent, modifier = Modifier.size(38.dp))
                }
                IconBtn(Ico.Next, "Next", tint = c.text, size = 34.dp, box = 56.dp) { app.player.next() }
                IconBtn(if (ui.repeat == Player.REPEAT_MODE_ONE) Ico.RepeatOne else Ico.Repeat, "Repeat",
                    tint = if (ui.repeat != Player.REPEAT_MODE_OFF) c.accentText else c.subtle) { app.player.cycleRepeat() }
            }
            Row(Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 8.dp), horizontalArrangement = Arrangement.SpaceEvenly) {
                PaneButton(Ico.Lyrics, "Lyrics", pane == NpPane.Lyrics) { pane = if (pane == NpPane.Lyrics) NpPane.Art else NpPane.Lyrics }
                SleepButton()
                PaneButton(Ico.Queue, "Queue", pane == NpPane.Queue) { pane = if (pane == NpPane.Queue) NpPane.Art else NpPane.Queue }
            }
        }
    }
}

@Composable
private fun PaneButton(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, on: Boolean, onClick: () -> Unit) {
    val c = LocalTune.current
    Row(Modifier.clip(RoundedCornerShape(20.dp)).background(if (on) c.accentSoft else Color.Transparent).clickable(onClick = onClick)
        .padding(horizontal = 14.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, tint = if (on) c.accentText else c.subtle, modifier = Modifier.size(20.dp))
        Text(label, style = Type.caption, color = if (on) c.accentText else c.subtle, modifier = Modifier.padding(start = 8.dp), fontWeight = FontWeight.SemiBold)
    }
}

@Composable
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
private fun SeekBar(durationMs: Long) {
    val app = LocalApp.current
    val c = LocalTune.current
    val pos = rememberPosition()
    var dragging by remember { mutableStateOf(false) }
    var dragValue by remember { mutableFloatStateOf(0f) }
    // after a seek, hold the new spot until the player catches up (no snapping back)
    var heldMs by remember { mutableLongStateOf(-1L) }
    val shown = when {
        dragging -> dragValue
        heldMs >= 0 && kotlin.math.abs(pos - heldMs) > 1500 -> heldMs.toFloat()
        else -> { if (heldMs >= 0) heldMs = -1; pos.toFloat() }
    }
    val max = durationMs.coerceAtLeast(1).toFloat()
    Column(Modifier.padding(top = 8.dp)) {
        val fraction = (shown / max).coerceIn(0f, 1f)
        // Tune's bar (same as Windows): thin accent fill, round accent thumb
        Slider(value = shown.coerceIn(0f, max), valueRange = 0f..max,
            onValueChange = { dragging = true; dragValue = it },
            onValueChangeFinished = { dragging = false; heldMs = dragValue.toLong(); app.player.seek(dragValue.toLong()) },
            thumb = {
                Box(Modifier.size(if (dragging) 20.dp else 16.dp).shadow(4.dp, CircleShape).clip(CircleShape).background(c.accent))
            },
            track = {
                Box(Modifier.fillMaxWidth().height(4.dp).clip(RoundedCornerShape(2.dp)).background(c.track)) {
                    Box(Modifier.fillMaxHeight().fillMaxWidth(fraction).background(c.accent))
                }
            })
        Row(Modifier.fillMaxWidth().offset(y = (-6).dp)) {
            Text(formatDuration((shown / 1000).toInt()), style = Type.caption, color = c.subtle, modifier = Modifier.weight(1f))
            Text(formatDuration((durationMs / 1000).toInt()), style = Type.caption, color = c.subtle)
        }
    }
}

@Composable
private fun LyricsPane(t: Track) {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val lyrics by produceState<Result<SongLyrics?>?>(null, t.videoId, version) {
        value = withContext(Dispatchers.IO) { runCatching { app.graph.lyrics.get(t) } }
    }
    val l = lyrics
    if (l == null) { Loading(); return }
    val song = l.getOrNull()
    if (song == null) { Message("No lyrics for this song yet."); return }
    val lines = song.lines
    if (lines == null) {
        LazyColumn(Modifier.fillMaxSize()) {
            item { Text(song.text, style = Type.title2.copy(lineHeight = 34.sp), color = c.text) }
            item { Text(song.source ?: "", style = Type.caption, color = c.subtle, modifier = Modifier.padding(vertical = 16.dp)) }
        }
        return
    }
    val pos = rememberPosition()
    val current = Lrc.indexAt(lines, pos + 250)
    val list = rememberLazyListState()
    LaunchedEffect(current) { if (current >= 0) list.animateScrollToItem((current - 2).coerceAtLeast(0)) }
    LazyColumn(state = list, modifier = Modifier.fillMaxSize()) {
        itemsIndexed(lines) { i, line ->
            val alpha by animateFloatAsState(if (i == current) 1f else if (i < current) 0.35f else 0.55f, label = "line")
            Text(line.text.ifBlank { "♪" }, style = Type.title2.copy(fontSize = 24.sp, lineHeight = 32.sp),
                color = c.text.copy(alpha = alpha), fontWeight = FontWeight.Bold,
                modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).clickable { app.player.seek(line.atMs) }.padding(vertical = 8.dp))
        }
        item { Text(song.source ?: "", style = Type.caption, color = c.subtle, modifier = Modifier.padding(vertical = 16.dp)) }
    }
}

// ============================== queue ==============================

@Composable
private fun QueuePane() {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    val upcoming = ui.queue.withIndex().drop(ui.index + 1).toList()
    val rowPx = with(LocalDensity.current) { 64.dp.toPx() }
    var dragIndex by remember { mutableIntStateOf(-1) }
    var dragOffset by remember { mutableFloatStateOf(0f) }
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Queue", style = Type.title2, color = c.text)
                val total = upcoming.sumOf { it.value.durationSec ?: 0 }
                if (upcoming.isNotEmpty()) Text("${upcoming.size} songs · ${total / 60} min", style = Type.caption, color = c.subtle)
            }
            if (upcoming.isNotEmpty()) IconBtn(Ico.Clear, "Clear the queue") { app.player.clearUpcoming(); app.graph.toast("Queue cleared") }
        }
        if (upcoming.isEmpty()) { Message("Nothing queued. Songs like this one will be added automatically."); return@Column }
        Text("NEXT UP · hold ≡ and drag to reorder", style = Type.label, color = c.subtle, modifier = Modifier.padding(top = 12.dp, bottom = 6.dp))
        LazyColumn(Modifier.fillMaxSize()) {
            itemsIndexed(upcoming, key = { _, it -> "${it.index}${it.value.videoId}" }) { pos, (index, t) ->
                val isDragged = dragIndex == pos
                Row(Modifier.fillMaxWidth().height(64.dp)
                    .graphicsLayer { translationY = if (isDragged) dragOffset else 0f; shadowElevation = if (isDragged) 16f else 0f }
                    .clip(RoundedCornerShape(10.dp)).background(if (isDragged) c.cardHover else Color.Transparent)
                    .clickable { app.player.skipTo(index) }.padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(40.dp).pointerInput(upcoming.size) {
                        detectDragGesturesAfterLongPress(
                            onDragStart = { dragIndex = pos; dragOffset = 0f },
                            onDrag = { change, d -> change.consume(); dragOffset += d.y },
                            onDragEnd = {
                                val to = (pos + (dragOffset / rowPx).roundToInt()).coerceIn(0, upcoming.size - 1)
                                if (to != pos) app.player.move(index, upcoming[to].index)
                                dragIndex = -1; dragOffset = 0f
                            },
                            onDragCancel = { dragIndex = -1; dragOffset = 0f })
                    }, contentAlignment = Alignment.Center) { Icon(Ico.Drag, "Drag to reorder", tint = c.subtle, modifier = Modifier.size(20.dp)) }
                    Art(t.thumbnail, 44.dp)
                    Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                        Text(t.title, style = Type.body, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            DownloadBadge(rememberDownloadState(t.videoId), 12.dp, videoId = t.videoId)
                            Text(t.artistLine, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(start = 4.dp))
                        }
                    }
                    IconBtn(Ico.Close, "Remove from queue", size = 18.dp, box = 40.dp) { app.player.remove(index) }
                }
            }
        }
    }
}

// ============================== sleep timer ==============================

@Composable
private fun SleepButton() {
    val app = LocalApp.current
    val c = LocalTune.current
    var open by remember { mutableStateOf(false) }
    val sleep = SleepTimer.state.collectAsState().value
    Box {
        PaneButton(Ico.Sleep, sleep?.let { if (it < 0) "End of song" else "${((it - System.currentTimeMillis()) / 60_000 + 1).coerceAtLeast(1)} min" } ?: "Sleep", sleep != null) { open = true }
        androidx.compose.material3.DropdownMenu(open, { open = false }, containerColor = c.cardHover) {
            listOf(15, 30, 45, 60, 90).forEach { m ->
                androidx.compose.material3.DropdownMenuItem({ Text("$m minutes") }, { open = false; SleepTimer.start(app, m) })
            }
            androidx.compose.material3.DropdownMenuItem({ Text("End of this song") }, { open = false; SleepTimer.endOfSong(app) })
            if (sleep != null) androidx.compose.material3.DropdownMenuItem({ Text("Turn off", color = c.accentText) }, { open = false; SleepTimer.cancel() })
        }
    }
}

/** Pauses after N minutes (fading out over the last 20 s) or at the end of the current song. */
object SleepTimer {
    /** null = off, -1 = end of song, otherwise the wall-clock time it pauses. */
    val state = kotlinx.coroutines.flow.MutableStateFlow<Long?>(null)
    private var job: kotlinx.coroutines.Job? = null
    private val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + Dispatchers.Main)

    fun start(app: AppState, minutes: Int) {
        cancel()
        val at = System.currentTimeMillis() + minutes * 60_000L
        state.value = at
        job = scope.launch {
            while (true) {
                val left = at - System.currentTimeMillis()
                if (left <= 0) break
                if (left < 20_000) app.player.setVolume((left / 20_000f).coerceIn(0f, 1f))
                delay(if (left < 25_000) 250 else 15_000)
            }
            app.player.pause(); app.player.setVolume(1f); state.value = null
            app.graph.toast("Sleep timer: paused")
        }
    }

    fun endOfSong(app: AppState) {
        cancel()
        state.value = -1
        val id = app.player.ui.value.track?.videoId
        job = scope.launch {
            app.player.ui.first { it.track?.videoId != id }
            app.player.pause(); state.value = null
            app.graph.toast("Sleep timer: paused")
        }
    }

    fun cancel() { job?.cancel(); job = null; state.value = null }
}
