package com.minirp.app.ui

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import android.system.Os
import android.system.OsConstants
import android.widget.Toast
import androidx.annotation.OptIn
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.media3.common.MediaItem
import androidx.media3.common.AudioAttributes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import coil3.compose.AsyncImage
import coil3.request.ImageRequest
import com.minirp.app.R
import com.minirp.app.data.AttachmentFile
import java.io.File
import kotlin.math.roundToInt
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import me.saket.telephoto.zoomable.coil3.ZoomableAsyncImage

@Composable
internal fun rememberAttachmentThumbnail(
    thumbnail: AttachmentThumbnail?,
    load: (suspend (AttachmentThumbnail) -> ByteArray)?,
): Bitmap? {
    if (thumbnail == null || load == null) return null
    val bitmap by produceState<Bitmap?>(null, thumbnail) {
        value = readPreview {
            val bytes = load(thumbnail)
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            if (bounds.outWidth in 1..1280 && bounds.outHeight in 1..1280)
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            else null
        }.getOrNull()
    }
    return bitmap
}

@Composable
internal fun AttachmentChip(
    name: String,
    file: AttachmentFile?,
    open: () -> Unit,
    modifier: Modifier = Modifier,
    preview: Bitmap? = null,
    remove: (() -> Unit)? = null,
    enabled: Boolean = true,
) {
    Surface(modifier, shape = MaterialTheme.shapes.medium, color = MaterialTheme.colorScheme.surfaceContainerLow) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Row(
                Modifier.widthIn(max = 226.dp).heightIn(min = 48.dp)
                    .clickable(enabled = enabled && file != null, onClickLabel = stringResource(R.string.preview_attachment, name),
                        role = Role.Button, onClick = open)
                    .padding(horizontal = 10.dp, vertical = 5.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                val square = Modifier.size(34.dp).clip(MaterialTheme.shapes.small)
                when {
                    preview != null -> Image(preview.asImageBitmap(), null, square, contentScale = ContentScale.Crop)
                    file?.mime?.startsWith("image/") == true ->
                        AsyncImage(file.uri, null, square, contentScale = ContentScale.Crop)
                    else -> Symbol(
                        if (file?.mime?.let { it.startsWith("video/") || it.startsWith("audio/") } == true) "play_arrow" else "browse",
                        size = 20,
                    )
                }
                Text(name, style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            if (remove != null) IconButton(onClick = remove, enabled = enabled) {
                Symbol("close", stringResource(R.string.remove_attachment, name), size = 20)
            }
        }
    }
}

@Composable
internal fun AttachmentPreviewDialog(file: AttachmentFile, close: () -> Unit) {
    val context = LocalContext.current
    val audioOnly = file.mime.startsWith("audio/")
    Dialog(onDismissRequest = close, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(
            Modifier.widthIn(max = if (audioOnly) 460.dp else 880.dp).fillMaxWidth()
                .then(if (audioOnly) Modifier.height(180.dp) else Modifier.fillMaxHeight(.90f))
                .padding(horizontal = 12.dp).testTag("attachment-preview"),
            shape = MaterialTheme.shapes.extraLarge,
        ) {
            Column {
                Row(
                    Modifier.fillMaxWidth().padding(start = 20.dp, end = 6.dp).heightIn(min = 64.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(file.name, Modifier.weight(1f), style = MaterialTheme.typography.titleMedium,
                        maxLines = 2, overflow = TextOverflow.Ellipsis)
                    IconButton(onClick = {
                        runCatching {
                            val intent = Intent(Intent.ACTION_VIEW).setDataAndType(file.openUri(context), file.mime)
                                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                            context.startActivity(intent)
                        }.onFailure {
                            Toast.makeText(context, R.string.attachment_preview_unavailable, Toast.LENGTH_SHORT).show()
                        }
                    }) { Symbol("arrow_forward", stringResource(R.string.open_attachment_external)) }
                    IconButton(onClick = close) { Symbol("close", stringResource(R.string.close)) }
                }
                Surface(
                    Modifier.weight(1f).fillMaxWidth().padding(start = 10.dp, end = 10.dp, bottom = 10.dp),
                    shape = MaterialTheme.shapes.large,
                    color = MaterialTheme.colorScheme.surfaceContainerLow,
                ) {
                    key(file.uri) {
                        when {
                            file.mime.startsWith("image/") -> ImagePreview(file)
                            file.mime.startsWith("video/") || file.mime.startsWith("audio/") -> MediaPreview(file)
                            file.mime == "application/pdf" -> PdfPreview(file)
                            file.mime.startsWith("text/") || file.mime in setOf("application/json", "application/xml") -> TextPreview(file)
                            else -> PreviewMessage(R.string.attachment_preview_unavailable)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun PreviewMessage(label: Int, loading: Boolean = false) {
    Box(Modifier.fillMaxSize().padding(14.dp), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
            if (loading) CircularProgressIndicator()
            Text(stringResource(label), style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
private fun ImagePreview(file: AttachmentFile) {
    val context = LocalContext.current
    var failed by remember { mutableStateOf(false) }
    var loaded by remember { mutableStateOf(false) }
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        if (failed) PreviewMessage(R.string.attachment_read_failed)
        else {
            val request = remember(file.uri) {
                ImageRequest.Builder(context).data(file.uri)
                    .listener(onSuccess = { _, _ -> loaded = true }, onError = { _, _ -> failed = true }).build()
            }
            ZoomableAsyncImage(model = request, contentDescription = file.name, modifier = Modifier.fillMaxSize(),
                contentScale = ContentScale.Fit)
            if (!loaded) CircularProgressIndicator()
        }
    }
}

@OptIn(UnstableApi::class)
@Composable
private fun MediaPreview(file: AttachmentFile) {
    val context = LocalContext.current
    val audioOnly = file.mime.startsWith("audio/")
    var failed by remember { mutableStateOf(false) }
    val player = remember(file.uri) {
        ExoPlayer.Builder(context)
            .setAudioAttributes(AudioAttributes.DEFAULT, true)
            .setHandleAudioBecomingNoisy(true)
            .build()
    }
    DisposableEffect(player) {
        val listener = object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) { failed = true }
        }
        player.addListener(listener)
        player.setMediaItem(MediaItem.fromUri(file.uri))
        player.prepare()
        onDispose {
            player.removeListener(listener)
            player.release()
        }
    }
    LifecycleEventEffect(Lifecycle.Event.ON_STOP) { player.pause() }
    if (failed) PreviewMessage(R.string.attachment_read_failed)
    else if (audioOnly) AttachmentAudioControls(player)
    else AndroidView(
        factory = { PlayerView(it).apply {
            this.player = player
            setShowPreviousButton(false)
            setShowNextButton(false)
        } },
        modifier = Modifier.fillMaxSize(),
        onRelease = { it.player = null },
    )
}

private data class PreviewText(val text: String, val truncated: Boolean)

@Composable
private fun TextPreview(file: AttachmentFile) {
    val context = LocalContext.current
    val result by produceState<Result<PreviewText>?>(null, file.uri) {
        value = readPreview {
            checkNotNull(context.contentResolver.openInputStream(file.uri)).use { input ->
                val limit = 256 * 1024
                val bytes = ByteArray(limit + 1)
                var size = 0
                while (size < bytes.size) {
                    ensureActive()
                    val count = input.read(bytes, size, bytes.size - size)
                    if (count <= 0) break
                    size += count
                }
                val first = bytes[0].toInt() and 255
                val second = bytes[1].toInt() and 255
                val encoding = when {
                    first == 255 && second == 254 -> Charsets.UTF_16LE
                    first == 254 && second == 255 -> Charsets.UTF_16BE
                    else -> Charsets.UTF_8
                }
                PreviewText(String(bytes, 0, minOf(size, limit), encoding).removePrefix("\uFEFF"), size > limit)
            }
        }
    }
    val contents = result?.getOrNull()
    when {
        result == null -> PreviewMessage(R.string.attachment_loading, loading = true)
        contents == null -> PreviewMessage(R.string.attachment_read_failed)
        else -> SelectionContainer {
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
                Text(contents.text, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.testTag("attachment-text"))
                if (contents.truncated) Text(stringResource(R.string.attachment_preview_truncated),
                    color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 12.dp))
            }
        }
    }
}

private data class PreviewPage(val image: Bitmap, val pageCount: Int)

@Composable
private fun PdfPreview(file: AttachmentFile) {
    val context = LocalContext.current
    var index by rememberSaveable { mutableIntStateOf(0) }
    val result by produceState<Result<PreviewPage>?>(null, file.uri, index) {
        value = readPreview {
            suspend fun render(descriptor: ParcelFileDescriptor): PreviewPage {
                val pdf = try { PdfRenderer(descriptor) } catch (failure: Exception) {
                    descriptor.close()
                    throw failure
                }
                return pdf.use {
                    pdf.openPage(index).use { page ->
                        val scale = 1800f / maxOf(page.width, page.height)
                        val bitmap = Bitmap.createBitmap((scale * page.width).roundToInt().coerceAtLeast(1),
                            (scale * page.height).roundToInt().coerceAtLeast(1), Bitmap.Config.ARGB_8888)
                        bitmap.eraseColor(Color.WHITE)
                        page.render(bitmap, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                        PreviewPage(bitmap, pdf.pageCount)
                    }
                }
            }
            val resolver = context.contentResolver
            val descriptor = checkNotNull(resolver.openFileDescriptor(file.uri, "r"))
            if (runCatching { Os.lseek(descriptor.fileDescriptor, 0, OsConstants.SEEK_CUR) }.isSuccess) {
                render(descriptor)
            } else {
                descriptor.close()
                val copy = File.createTempFile("attachment-pdf-", ".pdf", context.cacheDir)
                try {
                    checkNotNull(resolver.openInputStream(file.uri)).use { input ->
                        copy.outputStream().use { output ->
                            val buffer = ByteArray(32 * 1024)
                            var size = 0L
                            while (true) {
                                ensureActive()
                                val count = input.read(buffer)
                                if (count < 0) break
                                size += count
                                check(size <= 128L * 1024 * 1024)
                                output.write(buffer, 0, count)
                            }
                        }
                    }
                    render(ParcelFileDescriptor.open(copy, ParcelFileDescriptor.MODE_READ_ONLY))
                } finally { copy.delete() }
            }
        }
    }
    val page = result?.getOrNull()
    when {
        result == null -> PreviewMessage(R.string.attachment_loading, loading = true)
        page == null -> PreviewMessage(R.string.attachment_read_failed)
        else -> Column(Modifier.fillMaxSize()) {
            Image(page.image.asImageBitmap(), stringResource(R.string.preview_pdf_page, index + 1, page.pageCount),
                Modifier.weight(1f).fillMaxWidth().padding(8.dp), contentScale = ContentScale.Fit)
            if (page.pageCount > 1) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically) {
                val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
                IconButton(onClick = { index-- }, enabled = index > 0) {
                    Symbol(if (rtl) "chevron_right" else "chevron_left", stringResource(R.string.preview_previous))
                }
                Text(stringResource(R.string.preview_pdf_page, index + 1, page.pageCount), style = MaterialTheme.typography.labelLarge)
                IconButton(onClick = { index++ }, enabled = index + 1 < page.pageCount) {
                    Symbol(if (rtl) "chevron_left" else "chevron_right", stringResource(R.string.preview_next))
                }
            }
        }
    }
}

private suspend fun <T> readPreview(block: suspend kotlinx.coroutines.CoroutineScope.() -> T): Result<T> =
    try { Result.success(withContext(Dispatchers.IO, block)) }
    catch (cancelled: CancellationException) { throw cancelled }
    catch (failure: Exception) { Result.failure(failure) }
