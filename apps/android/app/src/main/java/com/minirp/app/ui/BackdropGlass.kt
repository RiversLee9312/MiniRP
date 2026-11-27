package com.minirp.app.ui

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.ColorMatrix
import android.graphics.ColorMatrixColorFilter
import android.graphics.Paint
import android.graphics.RenderEffect
import android.graphics.Shader
import android.os.Build
import android.view.Choreographer
import androidx.annotation.RequiresApi
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.LongState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.graphics.asComposeRenderEffect
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.layer.drawLayer
import androidx.compose.ui.graphics.rememberGraphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.roundToIntSize
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.withContext

private const val SOFTWARE_DOWNSCALE = 8

// systemMaterial from react-native-blur (MIT, see assets/licenses/react-native-blur.txt).
private class BackdropMaterial(dark: Boolean) {
    private val plate = if (dark) floatArrayOf(.16f, .26f, .10f, .10f)
        else floatArrayOf(.90f, .83f, .925f, .815f)
    private val brightness = if (dark) 0f else .10f
    val fallback = (if (dark) Color(0xFF1C1C1E) else Color(0xFFF5F5F7)).copy(alpha = .8875f)
    val colorFilter: ColorMatrixColorFilter

    init {
        // Fit the luminance plate's four samples at uniformly spaced levels from black to white.
        val slope = .3f * (-3 * plate[0] - plate[1] + plate[2] + 3 * plate[3])
        val intercept = plate.average().toFloat() - slope / 2
        val amount = .75f
        val neutral = amount * slope
        val r = neutral * .213f
        val g = neutral * .715f
        val b = neutral * .072f
        val offset = (amount * intercept + brightness) * 255
        val keep = 1 - amount
        val luminance = ColorMatrix(floatArrayOf(
            keep + r, g, b, 0f, offset,
            r, keep + g, b, 0f, offset,
            r, g, keep + b, 0f, offset,
            0f, 0f, 0f, 1f, 0f,
        ))
        colorFilter = ColorMatrixColorFilter(ColorMatrix().apply {
            setSaturation(1.5f)
            postConcat(luminance)
        })
    }

    @RequiresApi(31)
    fun effect(sigma: Float): RenderEffect {
        val radius = (sigma - .5f) / .57735f
        return RenderEffect.createChainEffect(
            RenderEffect.createBlurEffect(radius, radius, Shader.TileMode.CLAMP),
            RenderEffect.createColorFilterEffect(colorFilter),
        )
    }
}

@Composable
internal fun backdropFrameTime(
    background: PresetBackground,
    running: Boolean,
    intervalMillis: Long,
): LongState {
    val animated = background.mime == "image/gif" || background.mime.startsWith("video/")
    val frameTime = remember { mutableLongStateOf(0L) }
    DisposableEffect(animated, running, intervalMillis) {
        if (!animated || !running) return@DisposableEffect onDispose {}
        val choreographer = Choreographer.getInstance()
        val callback = object : Choreographer.FrameCallback {
            override fun doFrame(timeNanos: Long) {
                frameTime.longValue = timeNanos
                choreographer.postFrameCallbackDelayed(this, intervalMillis)
            }
        }
        choreographer.postFrameCallback(callback)
        onDispose { choreographer.removeFrameCallback(callback) }
    }
    return frameTime
}

@Composable
internal fun backdropGlass(background: PresetBackground, dark: Boolean, running: Boolean): Modifier {
    val material = remember(dark) { BackdropMaterial(dark) }
    val sigma = with(LocalDensity.current) { 30.dp.toPx() }
    val legacy = Build.VERSION.SDK_INT < 31
    val frameTime = backdropFrameTime(background, running, if (legacy) 66L else 33L)
    if (legacy) {
        return softwareBackdropGlass(background, material, sigma) { frameTime.longValue }
    }
    val source = rememberGraphicsLayer()
    val first = rememberGraphicsLayer()
    val second = if (Build.VERSION.SDK_INT < 33) rememberGraphicsLayer() else first
    val effect = remember(material, sigma) { material.effect(sigma).asComposeRenderEffect() }
    val turn = remember { object { var useFirst = true } }
    return Modifier.drawWithContent {
        // A TextureView may present a new frame without invalidating its Compose ancestor.
        frameTime.longValue
        source.record { this@drawWithContent.drawContent() }
        val glass = if (turn.useFirst) first else second
        turn.useFirst = !turn.useFirst
        // Android 12 may reuse a RenderEffect's first frame when its node stays the same.
        glass.renderEffect = effect
        glass.record { drawLayer(source) }
        drawLayer(glass)
    }
}

