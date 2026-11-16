package app.tune.ui

import androidx.compose.foundation.background
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
import app.tune.data.CardKind
import app.tune.BuildConfigLite
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
val BottomClearance = PaddingValues(bottom = 150.dp)

@Composable
fun Loading() = Box(Modifier.fillMaxWidth().padding(48.dp), contentAlignment = Alignment.Center) {
    CircularProgressIndicator(color = LocalTune.current.accent, strokeWidth = 3.dp, modifier = Modifier.size(32.dp))
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
    LaunchedEffect(online) { if (online) vm.load(force = state?.getOrNull()?.offline == true) { app.graph.feed.build() } }

    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item {
            Row(Modifier.statusBarsPadding().padding(start = 16.dp, end = 8.dp, top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(greeting(), style = Type.title1, color = c.text, modifier = Modifier.weight(1f))
                if (!online) OfflinePill()
                IconBtn(Ico.Settings, "Settings") { app.nav.navigate("settings") }
            }
        }
        if (!online || state?.getOrNull()?.offline == true) {
            item { OfflineHome() }
            return@LazyColumn
        }
        val feed = state?.getOrNull()
        if (feed == null) { item { Loading() }; return@LazyColumn }
        feed.shelves.take(8).forEachIndexed { i, shelf -> item(key = "shelf$i") { ShelfRow(shelf.copy(items = shelf.items.take(12)), featured = i == 0 && shelf.isLocal) } }
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
    val tracks = remember(v) { app.graph.downloads.downloaded() }
    val ui by app.player.ui.collectAsState()
    Column(Modifier.padding(16.dp)) {
        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(18.dp)).background(c.card).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(44.dp).clip(CircleShape).background(c.track), contentAlignment = Alignment.Center) { Icon(Ico.Offline, null, tint = c.text) }
            Column(Modifier.padding(start = 14.dp)) {
                Text("You're offline", style = Type.bodyStrong, color = c.text)
                Text(if (tracks.isEmpty()) "Nothing is downloaded yet. When you're back online, download playlists or songs to play them without internet."
                     else "${tracks.size} songs you can play without internet.", style = Type.caption, color = c.subtle)
            }
        }
        if (tracks.isNotEmpty()) {
            PlayButtons(onPlay = { app.playList(tracks, 0) }, onShuffle = { app.playList(tracks, 0, shuffle = true) }, title = "Your downloads")
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

@Composable
fun LibraryScreen() {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val dv by app.graph.downloads.version.collectAsState()
    val playlists = remember(version) { app.graph.library.playlists() }
    val likedCount = remember(version) { app.graph.library.liked().size }
    val downloaded = remember(dv) { app.graph.downloads.downloaded().size }
    var naming by remember { mutableStateOf(false) }
    if (naming) NameDialog("New playlist", "My playlist #${playlists.size + 1}") { name ->
        naming = false
        if (name != null) app.nav.navigate("local/${app.graph.library.createPlaylist(name)}")
    }
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item {
            Row(Modifier.statusBarsPadding().padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text("Your library", style = Type.title1, color = c.text, modifier = Modifier.weight(1f))
                IconBtn(Ico.Add, "New playlist", tint = c.text) { naming = true }
            }
        }
        item { LibraryEntry(Ico.Heart, "Liked songs", "$likedCount songs", accentTile = true) { app.nav.navigate("liked") } }
        item { LibraryEntry(Ico.Download, "Downloads", "$downloaded songs · play without internet", accentTile = true) { app.nav.navigate("downloads") } }
        items(playlists, key = { it.id }) { p ->
            Row(Modifier.fillMaxWidth().clickable { app.nav.navigate("local/${p.id}") }.padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Art(p.thumbnail, 60.dp, corner = 8.dp)
                Column(Modifier.padding(start = 14.dp)) {
                    Text(p.name, style = Type.bodyStrong, color = c.text)
                    Text("Playlist · ${p.count} songs", style = Type.caption, color = c.subtle)
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

enum class CollectionKind { Album, YtPlaylist, Liked, Local, Downloads }

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
                Collection("Downloads", "${it.size} songs · ${app.graph.downloads.downloadedBytes() / (1 shl 20)} MB", null, it)
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
                            Box {
                                IconBtn(Ico.More, "More options", size = 26.dp, box = 52.dp) { menu = true }
                                androidx.compose.material3.DropdownMenu(menu, { menu = false }, containerColor = c.cardHover) {
                                    if (any) androidx.compose.material3.DropdownMenuItem({ Text("Remove downloads from this ${if (kind == CollectionKind.Album) "album" else "playlist"}") }, {
                                        menu = false; app.removeDownloads(col.tracks.filter { app.graph.downloads.stateOf(it.videoId) != DownloadState.None })
                                    })
                                    if (kind == CollectionKind.Local) {
                                        androidx.compose.material3.DropdownMenuItem({ Text("Rename") }, { menu = false; renaming = true })
                                        androidx.compose.material3.DropdownMenuItem({ Text("Delete playlist") }, {
                                            menu = false; app.graph.library.deletePlaylist(id.toLong()); app.graph.toast("Deleted ${col.title}"); app.nav.popBackStack()
                                        })
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        if (col.tracks.isEmpty()) item {
            Message(when (kind) {
                CollectionKind.Liked -> "Songs you like will appear here. Tap ♡ on any song."
                CollectionKind.Downloads -> "Songs you download play without internet. Use the download button on a playlist or album, or ⋯ → Download on a song."
                CollectionKind.Local -> "This playlist is empty. Use ⋯ → Add to playlist on any song."
                else -> "Nothing here."
            })
        }
        itemsIndexed(col.tracks, key = { i, t -> "$i${t.videoId}" }) { i, t ->
            Box(Modifier.padding(horizontal = 8.dp)) {
                TrackRow(t, number = if (kind == CollectionKind.Album) i + 1 else null, isCurrent = ui.track?.videoId == t.videoId,
                    playlistId = if (kind == CollectionKind.Local) id.toLong() else null, playlistIndex = i) { app.playList(col.tracks, i) }
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

@Composable
fun SettingsScreen(accent: Color, onAccent: (Color) -> Unit) {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val synced = remember(version) { app.graph.library.setting("lyrics.synced") != "0" }
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item { BackRow("Settings") }
        item {
            Text("ACCENT COLOUR", style = Type.label, color = c.subtle, modifier = Modifier.padding(start = 20.dp, top = 12.dp, bottom = 12.dp))
            LazyRow(contentPadding = PaddingValues(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                items(Accents.presets) { (name, color) ->
                    Box(Modifier.size(44.dp).clip(CircleShape).background(color).clickable { onAccent(color) }, contentAlignment = Alignment.Center) {
                        if (Accents.hex(color) == Accents.hex(accent)) Icon(Ico.Check, name, tint = if (color.red + color.green + color.blue > 2f) Color.Black else Color.White)
                    }
                }
            }
        }
        item {
            Text("PLAYBACK", style = Type.label, color = c.subtle, modifier = Modifier.padding(start = 20.dp, top = 28.dp, bottom = 8.dp))
            Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Synced lyrics", style = Type.bodyStrong, color = c.text)
                    Text("Lyrics follow the song line by line (lrclib.net; title and artist are sent). Off: plain YouTube Music lyrics.",
                        style = Type.caption, color = c.subtle)
                }
                Switch(synced, { app.graph.library.setSetting("lyrics.synced", if (it) "1" else "0") },
                    colors = SwitchDefaults.colors(checkedTrackColor = c.accent, checkedThumbColor = c.onAccent))
            }
        }
        item {
            val dv by app.graph.downloads.version.collectAsState()
            val bytes = remember(dv) { app.graph.downloads.downloadedBytes() }
            Text("STORAGE", style = Type.label, color = c.subtle, modifier = Modifier.padding(start = 20.dp, top = 28.dp, bottom = 8.dp))
            Text("Downloads use ${bytes / (1 shl 20)} MB.", style = Type.body, color = c.text, modifier = Modifier.padding(horizontal = 20.dp))
            Text("Tune ${BuildConfigLite.VERSION}", style = Type.caption, color = c.subtle, modifier = Modifier.padding(20.dp))
        }
    }
}
