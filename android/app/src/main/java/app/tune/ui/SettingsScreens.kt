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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import app.tune.BuildConfigLite
import app.tune.data.HomeLayout
import app.tune.data.HomeSectionState
import app.tune.data.SpeedDialPins

/** Settings is a short list of categories; each opens its own page (no endless scrolling page). */
private data class SettingsCategory(val key: String, val title: String, val summary: String, val icon: ImageVector)

private val categories = listOf(
    SettingsCategory("appearance", "Appearance", "Accent colour", Ico.Palette),
    SettingsCategory("home", "Home", "Speed dial, sections and their order", Ico.Dashboard),
    SettingsCategory("playback", "Playback", "Lyrics", Ico.Play),
    SettingsCategory("offline", "Offline & storage", "Smart downloads, saved songs, space used", Ico.Download),
    SettingsCategory("library", "Library", "Import a YouTube Music playlist", Ico.PlaylistAdd),
    SettingsCategory("about", "About & updates", "Version, new releases", Ico.Info),
)

@Composable
fun SettingsScreen(accent: Color, onAccent: (Color) -> Unit) {
    val app = LocalApp.current
    val c = LocalTune.current
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item { BackRow("Settings") }
        items(categories, key = { it.key }) { cat ->
            Row(Modifier.fillMaxWidth().clickable { app.nav.navigate("settings/${cat.key}") }.padding(horizontal = 20.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(44.dp).clip(RoundedCornerShape(12.dp)).background(c.card), contentAlignment = Alignment.Center) {
                    Icon(cat.icon, null, tint = c.accentText, modifier = Modifier.size(22.dp))
                }
                Column(Modifier.weight(1f).padding(start = 16.dp)) {
                    Text(cat.title, style = Type.bodyStrong, color = c.text)
                    Text(cat.summary, style = Type.caption, color = c.subtle)
                }
                Icon(Ico.ChevronRight, null, tint = c.subtle, modifier = Modifier.size(22.dp))
            }
        }
    }
}

@Composable
fun SettingsCategoryScreen(key: String, accent: Color, onAccent: (Color) -> Unit) {
    val cat = categories.firstOrNull { it.key == key } ?: categories.first()
    LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
        item { BackRow(cat.title) }
        when (cat.key) {
            "appearance" -> item { AccentSettings(accent, onAccent) }
            "home" -> item { HomeSettings() }
            "playback" -> item { PlaybackSettings() }
            "offline" -> { item { SmartSection() }; item { StorageSettings() } }
            "library" -> item { ImportSection() }
            else -> item { UpdatesSection(); AboutLine() }
        }
    }
}

@Composable
private fun SettingLabel(text: String) {
    Text(text, style = Type.label, color = LocalTune.current.subtle, modifier = Modifier.padding(start = 20.dp, top = 12.dp, bottom = 10.dp))
}

@Composable
private fun AccentSettings(accent: Color, onAccent: (Color) -> Unit) {
    SettingLabel("ACCENT COLOUR")
    LazyRow(contentPadding = PaddingValues(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        items(Accents.presets) { (name, color) ->
            Box(Modifier.size(44.dp).clip(CircleShape).background(color).clickable { onAccent(color) }, contentAlignment = Alignment.Center) {
                if (Accents.hex(color) == Accents.hex(accent)) Icon(Ico.Check, name, tint = if (color.red + color.green + color.blue > 2f) Color.Black else Color.White)
            }
        }
    }
}

@Composable
private fun PlaybackSettings() {
    val app = LocalApp.current
    val c = LocalTune.current
    val version by app.graph.library.version.collectAsState()
    val synced = remember(version) { app.graph.library.setting("lyrics.synced") != "0" }
    Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text("Synced lyrics", style = Type.bodyStrong, color = c.text)
            Text("Lines follow the song (from lrclib.net). Off: plain lyrics.", style = Type.caption, color = c.subtle)
        }
        Switch(synced, { app.graph.library.setSetting("lyrics.synced", if (it) "1" else "0") },
            colors = SwitchDefaults.colors(checkedTrackColor = c.accent, checkedThumbColor = c.onAccent))
    }
}

