package app.tune.ui

import androidx.compose.foundation.gestures.detectTapGestures

import androidx.compose.foundation.gestures.detectHorizontalDragGestures

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
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.unit.Velocity
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

/**
 * Now Playing. The queue lives in a drawer under the controls (like YouTube Music): tap "Queue" or drag the bottom
 * row up and the drawer grows while the player slides up out of its way, ending as a small row at the top.
 */
@Composable
fun NowPlaying() {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    val t = ui.track
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var lyrics by remember { mutableStateOf(false) }
    var full by remember { mutableStateOf(false) }
    // 0 = queue drawer closed, 1 = open
    var sheet by remember { mutableFloatStateOf(0f) }
    var settleJob by remember { mutableStateOf<kotlinx.coroutines.Job?>(null) }
    fun settle(open: Boolean) {
        settleJob?.cancel()
        settleJob = scope.launch {
            androidx.compose.animation.core.animate(sheet, if (open) 1f else 0f,
                animationSpec = androidx.compose.animation.core.spring(dampingRatio = 0.9f, stiffness = 500f)) { v, _ -> sheet = v }
        }
    }
    fun dragBy(fraction: Float) { settleJob?.cancel(); sheet = (sheet + fraction).coerceIn(0f, 1f) }

    BackHandler {
        when {
            full -> full = false
            sheet > 0.01f -> settle(false)
            lyrics -> lyrics = false
            else -> app.nowPlayingOpen = false
        }
    }
    LaunchedEffect(app.queueOpen) { if (app.queueOpen) { settle(true); app.queueOpen = false } }
    if (full && t != null) { FullScreenPlayer(t) { full = false }; return }

    Box(Modifier.fillMaxSize().background(c.base)) {
        CoverGlow(t?.thumbnail)
        BoxWithConstraints(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
            val density = LocalDensity.current
            val handleH = 112.dp // Lyrics · Queue · Sleep, the arrow, and room above the screen edge
            val miniH = 68.dp
            val playerH = maxHeight - handleH
            val rangePx = with(density) { (maxHeight - miniH - handleH).toPx() }.coerceAtLeast(1f)
            val pushPx = rangePx * sheet
            var lastDy by remember { mutableFloatStateOf(0f) }
            fun release() = settle(if (kotlin.math.abs(lastDy) > 4f) lastDy < 0 else sheet > 0.5f)
            val sheetDrag = Modifier.pointerInput(rangePx) {
                detectVerticalDragGestures(onDragEnd = { release() }, onDragCancel = { release() }) { change, dy ->
                    change.consume(); lastDy = dy; dragBy(-dy / rangePx)
                }
            }

            // ---------- the player: slides up (and fades) as the drawer grows ----------
            Column(Modifier.fillMaxWidth().height(playerH)
                .graphicsLayer { translationY = -pushPx; alpha = (1f - sheet * 1.6f).coerceIn(0f, 1f) }
                // pull down to close; push up to open the queue
                .pointerInput(rangePx) {
                    var total = 0f
                    detectVerticalDragGestures(
                        onDragStart = { total = 0f },
                        onDragEnd = { if (sheet > 0f) release() },
                    ) { change, dy ->
                        total += dy; lastDy = dy
                        if (total < 0 || sheet > 0f) { change.consume(); dragBy(-dy / rangePx) }
                        else if (total > 120f && !lyrics) app.nowPlayingOpen = false
                    }
                }
                .padding(horizontal = 20.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconBtn(Ico.ChevronDown, "Close", tint = c.text, size = 30.dp) { app.nowPlayingOpen = false }
                    Text("NOW PLAYING", style = Type.label, color = c.subtle, modifier = Modifier.weight(1f), textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                    if (t != null) IconBtn(Ico.Fullscreen, "Full screen", tint = c.text) { full = true }
                    IconBtn(Ico.More, "More options", tint = c.text) { t?.let { app.menuFor = it; app.menuPlaylistId = null } }
                }
                if (t == null) { Message("Play a song to see it here."); return@Column }

                BoxWithConstraints(Modifier.weight(1f).fillMaxWidth().padding(vertical = 16.dp), contentAlignment = Alignment.Center) {
                    // as big as the space allows; swipe for next / previous, tap to play or pause
                    if (lyrics) LyricsPane(t) else SwipeArt(t, minOf(maxWidth, maxHeight))
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
                PlayControls(big = true)
            }

            // ---------- once the drawer is up: the song as a small row at the top ----------
            if (t != null && sheet > 0.4f) {
                Row(Modifier.fillMaxWidth().height(miniH).graphicsLayer { alpha = ((sheet - 0.55f) / 0.45f).coerceIn(0f, 1f) }
                    .clickable { settle(false) }.padding(horizontal = 20.dp), verticalAlignment = Alignment.CenterVertically) {
                    Art(t.thumbnail, 46.dp, corner = 8.dp)
                    Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                        Text(t.title, style = Type.bodyStrong, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(t.artistLine, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    IconBtn(if (ui.isPlaying) Ico.Pause else Ico.Play, "Play/Pause", tint = c.text, size = 30.dp) { app.player.toggle() }
                }
            }

            // ---------- the drawer: Lyrics · Queue · Sleep row on top, the queue under it ----------
            val sheetH = handleH + with(density) { pushPx.toDp() }
            val nested = remember(rangePx) {
                object : NestedScrollConnection {
                    override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                        // finger moving up while the drawer isn't fully open: open it first
                        if (available.y < 0 && sheet < 1f) { lastDy = available.y; dragBy(-available.y / rangePx); return Offset(0f, available.y) }
                        return Offset.Zero
                    }
                    override fun onPostScroll(consumed: Offset, available: Offset, source: NestedScrollSource): Offset {
                        // the list is at its top and the finger keeps pulling down: lower the drawer
                        if (available.y > 0 && source == NestedScrollSource.UserInput) {
                            lastDy = available.y; dragBy(-available.y / rangePx); return Offset(0f, available.y)
                        }
                        return Offset.Zero
                    }
                    override suspend fun onPreFling(available: Velocity): Velocity {
                        if (sheet > 0.001f && sheet < 0.999f) {
                            settle(if (kotlin.math.abs(available.y) > 300f) available.y < 0 else sheet > 0.5f)
                            return available
                        }
                        return Velocity.Zero
                    }
                }
            }
            Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(sheetH)
                .clip(RoundedCornerShape(topStart = 24.dp * sheet, topEnd = 24.dp * sheet))
                .background(Color(0xFF17171A).copy(alpha = 0.97f * (sheet * 3f).coerceAtMost(1f)))
                .nestedScroll(nested)) {
                Column(Modifier.fillMaxWidth().height(handleH).then(sheetDrag), horizontalAlignment = Alignment.CenterHorizontally) {
                    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                        PaneButton(Ico.Lyrics, "Lyrics", lyrics) { lyrics = !lyrics; if (sheet > 0f) settle(false) }
                        PaneButton(Ico.Queue, "Queue", sheet > 0.5f) { settle(sheet < 0.5f) }
                        SleepButton()
                    }
                    // a small arrow under Queue: the drawer slides up from here (flips once it's open)
                    Icon(Ico.ChevronDown, null, tint = c.subtle, modifier = Modifier.size(22.dp).graphicsLayer { rotationZ = 180f * (1f - sheet) })
                }
                if (sheet > 0.01f) QueueList(Modifier.weight(1f).graphicsLayer { alpha = (sheet * 2f).coerceAtMost(1f) })
            }
        }
    }
}

/** Shuffle · previous · play/pause · next · repeat. */
@Composable
private fun PlayControls(big: Boolean) {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    Row(Modifier.fillMaxWidth().padding(top = 4.dp, bottom = 8.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        IconBtn(Ico.Shuffle, "Shuffle", tint = if (ui.shuffle) c.accentText else c.subtle) { app.player.toggleShuffle() }
        IconBtn(Ico.Prev, "Previous", tint = c.text, size = 34.dp, box = 56.dp) { app.player.previous() }
        Box(Modifier.size(if (big) 72.dp else 64.dp).clip(CircleShape).background(c.accent)
            .clickable { app.player.toggle() }, contentAlignment = Alignment.Center) {
            Icon(if (ui.isPlaying) Ico.Pause else Ico.Play, "Play/Pause", tint = c.onAccent, modifier = Modifier.size(if (big) 38.dp else 34.dp))
        }
        IconBtn(Ico.Next, "Next", tint = c.text, size = 34.dp, box = 56.dp) { app.player.next() }
        IconBtn(if (ui.repeat == Player.REPEAT_MODE_ONE) Ico.RepeatOne else Ico.Repeat, "Repeat",
            tint = if (ui.repeat != Player.REPEAT_MODE_OFF) c.accentText else c.subtle) { app.player.cycleRepeat() }
    }
}

// ============================== full screen ==============================

/**
 * Full-screen player: system bars hidden, screen kept on, the cover with big synced lyrics. The controls slide down
 * out of sight after a few seconds of playing (a thin progress line stays along the bottom); a tap brings them back.
 */
@Composable
private fun FullScreenPlayer(t: Track, onExit: () -> Unit) {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    val view = androidx.compose.ui.platform.LocalView.current
    androidx.compose.runtime.DisposableEffect(Unit) {
        val window = (view.context as? android.app.Activity)?.window
        val bars = window?.let { androidx.core.view.WindowCompat.getInsetsController(it, view) }
        bars?.systemBarsBehavior = androidx.core.view.WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        bars?.hide(androidx.core.view.WindowInsetsCompat.Type.systemBars())
        view.keepScreenOn = true
        onDispose { bars?.show(androidx.core.view.WindowInsetsCompat.Type.systemBars()); view.keepScreenOn = false }
    }
    var shown by remember { mutableStateOf(true) }
    var poke by remember { mutableIntStateOf(0) }
    LaunchedEffect(shown, poke, ui.isPlaying) { if (shown && ui.isPlaying) { delay(3500); shown = false } }

    Box(Modifier.fillMaxSize().background(Color.Black)
        .pointerInput(Unit) { detectTapGestures(onTap = { shown = !shown; poke++ }) }) {
        CoverGlow(t.thumbnail)
        // the lyrics make room while the controls are up, and use the whole height when they're away
        val reserve by androidx.compose.animation.core.animateDpAsState(if (shown) 190.dp else 8.dp, label = "reserve")
        BoxWithConstraints(Modifier.fillMaxSize().padding(horizontal = 28.dp, vertical = 24.dp)) {
            val wide = maxWidth > maxHeight
            val artWide = minOf(maxHeight * 0.62f, maxWidth * 0.36f)
            val artTall = minOf(maxWidth * 0.6f, 260.dp)
            if (wide) Row(Modifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(0.42f)) { FullArt(t, artWide) }
                Box(Modifier.weight(0.58f).fillMaxHeight().padding(start = 32.dp, bottom = reserve)) { LyricsPane(t, big = true) }
            } else Column(Modifier.fillMaxSize()) {
                Spacer(Modifier.height(24.dp))
                FullArt(t, artTall)
                Box(Modifier.weight(1f).fillMaxWidth().padding(top = 20.dp, bottom = reserve)) { LyricsPane(t, big = true) }
            }
        }
        // the controls sink below the bottom edge when idle; a progress line takes their place
        val pos = rememberPosition()
        val fraction = if (ui.durationMs > 0) (pos.toFloat() / ui.durationMs).coerceIn(0f, 1f) else 0f
        val lineAlpha by animateFloatAsState(if (shown) 0f else 1f, label = "line")
        Box(Modifier.align(Alignment.BottomStart).fillMaxWidth().height(3.dp).graphicsLayer { alpha = lineAlpha }.background(Color.White.copy(alpha = 0.15f))) {
            Box(Modifier.fillMaxHeight().fillMaxWidth(fraction).background(c.accent))
        }
        androidx.compose.animation.AnimatedVisibility(shown, Modifier.align(Alignment.BottomCenter),
            enter = androidx.compose.animation.slideInVertically { it } + androidx.compose.animation.fadeIn(),
            exit = androidx.compose.animation.slideOutVertically { it } + androidx.compose.animation.fadeOut()) {
            Column(Modifier.fillMaxWidth().background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.75f))))
                .pointerInput(Unit) { detectTapGestures { poke++ } }
                .padding(horizontal = 24.dp).padding(top = 24.dp, bottom = 16.dp)) {
                SeekBar(ui.durationMs)
                PlayControls(big = false)
            }
        }
        androidx.compose.animation.AnimatedVisibility(shown, Modifier.align(Alignment.TopEnd),
            enter = androidx.compose.animation.slideInVertically { -it } + androidx.compose.animation.fadeIn(),
            exit = androidx.compose.animation.slideOutVertically { -it } + androidx.compose.animation.fadeOut()) {
            Box(Modifier.padding(8.dp)) {
                IconBtn(Ico.FullscreenExit, "Exit full screen", tint = c.text, size = 28.dp, box = 56.dp) { onExit() }
            }
        }
    }
}

