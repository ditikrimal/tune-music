package app.tune.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.tune.data.Card
import app.tune.data.SpeedDialPins
import app.tune.data.PlaylistLinks
import app.tune.data.CardKind
import app.tune.BuildConfigLite
import app.tune.SmartRefreshWorker
import app.tune.data.Collection
import app.tune.data.ArtistPage
import app.tune.data.DownloadState
import app.tune.data.Feed
import app.tune.data.SearchResults
import app.tune.data.Track
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Extra space at the bottom so lists clear the mini player and nav bar. */
val BottomClearance = PaddingValues(bottom = 210.dp)

@Composable
fun Loading() = Box(Modifier.fillMaxWidth().padding(48.dp), contentAlignment = Alignment.Center) {
    TuneLoader(size = 40.dp)
}

@Composable
fun Message(text: String) = Text(text, style = Type.body, color = LocalTune.current.subtle, modifier = Modifier.padding(24.dp))

// ============================== Home ==============================

@Composable
fun HomeScreen() {
    val app = LocalApp.current
    val c = LocalTune.current
    val online by app.graph.online.collectAsState()
    val vm: Loader<Feed> = viewModel(key = "home")
    val state by vm.state.collectAsState()
    val refreshing by vm.refreshing.collectAsState()
    LaunchedEffect(online) { if (online) vm.load(force = state?.getOrNull()?.offline == true) { app.graph.feed.build() } }
    // pins or the Home layout changed (here, in Settings, or synced from another device): rebuild
    val homeChanged = app.homeChanged
    val homeSynced by app.graph.library.homeSynced.collectAsState()
    LaunchedEffect(homeChanged, homeSynced) { if ((homeChanged > 0 || homeSynced > 0) && online) vm.load(force = true) { app.graph.feed.build() } }

    // pull down to rebuild Home from what you've been playing (and fresh YouTube Music rows)
    @OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
    androidx.compose.material3.pulltorefresh.PullToRefreshBox(
        isRefreshing = refreshing,
        onRefresh = { if (online) vm.refresh { app.graph.feed.build(fresh = true).also { app.graph.toast("Home updated") } } },
        modifier = Modifier.fillMaxSize(),
    ) {
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item {
            Row(Modifier.statusBarsPadding().padding(start = 16.dp, end = 8.dp, top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                val account = app.graph.account?.state?.collectAsState()?.value
                val first = account?.takeIf { it.signedIn }?.let { (it.name ?: it.email?.substringBefore('@'))?.trim()?.substringBefore(' ') }
                // "Good evening, Ditik" — shrinks to fit a long name, never wraps
                androidx.compose.foundation.text.BasicText(if (first.isNullOrBlank()) greeting() else "${greeting()}, $first",
                    style = Type.title1.copy(color = c.text), maxLines = 1, overflow = TextOverflow.Ellipsis,
                    autoSize = androidx.compose.foundation.text.TextAutoSize.StepBased(minFontSize = 20.sp, maxFontSize = 30.sp),
                    modifier = Modifier.weight(1f))
                if (!online) OfflinePill()
                ProfileButton()
            }
        }
        item { UpdateBanner() }
        if (!online || state?.getOrNull()?.offline == true) {
            item { OfflineHome() }
            return@LazyColumn
        }
        val feed = state?.getOrNull()
        if (feed == null) { item { Loading() }; return@LazyColumn }
        feed.shelves.take(9).forEachIndexed { i, shelf ->
            item(key = "shelf$i") {
                when {
                    shelf.isSpeedDial -> SpeedDialRow(shelf)
                    shelf.isList -> QuickPicksRow(shelf.copy(items = shelf.items.take(20)))
                    else -> ShelfRow(shelf.copy(items = shelf.items.take(12)), featured = i == 0 && shelf.isLocal)
                }
            }
        }
    }
    }
}

@Composable
fun OfflinePill() {
    val c = LocalTune.current
    Row(Modifier.clip(RoundedCornerShape(14.dp)).background(c.card).padding(horizontal = 12.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(Ico.Offline, null, tint = c.subtle, modifier = Modifier.size(14.dp))
        Text("Offline", style = Type.caption, color = c.text, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(start = 6.dp))
    }
}

/** Offline, Home becomes your downloads. */
@Composable
fun OfflineHome() {
    val app = LocalApp.current
    val c = LocalTune.current
    val v by app.graph.downloads.version.collectAsState()
    val tracks = remember(v) { app.graph.offlineTracks() }
    val ui by app.player.ui.collectAsState()
    Column(Modifier.padding(16.dp)) {
        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(c.card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(44.dp).clip(CircleShape).background(c.track), contentAlignment = Alignment.Center) { Icon(Ico.Offline, null, tint = c.text) }
            Column(Modifier.padding(start = 14.dp)) {
                Text("You're offline", style = Type.bodyStrong, color = c.text)
                Text(if (tracks.isEmpty()) "Nothing is saved yet. On Wi-Fi, Smart downloads saves the songs you play most; you can also download playlists."
                     else "${tracks.size} songs ready without internet: your downloads, smart downloads and songs you played recently.",
                     style = Type.caption, color = c.subtle)
            }
        }
        if (tracks.isNotEmpty()) {
            PlayButtons(onPlay = { app.playList(tracks, 0) }, onShuffle = { app.playList(tracks, 0, shuffle = true) }, title = "Ready offline")
            tracks.forEachIndexed { i, t -> TrackRow(t, isCurrent = ui.track?.videoId == t.videoId) { app.playList(tracks, i) } }
        }
    }
}

