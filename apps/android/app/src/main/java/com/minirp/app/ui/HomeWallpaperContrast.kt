package com.minirp.app.ui

import android.graphics.Bitmap
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.structuralEqualityPolicy
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.rememberGraphicsLayer
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInRoot
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.withContext
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.pow
import kotlin.math.roundToInt

internal class HomeWallpaperContrast {
    var bounds by mutableStateOf(Rect.Zero)
    var samples by mutableStateOf<WallpaperSamples?>(null)
    var captionLines by mutableStateOf(emptyList<Rect>())
    var nameLines by mutableStateOf(emptyList<Rect>())

    val ink by derivedStateOf(structuralEqualityPolicy()) {
        samples?.ink(bounds, captionLines + nameLines)
    }
}

internal data class WallpaperInk(val color: Color, val needsHalo: Boolean = false)

private val linearRgb = FloatArray(256) { channel ->
    val s = channel / 255f
    if (s <= .04045f) s / 12.92f else ((s + .055f) / 1.055f).pow(2.4f)
}

internal class WallpaperSamples(bitmap: Bitmap) {
    private val width = bitmap.width
    private val height = bitmap.height
    private val luminance = FloatArray(width * height)

    init {
        val readable = if (bitmap.config == Bitmap.Config.HARDWARE) bitmap.copy(Bitmap.Config.ARGB_8888, false) else bitmap
        val pixels = IntArray(luminance.size)
        readable.getPixels(pixels, 0, width, 0, 0, width, height)
        pixels.forEachIndexed { index, pixel ->
            luminance[index] = .2126f * linearRgb[(pixel ushr 16) and 255] +
                .7152f * linearRgb[(pixel ushr 8) and 255] + .0722f * linearRgb[pixel and 255]
        }
        if (readable !== bitmap) readable.recycle()
    }

    fun ink(bounds: Rect, lines: List<Rect>): WallpaperInk? {
        if (bounds.isEmpty) return null
        var blackFailure = 0f
        var whiteFailure = 0f
        var advantage = 0f
        var sampled = false
        for (line in lines) {
            if (line.isEmpty || !line.overlaps(bounds)) continue
            sampled = true
            val left = floor((line.left - bounds.left) * width / bounds.width).toInt().coerceIn(0, width - 1)
            val right = ceil((line.right - bounds.left) * width / bounds.width).toInt().coerceIn(left + 1, width)
            val top = floor((line.top - bounds.top) * height / bounds.height).toInt().coerceIn(0, height - 1)
            val bottom = ceil((line.bottom - bounds.top) * height / bounds.height).toInt().coerceIn(top + 1, height)
            var blackMisses = 0
            var whiteMisses = 0
            for (y in top until bottom) for (x in left until right) {
                val level = luminance[y * width + x] + .05f
                val black = level / .05f
                val white = 1.05f / level
                if (black < 3f) blackMisses++
                if (white < 3f) whiteMisses++
                advantage += black - white
            }
            val area = ((right - left) * (bottom - top)).toFloat()
            blackFailure = maxOf(blackFailure, blackMisses / area)
            whiteFailure = maxOf(whiteFailure, whiteMisses / area)
        }
        if (!sampled) return null
        // Protect the least readable line first. Use average contrast as the tie-breaker.
        val black = blackFailure < whiteFailure || blackFailure == whiteFailure && advantage >= 0f
        return WallpaperInk(if (black) Color.Black else Color.White, minOf(blackFailure, whiteFailure) > .10f)
    }
}

@Composable
internal fun sampleHomeWallpaper(
    background: PresetBackground,
    contrast: HomeWallpaperContrast,
    surface: Color,
    running: Boolean,
): Modifier {
    val capture = rememberGraphicsLayer()
    val frameTime = backdropFrameTime(background, running, 83L)
    val requests = remember(background, surface) { Channel<Unit>(Channel.CONFLATED) }
    val last = remember(requests) { object { var time = Long.MIN_VALUE; var size = IntSize.Zero } }
    LaunchedEffect(requests) {
        for (ignored in requests) {
            val bitmap = try {
                capture.toImageBitmap().asAndroidBitmap()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: IllegalStateException) {
                continue
            }
            contrast.samples = withContext(Dispatchers.Default) {
                try { WallpaperSamples(bitmap) } finally { bitmap.recycle() }
            }
        }
    }
    return Modifier.onGloballyPositioned {
        val origin = it.positionInRoot()
        contrast.bounds = Rect(origin.x, origin.y, origin.x + it.size.width, origin.y + it.size.height)
    }.drawWithContent {
        val frame = frameTime.longValue
        val fullSize = IntSize(size.width.roundToInt(), size.height.roundToInt())
        if (fullSize.width > 0 && fullSize.height > 0 && (last.time != frame || last.size != fullSize)) {
            last.time = frame
            last.size = fullSize
            val ratio = minOf(1f, 192f / maxOf(size.width, size.height))
            val sampleSize = IntSize(
                (size.width * ratio).roundToInt().coerceAtLeast(1),
                (size.height * ratio).roundToInt().coerceAtLeast(1),
            )
            val scaleX = sampleSize.width / size.width
            val scaleY = sampleSize.height / size.height
            capture.record(size = sampleSize) {
                drawRect(surface)
                scale(scaleX, scaleY, pivot = Offset.Zero) { this@drawWithContent.drawContent() }
            }
            requests.trySend(Unit)
        }
        drawContent()
    }
}

@Composable
internal fun HomeWallpaperText(
    text: String,
    contrast: HomeWallpaperContrast?,
    style: TextStyle,
    modifier: Modifier = Modifier,
    name: Boolean = false,
    maxLines: Int = Int.MAX_VALUE,
    overflow: TextOverflow = TextOverflow.Clip,
) {
    if (contrast == null) {
        Text(text, modifier, style = style, maxLines = maxLines, overflow = overflow)
        return
    }
    var origin by remember { mutableStateOf(Offset.Zero) }
    var lines by remember(text) { mutableStateOf(emptyList<Rect>()) }
    val positioned = lines.map { it.translate(origin) }
    SideEffect {
        if (name) contrast.nameLines = positioned else contrast.captionLines = positioned
    }
    val ink = contrast.ink ?: WallpaperInk(
        if (MaterialTheme.colorScheme.surface.luminance() >= .17913f) Color.Black else Color.White,
    )
    val halo = if (ink.needsHalo) Shadow(
        color = (if (ink.color == Color.Black) Color.White else Color.Black).copy(alpha = .80f),
        offset = Offset.Zero,
        blurRadius = with(LocalDensity.current) { 24.dp.toPx() },
    ) else null
    Text(
        text,
        modifier.onGloballyPositioned { origin = it.positionInRoot() },
        style = style.copy(color = ink.color, fontWeight = FontWeight.Black, shadow = halo),
        maxLines = maxLines,
        overflow = overflow,
        onTextLayout = { layout ->
            lines = List(layout.lineCount) { line ->
                Rect(layout.getLineLeft(line), layout.getLineTop(line), layout.getLineRight(line), layout.getLineBottom(line))
            }
        },
    )
}
