package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.core.CoreClient
import java.net.InetAddress
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
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

class NativeAssetWatchTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun signedPluginMonitorsADynamicAssetThroughItsDeclaredHook() = runBlocking<Unit> {
        withWatchPlugin { core ->
            val asset = "minirp://assets/test.asset_watch/"
            suspend fun read(id: String) = JSONObject(core.readAsset(asset + id, 4096).toString(Charsets.UTF_8))
            val update = read("update")
            assertTrue(update.getBoolean("listenable"))
            assertTrue(update.getBoolean("reserved"))
            assertEquals(1, update.getJSONObject("result").getInt("subscribers"))
            val receipt = withTimeout(10_000) {
                var result = read("receipt").optJSONObject("received")
                while (result == null) {
                    delay(20)
                    result = read("receipt").optJSONObject("received")
                }
                result
            }
            assertEquals("asset.changed", receipt.getString("name"))
            assertEquals(asset + "current", receipt.getString("url"))
            assertEquals("minichat://actions/test.asset_watch/events/current-changed", receipt.getString("source"))
            assertEquals("invalidate", receipt.getString("reason"))
            assertEquals("after", receipt.getString("text"))
        }
    }

    @Test
    fun chatSubscribesReceivesChangesAndCancelsThroughAssets() = runBlocking {
        val prefix = "minirp://assets/test.asset_watch/"
        val url = prefix + "current"
        val round = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse = try {
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    val items = body.getJSONArray("messages")
                    val messages = (0 until items.length()).map(items::getJSONObject)
                    val tools = body.optJSONArray("tools") ?: JSONArray()
                    val names = (0 until tools.length()).map {
                        tools.getJSONObject(it).getJSONObject("function").getString("name")
                    }
                    fun call(name: String, args: JSONObject): JSONObject {
                        assertTrue("Missing ${name} in ${names}", name in names)
                        return JSONObject().put("tool_calls", JSONArray().put(JSONObject().put("index", 0)
                            .put("id", "watch-${round.get()}").put("type", "function")
                            .put("function", JSONObject().put("name", name).put("arguments", args.toString()))))
                    }
                    fun activate(name: String) = call("activate_skill", JSONObject().put("name", name))
                    fun watching() = JSONObject(messages.last { it.optString("role") == "tool" }.getString("content"))
                        .getBoolean("watching")
                    val delta = if (names.isEmpty()) JSONObject().put("content", "Watch fixture") else when (round.getAndIncrement()) {
                        0 -> activate("asset-watch-probe-updates")
                        1 -> call("plugin_asset-watch-probe_grant", JSONObject())
                        2 -> activate("assets")
                        3 -> call("watch_asset", JSONObject().put("url", url))
                        4 -> {
                            assertTrue(watching())
                            JSONObject().put("content", "Subscribed")
                        }
                        5 -> {
                            assertEquals(1, messages.count { it.optString("content").contains("Watched Asset $url changed") })
                            call("unwatch_asset", JSONObject().put("url", url))
                        }
                        6 -> {
                            assertEquals(false, watching())
                            JSONObject().put("content", "Unsubscribed")
                        }
                        7 -> {
                            assertEquals(1, messages.count { it.optString("content").contains("Watched Asset $url changed") })
                            JSONObject().put("content", "No new notification")
                        }
                        else -> error("Unexpected watch request")
                    }
                    val event = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream").body("data: $event\n\ndata: [DONE]\n\n").build()
                } catch (error: Throwable) {
                    failure.compareAndSet(null, error)
                    MockResponse.Builder().code(500).build()
                }
            }
            server.start(InetAddress.getByName("127.0.0.1"), 0)
            withWatchPlugin { core ->
                core.send(JSONObject().put("op", "configure").put("provider",
                    JSONObject().put("url", server.url("/v1").toString()).put("model", "watch-fixture")))
                suspend fun turn(text: String, expected: String) {
                    core.send(JSONObject().put("op", "message").put("text", text))
                    val answer = withTimeout(30_000) {
                        core.events.first {
                            failure.get()?.let { error -> throw AssertionError("Asset watch fixture failed", error) }
                            check(it.optString("kind") != "error") { it.toString() }
                            it.optString("kind") == "completed"
                        }
                    }
                    assertEquals(expected, answer.getString("text"))
                }
                turn("Watch the fixture Asset.", "Subscribed")
                core.readAsset(prefix + "update", 4096)
                core.readAsset(prefix + "update", 4096)
                turn("Cancel my subscription.", "Unsubscribed")
                core.readAsset(prefix + "update", 4096)
                turn("Check new changes.", "No new notification")
            }
        }
    }

    private suspend fun withWatchPlugin(run: suspend (CoreClient) -> Unit) {
        val directory = temporary.newFolder()
        val assets = InstrumentationRegistry.getInstrumentation().context.assets
        val files = listOf("manifest.json", "index.js").associateWith { name ->
            assets.open("asset-watch/$name").use { it.readBytes() }
        }
        val archive = writeSignedPlugin(directory.resolve("workspace"), UUID.randomUUID().toString(),
            "asset-watch-probe", files, directory.resolve("asset-watch-probe.plugin"))
        val app = ApplicationProvider.getApplicationContext<Context>()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        try {
            CoreClient(context, scope, seedDefaultPreset = true).use { core ->
                core.query(JSONObject().put("op", "plugin_install").put("path", archive.path))
                run(core)
            }
        } finally {
            scope.cancel()
        }
    }
}
