package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.minirp.app.core.CoreClient
import java.io.File
import java.net.InetAddress
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
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NativeCurlTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun agentDownloadsAndReadsAssetsThroughNativeCurl() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val marker = File(directory, "outside.txt").apply { writeText("Out of scope") }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val round = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        var destination = ""
        var received = ""
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (request.url.encodedPath == "/artifact") return MockResponse.Builder().body("Portable curl marker\n").build()
                    return try {
                        val model = JSONObject(checkNotNull(request.body).utf8())
                        fun call(name: String, args: JSONObject): JSONObject = JSONObject().put("tool_calls", JSONArray().put(
                            JSONObject().put("index", 0).put("id", "curl-${round.get()}").put("type", "function")
                                .put("function", JSONObject().put("name", name).put("arguments", args.toString()))))
                        fun activate(name: String) = call("activate_skill", JSONObject().put("name", name))
                        fun result(): JSONObject {
                            val messages = model.getJSONArray("messages")
                            val last = (0 until messages.length()).map(messages::getJSONObject).last { it.optString("role") == "tool" }
                            return JSONObject(last.getString("content"))
                        }
                        val delta = if ((model.optJSONArray("tools")?.length() ?: 0) == 0) JSONObject().put("content", "Native curl")
                        else when (round.getAndIncrement()) {
                            0 -> activate("asset_storage")
                            1 -> call("create_asset", JSONObject().put("name", "curl-result.txt"))
                            2 -> {
                                destination = result().getString("url")
                                activate("curl")
                            }
                            3 -> call("curl", JSONObject().put("args", JSONArray(listOf("-fsS", server.url("/artifact").toString(), "-o", destination)))
                                .put("write_assets", JSONArray(listOf(destination))))
                            4 -> {
                                assertEquals(result().toString(), 0, result().getInt("exit_code"))
                                assertEquals(destination, result().getJSONArray("written_assets").getString(0))
                                call("curl", JSONObject().put("args", JSONArray(listOf("-fsS", "file:///$destination")))
                                    .put("read_assets", JSONArray(listOf(destination))))
                            }
                            5 -> {
                                assertEquals(result().toString(), 0, result().getInt("exit_code"))
                                received = result().getString("asset_url")
                                activate("view_asset")
                            }
                            6 -> call("raw_grep", JSONObject().put("url", received)
                                .put("pattern", "50 6F 72 74 61 62 6C 65 20 63 75 72 6C").put("literal", true))
                            7 -> {
                                assertEquals(1, result().getInt("match_count"))
                                call("curl", JSONObject().put("args", JSONArray(listOf("-fsS", "file://${marker.absolutePath}"))))
                            }
                            8 -> {
                                assertEquals(result().toString(), 37, result().getInt("exit_code"))
                                assertTrue(result().isNull("asset_url"))
                                JSONObject().put("content", "Assets verified")
                            }
                            else -> error("Unexpected model request.")
                        }
                        val event = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                        MockResponse.Builder().setHeader("Content-Type", "text/event-stream").body("data: $event\n\ndata: [DONE]\n\n").build()
                    } catch (error: Throwable) {
                        failure.compareAndSet(null, error)
                        MockResponse.Builder().code(500).body("Unexpected model request").build()
                    }
                }
            }
            server.start(InetAddress.getByName("127.0.0.1"), 0)
            val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
            val core = CoreClient(context, scope, seedDefaultPreset = true)
            try {
                core.send(JSONObject().put("op", "configure").put("provider", JSONObject().put("url", server.url("/v1").toString()).put("model", "fixture")))
                core.send(JSONObject().put("op", "message").put("text", "Verify Asset-only curl."))
                val answer = withTimeout(60_000) {
                    core.events.first {
                        failure.get()?.let { error -> throw AssertionError("Native curl fixture failed", error) }
                        check(it.optString("kind") != "error") { it.toString() }
                        it.optString("kind") == "completed"
                    }
                }
                assertEquals("Assets verified", answer.getString("text"))
            } finally {
                core.close()
                scope.cancel()
            }
        }
    }
}