@Composable
fun PlayButtons(onPlay: () -> Unit, onShuffle: () -> Unit, title: String? = null, extra: @Composable () -> Unit = {}) {
    val c = LocalTune.current
    Row(Modifier.padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(56.dp).clip(CircleShape).background(c.accent).clickable(onClick = onPlay), contentAlignment = Alignment.Center) {
            Icon(Ico.Play, "Play", tint = c.onAccent, modifier = Modifier.size(30.dp))
        }
        IconBtn(Ico.Shuffle, "Shuffle play", size = 26.dp, box = 52.dp, modifier = Modifier.padding(start = 8.dp), onClick = onShuffle)
        extra()
        title?.let { Text(it, style = Type.section, color = c.text, modifier = Modifier.padding(start = 12.dp)) }
    }
}

// ============================== Search ==============================

@Composable
fun SearchScreen() {
    val app = LocalApp.current
    val c = LocalTune.current
    var query by rememberSaveable { mutableStateOf("") }
    var results by remember { mutableStateOf<SearchResults?>(null) }
    var loading by remember { mutableStateOf(false) }
    var job by remember { mutableStateOf<Job?>(null) }
    val scope = rememberCoroutineScope()
    val ui by app.player.ui.collectAsState()
    fun search(now: Boolean) {
        job?.cancel()
        val q = query.trim()
        if (q.length < 2) { results = null; return }
        job = scope.launch {
            if (!now) delay(350)
            loading = true
            results = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { runCatching { app.graph.yt.search(q) }.getOrNull() }
            loading = false
        }
    }
    LaunchedEffect(Unit) { if (query.isNotBlank() && results == null) search(true) }

    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item {
            Column(Modifier.statusBarsPadding().padding(16.dp)) {
                Text("Search", style = Type.title1, color = c.text)
                Spacer(Modifier.height(14.dp))
                OutlinedTextField(query, { query = it; search(false) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    placeholder = { Text("What do you want to play?", color = c.subtle) },
                    leadingIcon = { Icon(Ico.Search, null, tint = c.subtle) },
                    trailingIcon = { if (query.isNotEmpty()) IconBtn(Ico.Close, "Clear") { query = ""; results = null } },
                    shape = RoundedCornerShape(26.dp),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    keyboardActions = KeyboardActions(onSearch = { search(true) }),
                    colors = OutlinedTextFieldDefaults.colors(focusedBorderColor = c.accent, unfocusedBorderColor = Color.Transparent,
                        unfocusedContainerColor = c.card, focusedContainerColor = c.card, cursorColor = c.accent))
            }
        }
        if (loading && results == null) item { Loading() }
        val r = results ?: return@LazyColumn
        r.top?.takeIf { it.track == null }?.let { top -> item { TopResult(top) } }
        if (r.songs.isNotEmpty()) {
            item { SectionTitle("Songs") }
            itemsIndexed(r.songs.take(20), key = { _, t -> "s" + t.videoId }) { _, t ->
                Box(Modifier.padding(horizontal = 8.dp)) { TrackRow(t, isCurrent = ui.track?.videoId == t.videoId) { app.player.playRadio(t) } }
            }
        }
        for ((title, cards) in listOf("Artists" to r.artists, "Albums" to r.albums, "Playlists" to r.playlists)) {
            if (cards.isEmpty()) continue
            item(key = title) {
                SectionTitle(title)
                LazyRow(contentPadding = PaddingValues(horizontal = 10.dp)) { items(cards) { CardItem(it) } }
            }
        }
    }
}

