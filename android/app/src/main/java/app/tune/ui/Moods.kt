package app.tune.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.scale
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.tune.data.MoodCategory
import app.tune.data.MoodSection
import app.tune.data.Shelf
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** Moods & genres, shown on the Search page while nothing is typed: two tiles per row. */
fun LazyListScope.moodsBrowse(sections: List<MoodSection>) {
    for (section in sections) {
        item(key = "ms-" + section.title) { SectionTitle(section.title) }
        section.items.chunked(2).forEachIndexed { i, pair ->
            item(key = "mr-${section.title}-$i") {
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 5.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    pair.forEach { MoodTile(it, Modifier.weight(1f)) }
                    if (pair.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}

private fun Long.toColor() = Color(this.toInt() or 0xFF000000.toInt())

/** A colour-washed card with the mood's playlist cover tilted into the corner (straightens a little when pressed). */
@Composable
private fun MoodTile(mood: MoodCategory, modifier: Modifier) {
    val app = LocalApp.current
    val color = mood.color.toColor()
    val cover by produceState(mood.thumbnail, mood.params) {
        if (value == null) value = withContext(Dispatchers.IO) { app.graph.moods.cover(mood) }
    }
    val press = remember { MutableInteractionSource() }
    val pressed by press.collectIsPressedAsState()
    val tilt by animateFloatAsState(if (pressed) 10f else 20f, label = "tilt")
    val shrink by animateFloatAsState(if (pressed) 0.97f else 1f, label = "press")
    Box(modifier.height(96.dp).scale(shrink).clip(RoundedCornerShape(14.dp))
        .background(Brush.linearGradient(listOf(lerp(Color(0xFF1C1C1E), color, 0.32f), lerp(Color(0xFF171719), color, 0.08f))))
        .clickable(interactionSource = press, indication = null) { app.nav.navigate("mood/${android.net.Uri.encode(mood.params)}/${mood.color}/${android.net.Uri.encode(mood.title)}") }
    ) {
        if (cover != null)
            Box(Modifier.align(Alignment.BottomEnd).offset(x = 12.dp, y = 14.dp).rotate(tilt).shadow(10.dp, RoundedCornerShape(8.dp))) {
                Art(cover, 72.dp, corner = 8.dp)
            }
        Text(mood.title, color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.Bold, maxLines = 2, overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = 14.dp, top = 12.dp, end = 70.dp))
        Box(Modifier.align(Alignment.BottomStart).padding(start = 14.dp, bottom = 14.dp).size(24.dp, 4.dp).clip(RoundedCornerShape(2.dp)).background(color))
    }
}

/** The tiles for the Search page (cached for a week; empty while loading or offline with no cache). */
@Composable
fun rememberMoods(): List<MoodSection> {
    val app = LocalApp.current
    var sections by remember { mutableStateOf(emptyList<MoodSection>()) }
    LaunchedEffect(Unit) { sections = withContext(Dispatchers.IO) { app.graph.moods.get() } }
    return sections
}

/** One mood or genre: YouTube Music's rows of playlists for it, under the mood's colour. */
@Composable
fun MoodScreen(params: String, title: String, color: Long) {
    val app = LocalApp.current
    val c = LocalTune.current
    val vm: Loader<List<Shelf>> = viewModel(key = "mood/$params")
    val state by vm.state.collectAsState()
    LaunchedEffect(params) { vm.load { app.graph.yt.mood(params) } }
    Box(Modifier.fillMaxSize().background(c.base)) {
        Box(Modifier.fillMaxWidth().height(280.dp).background(Brush.verticalGradient(listOf(color.toColor().copy(alpha = 0.35f), Color.Transparent))))
        LazyColumn(contentPadding = BottomClearance, modifier = Modifier.fillMaxSize()) {
            item { BackRow() }
            item { Text(title, style = Type.title1, color = c.text, modifier = Modifier.padding(start = 16.dp, end = 16.dp, bottom = 8.dp)) }
            val shelves = state?.getOrNull()
            if (shelves == null) item { if (state?.isFailure == true) Message("Couldn't load this — check your connection.") else Loading() }
            else shelves.forEachIndexed { i, s -> item(key = "sh$i") { ShelfRow(s) } }
        }
    }
}
