package com.minirp.app

import android.content.Context
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.pdf.PdfDocument
import androidx.test.core.app.ApplicationProvider
import com.minirp.app.core.AgentMediaSampler
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test

class AgentMediaSamplerTest {
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
        } finally {
            directory.deleteRecursively()
        }
    }
}
