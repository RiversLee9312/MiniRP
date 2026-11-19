package com.minirp.app.core

import android.graphics.Bitmap
import android.graphics.Matrix
import android.graphics.pdf.PdfRenderer
import android.media.AudioFormat
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMetadataRetriever
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import org.json.JSONObject

internal object AgentMediaSampler {
    suspend fun sample(directory: File, event: JSONObject): JSONObject {
        val source = file(directory, event.getString("source"), "asset-source-")
        val output = file(directory, event.getString("output"), "asset-sample-")
        check(source.isFile && !output.exists()) { "Invalid media sample files" }
        val start = event.optLong("start_ms").coerceAtLeast(0)
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
                    renderAudio(
                        source,
                        output,
                        start,
                        event.optLong("duration_ms", 10_000).coerceIn(100, 30_000),
                    )
                    "audio/wav"
                }
                else -> error("Unknown asset sample mode")
            }
        return JSONObject().put("mime", mime)
    }

    private fun file(directory: File, id: String, prefix: String): File {
        require(id.startsWith(prefix) && id.removePrefix(prefix).matches(Regex("[a-zA-Z0-9_-]+")))
        return directory.resolve(id)
    }

    private fun renderPdf(source: File, output: File, page: Int) {
        ParcelFileDescriptor.open(source, ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor ->
            PdfRenderer(descriptor).use { renderer ->
                require(page in 1..renderer.pageCount) { "PDF page is outside the document" }
                renderer.openPage(page - 1).use {
                    val scale = 1400f / maxOf(it.width, it.height)
                    val bitmap =
                        Bitmap.createBitmap(
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
    }

    private fun renderVideo(source: File, output: File, start: Long) {
        val retriever = MediaMetadataRetriever()
        try {
            retriever.setDataSource(source.path)
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
        } finally {
            retriever.release()
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