@Composable
private fun FullArt(t: Track, size: androidx.compose.ui.unit.Dp) {
    val c = LocalTune.current
    Art(t.thumbnail, size, corner = 16.dp, modifier = Modifier.shadow(24.dp, RoundedCornerShape(16.dp)))
    Text(t.title, style = Type.title2, color = c.text, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 16.dp))
    Text(t.artistLine, style = Type.body, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
}

/**
 * The song's cover, small and heavily blurred, filling the screen behind Now Playing (like the Windows full screen),
 * darkened towards the bottom so the controls stay readable. Crossfades when the song changes.
 */
@Composable
private fun CoverGlow(url: String?) {
    androidx.compose.animation.Crossfade(url, animationSpec = androidx.compose.animation.core.tween(600), label = "glow") { u ->
        Box(Modifier.fillMaxSize()) {
            if (u != null)
                coil3.compose.AsyncImage(model = app.tune.data.Thumbs.sized(u, 120), contentDescription = null,
                    contentScale = androidx.compose.ui.layout.ContentScale.Crop,
                    modifier = Modifier.fillMaxSize().graphicsLayer { scaleX = 1.3f; scaleY = 1.3f }
                        .blur(70.dp, androidx.compose.ui.draw.BlurredEdgeTreatment.Unbounded))
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(
                0f to Color.Black.copy(alpha = 0.30f), 0.45f to Color.Black.copy(alpha = 0.45f), 1f to Color.Black.copy(alpha = 0.88f))))
        }
    }
}