@Composable
private fun softwareBackdropGlass(
    background: PresetBackground,
    material: BackdropMaterial,
    sigma: Float,
    frameTime: () -> Long,
): Modifier {
    val capture = rememberGraphicsLayer()
    val requests = remember(background, material, sigma) { Channel<Unit>(Channel.CONFLATED) }
    val lastFrame = remember(requests) { object { var time = Long.MIN_VALUE } }
    var softened by remember(requests) { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(requests) {
        for (request in requests) {
            val bitmap = try {
                capture.toImageBitmap().asAndroidBitmap()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: IllegalStateException) {
                continue
            }
            softened = withContext(Dispatchers.Default) {
                softenBackdrop(bitmap, (sigma / SOFTWARE_DOWNSCALE).toInt().coerceIn(1, 24), material.colorFilter)
            }
        }
    }
    return Modifier.drawWithContent {
        val time = frameTime()
        val width = (size.width.toInt() / SOFTWARE_DOWNSCALE).coerceAtLeast(1)
        val height = (size.height.toInt() / SOFTWARE_DOWNSCALE).coerceAtLeast(1)
        val sampleSize = IntSize(width, height)
        if (lastFrame.time != time || capture.size != sampleSize) {
            lastFrame.time = time
            val x = width / size.width.coerceAtLeast(1f)
            val y = height / size.height.coerceAtLeast(1f)
            capture.record(size = sampleSize) {
                scale(x, y, pivot = Offset.Zero) { this@drawWithContent.drawContent() }
            }
            requests.trySend(Unit)
        }
        val frame = softened
        if (frame == null) {
            drawContent()
            drawRect(material.fallback)
        } else {
            drawImage(frame, dstSize = size.roundToIntSize(), filterQuality = FilterQuality.Low)
        }
    }
}

private fun softenBackdrop(bitmap: Bitmap, radius: Int, filter: ColorMatrixColorFilter): ImageBitmap {
    val width = bitmap.width
    val height = bitmap.height
    val result = bitmap.copy(Bitmap.Config.ARGB_8888, true)
    val pixels = IntArray(width * height)
    val scratch = IntArray(pixels.size)
    result.getPixels(pixels, 0, width, 0, 0, width, height)
    // Three separable box passes approximate the Gaussian on the reduced backdrop.
    repeat(3) {
        averageBackdrop(pixels, scratch, height, width, width, 1, radius)
        averageBackdrop(scratch, pixels, width, height, 1, width, radius)
    }
    result.setPixels(pixels, 0, width, 0, 0, width, height)
    val tinted = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    Canvas(tinted).drawBitmap(result, 0f, 0f, Paint().apply { colorFilter = filter })
    result.recycle()
    return tinted.asImageBitmap()
}

private fun averageBackdrop(
    input: IntArray,
    output: IntArray,
    lines: Int,
    length: Int,
    lineStride: Int,
    pixelStride: Int,
    radius: Int,
) {
    val window = 2 * radius + 1
    repeat(lines) { row ->
        val start = row * lineStride
        fun pixel(index: Int) = input[start + index.coerceIn(0, length - 1) * pixelStride]
        var alpha = 0
        var red = 0
        var green = 0
        var blue = 0
        for (index in -radius..radius) {
            val color = pixel(index)
            alpha += android.graphics.Color.alpha(color)
            red += android.graphics.Color.red(color)
            green += android.graphics.Color.green(color)
            blue += android.graphics.Color.blue(color)
        }
        repeat(length) { index ->
            output[start + index * pixelStride] = android.graphics.Color.argb(
                alpha / window, red / window, green / window, blue / window,
            )
            val entering = pixel(index + radius + 1)
            val leaving = pixel(index - radius)
            alpha += android.graphics.Color.alpha(entering) - android.graphics.Color.alpha(leaving)
            red += android.graphics.Color.red(entering) - android.graphics.Color.red(leaving)
            green += android.graphics.Color.green(entering) - android.graphics.Color.green(leaving)
            blue += android.graphics.Color.blue(entering) - android.graphics.Color.blue(leaving)
        }
    }
}
