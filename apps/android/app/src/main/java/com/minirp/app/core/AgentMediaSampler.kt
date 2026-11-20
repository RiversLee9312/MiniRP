package com.minirp.app.core

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.graphics.pdf.PdfDocument
import android.graphics.pdf.PdfRenderer
import android.media.AudioFormat
import android.media.ExifInterface
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMetadataRetriever
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.util.UnstableApi
import androidx.media3.transformer.Composition
import androidx.media3.transformer.ExportException
import androidx.media3.transformer.ExportResult
import androidx.media3.transformer.Transformer
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import org.json.JSONObject

internal object AgentMediaSampler {
    suspend fun sample(directory: File, event: JSONObject, context: Context? = null): JSONObject {
        val source = file(directory, event.getString("source"), "asset-source-")
        val output = file(directory, event.getString("output"), "asset-sample-")
        check(source.isFile && !output.exists()) { "Invalid media sample files" }
        when (event.getString("mode")) {
            "pdf_metadata" -> return pdfMetadata(source)
            "image_metadata" -> return imageMetadata(source)
            "video_metadata" -> return videoMetadata(source)
        }
        val start = event.optLong("start_ms").coerceAtLeast(0)
        val duration = event.optLong("duration_ms", 10_000).coerceIn(100, 30_000)
        val mime =
            when (event.getString("mode")) {
                "pdf_page" -> {
                    renderPdf(source, output, event.optInt("page", 1))
                    "image/png"
                }
                "video_frame" -> {
                    renderVideo(source, output, start)
                    "image/png"
                }
                "audio_clip" -> {
                    renderAudio(source, output, start, duration)
                    "audio/wav"
                }
                "image_decode" -> return decodeImage(source, output)
                "image_encode" -> encodeImage(source, output, event.optString("format"), event.optInt("quality", 90))
                "pdf_create_page" -> {
                    createPdfPage(source, output)
                    "application/pdf"
                }
                "video_encode" -> {
                    encodeVideo(checkNotNull(context), source, output, start, duration)
                    "video/mp4"
                }
                else -> error("Unknown asset sample mode")
            }
        return JSONObject().put("mime", mime)
    }

    private fun file(directory: File, id: String, prefix: String): File {
        require(id.startsWith(prefix) && id.removePrefix(prefix).matches(Regex("[a-zA-Z0-9_-]+")))
        return directory.resolve(id)
    }

    private fun pdfMetadata(source: File): JSONObject =
        withPdf(source) { renderer ->
            JSONObject()
                .put("page_count", renderer.pageCount)
                .put("scales_for_printing", renderer.shouldScaleForPrinting())
                .apply {
                    if (renderer.pageCount > 0) renderer.openPage(0).use {
                        put("first_page_width_points", it.width)
                        put("first_page_height_points", it.height)
                    }
                    if (Build.VERSION.SDK_INT >= 35) {
                        put("linearized", renderer.documentLinearizationType == PdfRenderer.DOCUMENT_LINEARIZED_TYPE_LINEARIZED)
                        put("form_type", renderer.pdfFormType)
                    }
                }
        }