@Composable
private fun TopResult(card: Card) {
    val app = LocalApp.current
    val c = LocalTune.current
    Row(Modifier.padding(horizontal = 16.dp).fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(c.card).clickable { app.openCard(card) }.padding(14.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Art(card.thumbnail, 72.dp, corner = 10.dp, circle = card.kind == CardKind.Artist)
        Column(Modifier.padding(start = 14.dp)) {
            Text("Top result", style = Type.label, color = c.subtle)
            Text(card.title, style = Type.title2, color = c.text, maxLines = 2)
            card.subtitle?.let { Text(it, style = Type.caption, color = c.subtle, maxLines = 1) }
        }
    }
}

// ============================== Library ==============================

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun LibraryScreen() {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val dv by app.graph.downloads.version.collectAsState()
    val playlists = remember(version) { app.graph.library.playlists() }
    val likedCount = remember(version) { app.graph.library.liked().size }
    val history = remember(version) { app.graph.library.history(20).map { it.first } }
    val ui by app.player.ui.collectAsState()
    val downloaded = remember(dv) { app.graph.downloads.downloaded().size }
    var naming by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    if (naming) NameDialog("New playlist", "My playlist #${playlists.size + 1}",
        onLink = { link ->
            naming = false
            app.graph.toast("Importing\u2026")
            scope.launch {
                val (message, id) = app.importPlaylist(link)
                if (id != null) app.nav.navigate("local/$id") else app.graph.toast(message)
            }
        }) { name ->
        naming = false
        if (name != null) app.nav.navigate("local/${app.graph.library.createPlaylist(name)}")
    }
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item {
            Row(Modifier.statusBarsPadding().padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Text("Your library", style = Type.title1, color = c.text, modifier = Modifier.weight(1f))
                IconBtn(Ico.Add, "New playlist", tint = c.text, size = 26.dp) { naming = true }
                ProfileButton()
            }
        }
        // like YouTube's "You" tab: your recent history first, as a row you can swipe
        if (history.isNotEmpty()) {
            item {
                Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("History", style = Type.section, color = c.text, modifier = Modifier.weight(1f))
                    Text("See all", style = Type.bodyStrong.copy(fontSize = 13.sp), color = c.text,
                        modifier = Modifier.clip(RoundedCornerShape(16.dp)).border(1.dp, c.track, RoundedCornerShape(16.dp))
                            .clickable { app.nav.navigate("history") }.padding(horizontal = 14.dp, vertical = 6.dp))
                }
            }
            item {
                LazyRow(contentPadding = PaddingValues(horizontal = 10.dp)) {
                    itemsIndexed(history, key = { _, t -> "h" + t.videoId }) { i, t ->
                        val current = ui.track?.videoId == t.videoId
                        Column(Modifier.width(132.dp).clip(RoundedCornerShape(12.dp)).combinedClickable(
                            onClick = { app.playList(history, i) }, onLongClick = { app.menuFor = t; app.menuPlaylistId = null }).padding(6.dp)) {
                            Art(t.thumbnail, 120.dp, corner = 10.dp)
                            Text(t.title, style = Type.bodyStrong.copy(fontSize = 14.sp), color = if (current) c.accentText else c.text,
                                maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 6.dp))
                            Text(t.artistLine, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
            }
        }
        // the built-in collections as calm rows
        item { Spacer(Modifier.height(10.dp)) }
        item { LibraryRow(Ico.Heart, "Liked songs", "$likedCount songs",
            onLongClick = { app.collectionMenuFor = CollectionTarget(CollectionKind.Liked, "liked", "Liked songs", "$likedCount songs", null) }) { app.nav.navigate("liked") } }
        item {
            val pending = remember(dv) { app.graph.downloads.pendingCount }
            LibraryRow(Ico.Download, "Downloads", if (pending > 0) "$downloaded songs \u2022 downloading $pending\u2026" else "$downloaded songs \u2022 play without internet",
                onLongClick = { app.collectionMenuFor = CollectionTarget(CollectionKind.Downloads, "downloads", "Downloads", "$downloaded songs", null) }) { app.nav.navigate("downloads") }
        }
        item { LibraryRow(Ico.History, "History", "Everything you played, by day",
            onLongClick = { app.collectionMenuFor = CollectionTarget(CollectionKind.History, "history", "History", "Songs you played", null) }) { app.nav.navigate("history") } }
        item {
            Row(Modifier.padding(start = 16.dp, end = 16.dp, top = 22.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                Text("Playlists", style = Type.section, color = c.text, modifier = Modifier.weight(1f))
                if (playlists.isNotEmpty()) Text("${playlists.size}", style = Type.caption, color = c.subtle)
            }
        }
        if (playlists.isEmpty()) item {
            Column(Modifier.padding(horizontal = 16.dp).fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(c.card).clickable { naming = true }.padding(18.dp)) {
                Text("No playlists yet", style = Type.bodyStrong, color = c.text)
                Text("Tap + to make one, or use the link icon in that box to import one from YouTube Music.", style = Type.caption, color = c.subtle)
            }
        }
        items(playlists, key = { it.id }) { p ->
            @OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
            Row(Modifier.fillMaxWidth().combinedClickable(
                onClick = { app.nav.navigate("local/${p.id}") },
                onLongClick = { app.collectionMenuFor = CollectionTarget(CollectionKind.Local, p.id.toString(), p.name, "Playlist • ${p.count} songs", p.thumbnail) })
                .padding(horizontal = 16.dp, vertical = 7.dp), verticalAlignment = Alignment.CenterVertically) {
                Art(p.thumbnail, 58.dp, corner = 12.dp)
                Column(Modifier.weight(1f).padding(start = 14.dp)) {
                    Text(p.name, style = Type.bodyStrong, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text("Playlist \u2022 ${p.count} songs", style = Type.caption, color = c.subtle)
                }
                Icon(Ico.ChevronRight, null, tint = c.subtle, modifier = Modifier.size(22.dp))
            }
        }
    }
}

/** A quiet library row: icon in a soft tile, title, subtitle, chevron (same rhythm as the playlist rows). */
@Composable
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
private fun LibraryRow(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, subtitle: String, onLongClick: (() -> Unit)? = null, onClick: () -> Unit) {
    val c = LocalTune.current
    Row(Modifier.fillMaxWidth().combinedClickable(onClick = onClick, onLongClick = onLongClick).padding(horizontal = 16.dp, vertical = 7.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(58.dp).clip(RoundedCornerShape(12.dp)).background(c.card), contentAlignment = Alignment.Center) {
            Icon(icon, null, tint = c.accentText, modifier = Modifier.size(26.dp))
        }
        Column(Modifier.weight(1f).padding(start = 14.dp)) {
            Text(title, style = Type.bodyStrong, color = c.text)
            Text(subtitle, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Icon(Ico.ChevronRight, null, tint = c.subtle, modifier = Modifier.size(22.dp))
    }
}

/** History: songs you played, newest first, grouped Today / Yesterday / This week / Earlier. */
@Composable
fun HistoryScreen() {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val ui by app.player.ui.collectAsState()
    val items = remember(version) { app.graph.library.history(300) }
    val tracks = remember(items) { items.map { it.first } }
    val selection = remember { SongSelection() }
    LaunchedEffect(app.startSelecting) { if (app.startSelecting == "History/history") { selection.active = true; app.startSelecting = null } }
    androidx.activity.compose.BackHandler(enabled = selection.active) { selection.close() }
    val zone = java.time.ZoneId.systemDefault()
    val today = java.time.LocalDate.now(zone)
    fun group(at: Long): String {
        val d = java.time.Instant.ofEpochMilli(at).atZone(zone).toLocalDate()
        return when {
            d == today -> "Today"
            d == today.minusDays(1) -> "Yesterday"
            d.isAfter(today.minusDays(7)) -> "This week"
            else -> "Earlier"
        }
    }
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item { BackRow() }
        item {
            Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(64.dp).clip(RoundedCornerShape(16.dp)).background(c.card),
                    contentAlignment = Alignment.Center) { Icon(Ico.History, null, tint = c.accentText, modifier = Modifier.size(30.dp)) }
                Column(Modifier.padding(start = 16.dp)) {
                    Text("History", style = Type.title1.copy(fontSize = 26.sp), color = c.text)
                    Text("${items.size} songs you played", style = Type.caption, color = c.subtle)
                }
            }
        }
        if (items.isNotEmpty()) item {
            Box(Modifier.padding(horizontal = 16.dp)) {
                PlayButtons(onPlay = { app.playList(tracks, 0) }, onShuffle = { app.playList(tracks, 0, shuffle = true) }) {
                    IconBtn(Ico.SelectAll, "Select songs", size = 24.dp, box = 52.dp) { selection.active = true }
                }
            }
        } else item { Message("Songs you play show up here.") }
        if (selection.active) {
            @OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
            stickyHeader(key = "selection") {
                SelectionBar(selection, tracks.size, selectionActions(app, { selection.tracks(tracks) }, { selection.close() }))
            }
        }
        var last: String? = null
        items.forEachIndexed { i, (t, at) ->
            val g = group(at)
            if (g != last) {
                last = g
                item(key = "g$g") { Text(g, style = Type.label, color = c.subtle, modifier = Modifier.padding(start = 20.dp, top = 18.dp, bottom = 6.dp)) }
            }
            item(key = "h$i${t.videoId}") {
                Box(Modifier.padding(horizontal = 8.dp)) {
                    TrackRow(t, isCurrent = ui.track?.videoId == t.videoId, selecting = selection.active, selected = i in selection.chosen,
                        onToggle = { selection.toggle(i) }) { app.playList(tracks, i) }
                }
            }
        }
    }
}

@Composable
private fun LibraryEntry(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, subtitle: String, accentTile: Boolean, onClick: () -> Unit) {
    val c = LocalTune.current
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(60.dp).clip(RoundedCornerShape(8.dp)).background(if (accentTile) Brush.linearGradient(listOf(c.accent, c.accent.copy(alpha = 0.45f))) else Brush.linearGradient(listOf(c.card, c.card))),
            contentAlignment = Alignment.Center) { Icon(icon, null, tint = c.onAccent, modifier = Modifier.size(28.dp)) }
        Column(Modifier.padding(start = 14.dp)) {
            Text(title, style = Type.bodyStrong, color = c.text)
            Text(subtitle, style = Type.caption, color = c.subtle)
        }
    }
}

// ============================== Album / playlist / liked / downloads ==============================

enum class CollectionKind { Album, YtPlaylist, Liked, Local, Downloads, History }

@Composable
fun CollectionScreen(kind: CollectionKind, id: String) {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val dv by app.graph.downloads.version.collectAsState()
    val ui by app.player.ui.collectAsState()
    val remote = kind == CollectionKind.Album || kind == CollectionKind.YtPlaylist
    val vm: Loader<Collection> = viewModel(key = "$kind/$id")
    val remoteState by vm.state.collectAsState()
    if (remote) LaunchedEffect(id) { vm.load { if (kind == CollectionKind.Album) app.graph.yt.album(id) else app.graph.yt.playlist(id) } }
    val local: Collection? = if (remote) null else remember(version, dv) {
        when (kind) {
            CollectionKind.Liked -> app.graph.library.liked().let { Collection("Liked songs", "${it.size} songs", null, it) }
            CollectionKind.Downloads -> app.graph.downloads.downloaded().let {
                val smart = app.graph.downloads.smartIds().count { id -> app.graph.downloads.localFile(id) != null }
                Collection("Downloads", "${it.size} songs · ${app.graph.downloads.downloadedBytes() / (1 shl 20)} MB" +
                    (if (smart > 0) " · $smart picked by Smart downloads" else ""), null, it)
            }
            else -> {
                val p = app.graph.library.playlists().firstOrNull { it.id.toString() == id }
                val t = app.graph.library.playlistTracks(id.toLong())
                Collection(p?.name ?: "Playlist", "${t.size} songs", p?.thumbnail, t)
            }
        }
    }
    val col = local ?: remoteState?.getOrNull()
    var menu by remember { mutableStateOf(false) }
    val selection = remember(kind, id) { SongSelection() }
    LaunchedEffect(app.startSelecting) { if (app.startSelecting == "$kind/$id") { selection.active = true; app.startSelecting = null } }
    androidx.activity.compose.BackHandler(enabled = selection.active) { selection.close() }
    var renaming by remember { mutableStateOf(false) }

    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item { BackRow() }
        if (col == null) {
            item { if (remoteState?.isFailure == true) Message("Couldn't load this — check your connection.") else Loading() }
            return@LazyColumn
        }
        item {
            Column(Modifier.padding(horizontal = 16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                val thumb = col.thumbnail ?: col.tracks.firstOrNull()?.thumbnail
                if (kind == CollectionKind.Liked || kind == CollectionKind.Downloads)
                    Box(Modifier.size(220.dp).clip(RoundedCornerShape(12.dp)).background(Brush.linearGradient(listOf(c.accent, c.accent.copy(alpha = 0.35f)))),
                        contentAlignment = Alignment.Center) {
                        Icon(if (kind == CollectionKind.Liked) Ico.Heart else Ico.Download, null, tint = c.onAccent, modifier = Modifier.size(84.dp))
                    }
                else Art(thumb, 220.dp, corner = 12.dp)
                Spacer(Modifier.height(16.dp))
                Text(col.title, style = Type.title1.copy(fontSize = 26.sp), color = c.text, maxLines = 2, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.fillMaxWidth().clickable(enabled = kind == CollectionKind.Local) { renaming = true })
                col.subtitle?.let { Text(it, style = Type.caption, color = c.subtle, modifier = Modifier.fillMaxWidth().padding(top = 4.dp), maxLines = 2) }
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                    PlayButtons(onPlay = { app.playList(col.tracks, 0) }, onShuffle = { app.playList(col.tracks, 0, shuffle = true) }) {
                        if (kind != CollectionKind.Downloads && col.tracks.isNotEmpty()) {
                            val states = remember(dv, col.tracks) { col.tracks.map { app.graph.downloads.stateOf(it.videoId) } }
                            val all = states.all { it != DownloadState.None }
                            val any = states.any { it != DownloadState.None }
                            // download-only: pressing again after adding songs fetches just the new ones
                            IconBtn(if (all) Ico.DownloadDone else Ico.Download, if (all) "Downloaded" else "Download",
                                tint = if (any) c.accentText else c.subtle, size = 26.dp, box = 52.dp) {
                                if (all) app.graph.toast("Everything here is downloaded") else app.download(col.tracks)
                            }
                            IconBtn(Ico.More, "More options", size = 26.dp, box = 52.dp) {
                                app.collectionMenuFor = CollectionTarget(kind, id, col.title, col.subtitle, col.thumbnail ?: col.tracks.firstOrNull()?.thumbnail)
                            }
                        }
                        if (col.tracks.isNotEmpty()) IconBtn(Ico.SelectAll, "Select songs", size = 24.dp, box = 52.dp) { selection.active = true }
                    }
                }
            }
        }
        // how far along this playlist/album's downloads are
        if (kind != CollectionKind.Downloads && col.tracks.isNotEmpty()) item {
            val states = remember(dv, col.tracks) { col.tracks.map { app.graph.downloads.stateOf(it.videoId) } }
            val done = states.count { it == DownloadState.Done }
            val pending = states.count { it == DownloadState.Queued || it == DownloadState.Downloading }
            if (pending > 0) {
                val p by app.graph.downloads.progress.collectAsState()
                val current = if (col.tracks.any { it.videoId == p.first }) p.second else 0f
                val total = done + pending
                Column(Modifier.padding(horizontal = 20.dp, vertical = 4.dp)) {
                    Text("Downloading \u2022 $done of $total saved", style = Type.caption, color = c.subtle)
                    androidx.compose.material3.LinearProgressIndicator(progress = { ((done + current) / total).coerceIn(0f, 1f) },
                        modifier = Modifier.fillMaxWidth().padding(top = 6.dp).height(4.dp).clip(RoundedCornerShape(2.dp)),
                        color = c.accent, trackColor = c.track, drawStopIndicator = {})
                }
            }
        }
        if (kind == CollectionKind.Downloads) item { DownloadQueueCard() }
        if (col.tracks.isEmpty()) item {
            Message(when (kind) {
                CollectionKind.Liked -> "Songs you like will appear here. Tap ♡ on any song."
                CollectionKind.Downloads -> "Songs you download play without internet. Use the download button on a playlist or album, or ⋯ → Download on a song."
                CollectionKind.Local -> "This playlist is empty. Use ⋯ → Add to playlist on any song."
                else -> "Nothing here."
            })
        }
        if (selection.active) {
            @OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
            stickyHeader(key = "selection") {
                val removeLabel = when (kind) { CollectionKind.Local -> "Remove from playlist"; CollectionKind.Liked -> "Remove from Liked songs"; else -> null }
                SelectionBar(selection, col.tracks.size, selectionActions(app, { selection.tracks(col.tracks) }, { selection.close() }, removeLabel,
                    remove = when (kind) {
                        CollectionKind.Local -> { -> selection.chosen.sortedDescending().forEach { app.graph.library.removeFromPlaylist(id.toLong(), it) }
                            app.graph.toast("Removed ${selection.chosen.size} songs") }
                        CollectionKind.Liked -> { -> selection.tracks(col.tracks).forEach { app.graph.library.setLiked(it, false) }
                            app.graph.toast("Removed ${selection.chosen.size} songs from Liked songs") }
                        else -> null
                    }))
            }
        }
        itemsIndexed(col.tracks, key = { i, t -> "$i${t.videoId}" }) { i, t ->
            Box(Modifier.padding(horizontal = 8.dp)) {
                TrackRow(t, number = if (kind == CollectionKind.Album) i + 1 else null, isCurrent = ui.track?.videoId == t.videoId,
                    playlistId = if (kind == CollectionKind.Local) id.toLong() else null, playlistIndex = i,
                    selecting = selection.active, selected = i in selection.chosen, onToggle = { selection.toggle(i) }) { app.playList(col.tracks, i) }
            }
        }
    }
    if (renaming && local != null) NameDialog("Rename playlist", local.title, ok = "Save") { name ->
        renaming = false
        if (name != null) app.graph.library.renamePlaylist(id.toLong(), name)
    }
}

