package com.minirp.app.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.ImageDecoder
import android.graphics.Matrix
import android.graphics.Movie
import android.graphics.RenderEffect
import android.graphics.Shader
import android.graphics.SurfaceTexture
import android.graphics.drawable.Animatable
import android.graphics.drawable.Drawable
import android.media.MediaMetadataRetriever
import android.media.MediaPlayer
import android.os.Build
import android.os.SystemClock
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageView
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
import androidx.palette.graphics.Palette
import java.io.File
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.withContext

data class PresetAppearance(
    val backgroundUrl: String = "",
    val blurBackground: Boolean = true,
    val themeColor: Int? = null,
)

internal data class PresetBackground(val file: File, val mime: String, val poster: Bitmap, val seedColor: Color)

@Composable
internal fun rememberPresetBackground(model: ChatViewModel, state: ChatState): PresetBackground? {
    val url = state.appearance.backgroundUrl
    if (state.preset.isEmpty() || url.isEmpty()) return null
    val packageName = url.substringAfter("://assets/", "").substringBefore('/')
    val owner = state.installedPlugins.firstOrNull {
        it.presetId == state.preset && it.availableIn(state.preset) && it.packageName == packageName
    } ?: return null
    val cache = LocalContext.current.cacheDir
    var result by remember(model, state.preset, url, owner.generation) { mutableStateOf<PresetBackground?>(null) }
    LaunchedEffect(model, state.preset, url, owner.generation) {
        var file: File? = null
        try {
            result = withContext(Dispatchers.IO) {
                val source = File.createTempFile("preset-background-", ".media", cache).also { file = it }
                val mime = model.savePresetBackground(state.preset, url, source).lowercase()
                decodePresetBackground(source, mime)
            }
            awaitCancellation()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (_: Exception) {
            // A missing, disabled or undecodable Asset leaves the standard conversation surface visible.
        } finally {
            withContext(NonCancellable + Dispatchers.IO) { file?.delete() }
        }
    }
    return result
}

internal fun decodePresetBackground(file: File, mime: String): PresetBackground {
    val poster = checkNotNull(if (mime.startsWith("video/")) videoPoster(file) else imagePoster(file))
    val palette = Palette.from(poster).maximumColorCount(16).generate()
    val seed = Color((palette.vibrantSwatch ?: palette.dominantSwatch)?.rgb
        ?: poster.getPixel(poster.width / 2, poster.height / 2))
    return PresetBackground(file, mime, poster, seed)
}

private const val MAX_POSTER_EDGE = 1600

private fun imagePoster(file: File): Bitmap? {
    if (Build.VERSION.SDK_INT >= 28) return ImageDecoder.decodeBitmap(ImageDecoder.createSource(file)) { decoder, info, _ ->
        decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
        val scale = maxOf(info.size.width, info.size.height).toFloat() / MAX_POSTER_EDGE
        if (scale > 1) decoder.setTargetSize(
            (info.size.width / scale).toInt().coerceAtLeast(1), (info.size.height / scale).toInt().coerceAtLeast(1),
        )
    }
    val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeFile(file.path, options)
    options.inJustDecodeBounds = false
    options.inSampleSize = 1
    while (maxOf(options.outWidth, options.outHeight) / options.inSampleSize > MAX_POSTER_EDGE) options.inSampleSize *= 2
    return BitmapFactory.decodeFile(file.path, options)
}

private fun videoPoster(file: File): Bitmap? {
    val video = MediaMetadataRetriever()
    try {
        video.setDataSource(file.path)
        val duration = video.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0
        val timeUs = duration.coerceIn(0, 2000) * 500
        return if (Build.VERSION.SDK_INT >= 27) {
            video.getScaledFrameAtTime(timeUs, MediaMetadataRetriever.OPTION_CLOSEST, MAX_POSTER_EDGE, MAX_POSTER_EDGE)
        } else video.getFrameAtTime(timeUs, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
    } finally { video.release() }
}

@Composable
internal fun PresetBackdrop(background: PresetBackground?, conversation: Boolean, blur: Boolean, modifier: Modifier) {
    if (background == null) return
    val dark = LocalMiniRPDarkTheme.current
    val radius = if (conversation && blur) 20.dp else 0.dp
    val running = LocalLifecycleOwner.current.lifecycle.currentStateAsState().value.isAtLeast(Lifecycle.State.RESUMED)
    Box(modifier.clipToBounds().testTag("preset-background")) {
        Image(
            remember(background) { background.poster.asImageBitmap() }, null,
            Modifier.matchParentSize().blur(radius), contentScale = ContentScale.Crop,
        )
        val pixels = with(LocalDensity.current) { radius.toPx() }
        when {
            background.mime.startsWith("video/") ->
                AndroidView(
                    factory = { context -> VideoBackground(context) },
                    modifier = Modifier.matchParentSize(),
                    onReset = null,
                    onRelease = VideoBackground::release,
                    update = { it.update(background.file, running, pixels) },
                )
            background.mime == "image/gif" -> GifBackground(background.file, running, pixels, Modifier.matchParentSize())
        }
        if (conversation) Box(Modifier.matchParentSize().background(
            if (dark) Color.Black.copy(alpha = 0.30f) else Color.White.copy(alpha = 0.32f),
        ))
    }
}

private fun View.backgroundBlur(radius: Float) {
    if (Build.VERSION.SDK_INT >= 31) setRenderEffect(
        if (radius > 0f) RenderEffect.createBlurEffect(radius, radius, Shader.TileMode.CLAMP) else null,
    )
}

@Composable
private fun GifBackground(file: File, running: Boolean, radius: Float, modifier: Modifier) {
    if (Build.VERSION.SDK_INT < 28) {
        LegacyGifBackground(file, running, radius, modifier)
        return
    }
    var image by remember(file) { mutableStateOf<Drawable?>(null) }
    LaunchedEffect(file) {
        image = withContext(Dispatchers.IO) {
            runCatching {
                ImageDecoder.decodeDrawable(ImageDecoder.createSource(file)) { decoder, info, _ ->
                    val scale = maxOf(info.size.width, info.size.height).toFloat() / MAX_POSTER_EDGE
                    if (scale > 1) decoder.setTargetSize(
                        (info.size.width / scale).toInt().coerceAtLeast(1), (info.size.height / scale).toInt().coerceAtLeast(1),
                    )
                }
            }.getOrNull()
        }
    }
    AndroidView(
        factory = { ImageView(it).apply { scaleType = ImageView.ScaleType.CENTER_CROP } },
        modifier = modifier,
        onReset = null,
        onRelease = { (it.drawable as? Animatable)?.stop(); it.setImageDrawable(null) },
        update = { view ->
            if (view.drawable !== image) {
                (view.drawable as? Animatable)?.stop()
                view.setImageDrawable(image)
                (image as? android.graphics.drawable.AnimatedImageDrawable)?.repeatCount =
                    android.graphics.drawable.AnimatedImageDrawable.REPEAT_INFINITE
            }
            (image as? Animatable)?.let { if (running) it.start() else it.stop() }
            view.backgroundBlur(radius)
        },
    )
}

@Suppress("DEPRECATION")
@Composable
private fun LegacyGifBackground(file: File, running: Boolean, radius: Float, modifier: Modifier) {
    var movie by remember(file) { mutableStateOf<Movie?>(null) }
    LaunchedEffect(file) { movie = withContext(Dispatchers.IO) { Movie.decodeFile(file.path) } }
    AndroidView(factory = ::MovieBackground, modifier = modifier) {
        it.movie = movie
        it.running = running
        it.backgroundBlur(radius)
        it.invalidate()
    }
}

@Suppress("DEPRECATION")
private class MovieBackground(context: Context) : View(context) {
    var movie: Movie? = null
    var running = false
    private var start = 0L

    override fun onDraw(canvas: Canvas) {
        val gif = movie ?: return
        if (gif.width() < 1 || gif.height() < 1) return
        val now = SystemClock.uptimeMillis()
        if (start == 0L) start = now
        gif.setTime(((now - start) % gif.duration().coerceAtLeast(1)).toInt())
        val scale = maxOf(width.toFloat() / gif.width(), height.toFloat() / gif.height())
        canvas.save()
        canvas.translate((width - gif.width() * scale) / 2, (height - gif.height() * scale) / 2)
        canvas.scale(scale, scale)
        gif.draw(canvas, 0f, 0f)
        canvas.restore()
        if (running) postInvalidateOnAnimation()
    }
}

private class VideoBackground(context: Context) : FrameLayout(context), TextureView.SurfaceTextureListener {
    private val texture = TextureView(context).apply {
        isOpaque = false
        surfaceTextureListener = this@VideoBackground
    }
    private var file: File? = null
    private var player: MediaPlayer? = null
    private var prepared = false
    private var running = false

    init { addView(texture, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT)) }

    fun update(source: File, play: Boolean, radius: Float) {
        running = play
        backgroundBlur(radius)
        if (file != source) {
            file = source
            release()
            texture.surfaceTexture?.takeIf { texture.isAvailable }?.let(::prepare)
        }
        updatePlayback()
    }

    private fun prepare(surface: SurfaceTexture) {
        val source = file ?: return
        val active = MediaPlayer()
        player = active
        try {
            active.setDataSource(source.path)
            val output = Surface(surface)
            try { active.setSurface(output) } finally { output.release() }
            active.setOnPreparedListener {
                if (player == it) {
                    prepared = true
                    it.isLooping = true
                    it.setVolume(0f, 0f)
                    fillTexture(it.videoWidth, it.videoHeight)
                    updatePlayback()
                }
            }
            active.setOnVideoSizeChangedListener { sourcePlayer, width, height ->
                if (player == sourcePlayer) fillTexture(width, height)
            }
            active.setOnErrorListener { failed, _, _ ->
                if (player == failed) release()
                true
            }
            active.prepareAsync()
        } catch (_: Exception) { release() }
    }

    private fun updatePlayback() {
        val active = player?.takeIf { prepared } ?: return
        if (running && !active.isPlaying) active.start()
        if (!running && active.isPlaying) active.pause()
    }

    private fun fillTexture(width: Int, height: Int) {
        if (width < 1 || height < 1 || texture.width < 1 || texture.height < 1) return
        val aspect = (width.toFloat() / height) / (texture.width.toFloat() / texture.height)
        texture.setTransform(Matrix().apply {
            setScale(maxOf(1f, aspect), maxOf(1f, 1f / aspect), texture.width / 2f, texture.height / 2f)
        })
    }

    fun release() {
        prepared = false
        player?.release()
        player = null
    }

    override fun onSurfaceTextureAvailable(surface: SurfaceTexture, width: Int, height: Int) = prepare(surface)
    override fun onSurfaceTextureSizeChanged(surface: SurfaceTexture, width: Int, height: Int) {
        player?.takeIf { prepared }?.let { fillTexture(it.videoWidth, it.videoHeight) }
    }
    override fun onSurfaceTextureDestroyed(surface: SurfaceTexture): Boolean { release(); return true }
    override fun onSurfaceTextureUpdated(surface: SurfaceTexture) = Unit
}
