package app.tune

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import app.tune.playback.PlayerConnection
import app.tune.ui.Accents
import app.tune.ui.AddToPlaylistDialog
import app.tune.ui.AppState
import app.tune.ui.ArtistScreen
import app.tune.ui.CollectionKind
import app.tune.ui.CollectionScreen
import app.tune.ui.HomeScreen
import app.tune.ui.HistoryScreen
import app.tune.ui.Ico
import app.tune.ui.LibraryScreen
import app.tune.ui.LocalApp
import app.tune.ui.LocalTune
import app.tune.ui.MiniPlayer
import app.tune.ui.NowPlaying
import app.tune.ui.SearchScreen
import app.tune.ui.SettingsScreen
import app.tune.ui.SettingsCategoryScreen
import app.tune.ui.TrackMenuSheet
import app.tune.ui.CollectionMenuSheet
import app.tune.ui.ConfirmDialogHost
import app.tune.ui.RenamePlaylistDialog
import app.tune.ui.TuneTheme
import app.tune.ui.Type
import kotlinx.coroutines.delay

object BuildConfigLite { const val VERSION = "1.4.0" }

class MainActivity : ComponentActivity() {
    /** Back in Tune: catch up with changes made on other devices. */
    override fun onStart() {
        super.onStart()
        (application as TuneApp).graph.account?.syncSoon()
    }

    private val askNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(Color.Transparent.toArgb()), navigationBarStyle = SystemBarStyle.dark(Color.Transparent.toArgb()))
        if (Build.VERSION.SDK_INT >= 33) askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS) // media controls in the shade
        val graph = (application as TuneApp).graph
        val player = PlayerConnection(this)
        if (graph.smart.enabled && System.currentTimeMillis() - graph.smart.lastRunMs > 12 * 3_600_000L) SmartRefreshWorker.runNow(this)
        setContent {
            var accent by remember { mutableStateOf(Accents.parse(graph.library.setting("theme.accent"))) }
            TuneTheme(accent) {
                val nav = rememberNavController()
                val app = remember { AppState(graph, player, nav) }
                CompositionLocalProvider(LocalApp provides app) {
                    TuneRoot(accent) { accent = it; graph.library.setSetting("theme.accent", Accents.hex(it)) }
                }
            }
        }
    }
}