@Composable
fun BackRow(title: String? = null) {
    val app = LocalApp.current
    Row(Modifier.statusBarsPadding().padding(4.dp), verticalAlignment = Alignment.CenterVertically) {
        IconBtn(Ico.Back, "Back", tint = LocalTune.current.text) { app.nav.popBackStack() }
        title?.let { Text(it, style = Type.section, color = LocalTune.current.text) }
    }
}

// ============================== Artist ==============================

@Composable
fun ArtistScreen(browseId: String) {
    val app = LocalApp.current
    val c = LocalTune.current
    val ui by app.player.ui.collectAsState()
    val vm: Loader<ArtistPage> = viewModel(key = "artist/$browseId")
    val state by vm.state.collectAsState()
    LaunchedEffect(browseId) { vm.load { app.graph.yt.artist(browseId) } }
    val page = state?.getOrNull()
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item { BackRow() }
        if (page == null) { item { if (state?.isFailure == true) Message("Couldn't load this artist.") else Loading() }; return@LazyColumn }
        item {
            Column(Modifier.fillMaxWidth().padding(16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Art(page.thumbnail, 180.dp, circle = true)
                Text(page.name, style = Type.title1, color = c.text, modifier = Modifier.padding(top = 14.dp))
                if (page.topSongs.isNotEmpty())
                    PlayButtons(onPlay = { app.playList(page.topSongs, 0) }, onShuffle = { app.playList(page.topSongs, 0, shuffle = true) })
            }
        }
        if (page.topSongs.isNotEmpty()) {
            item { SectionTitle("Popular") }
            itemsIndexed(page.topSongs.take(10), key = { i, t -> "t$i${t.videoId}" }) { i, t ->
                Box(Modifier.padding(horizontal = 8.dp)) { TrackRow(t, number = i + 1, isCurrent = ui.track?.videoId == t.videoId) { app.playList(page.topSongs, i) } }
            }
        }
        page.shelves.forEachIndexed { i, s -> item(key = "sh$i") { ShelfRow(s) } }
    }
}

