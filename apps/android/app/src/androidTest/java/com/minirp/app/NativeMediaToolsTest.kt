package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.minirp.app.core.CoreClient
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
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
class NativeMediaToolsTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun agentProbesAndTranscodesAuthorizedAssets() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val attachments = directory.resolve("workspace/attachments").apply { mkdirs() }
        val id = UUID.randomUUID().toString()
        val input = "minirp://assets/minirp.attachments/$id"
        val wave = ByteBuffer.allocate(44 + 3200).order(ByteOrder.LITTLE_ENDIAN)
            .put("RIFF".toByteArray()).putInt(3236).put("WAVEfmt ".toByteArray()).putInt(16)
            .putShort(1).putShort(1).putInt(16000).putInt(32000).putShort(2).putShort(16)
            .put("data".toByteArray()).putInt(3200).array()
        val file = attachments.resolve(id).apply { writeBytes(wave) }
        val frameId = UUID.randomUUID().toString()
        val frames = "minirp://assets/minirp.attachments/$frameId"
        attachments.resolve(frameId).writeBytes(ByteArray(16 * 16 * 3 / 2 * 4) { 128.toByte() })
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val round = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        var destination = ""
        var video = ""

        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse = try {
                    val model = JSONObject(checkNotNull(request.body).utf8())
                    fun tool(name: String, args: JSONObject) = JSONObject().put("tool_calls", JSONArray().put(
                        JSONObject().put("index", 0).put("id", "media-${round.get()}").put("type", "function")
                            .put("function", JSONObject().put("name", name).put("arguments", args.toString()))))
                    fun activate(name: String) = tool("activate_skill", JSONObject().put("name", name))
                    fun result(): JSONObject {
                        val messages = model.getJSONArray("messages")
                        val last = (0 until messages.length()).map(messages::getJSONObject).last { it.optString("role") == "tool" }
                        return JSONObject(last.getString("content"))
                    }
                    fun probe(url: String, grant: Boolean = true) = tool("ffprobe",
                        JSONObject().put("args", JSONArray(listOf("-v", "error", "-show_entries", "stream=sample_rate", "-of", "json", url)))
                            .put("read_assets", if (grant) JSONArray(listOf(url)) else JSONArray()))
                    fun sampleRate(): String {
                        val reply = result()
                        assertEquals(reply.toString(), 0, reply.getInt("exit_code"))
                        assertTrue(reply.getString("asset_url").startsWith("minirp://assets/"))
                        return JSONObject(reply.getString("output")).getJSONArray("streams").getJSONObject(0).getString("sample_rate")
                    }
                    val delta = if ((model.optJSONArray("tools")?.length() ?: 0) == 0) JSONObject().put("content", "Media fixture")
                    else when (round.getAndIncrement()) {
                        0 -> activate("ffprobe")
                        1 -> {
                            val tools = model.getJSONArray("tools")
                            val names = (0 until tools.length()).map { tools.getJSONObject(it).getJSONObject("function").getString("name") }
                            assertTrue("ffprobe" in names)
                            assertFalse("ffmpeg" in names)
                            probe(input)
                        }
                        2 -> {
                            assertEquals("16000", sampleRate())
                            activate("assets")
                        }
                        3 -> tool("create_asset", JSONObject().put("name", "output.wav").put("mime", "audio/wav"))
                        4 -> {
                            destination = result().getString("url")
                            activate("ffmpeg")
                        }
                        5 -> tool("ffmpeg", JSONObject().put("args", JSONArray(listOf("-nostdin", "-v", "error", "-y",
                            "-i", input, "-ar", "8000", "-c:a", "pcm_s16le", "-f", "wav", destination)))
                            .put("read_assets", JSONArray(listOf(input))).put("write_assets", JSONArray(listOf(destination))))
                        6 -> {
                            val reply = result()
                            assertEquals(reply.toString(), 0, reply.getInt("exit_code"))
                            assertEquals(destination, reply.getJSONArray("written_assets").getString(0))
                            probe(destination)
                        }
                        7 -> {
                            assertEquals("8000", sampleRate())
                            probe(file.absolutePath, grant = false)
                        }
                        8 -> {
                            assertTrue(result().getInt("exit_code") != 0)
                            tool("create_asset", JSONObject().put("name", "output.mp4").put("mime", "video/mp4"))
                        }
                        9 -> {
                            video = result().getString("url")
                            tool("ffmpeg", JSONObject().put("args", JSONArray(listOf("-nostdin", "-v", "error", "-y",
                                "-f", "rawvideo", "-pixel_format", "yuv420p", "-video_size", "16x16", "-framerate", "4",
                                "-i", frames, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mp4", video)))
                                .put("read_assets", JSONArray(listOf(frames))).put("write_assets", JSONArray(listOf(video))))
                        }
                        10 -> {
                            val reply = result()
                            assertEquals(reply.toString(), 0, reply.getInt("exit_code"))
                            assertEquals(video, reply.getJSONArray("written_assets").getString(0))
                            tool("ffprobe", JSONObject().put("args",
                                JSONArray(listOf("-v", "error", "-show_entries", "stream=codec_name,width,height", "-of", "json", video)))
                                .put("read_assets", JSONArray(listOf(video))))
                        }
                        11 -> {
                            val reply = result()
                            assertEquals(reply.toString(), 0, reply.getInt("exit_code"))
                            val stream = JSONObject(reply.getString("output")).getJSONArray("streams").getJSONObject(0)
                            assertEquals("h264", stream.getString("codec_name"))
                            assertEquals(16, stream.getInt("width"))
                            assertEquals(16, stream.getInt("height"))
                            JSONObject().put("content", "Media verified")
                        }
                        else -> error("Unexpected model request.")
                    }
                    val event = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream").body("data: $event\n\ndata: [DONE]\n\n").build()
                } catch (error: Throwable) {
                    failure.compareAndSet(null, error)
                    MockResponse.Builder().code(500).build()
                }
            }
            server.start(InetAddress.getByName("127.0.0.1"), 0)
            val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
            CoreClient(context, scope, seedDefaultPreset = true).use { core ->
                try {
                    core.send(JSONObject().put("op", "configure").put("provider",
                        JSONObject().put("url", server.url("/v1").toString()).put("model", "fixture")))
                    core.send(JSONObject().put("op", "message").put("text", "Inspect the audio and encode these video frames.")
                        .put("attachments", JSONArray()
                            .put(JSONObject().put("id", id).put("name", "input.wav").put("mime", "audio/wav"))
                            .put(JSONObject().put("id", frameId).put("name", "frames.yuv").put("mime", "application/octet-stream"))))
                    val answer = withTimeout(60_000) {
                        core.events.first {
                            failure.get()?.let { error -> throw AssertionError("Native media fixture failed", error) }
                            check(it.optString("kind") != "error") { it.toString() }
                            it.optString("kind") == "completed"
                        }
                    }
                    assertEquals("Media verified", answer.getString("text"))
                } finally {
                    scope.cancel()
                }
            }
        }
    }

    @Test
    fun pluginTemporaryAgentRunsBothNativeMediaTools() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val variant = "test.temporary_media"
        val files = mapOf(
            "manifest.json" to """
                {
                  "id": "temporary-media-test", "packageName": "$variant", "name": "Temporary media fixture",
                  "apiVersion": 2, "entry": "index.js", "permissions": ["harness.write"],
                  "actions": { "handlers": { "verify": "verify" } },
                  "assets": [{ "id": "result", "type": "dynamic", "mimeType": "text/plain", "accessibility": "public",
                    "handler": "minirp://actions/$variant/hooks/verify" }]
                }
            """.trimIndent().toByteArray(),
            "index.js" to """
                globalThis.handlers = {
                    async verify(_event, api) {
                        const { id } = await api.harness.createTemporarySession();
                        try {
                            const { text } = await api.harness.runTemporary({ id, text: "Check both media versions." });
                            return Uint8Array.from(text, character => character.charCodeAt(0));
                        } finally {
                            await api.harness.closeTemporary({ id });
                        }
                    },
                };
            """.trimIndent().toByteArray(),
        )
        val archive = writeSignedPlugin(directory.resolve("workspace"), UUID.randomUUID().toString(),
            "temporary-media-test", files, directory.resolve("temporary-media-test.plugin"))
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val round = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse = try {
                    val model = JSONObject(checkNotNull(request.body).utf8())
                    val tools = model.getJSONArray("tools")
                    val names = (0 until tools.length()).map { tools.getJSONObject(it).getJSONObject("function").getString("name") }
                    fun call(name: String, args: JSONObject) = JSONObject().put("tool_calls", JSONArray().put(
                        JSONObject().put("index", 0).put("id", "temporary-media-${round.get()}").put("type", "function")
                            .put("function", JSONObject().put("name", name).put("arguments", args.toString()))))
                    fun version(name: String) = call(name, JSONObject().put("args", JSONArray(listOf("-version"))))
                    fun result(): JSONObject {
                        val messages = model.getJSONArray("messages")
                        val last = (0 until messages.length()).map(messages::getJSONObject).last { it.optString("role") == "tool" }
                        return JSONObject(last.getString("content")).also { assertEquals(it.toString(), 0, it.getInt("exit_code")) }
                    }
                    val delta = when (round.getAndIncrement()) {
                        0 -> call("activate_skill", JSONObject().put("name", "ffprobe"))
                        1 -> {
                            assertTrue("ffprobe" in names)
                            assertFalse("ffmpeg" in names)
                            version("ffprobe")
                        }
                        2 -> {
                            assertTrue(result().getString("output").contains("--enable-libx264"))
                            call("activate_skill", JSONObject().put("name", "ffmpeg"))
                        }
                        3 -> {
                            assertTrue("ffmpeg" in names)
                            version("ffmpeg")
                        }
                        4 -> {
                            assertTrue(result().getString("asset_url").startsWith("minirp://assets/"))
                            JSONObject().put("content", "Temporary media verified")
                        }
                        else -> error("Unexpected model request.")
                    }
                    val event = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream").body("data: $event\n\ndata: [DONE]\n\n").build()
                } catch (error: Throwable) {
                    failure.compareAndSet(null, error)
                    MockResponse.Builder().code(500).build()
                }
            }
            server.start(InetAddress.getByName("127.0.0.1"), 0)
            CoreClient(context, scope, seedDefaultPreset = true).use { core ->
                try {
                    core.send(JSONObject().put("op", "configure").put("provider",
                        JSONObject().put("url", server.url("/v1").toString()).put("model", "fixture")))
                    core.query(JSONObject().put("op", "plugin_install").put("path", archive.path))
                    val result = try {
                        withTimeout(60_000) { core.readAsset("minirp://assets/$variant/result", 4096) }
                    } catch (error: Exception) {
                        throw AssertionError("Temporary media fixture failed", failure.get() ?: error)
                    }
                    assertEquals("Temporary media verified", result.toString(Charsets.UTF_8))
                } finally {
                    scope.cancel()
                }
            }
        }
    }
}