/** Settings > Home: which sections Home shows, in which order; and the speed dial pins. Synced with the account. */
@Composable
private fun HomeSettings() {
    val app = LocalApp.current
    val c = LocalTune.current
    val lib = app.graph.library
    var layout by remember { mutableStateOf(HomeLayout.load(lib)) }
    var pins by remember { mutableStateOf(SpeedDialPins.load(lib).size) }
    fun save(new: List<HomeSectionState>) { layout = new; HomeLayout.save(lib, new); app.homeChanged++ }

    SettingLabel("SECTIONS")
    Text("Choose what Home shows, top to bottom.", style = Type.caption, color = c.subtle, modifier = Modifier.padding(horizontal = 20.dp))
    Spacer(Modifier.height(6.dp))
    layout.forEachIndexed { i, s ->
        Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 12.dp, top = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(s.section.label, style = Type.bodyStrong, color = if (s.visible) c.text else c.subtle)
                Text(s.section.description, style = Type.caption, color = c.subtle)
            }
            IconBtn(Ico.ArrowUp, "Move up", tint = if (i > 0) c.text else c.track, size = 22.dp, box = 38.dp) {
                if (i > 0) save(layout.toMutableList().apply { add(i - 1, removeAt(i)) })
            }
            IconBtn(Ico.ArrowDown, "Move down", tint = if (i < layout.size - 1) c.text else c.track, size = 22.dp, box = 38.dp) {
                if (i < layout.size - 1) save(layout.toMutableList().apply { add(i + 1, removeAt(i)) })
            }
            Switch(s.visible, { on -> save(layout.toMutableList().apply { set(i, s.copy(visible = on)) }) },
                colors = SwitchDefaults.colors(checkedTrackColor = c.accent, checkedThumbColor = c.onAccent))
        }
    }
    Text("Reset to default", style = Type.bodyStrong, color = c.accentText,
        modifier = Modifier.padding(start = 8.dp, top = 6.dp).clip(RoundedCornerShape(16.dp)).clickable { save(HomeLayout.default) }.padding(12.dp))

    SettingLabel("SPEED DIAL")
    Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(if (pins == 0) "Nothing pinned" else "$pins pinned", style = Type.bodyStrong, color = c.text)
            Text("Long-press a song, album or playlist to pin it.", style = Type.caption, color = c.subtle)
        }
        if (pins > 0) Text("Clear", style = Type.bodyStrong, color = c.accentText, modifier = Modifier.clip(RoundedCornerShape(16.dp)).clickable {
            SpeedDialPins.save(lib, emptyList()); pins = 0; app.homeChanged++
        }.padding(12.dp))
    }
}

@Composable
private fun StorageSettings() {
    val app = LocalApp.current
    val c = LocalTune.current
    val dv by app.graph.downloads.version.collectAsState()
    val bytes = remember(dv) { app.graph.downloads.downloadedBytes() }
    var cacheBytes by remember { mutableStateOf(app.graph.streamCache.usedBytes()) }
    SettingLabel("STORAGE")
    Text("Downloads use ${bytes / (1 shl 20)} MB.", style = Type.body, color = c.text, modifier = Modifier.padding(horizontal = 20.dp))
    Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text("Recently played: ${cacheBytes / (1 shl 20)} MB of 1 GB", style = Type.body, color = c.text)
            Text("Kept so they play again offline. Oldest go first.", style = Type.caption, color = c.subtle)
        }
        Text("Clear", style = Type.bodyStrong, color = c.accentText, modifier = Modifier.clip(RoundedCornerShape(16.dp)).clickable {
            app.graph.streamCache.clear(); cacheBytes = app.graph.streamCache.usedBytes(); app.graph.toast("Cleared recently played songs")
        }.padding(12.dp))
    }
}

@Composable
private fun AboutLine() {
    Text("Tune ${BuildConfigLite.VERSION} · no ads, no tracking", style = Type.caption, color = LocalTune.current.subtle, modifier = Modifier.padding(20.dp))
}