// ============================== Settings ==============================

/**
 * Downloads the new APK and opens Android's installer. The first time, Android needs "Allow from this source"
 * for Tune: we open that screen and the user taps Update again.
 */
private fun startUpdate(app: AppState, context: android.content.Context, scope: kotlinx.coroutines.CoroutineScope) {
    val updater = app.graph.updater
    val release = updater.available.value ?: return
    if (updater.progress.value != null) return
    if (!updater.canInstall()) {
        app.graph.toast("Allow Tune to install updates, then tap Update again")
        runCatching { context.startActivity(updater.openInstallPermission()) }
        return
    }
    scope.launch {
        runCatching { updater.download(release) }
            .onSuccess { context.startActivity(it) }
            .onFailure { app.graph.toast("The update didn’t download — try again on Wi-Fi") }
    }
}

/** Home: a newer Tune is out. One tap downloads it and opens the installer (your library stays). */
@Composable
private fun UpdateBanner() {
    val app = LocalApp.current
    val c = LocalTune.current
    val release by app.graph.updater.available.collectAsState()
    val progress by app.graph.updater.progress.collectAsState()
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val r = release ?: return
    Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp).fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(c.card)
        .padding(start = 18.dp, end = 10.dp, top = 12.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text("Tune ${r.versionName} is available", style = Type.bodyStrong, color = c.text)
            Text(if (progress != null) "Downloading… ${((progress ?: 0f) * 100).toInt()}%" else "Update to get the newest features and fixes.",
                style = Type.caption, color = c.subtle)
        }
        Box(Modifier.clip(RoundedCornerShape(20.dp)).background(if (progress != null) c.track else c.accent)
            .clickable(enabled = progress == null) { startUpdate(app, context, scope) }.padding(horizontal = 18.dp, vertical = 10.dp)) {
            Text("Update", style = Type.bodyStrong, color = if (progress != null) c.subtle else c.onAccent)
        }
    }
}

