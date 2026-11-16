package app.tune.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/** Tune's colours: one dark surface + the user's accent (same as the Windows app). */
@Immutable
data class TuneColors(
    val accent: Color,
    val onAccent: Color = if (accent.luminance() > 0.45f) Color.Black else Color.White,
    /** The accent as text on dark: dark/greyish accents lifted until readable. */
    val accentText: Color = liftForText(accent),
    val accentSoft: Color = accent.copy(alpha = 0.18f),
    val base: Color = Color(0xFF121212),
    val card: Color = Color(0xFF1C1C1E),
    val cardHover: Color = Color(0xFF262628),
    val text: Color = Color(0xFFF2F2F2),
    val subtle: Color = Color(0xFFA7A7A7),
    val track: Color = Color(0xFF3A3A3C),
)

private fun liftForText(c: Color): Color {
    var x = c
    var i = 0
    while (x.luminance() < 0.22f && i++ < 80) x = Color((x.red + 0.02f).coerceAtMost(1f), (x.green + 0.02f).coerceAtMost(1f), (x.blue + 0.02f).coerceAtMost(1f))
    return x
}

val LocalTune = staticCompositionLocalOf { TuneColors(Color(0xFF9D7BFF)) }

object Accents {
    val presets = listOf(
        "Violet" to Color(0xFF9D7BFF), "Green" to Color(0xFF1ED760), "Blue" to Color(0xFF3D8BFF), "Pink" to Color(0xFFFF4FA3),
        "Orange" to Color(0xFFFF7A1A), "Red" to Color(0xFFFF4D4D), "Teal" to Color(0xFF14C8B4), "Yellow" to Color(0xFFFFCC33),
        "Indigo" to Color(0xFF7B83FF), "Silver" to Color(0xFFD7D7DC),
    )

    fun parse(hex: String?): Color = runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(presets[0].second)
    fun hex(c: Color) = "#%02X%02X%02X".format((c.red * 255).toInt(), (c.green * 255).toInt(), (c.blue * 255).toInt())
}

object Type {
    val title1 = TextStyle(fontSize = 30.sp, fontWeight = FontWeight.Bold, letterSpacing = (-0.5).sp)
    val title2 = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.Bold)
    val section = TextStyle(fontSize = 20.sp, fontWeight = FontWeight.Bold)
    val body = TextStyle(fontSize = 15.sp)
    val bodyStrong = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
    val caption = TextStyle(fontSize = 13.sp)
    val label = TextStyle(fontSize = 11.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
}

@Composable
fun TuneTheme(accent: Color, content: @Composable () -> Unit) {
    val colors = TuneColors(accent)
    MaterialTheme(colorScheme = darkColorScheme(
        primary = accent, onPrimary = colors.onAccent, background = colors.base, surface = colors.base,
        surfaceVariant = colors.card, onSurface = colors.text, onBackground = colors.text, secondary = accent,
        surfaceContainer = colors.card, surfaceContainerHigh = colors.cardHover, surfaceContainerHighest = colors.cardHover,
    )) {
        CompositionLocalProvider(LocalTune provides colors, content = content)
    }
}