/**
 * The cover on Now Playing: follows a sideways swipe — far enough left plays the next song, right the previous —
 * and a tap plays or pauses.
 */
@Composable
private fun SwipeArt(t: Track, size: androidx.compose.ui.unit.Dp) {
    val app = LocalApp.current
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    val offset = remember { androidx.compose.animation.core.Animatable(0f) }
    val density = androidx.compose.ui.platform.LocalDensity.current
    val threshold = with(density) { 90.dp.toPx() }
    LaunchedEffect(t.videoId) { offset.snapTo(0f) } // a new song: back in the middle
    Box(Modifier.size(size)
        .graphicsLayer {
            translationX = offset.value
            alpha = 1f - (kotlin.math.abs(offset.value) / (size.toPx() * 1.2f)).coerceIn(0f, 0.6f)
            rotationZ = offset.value / size.toPx() * 4f
        }
        .pointerInput(t.videoId) {
            detectHorizontalDragGestures(
                onDragEnd = {
                    val dx = offset.value
                    scope.launch {
                        when {
                            dx < -threshold -> { offset.animateTo(-size.toPx(), androidx.compose.animation.core.tween(160)); app.player.next() }
                            dx > threshold -> { offset.animateTo(size.toPx(), androidx.compose.animation.core.tween(160)); app.player.previous(); offset.snapTo(0f) }
                            else -> offset.animateTo(0f, androidx.compose.animation.core.spring(dampingRatio = 0.7f))
                        }
                    }
                },
                onDragCancel = { scope.launch { offset.animateTo(0f) } },
            ) { change, dx -> change.consume(); scope.launch { offset.snapTo(offset.value + dx) } }
        }
        .pointerInput(Unit) { detectTapGestures(onTap = { app.player.toggle() }) }) {
        Art(t.thumbnail, size, corner = 18.dp, modifier = Modifier.shadow(24.dp, RoundedCornerShape(18.dp)))
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
private fun LyricsPane(t: Track, big: Boolean = false) {
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
            Text(line.text.ifBlank { "♪" }, style = Type.title2.copy(fontSize = if (big) 30.sp else 24.sp, lineHeight = if (big) 40.sp else 32.sp),
                color = c.text.copy(alpha = alpha), fontWeight = FontWeight.Bold,
                modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).clickable { app.player.seek(line.atMs) }.padding(vertical = 8.dp))
        }
        item { Text(song.source ?: "", style = Type.caption, color = c.subtle, modifier = Modifier.padding(vertical = 16.dp)) }
    }
}