/** Settings > Updates: this version, and a button to check now / install. */
@Composable
internal fun UpdatesSection() {
    val app = LocalApp.current
    val c = LocalTune.current
    val updater = app.graph.updater
    val release by updater.available.collectAsState()
    val progress by updater.progress.collectAsState()
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf<String?>(null) }
    Column(Modifier.padding(horizontal = 20.dp)) {
        Text("UPDATES", style = Type.label, color = c.subtle, modifier = Modifier.padding(top = 28.dp, bottom = 8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Tune ${updater.currentVersion}", style = Type.bodyStrong, color = c.text)
                Text(when {
                    progress != null -> "Downloading… ${((progress ?: 0f) * 100).toInt()}%"
                    release != null -> "Tune ${release!!.versionName} is available."
                    else -> status ?: "Tune checks for new versions every few hours."
                }, style = Type.caption, color = c.subtle)
            }
            Text(if (release != null) "Update" else "Check", style = Type.bodyStrong, color = c.accentText,
                modifier = Modifier.clip(RoundedCornerShape(16.dp)).clickable(enabled = progress == null) {
                    if (release != null) startUpdate(app, context, scope)
                    else {
                        status = "Checking…"
                        scope.launch { status = if (updater.check(force = true) == null) "You have the latest version." else null }
                    }
                }.padding(12.dp))
        }
    }
}