    private fun imageMetadata(source: File): JSONObject {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(source.path, bounds)
        check(bounds.outWidth > 0 && bounds.outHeight > 0) { "Image metadata is unavailable" }
        val orientation = runCatching {
            ExifInterface(source.path).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_UNDEFINED)
        }.getOrNull()
        return JSONObject().put("width", bounds.outWidth).put("height", bounds.outHeight)
            .put("detected_mime", bounds.outMimeType)
            .put("orientation", orientation?.takeUnless { it == ExifInterface.ORIENTATION_UNDEFINED })
    }

    private inline fun <T> withBitmap(source: File, block: (Bitmap) -> T): T {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(source.path, bounds)
        val longest = maxOf(bounds.outWidth, bounds.outHeight)
        check(bounds.outWidth > 0 && bounds.outHeight > 0) { "Unable to decode image" }
        var divisor = 1
        while (longest / divisor > 2048) divisor *= 2
        val options = BitmapFactory.Options().apply { inSampleSize = divisor; inPreferredConfig = Bitmap.Config.ARGB_8888 }
        val bitmap = checkNotNull(BitmapFactory.decodeFile(source.path, options)) { "Unable to decode image" }
        return try { block(bitmap) } finally { bitmap.recycle() }
    }

    private suspend fun decodeImage(source: File, output: File): JSONObject = withBitmap(source) { bitmap ->
        val row = IntArray(bitmap.width)
        val rgba = ByteArray(bitmap.width * 4)
        output.outputStream().buffered().use { stream ->
            for (y in 0 until bitmap.height) {
                currentCoroutineContext().ensureActive()
                bitmap.getPixels(row, 0, bitmap.width, 0, y, bitmap.width, 1)
                row.forEachIndexed { x, argb ->
                    val offset = x * 4
                    rgba[offset] = (argb ushr 16).toByte()
                    rgba[offset + 1] = (argb ushr 8).toByte()
                    rgba[offset + 2] = argb.toByte()
                    rgba[offset + 3] = (argb ushr 24).toByte()
                }
                stream.write(rgba)
            }
        }
        JSONObject().put("mime", "application/octet-stream")
            .put("width", bitmap.width).put("height", bitmap.height).put("pixel_format", "rgba8")
    }

    @Suppress("DEPRECATION")
    private fun encodeImage(source: File, output: File, format: String, quality: Int): String = withBitmap(source) { bitmap ->
        val codec = when (format) {
            "png" -> Bitmap.CompressFormat.PNG
            "jpeg" -> Bitmap.CompressFormat.JPEG
            "webp" -> Bitmap.CompressFormat.WEBP
            else -> error("Unsupported image format")
        }
        output.outputStream().use { check(bitmap.compress(codec, quality, it)) { "Image encoding failed" } }
        "image/$format"
    }

    private fun createPdfPage(source: File, output: File) = withBitmap(source) { bitmap ->
        val doc = PdfDocument()
        try {
            val page = doc.startPage(PdfDocument.PageInfo.Builder(bitmap.width, bitmap.height, 1).create())
            page.canvas.drawColor(android.graphics.Color.WHITE)
            page.canvas.drawBitmap(bitmap, 0f, 0f, null)
            doc.finishPage(page)
            output.outputStream().use(doc::writeTo)
        } finally { doc.close() }
    }

    @androidx.annotation.OptIn(UnstableApi::class)
    private suspend fun encodeVideo(context: Context, source: File, output: File, start: Long, duration: Long) =
        withContext(Dispatchers.Main) {
            suspendCancellableCoroutine<Unit> { continuation ->
                val transformer = Transformer.Builder(context)
                    .setVideoMimeType(MimeTypes.VIDEO_H264)
                    .setAudioMimeType(MimeTypes.AUDIO_AAC)
                    .addListener(object : Transformer.Listener {
                        override fun onCompleted(composition: Composition, exportResult: ExportResult) {
                            if (continuation.isActive) continuation.resume(Unit)
                        }
                        override fun onError(composition: Composition, exportResult: ExportResult, exportException: ExportException) {
                            if (continuation.isActive) continuation.resumeWithException(exportException)
                        }
                    }).build()
                continuation.invokeOnCancellation { Handler(Looper.getMainLooper()).post(transformer::cancel) }
                val item = MediaItem.Builder().setUri(android.net.Uri.fromFile(source))
                    .setClippingConfiguration(MediaItem.ClippingConfiguration.Builder()
                        .setStartPositionMs(start).setEndPositionMs(start + duration).build())
                    .build()
                transformer.start(item, output.path)
            }
        }

    private fun videoMetadata(source: File): JSONObject = withVideo(source) { video ->
        fun value(key: Int) = video.extractMetadata(key)
        val width = value(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull()
        val height = value(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull()
        check(width != null && height != null) { "Video metadata is unavailable" }
        JSONObject().put("width", width).put("height", height)
            .put("duration_ms", value(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull())
            .put("rotation_degrees", value(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION)?.toIntOrNull())
            .put("bit_rate", value(MediaMetadataRetriever.METADATA_KEY_BITRATE)?.toLongOrNull())
            .put("has_audio", value(MediaMetadataRetriever.METADATA_KEY_HAS_AUDIO) == "yes")
            .put("detected_mime", value(MediaMetadataRetriever.METADATA_KEY_MIMETYPE))
    }

    private inline fun <T> withPdf(source: File, block: (PdfRenderer) -> T): T =
        ParcelFileDescriptor.open(source, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use(block)
        }

    private fun renderPdf(source: File, output: File, page: Int) {
        withPdf(source) { renderer ->
            require(page in 1..renderer.pageCount) { "PDF page is outside the document" }
            renderer.openPage(page - 1).use {
                val scale = 1400f / maxOf(it.width, it.height)
                val bitmap = Bitmap.createBitmap(
                    (it.width * scale).toInt().coerceAtLeast(1),
                    (it.height * scale).toInt().coerceAtLeast(1),
                    Bitmap.Config.ARGB_8888,
                )
                try {
                    bitmap.eraseColor(android.graphics.Color.WHITE)
                    it.render(
                        bitmap,
                        null,
                        Matrix().apply { setScale(scale, scale) },
                        PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY,
                    )
                    encode(bitmap, output)
                } finally {
                    bitmap.recycle()
                }
            }
        }
    }

    private inline fun <T> withVideo(source: File, block: (MediaMetadataRetriever) -> T): T {
        val retriever = MediaMetadataRetriever()
        try {
            retriever.setDataSource(source.path)
            return block(retriever)
        } finally {
            retriever.release()
        }
    }

    private fun renderVideo(source: File, output: File, start: Long) {
        withVideo(source) { retriever ->
            val duration =
                retriever
                    .extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)
                    ?.toLongOrNull()
            require(duration == null || start < duration) { "Timestamp is outside the video" }
            val width =
                retriever
                    .extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)
                    ?.toIntOrNull() ?: 1280
            val height =
                retriever
                    .extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)
                    ?.toIntOrNull() ?: 720
            val rotated =
                retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION) in
                    listOf("90", "270")
            val scale = minOf(1f, 1400f / maxOf(width, height).coerceAtLeast(1))
            val frame =
                if (Build.VERSION.SDK_INT >= 27) {
                    retriever.getScaledFrameAtTime(
                        start * 1000,
                        MediaMetadataRetriever.OPTION_CLOSEST,
                        ((if (rotated) height else width) * scale).toInt().coerceAtLeast(1),
                        ((if (rotated) width else height) * scale).toInt().coerceAtLeast(1),
                    )
                } else retriever.getFrameAtTime(start * 1000, MediaMetadataRetriever.OPTION_CLOSEST)
            checkNotNull(frame) { "No video frame at this time" }
            try {
                encode(frame, output)
            } finally {
                frame.recycle()
            }
        }
    }

    private fun encode(bitmap: Bitmap, output: File) =
        output.outputStream().use {
            check(bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)) { "Image encoding failed" }
        }

    private suspend fun renderAudio(source: File, output: File, start: Long, duration: Long) {
        val extractor = MediaExtractor()
        try {
            extractor.setDataSource(source.path)
            val track =
                (0 until extractor.trackCount).firstOrNull {
                    extractor
                        .getTrackFormat(it)
                        .getString(MediaFormat.KEY_MIME)
                        ?.startsWith("audio/") == true
                } ?: error("Asset has no audio track")
            val format = extractor.getTrackFormat(track)
            extractor.selectTrack(track)
            extractor.seekTo(start * 1000, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
            val decoder =
                MediaCodec.createDecoderByType(checkNotNull(format.getString(MediaFormat.KEY_MIME)))
            try {
                decoder.configure(format, null, null, 0)
                decoder.start()
                writeClip(
                    extractor,
                    decoder,
                    format,
                    output,
                    start * 1000,
                    (start + duration) * 1000,
                )
            } finally {
                decoder.release()
            }
        } finally {
            extractor.release()
        }
    }

    private suspend fun writeClip(
        extractor: MediaExtractor,
        decoder: MediaCodec,
        inputFormat: MediaFormat,
        output: File,
        start: Long,
        end: Long,
    ) {
        val info = MediaCodec.BufferInfo()
        val deadline = SystemClock.elapsedRealtime() + 20_000
        var rate = inputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
        var channels = inputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
        var inputEnded = false
        RandomAccessFile(output, "rw").use { file ->
            file.seek(44)
            while (true) {
                currentCoroutineContext().ensureActive()
                check(SystemClock.elapsedRealtime() < deadline) { "Audio decoding timed out" }
                if (!inputEnded) {
                    val slot = decoder.dequeueInputBuffer(5_000)
                    if (slot >= 0) {
                        val buffer = checkNotNull(decoder.getInputBuffer(slot))
                        val timestamp = extractor.sampleTime
                        val count =
                            if (timestamp in 0 until end) extractor.readSampleData(buffer, 0)
                            else -1
                        inputEnded = count < 0
                        decoder.queueInputBuffer(
                            slot,
                            0,
                            count.coerceAtLeast(0),
                            timestamp.coerceAtLeast(0),
                            if (inputEnded) MediaCodec.BUFFER_FLAG_END_OF_STREAM else 0,
                        )
                        if (!inputEnded) extractor.advance()
                    }
                }
                val slot = decoder.dequeueOutputBuffer(info, 5_000)
                if (slot == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    val format = decoder.outputFormat
                    rate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                    channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                    check(
                        !format.containsKey(MediaFormat.KEY_PCM_ENCODING) ||
                            format.getInteger(MediaFormat.KEY_PCM_ENCODING) ==
                                AudioFormat.ENCODING_PCM_16BIT
                    ) {
                        "Unsupported audio sample format"
                    }
                } else if (slot >= 0) {
                    val frameSize = channels * 2
                    val frames = info.size / frameSize
                    fun frameAt(time: Long) =
                        ((time - info.presentationTimeUs) * rate / 1_000_000)
                            .coerceIn(0, frames.toLong())
                            .toInt()
                    val first = frameAt(start)
                    val last = frameAt(end)
                    if (last > first) {
                        val bytes = ByteArray((last - first) * frameSize)
                        val buffer = checkNotNull(decoder.getOutputBuffer(slot))
                        buffer.position(info.offset + first * frameSize)
                        buffer.get(bytes)
                        check(file.length() + bytes.size <= 16_000_000) {
                            "Request a shorter audio clip"
                        }
                        file.write(bytes)
                    }
                    decoder.releaseOutputBuffer(slot, false)
                    if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                }
            }
            val bytes = (file.length() - 44).toInt()
            check(bytes > 0) { "No audio in the requested interval" }
            val header =
                ByteBuffer.allocate(44)
                    .order(ByteOrder.LITTLE_ENDIAN)
                    .put("RIFF".toByteArray())
                    .putInt(36 + bytes)
                    .put("WAVEfmt ".toByteArray())
                    .putInt(16)
                    .putShort(1)
                    .putShort(channels.toShort())
                    .putInt(rate)
                    .putInt(rate * channels * 2)
                    .putShort((channels * 2).toShort())
                    .putShort(16)
                    .put("data".toByteArray())
                    .putInt(bytes)
            file.seek(0)
            file.write(header.array())
        }
    }
}
