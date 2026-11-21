package com.minirp.app

import android.content.Context
import android.graphics.BitmapFactory
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.core.AgentMediaSampler
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AgentMediaSamplerTest {
    @Test
    fun boundsWholeImageAndCropsOriginalRasterAndPdfCoordinates(): Unit = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val directory = context.cacheDir.resolve("media-sample-${System.nanoTime()}").apply { mkdirs() }
        fun viewport(x: Int, y: Int, width: Int, height: Int) =
            JSONObject().put("x", x).put("y", y).put("width", width).put("height", height)
        suspend fun sample(mode: String, source: File, id: String, crop: JSONObject? = null): Bitmap {
            val target = directory.resolve("asset-sample-$id")
            val args = JSONObject().put("mode", mode).put("source", source.name).put("output", target.name)
                .put("viewport", crop)
            val result = AgentMediaSampler.sample(directory, args)
            assertTrue(result.getInt("width") <= 4096 && result.getInt("height") <= 4096)
            return requireNotNull(BitmapFactory.decodeFile(target.path))
        }
        try {
            val raster = directory.resolve("asset-source-large-image")
            val bitmap = Bitmap.createBitmap(5200, 240, Bitmap.Config.ARGB_8888)
            try {
                val canvas = Canvas(bitmap)
                canvas.drawColor(Color.BLUE)
                canvas.drawRect(4800f, 0f, 5200f, 240f, Paint().apply { color = Color.RED })
                raster.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            } finally { bitmap.recycle() }
            val whole = sample("image_preview", raster, "whole")
            try { assertTrue(whole.width <= 4096 && whole.width < 5200) }
            finally { whole.recycle() }
            val crop = sample("image_preview", raster, "crop", viewport(4900, 80, 240, 100))
            try {
                assertEquals(240, crop.width)
                assertEquals(100, crop.height)
                assertEquals(Color.RED, crop.getPixel(120, 50))
            } finally { crop.recycle() }

            val pdf = directory.resolve("asset-source-region-pdf")
            val document = PdfDocument()
            try {
                val page = document.startPage(PdfDocument.PageInfo.Builder(100, 200, 1).create())
                page.canvas.drawColor(Color.BLUE)
                page.canvas.drawRect(70f, 0f, 100f, 200f, Paint().apply { color = Color.RED })
                document.finishPage(page)
                pdf.outputStream().use(document::writeTo)
            } finally { document.close() }
            val page = sample("pdf_page", pdf, "pdf-crop", viewport(75, 40, 20, 100))
            try {
                assertEquals(4096, page.height)
                assertEquals(Color.RED, page.getPixel(page.width / 2, page.height / 2))
            } finally { page.recycle() }

            InstrumentationRegistry.getArguments().getString("mediaFixture")?.let { path ->
                val video = File(path).copyTo(directory.resolve("asset-source-region-video"))
                val frame = sample("video_frame", video, "frame-crop", viewport(15, 12, 32, 48))
                try {
                    assertEquals(32, frame.width)
                    assertEquals(48, frame.height)
                } finally { frame.recycle() }
            }
        } finally { directory.deleteRecursively() }
    }

    @Test
    fun rendersSelectedPdfPageAndExtractsRequestedAudioInterval() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val directory =
            File(context.cacheDir, "media-sample-${System.nanoTime()}").apply { mkdirs() }
        try {
            val pdf = directory.resolve("asset-source-pdf")
            val document = PdfDocument()
            try {
                listOf(Color.BLUE, Color.RED).forEachIndexed { index, color ->
                    val page =
                        document.startPage(
                            PdfDocument.PageInfo.Builder(100, 200, index + 1).create()
                        )
                    page.canvas.drawColor(color)
                    document.finishPage(page)
                }
                pdf.outputStream().use(document::writeTo)
            } finally {
                document.close()
            }
            val rendered = directory.resolve("asset-sample-page")
            val page =
                JSONObject()
                    .put("source", pdf.name)
                    .put("output", rendered.name)
                    .put("mode", "pdf_page")
                    .put("page", 2)
            assertEquals("image/png", AgentMediaSampler.sample(directory, page).getString("mime"))
            val image = BitmapFactory.decodeFile(rendered.path)
            try {
                assertEquals(Color.RED, image.getPixel(image.width / 2, image.height / 2))
            } finally {
                image.recycle()
            }

            val png = rendered.copyTo(directory.resolve("asset-source-rendered"))
            suspend fun convert(mode: String, source: File, options: JSONObject = JSONObject()): JSONObject =
                AgentMediaSampler.sample(directory, options.put("mode", mode).put("source", source.name)
                    .put("output", "asset-sample-$mode"), context)

            val pixels = convert("image_decode", png)
            assertEquals("rgba8", pixels.getString("pixel_format"))
            assertEquals(pixels.getLong("width") * pixels.getLong("height") * 4,
                directory.resolve("asset-sample-image_decode").length())
            assertEquals("image/webp", convert("image_encode", png, JSONObject().put("format", "webp")).getString("mime"))
            assertEquals("application/pdf", convert("pdf_create_page", png).getString("mime"))
            val generatedPdf = directory.resolve("asset-sample-pdf_create_page").copyTo(directory.resolve("asset-source-generated"))
            assertEquals(1, convert("pdf_metadata", generatedPdf).getInt("page_count"))

            val input = directory.resolve("asset-source-wav")
            val rate = 16000
            val bytes = 2 * rate
            input.writeBytes(
                ByteBuffer.allocate(44 + bytes)
                    .order(ByteOrder.LITTLE_ENDIAN)
                    .put("RIFF".toByteArray())
                    .putInt(36 + bytes)
                    .put("WAVEfmt ".toByteArray())
                    .putInt(16)
                    .putShort(1)
                    .putShort(1)
                    .putInt(rate)
                    .putInt(rate * 2)
                    .putShort(2)
                    .putShort(16)
                    .put("data".toByteArray())
                    .putInt(bytes)
                    .array()
            )
            val clip = directory.resolve("asset-sample-clip")
            val audio =
                JSONObject()
                    .put("source", input.name)
                    .put("output", clip.name)
                    .put("mode", "audio_clip")
                    .put("start_ms", 250)
                    .put("duration_ms", 250)
            assertEquals("audio/wav", AgentMediaSampler.sample(directory, audio).getString("mime"))
            assertEquals(44L + bytes / 4, clip.length())

            val fixture = InstrumentationRegistry.getArguments().getString("mediaFixture")
            if (fixture != null) {
                val video = File(fixture).copyTo(directory.resolve("asset-source-video"))
                val metadata = convert("video_metadata", video)
                assertTrue(metadata.getInt("width") > 0)
                assertTrue(metadata.getBoolean("has_audio"))
                assertEquals("image/png", convert("video_frame", video, JSONObject().put("start_ms", 200)).getString("mime"))
                assertEquals("audio/wav", convert("audio_clip", video, JSONObject().put("duration_ms", 500)).getString("mime"))
                assertTrue(directory.resolve("asset-sample-audio_clip").length() > 44)
                assertEquals("video/mp4", convert("video_encode", video, JSONObject().put("duration_ms", 1000)).getString("mime"))
                val encoded = directory.resolve("asset-sample-video_encode").copyTo(directory.resolve("asset-source-encoded"))
                val clipInfo = convert("video_metadata", encoded)
                assertTrue(clipInfo.getBoolean("has_audio"))
                assertTrue(clipInfo.getLong("duration_ms") in 800..1200)
            }
        } finally {
            directory.deleteRecursively()
        }
    }
}