/** Settings > Import: paste a YouTube Music playlist link; its songs become a playlist in the library. */
@Composable
internal fun ImportSection() {
    val app = LocalApp.current
    val c = LocalTune.current
    val scope = rememberCoroutineScope()
    var link by rememberSaveable { mutableStateOf("") }
    var status by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    fun import() {
        val id = PlaylistLinks.parse(link)
        if (id == null) { status = "That doesn\u2019t look like a playlist link. It should contain list=\u2026"; return }
        busy = true; status = "Importing\u2026"
        scope.launch {
            val result = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { runCatching { app.graph.yt.playlist(id) } }
            busy = false
            val col = result.getOrNull()
            status = when {
                col == null -> "Couldn\u2019t import \u2014 check the link and your connection."
                col.tracks.isEmpty() -> "That playlist is empty or private (only public and unlisted playlists can be imported)."
                else -> {
                    val pid = app.graph.library.createPlaylist(col.title)
                    app.graph.library.addManyToPlaylist(pid, col.tracks)
                    link = ""
                    app.graph.toast("Imported ${col.title}")
                    "Imported ${col.tracks.size} songs into \u201c${col.title}\u201d."
                }
            }
        }
    }
    Column(Modifier.padding(horizontal = 20.dp)) {
        Text("IMPORT", style = Type.label, color = c.subtle, modifier = Modifier.padding(top = 28.dp, bottom = 8.dp))
        Text("Import a YouTube Music playlist", style = Type.bodyStrong, color = c.text)
        Text("Paste a playlist link (music.youtube.com or youtube.com). Its songs become a playlist in your library. Public and unlisted playlists work.",
            style = Type.caption, color = c.subtle, modifier = Modifier.padding(bottom = 10.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(link, { link = it }, singleLine = true, modifier = Modifier.weight(1f),
                placeholder = { Text("https://music.youtube.com/playlist?list=\u2026", color = c.subtle, maxLines = 1) },
                shape = RoundedCornerShape(24.dp),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Go),
                keyboardActions = KeyboardActions(onGo = { if (!busy) import() }),
                colors = OutlinedTextFieldDefaults.colors(focusedBorderColor = c.accent, unfocusedBorderColor = Color.Transparent,
                    unfocusedContainerColor = c.card, focusedContainerColor = c.card, cursorColor = c.accent))
            Box(Modifier.padding(start = 10.dp).clip(RoundedCornerShape(22.dp)).background(if (busy) c.track else c.accent)
                .clickable(enabled = !busy) { import() }.padding(horizontal = 18.dp, vertical = 12.dp)) {
                Text("Import", style = Type.bodyStrong, color = if (busy) c.subtle else c.onAccent)
            }
        }
        status?.let { Text(it, style = Type.caption, color = c.subtle, modifier = Modifier.padding(top = 10.dp)) }
    }
}

