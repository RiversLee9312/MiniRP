package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import com.minirp.app.core.BrowserHost
import com.minirp.app.core.CoreClient
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
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

class PluginAgentResourcesDeviceTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun presetAndPluginShareAssetsContainerAndResearch() = runBlocking {
        val directory = temporary.newFolder()
        val app = ApplicationProvider.getApplicationContext<Context>()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        MockWebServer().use { server ->
            val fixture = Models(server)
            server.dispatcher = fixture
            server.start()
            val core = CoreClient(context, scope, seedDefaultPreset = false)
            val browser = BrowserHost(app)
            core.browser = browser::execute
            try {
                val preset = core.query(obj("op" to "create_preset", "open" to true,
                    "interview" to false, "name" to "Shared resource fixture")).getString("preset")
                core.send(obj(
                    "op" to "configure",
                    "provider" to obj("url" to server.url("/v1").toString(), "model" to "fixture"),
                    "settings" to obj("search_key" to "test", "search_url" to server.url("/").toString()),
                ))
                core.send(obj("op" to "message", "agent" to "preset", "preset" to preset,
                    "text" to "Delegate a task using the shared preset resources."))
                val completed = withTimeout(150_000) {
                    core.events.first {
                        fixture.failure.get()?.let { error -> throw AssertionError("Shared resource fixture failed", error) }
                        check(it.optString("kind") != "error") { it.toString() }
                        it.optString("kind") == "completed"
                    }
                }
                assertEquals("Shared preset resources verified.", completed.getString("text"))
            } finally {
                core.close()
                withContext(Dispatchers.Main) { browser.close() }
                scope.cancel()
            }
        }
    }

    private class Models(private val server: MockWebServer) : Dispatcher() {
        val failure = AtomicReference<Throwable>()
        private val parentRound = AtomicInteger()
        private val workerRound = AtomicInteger()
        private val browserRound = AtomicInteger()
        private val callId = AtomicInteger()
        @Volatile private var parentAsset = ""
        @Volatile private var curlAsset = ""
        @Volatile private var pageAsset = ""

        override fun dispatch(request: RecordedRequest): MockResponse = try {
            if (request.url.encodedPath in setOf("/v1/chat/completions", "/v1/tools/search")) {
                assertEquals("Mini Agent Harness/${BuildConfig.VERSION_NAME}", request.headers["User-Agent"])
            }
            when (request.url.encodedPath) {
                "/artifact" -> MockResponse.Builder().body("Curl download fixture").build()
                "/page" -> {
                    assertTrue(request.headers["User-Agent"].orEmpty().startsWith("Mozilla/"))
                    MockResponse.Builder().setHeader("Content-Type", "text/html; charset=utf-8")
                        .body("<html><head><title>Shared browser page</title></head><body><main>" +
                            "<h1>Shared browser page</h1><p>" + "Browser fixture paragraph. ".repeat(20) +
                            "</p></main></body></html>").build()
                }
                "/favicon.ico" -> MockResponse.Builder().code(404).build()
                "/v1/tools/search" -> MockResponse.Builder().setHeader("Content-Type", "application/json")
                    .body(obj("results" to JSONArray().put(obj("url" to server.url("/page").toString(),
                        "title" to "Search fixture", "snippet" to "Shared search result"))).toString()).build()
                else -> {
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    val messages = body.getJSONArray("messages")
                    val history = (0 until messages.length()).map(messages::getJSONObject)
                    val result = history.lastOrNull { it.optString("role") == "tool" }?.getString("content").orEmpty()
                    val prompt = history.first().getString("content")
                    val delta = when {
                        prompt.startsWith("You are PluginAgent") -> worker(result, history)
                        prompt.startsWith("You are PresetAgent") -> parent(result)
                        prompt.startsWith("You are FetchAgent") -> browser(result)
                        else -> error("Unexpected model prompt")
                    }
                    val event = obj("choices" to JSONArray().put(obj("delta" to delta)))
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body("data: $event\n\ndata: [DONE]\n\n").build()
                }
            }
        } catch (error: Throwable) {
            failure.compareAndSet(null, error)
            MockResponse.Builder().code(500).body("Unexpected shared resource operation.").build()
        }

        private fun parent(result: String): JSONObject = when (parentRound.getAndIncrement()) {
            0 -> tools("activate_skill", "name" to "asset_storage")
            1 -> tools("create_asset", "name" to "parent.txt", "text" to "Parent Asset bytes")
            2 -> {
                parentAsset = JSONObject(result).getString("url")
                tools("activate_skill", "name" to "container")
            }
            3 -> tools("shell", "command" to "printf 'Parent guest file' > parent-guest.txt")
            4 -> {
                assertEquals(result, 0, JSONObject(result).getInt("exit_code"))
                tools("activate_skill", "name" to "base-plugin-development")
            }
            5 -> tools("plugin_base_plugin_spawn", "name" to "Resource worker", "message" to "Read $parentAsset and the guest file.")
            6 -> tools("plugin_base_plugin_wait", "ids" to JSONArray().put(JSONObject(result).getString("id")), "seconds" to 100)
            7 -> {
                val worker = JSONArray(result).getJSONObject(0)
                assertEquals(result, "finished", worker.getString("status"))
                assertTrue(result, worker.getString("result").contains("$curlAsset $pageAsset"))
                tools("activate_skill", "name" to "view_asset")
            }
            8 -> tools("plugin_base_raw_grep", "url" to curlAsset, "pattern" to "43 75 72 6C 20 64 6F 77 6E 6C 6F 61 64", "literal" to true)
            9 -> {
                assertEquals(result, 1, JSONObject(result).getInt("match_count"))
                tools("plugin_base_grep", "url" to pageAsset, "pattern" to "Shared browser page")
            }
            10 -> {
                assertTrue(result, JSONObject(result).getInt("match_count") > 0)
                tools("shell", "command" to "cat worker-guest.txt")
            }
            11 -> {
                assertEquals("Worker guest file", JSONObject(result).getString("output"))
                obj("content" to "Shared preset resources verified.")
            }
            else -> error("Unexpected parent request")
        }

        private fun worker(result: String, history: List<JSONObject>): JSONObject = when (workerRound.getAndIncrement()) {
            0 -> {
                assertTrue(history.any { it.optString("content").contains(parentAsset) })
                tools("activate_skill", "name" to "container")
            }
            1 -> tools("export_asset", "url" to parentAsset, "path" to "parent-asset.txt")
            2 -> tools("shell", "command" to "cat parent-guest.txt parent-asset.txt && printf 'Worker guest file' > worker-guest.txt")
            3 -> {
                assertEquals("Parent guest fileParent Asset bytes", JSONObject(result).getString("output"))
                tools("activate_skill", "name" to "curl")
            }
            4 -> tools("curl", "args" to JSONArray().put("-fsS").put(server.url("/artifact").toString()))
            5 -> {
                val curl = JSONObject(result)
                assertEquals(result, 0, curl.getInt("exit_code"))
                curlAsset = curl.getString("asset_url")
                tools("real_search", "query" to "shared search fixture")
            }
            6 -> {
                val hit = JSONObject(result).getJSONArray("results").getJSONObject(0)
                assertEquals("Shared search result", hit.getString("snippet"))
                tools("real_fetch", "url" to hit.getString("url"), "backend" to "local")
            }
            7 -> {
                val page = JSONObject(result)
                assertTrue(result, page.getString("content").contains("Shared browser page"))
                pageAsset = page.getString("asset_url")
                obj("content" to "Results: $curlAsset $pageAsset")
            }
            else -> error("Unexpected worker request")
        }

        private fun browser(result: String) = when (browserRound.getAndIncrement()) {
            0 -> tools("browser_navigate", "url" to server.url("/page").toString())
            1 -> {
                assertTrue(result, JSONObject(result).optString("snapshot").contains("Shared browser page"))
                tools("capture_page")
            }
            2 -> {
                assertTrue(result, JSONObject(result).getString("preview").contains("Browser fixture paragraph"))
                tools("finish_fetch", "complete" to true)
            }
            else -> error("Unexpected browser request after $result")
        }

        private fun tools(name: String, vararg args: Pair<String, Any>) = obj("tool_calls" to JSONArray().put(
            obj("index" to 0, "id" to "fixture-${callId.incrementAndGet()}", "type" to "function",
                "function" to obj("name" to name, "arguments" to obj(*args).toString())),
        ))
    }

    private companion object {
        fun obj(vararg values: Pair<String, Any>) = JSONObject().apply {
            for ((key, value) in values) put(key, value)
        }
    }
}
