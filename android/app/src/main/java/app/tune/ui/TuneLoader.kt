package app.tune.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import kotlin.math.PI
import kotlin.math.sin

/**
 * Tune's loading animation: the six bars of the logo bouncing like an equalizer, on a transparent background.
 * Used everywhere something is loading instead of a spinner.
 */
@Composable
fun TuneLoader(modifier: Modifier = Modifier, size: Dp = 36.dp, color: Color = LocalTune.current.accent) {
    val t by rememberInfiniteTransition(label = "tune-loader")
        .animateFloat(0f, 1f, infiniteRepeatable(tween(1100, easing = LinearEasing), RepeatMode.Restart), label = "phase")
    Canvas(modifier.size(size)) {
        val bars = 6
        val gap = this.size.width * 0.07f
        val w = (this.size.width - gap * (bars - 1)) / bars
        val h = this.size.height
        for (i in 0 until bars) {
            // each bar its own phase, so the wave travels left to right; never fully flat
            val wave = (sin(2 * PI * (t - i * 0.14)) + 1) / 2
            val bh = h * (0.22f + 0.78f * wave.toFloat())
            drawRoundRect(color, topLeft = Offset(i * (w + gap), (h - bh) / 2), size = Size(w, bh), cornerRadius = CornerRadius(w / 2, w / 2))
        }
    }
}