/** Settings > Smart downloads: Tune keeps your most-played songs ready offline (like YouTube Music). */
@Composable
internal fun SmartSection() {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val dv by app.graph.downloads.version.collectAsState()
    val enabled = remember(version) { app.graph.smart.enabled }
    val target = remember(version) { app.graph.smart.target }
    val lookahead = remember(version) { app.graph.library.setting("lookahead.enabled") != "0" }
    // count every picked song that's on the phone, including ones you'd already downloaded yourself
    val picked = remember(dv, version) { app.graph.smart.pickedIds }
    val ready = remember(dv, version) { picked.count { app.graph.downloads.localFile(it) != null } }
    val waiting = picked.size - ready
    val last = remember(version) { app.graph.smart.lastRunMs }
    val context = androidx.compose.ui.platform.LocalContext.current
    Column(Modifier.padding(horizontal = 20.dp)) {
        Text("SMART DOWNLOADS", style = Type.label, color = c.subtle, modifier = Modifier.padding(top = 28.dp, bottom = 8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Keep my favourites offline", style = Type.bodyStrong, color = c.text)
                Text("Tune downloads the songs you play most and like, plus a few similar ones, on Wi-Fi. They're swapped as your taste changes; your own downloads are never touched.",
                    style = Type.caption, color = c.subtle)
            }
            Switch(enabled, {
                app.graph.library.setSetting("smart.enabled", if (it) "1" else "0")
                SmartRefreshWorker.runNow(context)
            }, colors = SwitchDefaults.colors(checkedTrackColor = c.accent, checkedThumbColor = c.onAccent))
        }
        if (enabled) {
            Text("How many songs", style = Type.caption, color = c.subtle, modifier = Modifier.padding(top = 14.dp, bottom = 8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(25, 50, 100, 250, 500).forEach { n ->
                    val on = n == target
                    Text("$n", style = Type.bodyStrong, color = if (on) c.onAccent else c.text,
                        modifier = Modifier.clip(RoundedCornerShape(18.dp)).background(if (on) c.accent else c.card).clickable {
                            app.graph.library.setSetting("smart.count", n.toString()); SmartRefreshWorker.runNow(context)
                        }.padding(horizontal = 16.dp, vertical = 8.dp))
                }
            }
            val ago = if (last == 0L) "not yet" else android.text.format.DateUtils.getRelativeTimeSpanString(last).toString().lowercase()
            Text("$ready of ${picked.size} ready offline" + (if (waiting > 0) " · $waiting waiting for Wi-Fi" else "") + " · updated $ago",
                style = Type.caption, color = c.subtle, modifier = Modifier.padding(top = 10.dp))
            Text("Update now", style = Type.bodyStrong, color = c.accentText, modifier = Modifier.padding(top = 4.dp).clip(RoundedCornerShape(16.dp))
                .clickable { SmartRefreshWorker.runNow(context); app.graph.toast("Updating smart downloads") }.padding(vertical = 8.dp))
        }
        Row(Modifier.padding(top = 14.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Get ahead of dead zones", style = Type.bodyStrong, color = c.text)
                Text("While you listen, the next 3 songs in your queue are saved too (a few MB), so music keeps going when the signal drops.",
                    style = Type.caption, color = c.subtle)
            }
            Switch(lookahead, { app.graph.library.setSetting("lookahead.enabled", if (it) "1" else "0") },
                colors = SwitchDefaults.colors(checkedTrackColor = c.accent, checkedThumbColor = c.onAccent))
        }
    }
}

/** Downloads page: what's downloading right now (live bar) and what's waiting, each cancellable. */
@Composable
private fun DownloadQueueCard() {
    val app = LocalApp.current
    val c = LocalTune.current
    val dv by app.graph.downloads.version.collectAsState()
    val p by app.graph.downloads.progress.collectAsState()
    val unmetered by app.graph.unmetered.collectAsState()
    val queue = remember(dv) { app.graph.downloads.queued() }
    if (queue.isEmpty()) return
    val active = queue.firstOrNull { it.first.videoId == p.first }
    val waiting = queue.filter { it.first.videoId != p.first }
    var expanded by remember { mutableStateOf(false) }
    Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp).fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(c.card).padding(14.dp)) {
        Text(if (active != null) "Downloading now" else "Waiting to download", style = Type.label, color = c.subtle)
        if (active != null) {
            Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Art(active.first.thumbnail, 44.dp)
                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                    Text(active.first.title, style = Type.bodyStrong, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text("${(p.second * 100).toInt()}%", style = Type.caption, color = c.accentText)
                }
                IconBtn(Ico.Close, "Cancel", size = 18.dp, box = 40.dp) { app.removeDownloads(listOf(active.first)) }
            }
            androidx.compose.material3.LinearProgressIndicator(progress = { p.second.coerceIn(0f, 1f) },
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp).height(4.dp).clip(RoundedCornerShape(2.dp)),
                color = c.accent, trackColor = c.track, drawStopIndicator = {})
        }
        if (waiting.isNotEmpty()) {
            val smartWaiting = waiting.count { it.second }
            val note = if (smartWaiting > 0 && !unmetered) " \u2022 $smartWaiting smart downloads wait for Wi-Fi" else ""
            Row(Modifier.fillMaxWidth().padding(top = 12.dp).clickable { expanded = !expanded }, verticalAlignment = Alignment.CenterVertically) {
                Text("${waiting.size} waiting$note", style = Type.caption, color = c.subtle, modifier = Modifier.weight(1f))
                Text(if (expanded) "Hide" else "Show", style = Type.caption, color = c.accentText, fontWeight = FontWeight.SemiBold)
            }
            if (expanded) waiting.take(50).forEach { (t, smart) ->
                Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Art(t.thumbnail, 36.dp)
                    Column(Modifier.weight(1f).padding(horizontal = 10.dp)) {
                        Text(t.title, style = Type.body, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(if (smart && !unmetered) "Waiting for Wi-Fi" else if (smart) "Smart download \u2022 waiting" else "Waiting",
                            style = Type.caption, color = c.subtle)
                    }
                    IconBtn(Ico.Close, "Cancel", size = 16.dp, box = 36.dp) { app.removeDownloads(listOf(t)) }
                }
            }
        }
    }
}