// ============================== queue ==============================

/** The queue inside Now Playing's drawer: the song playing now, then what's up next (hold ≡ and drag to reorder). */
@Composable
private fun QueueList(modifier: Modifier = Modifier) {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    val current = ui.queue.getOrNull(ui.index)
    val upcoming = ui.queue.withIndex().drop(ui.index + 1).toList()
    val rowPx = with(LocalDensity.current) { 64.dp.toPx() }
    var dragIndex by remember { mutableIntStateOf(-1) }
    var dragOffset by remember { mutableFloatStateOf(0f) }
    LazyColumn(modifier.fillMaxWidth(), contentPadding = androidx.compose.foundation.layout.PaddingValues(start = 12.dp, end = 12.dp, bottom = 24.dp)) {
        item {
            Row(Modifier.padding(start = 8.dp, top = 4.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Up next", style = Type.title2, color = c.text)
                    val total = upcoming.sumOf { it.value.durationSec ?: 0 }
                    Text(if (upcoming.isEmpty()) "Songs like this one will be added automatically" else "${upcoming.size} songs · ${total / 60} min",
                        style = Type.caption, color = c.subtle)
                }
                if (upcoming.isNotEmpty()) IconBtn(Ico.Clear, "Clear the queue") { app.player.clearUpcoming(); app.graph.toast("Queue cleared") }
            }
        }
        if (current != null) item(key = "now") {
            Row(Modifier.fillMaxWidth().height(64.dp).clip(RoundedCornerShape(10.dp)).background(c.accentSoft).padding(horizontal = 8.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Art(current.thumbnail, 44.dp)
                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                    Text(current.title, style = Type.bodyStrong, color = c.accentText, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(current.artistLine, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Text("Playing", style = Type.label, color = c.accentText, modifier = Modifier.padding(end = 8.dp))
            }
        }
        itemsIndexed(upcoming, key = { _, it -> "${it.index}${it.value.videoId}" }) { pos, (index, t) ->
            val isDragged = dragIndex == pos
            Row(Modifier.fillMaxWidth().height(64.dp)
                .graphicsLayer { translationY = if (isDragged) dragOffset else 0f; shadowElevation = if (isDragged) 16f else 0f }
                .clip(RoundedCornerShape(10.dp)).background(if (isDragged) c.cardHover else Color.Transparent)
                .clickable { app.player.skipTo(index) }.padding(start = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Art(t.thumbnail, 44.dp)
                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                    Text(t.title, style = Type.body, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        DownloadBadge(rememberDownloadState(t.videoId), 12.dp, videoId = t.videoId)
                        Text(t.artistLine, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(start = 4.dp))
                    }
                }
                IconBtn(Ico.Close, "Remove from queue", size = 18.dp, box = 40.dp) { app.player.remove(index) }
                Box(Modifier.size(44.dp).pointerInput(upcoming.size) {
                    detectDragGesturesAfterLongPress(
                        onDragStart = { dragIndex = pos; dragOffset = 0f },
                        onDrag = { change, d -> change.consume(); dragOffset += d.y },
                        onDragEnd = {
                            val to = (pos + (dragOffset / rowPx).roundToInt()).coerceIn(0, upcoming.size - 1)
                            if (to != pos) app.player.move(index, upcoming[to].index)
                            dragIndex = -1; dragOffset = 0f
                        },
                        onDragCancel = { dragIndex = -1; dragOffset = 0f })
                }, contentAlignment = Alignment.Center) { Icon(Ico.Drag, "Hold and drag to reorder", tint = c.subtle, modifier = Modifier.size(22.dp)) }
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
