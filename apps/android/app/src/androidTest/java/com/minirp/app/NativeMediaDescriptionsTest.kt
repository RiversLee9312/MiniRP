package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import android.graphics.Bitmap
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.minirp.app.core.CoreClient
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NativeMediaDescriptionsTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun descriptionsReachTextOnlyChatThroughNativeCore() = runBlocking {
        val directory = temporary.newFolder()
        val attachments = directory.resolve("workspace/attachments").apply { mkdirs() }
        val imageId = UUID.randomUUID().toString()
        val audioId = UUID.randomUUID().toString()
        val bitmap = Bitmap.createBitmap(2, 2, Bitmap.Config.ARGB_8888)
        try {
            attachments.resolve(imageId).outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        } finally {
            bitmap.recycle()
        }
        val wave = ByteBuffer.allocate(3244).order(ByteOrder.LITTLE_ENDIAN)
            .put("RIFF".toByteArray()).putInt(3236).put("WAVEfmt ".toByteArray()).putInt(16)
            .putShort(1).putShort(1).putInt(16000).putInt(32000).putShort(2).putShort(16)
            .put("data".toByteArray()).putInt(3200).array()
        attachments.resolve(audioId).writeBytes(wave)
        val failure = AtomicReference<Throwable>()
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse = try {
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    val messages = body.getJSONArray("messages")
                    val user = (0 until messages.length()).map(messages::getJSONObject).first { it.optString("role") == "user" }
                    val reply = when (body.getString("model")) {
                        "vision-fixture", "audio-fixture" -> {
                            assertFalse(body.toString().contains("Ask about both attachments"))
                            val part = user.getJSONArray("content").getJSONObject(1)
                            if (body.getString("model") == "vision-fixture") {
                                assertEquals("image_url", part.getString("type"))
                                assertTrue(part.getJSONObject("image_url").getString("url").startsWith("data:image/png;base64,"))
                                "A small square."
                            } else {
                                assertEquals("input_audio", part.getString("type"))
                                assertEquals("wav", part.getJSONObject("input_audio").getString("format"))
                                "A short silent recording."
                            }
                        }
                        "chat-fixture" -> {
                            val content = user.getString("content")
                            assertTrue(content.contains("A small square."))
                            assertTrue(content.contains("A short silent recording."))
                            assertTrue(content.contains("minirp://assets/minirp.attachments/$imageId"))
                            assertTrue(content.contains("minirp://assets/minirp.attachments/$audioId"))
                            "Descriptions verified"
                        }
                        else -> error("Unexpected model")
                    }
                    val event = JSONObject().put("choices", JSONArray().put(
                        JSONObject().put("delta", JSONObject().put("content", reply))))
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream").body("data: $event\n\ndata: [DONE]\n\n").build()
                } catch (error: Throwable) {
                    failure.compareAndSet(null, error)
                    MockResponse.Builder().code(500).build()
                }
            }
            server.start(InetAddress.getByName("127.0.0.1"), 0)
            fun provider(model: String, modality: String) = JSONObject()
                .put("url", server.url("/v1").toString()).put("model", model).put("input_modalities", JSONArray(listOf(modality)))
            val app = ApplicationProvider.getApplicationContext<Context>()
            val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
            CoreClient(context, scope, seedDefaultPreset = true).use { core ->
                try {
                    core.send(JSONObject().put("op", "configure").put("provider", provider("chat-fixture", "text"))
                        .put("agents", JSONObject().put("image_description", provider("vision-fixture", "image"))
                            .put("audio_description", provider("audio-fixture", "audio"))))
                    core.send(JSONObject().put("op", "message").put("text", "Ask about both attachments")
                        .put("attachments", JSONArray()
                            .put(JSONObject().put("id", imageId).put("name", "square.png").put("mime", "image/png"))
                            .put(JSONObject().put("id", audioId).put("name", "silence.wav").put("mime", "audio/wav"))))
                    val answer = withTimeout(60_000) {
                        core.events.first {
                            failure.get()?.let { error -> throw AssertionError("Media description fixture failed", error) }
                            check(it.optString("kind") != "error") { it.toString() }
                            it.optString("kind") == "completed"
                        }
                    }
                    assertEquals("Descriptions verified", answer.getString("text"))
                } finally {
                    scope.cancel()
                }
            }
        }
    }
}