@Composable
private fun TuneRoot(accent: Color, onAccent: (Color) -> Unit) {
    val app = LocalApp.current
    // new Tune release on GitHub? (at most every few hours; shows the Update banner on Home)
    androidx.compose.runtime.LaunchedEffect(Unit) { app.graph.updater.check() }
    val c = LocalTune.current
    // every page is opaque (with its own accent glow), so pages slide over each other cleanly
    val page: @Composable (@Composable () -> Unit) -> Unit = { content ->
        Box(Modifier.fillMaxSize().background(c.base)) {
            Box(Modifier.fillMaxWidth().height(260.dp).background(Brush.verticalGradient(listOf(c.accent.copy(alpha = 0.22f), Color.Transparent))))
            content()
        }
    }
    val tabs = setOf("home", "search", "library")
    fun isTab(e: androidx.navigation.NavBackStackEntry) = e.destination.route in tabs
    val slide = androidx.compose.animation.core.tween<androidx.compose.ui.unit.IntOffset>(320, easing = androidx.compose.animation.core.FastOutSlowInEasing)
    Box(Modifier.fillMaxSize().background(c.base)) {
        NavHost(app.nav, startDestination = "home", modifier = Modifier.fillMaxSize(),
            // pages slide in from the right and back out to the right; switching tabs is instant
            enterTransition = { if (isTab(targetState)) androidx.compose.animation.EnterTransition.None else slideInHorizontally(slide) { it } },
            exitTransition = { if (isTab(targetState)) androidx.compose.animation.ExitTransition.None else slideOutHorizontally(slide) { -it / 4 } },
            // back is always a slide (it follows the finger during Android's back gesture): this page goes right, the one below comes in
            popEnterTransition = { slideInHorizontally(slide) { -it / 4 } },
            popExitTransition = { slideOutHorizontally(slide) { it } },
        ) {
            composable("home") { page { HomeScreen() } }
            composable("search") { page { SearchScreen() } }
            composable("library") { page { LibraryScreen() } }
            composable("liked") { page { CollectionScreen(CollectionKind.Liked, "liked") } }
            composable("downloads") { page { CollectionScreen(CollectionKind.Downloads, "downloads") } }
            composable("history") { page { HistoryScreen() } }
            composable("local/{id}") { page { CollectionScreen(CollectionKind.Local, it.arguments?.getString("id")!!) } }
            composable("album/{id}") { page { CollectionScreen(CollectionKind.Album, it.arguments?.getString("id")!!) } }
            composable("ytplaylist/{id}") { page { CollectionScreen(CollectionKind.YtPlaylist, it.arguments?.getString("id")!!) } }
            composable("artist/{id}") { page { ArtistScreen(it.arguments?.getString("id")!!) } }
            composable("settings") { page { SettingsScreen(accent, onAccent) } }
            composable("settings/{cat}") { page { SettingsCategoryScreen(it.arguments?.getString("cat")!!, accent, onAccent) } }
        }

        // mini player + tabs: taps that land between their buttons stop here instead of reaching the page underneath
        Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth()
            .pointerInput(Unit) { awaitPointerEventScope { while (true) awaitPointerEvent() } }) {
            // fade the page out above the mini player / tabs
            Box(Modifier.fillMaxWidth().height(28.dp).background(Brush.verticalGradient(listOf(Color.Transparent, Color(0xFF121212)))))
            MiniPlayer(Modifier.fillMaxWidth().background(Color(0xFF121212)))
            BottomBar()
        }

        AnimatedVisibility(app.nowPlayingOpen, enter = slideInVertically { it } + fadeIn(), exit = slideOutVertically { it } + fadeOut()) { NowPlaying() }

        Toasts(Modifier.align(Alignment.BottomCenter).padding(bottom = 170.dp))
        TrackMenuSheet()
        CollectionMenuSheet()
        ConfirmDialogHost()
        RenamePlaylistDialog()
        AddToPlaylistDialog()
    }
}

@Composable
private fun BottomBar() {
    val app = LocalApp.current
    val c = LocalTune.current
    val entry by app.nav.currentBackStackEntryAsState()
    val route = entry?.destination?.route
    val tabs = listOf(Triple("home", "Home", Ico.Home), Triple("search", "Search", Ico.Search), Triple("library", "Library", Ico.Library))
    // each tab owns its whole third of the bar (full height), so a tap anywhere in it counts
    Row(Modifier.fillMaxWidth().background(Color(0xFF121212)).navigationBarsPadding().height(68.dp)) {
        tabs.forEach { (key, label, icon) ->
            val on = route == key || (key == "library" && route in setOf("liked", "downloads", "history", "local/{id}"))
            Column(Modifier.weight(1f).fillMaxHeight().clickable {
                // a tab always lands on its main screen: Home pops back to itself (e.g. out of Settings),
                // other tabs start fresh on top of Home — no stale pages restored on top
                if (key == "home") app.nav.popBackStack("home", inclusive = false)
                else if (route != key) app.nav.navigate(key) { popUpTo("home"); launchSingleTop = true }
            }, horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = androidx.compose.foundation.layout.Arrangement.Center) {
                Box(Modifier.clip(RoundedCornerShape(14.dp)).background(if (on) c.accentSoft else Color.Transparent).padding(horizontal = 18.dp, vertical = 4.dp)) {
                    Icon(icon, label, tint = if (on) c.accentText else c.subtle, modifier = Modifier.size(24.dp))
                }
                Text(label, style = Type.caption, color = if (on) c.text else c.subtle, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal)
            }
        }
    }
}

@Composable
private fun Toasts(modifier: Modifier) {
    val app = LocalApp.current
    val c = LocalTune.current
    var message by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) {
        app.graph.toasts.collect { message = it; delay(3000); message = null }
    }
    AnimatedVisibility(message != null, modifier = modifier, enter = fadeIn(), exit = fadeOut()) {
        Text(message ?: "", style = Type.bodyStrong, color = c.onAccent,
            modifier = Modifier.clip(RoundedCornerShape(20.dp)).background(c.accent).padding(horizontal = 18.dp, vertical = 10.dp))
    }
}
