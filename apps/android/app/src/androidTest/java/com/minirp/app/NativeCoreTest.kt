package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import android.graphics.BitmapFactory
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.core.BrowserHost
import com.minirp.app.core.CoreClient
import java.io.File
import java.io.ByteArrayOutputStream
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.ZipFile
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NativeCoreTest {
    @get:Rule
    val temporary = TemporaryFolder(ApplicationProvider.getApplicationContext<Context>().cacheDir)
    @Test
    fun chatOpenUrlDeliversMarkdownToTheModel() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val url = "https://fixture.invalid/docs/article"
        val round = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        val researchAsset = AtomicReference<String>()
        val pageRef = AtomicReference<String>()
        fun tool(name: String, args: JSONObject) = JSONObject().put("tool_calls", JSONArray().put(
            JSONObject().put("index", 0).put("id", "tool-${round.get()}").put("type", "function")
                .put("function", JSONObject().put("name", name).put("arguments", args.toString()))
        ))
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse = try {
                    assertEquals("Mini Agent Harness/${BuildConfig.VERSION_NAME}", request.headers["User-Agent"])
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    if (request.url.encodedPath == "/v1/tools/fetch") {
                        assertEquals(url, body.getString("url"))
                        MockResponse.Builder().setHeader("Content-Type", "application/json").body(JSONObject()
                            .put("content", "<html><head><title>Source</title></head><body><nav>Junk</nav>" +
                                "<main><h1>Article</h1><a href='next'>Next</a></main></body></html>").toString()).build()
                    } else {
                        val messages = body.getJSONArray("messages")
                        val latest = messages.getJSONObject(messages.length() - 1)
                        val delta = when (round.incrementAndGet()) {
                            1 -> {
                                val tools = body.getJSONArray("tools")
                                assertTrue((0 until tools.length()).any { index ->
                                    tools.getJSONObject(index).getJSONObject("function").getString("name") == "open_url"
                                })
                                tool("open_url", JSONObject().put("url", url))
                            }
                            2 -> tool("real_fetch", JSONObject().put("url", url))
                            3 -> {
                                val result = JSONObject(latest.getString("content"))
                                researchAsset.set(result.getString("asset_url"))
                                pageRef.set(result.getString("ref"))
                                assertTrue(researchAsset.get().startsWith("minirp://assets/minirp.base/research-"))
                                tool("activate_skill", JSONObject().put("name", "assets"))
                            }
                            4 -> tool("text_edit", JSONObject().put("url", researchAsset.get())
                                .put("old_string", "# Article").put("new_string", "# Updated article"))
                            5 -> {
                                assertEquals(1, JSONObject(latest.getString("content")).getInt("changes"))
                                tool("submit_page", JSONObject().put("ref", pageRef.get()))
                            }
                            6 -> {
                                val page = JSONObject(latest.getString("content"))
                                assertEquals("Source", page.getString("title"))
                                assertEquals(url, page.getString("url"))
                                assertEquals(researchAsset.get(), page.getString("asset_url"))
                                assertEquals("# Updated article\n\n[Next](https://fixture.invalid/docs/next)", page.getString("content"))
                                JSONObject().put("content", "Page read. [Save page](${researchAsset.get()})")
                            }
                            else -> JSONObject().put("content", "Article")
                        }
                        val event = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                        MockResponse.Builder().setHeader("Content-Type", "text/event-stream").body("data: $event\n\ndata: [DONE]\n\n").build()
                    }
                } catch (error: Throwable) {
                    failure.compareAndSet(null, error)
                    MockResponse.Builder().code(500).body("Unexpected request").build()
                }
            }
            server.start()
            val core = CoreClient(object : ContextWrapper(app) { override fun getFilesDir() = directory }, scope, seedDefaultPreset = true)
            try {
                val preset = seededPreset(core)
                core.send(JSONObject().put("op", "configure")
                    .put("provider", JSONObject().put("url", server.url("/v1")).put("model", "fixture"))
                    .put("settings", JSONObject().put("search_key", "test").put("search_url", server.url("/").toString())
                        .put("fetch_backend", "api")))
                core.send(JSONObject().put("op", "message").put("text", "Open the source."))
                var block = ""
                var room = ""
                val completed = withTimeout(20_000) {
                    core.events.first { event ->
                        failure.get()?.let { throw AssertionError("open_url fixture failed", it) }
                        check(event.optString("kind") != "error") { event.toString() }
                        if (event.optString("kind") == "loaded") room = event.getJSONObject("chat").getString("id")
                        if (event.optString("kind") == "turn_end" &&
                            event.optJSONObject("asset_links")?.has(researchAsset.get()) == true) block = event.getString("block")
                        event.optString("kind") == "completed"
                    }
                }
                assertEquals("Page read. [Save page](${researchAsset.get()})", completed.getString("text"))
                val download = JSONObject().put("op", "display_asset_open").put("url", researchAsset.get())
                    .put("preset", preset).put("room", room).put("block", block)
                assertEquals("Source.md", core.displayAssetInfo(download).getString("name"))
                val bytes = ByteArrayOutputStream().also { core.saveDisplayAsset(download, it) }
                assertEquals("# Updated article\n\n[Next](https://fixture.invalid/docs/next)", bytes.toString(Charsets.UTF_8))
            } finally {
                core.close()
                scope.cancel()
                directory.deleteRecursively()
            }
        }
    }

    @Test
    fun longPresetSessionAppliesOneCheckpointAndSendsOnlyTheRetainedContext() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) {
            override fun getFilesDir() = directory
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        MockWebServer().use { server ->
            server.start()
            fun reply(text: String, promptTokens: Int = 0) {
                val event = JSONObject().put("choices", JSONArray().put(
                    JSONObject().put("delta", JSONObject().put("content", text))
                ))
                if (promptTokens > 0) event.put("usage", JSONObject().put("prompt_tokens", promptTokens))
                server.enqueue(MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                    .body("data: $event\n\ndata: [DONE]\n\n").build())
            }
            reply("已记录。", promptTokens = 16_400)
            reply("接续状态：保留绿色灯笼。")
            reply("继续编辑。")
            reply("完成。")
            val core = CoreClient(context, scope)
            try {
                val preset = core.query(JSONObject().put("op", "create_preset").put("open", true)
                    .put("interview", false).put("name", "长预设回归")).getString("preset")
                core.send(JSONObject().put("op", "configure").put("provider", JSONObject()
                    .put("id", "fixture").put("protocol", "openai").put("url", server.url("/v1").toString())
                    .put("model", "fixture").put("context_window", 20_000).put("max_output", 2_000)
                    .put("compact_at_percent", 80).put("background_compaction", false)))
                val original = "绿色灯笼原始记录 " + "长".repeat(5_000)
                suspend fun turn(text: String, answer: String) {
                    core.send(JSONObject().put("op", "message").put("agent", "preset")
                        .put("preset", preset).put("text", text))
                    val completed = withTimeout(15_000) {
                        core.events.first { event ->
                            check(event.optString("kind") != "error") { event.toString() }
                            event.optString("kind") == "completed" && event.optString("agent") == "preset"
                        }
                    }
                    assertEquals(answer, completed.getString("text"))
                }
                turn(original, "已记录。")
                turn("继续编辑预设", "继续编辑。")
                turn("再改一处", "完成。")
                assertEquals(4, server.requestCount)
                val requests = List(4) {
                    JSONObject(checkNotNull(server.takeRequest(2, java.util.concurrent.TimeUnit.SECONDS))
                        .body!!.utf8())
                }
                val messages = requests.map { it.getJSONArray("messages") }
                assertEquals(original, messages[0].getJSONObject(messages[0].length() - 1).getString("content"))
                val checkpoint = messages[1]
                assertTrue(checkpoint.getJSONObject(0).getString("content").contains("PresetAgent"))
                assertTrue((1 until checkpoint.length()).any {
                    checkpoint.getJSONObject(it).getString("content").startsWith("Write a continuation checkpoint")
                })
                val tools = requests[1].getJSONArray("tools")
                assertEquals(setOf("db_list", "db_read", "db_search", "history_search", "history_recall"),
                    (0 until tools.length()).map { tools.getJSONObject(it).getJSONObject("function").getString("name") }.toSet())
                for (retained in messages.drop(2)) {
                    assertTrue(retained.getJSONObject(0).getString("content").contains("保留绿色灯笼"))
                    assertFalse(retained.toString().contains(original))
                }
                assertEquals("再改一处", messages.last().getJSONObject(messages.last().length() - 1).getString("content"))
            } finally {
                core.close()
                scope.cancel()
                directory.deleteRecursively()
            }
        }
    }

    @Test
    fun aPresetTurnRetriesAServiceUnavailableResponseOnAndroid() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(503).body("Fixture provider unavailable").build())
            server.enqueue(MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                .body("data: {\"choices\":[{\"delta\":{\"content\":\"Preset reply after retry.\"}}]}\n\ndata: [DONE]\n\n").build())
            val core = CoreClient(context, scope)
            try {
                val preset = core.query(JSONObject().put("op", "create_preset").put("open", true)
                    .put("interview", false).put("name", "Retry fixture")).getString("preset")
                core.send(JSONObject().put("op", "configure").put("provider", JSONObject()
                    .put("id", "fixture").put("protocol", "openai").put("url", server.url("/v1").toString())
                    .put("model", "fixture")))
                val text = "Edit the existing preset."
                core.send(JSONObject().put("op", "message").put("agent", "preset").put("preset", preset).put("text", text))
                val completed = withTimeout(15_000) {
                    core.events.first { event ->
                        check(event.optString("kind") != "error") { event.optString("message") }
                        event.optString("kind") == "completed" && event.optString("agent") == "preset"
                    }
                }
                assertEquals("Preset reply after retry.", completed.getString("text"))
                assertEquals(2, server.requestCount)
                val messages = List(2) {
                    JSONObject(checkNotNull(server.takeRequest(2, java.util.concurrent.TimeUnit.SECONDS))
                        .body!!.utf8()).getJSONArray("messages")
                }
                assertEquals(messages[0].toString(), messages[1].toString())
                assertEquals(text, messages[1].getJSONObject(messages[1].length() - 1).getString("content"))
            } finally {
                core.close()
                scope.cancel()
            }
        }
    }

    @Test
    fun nativeCoreRestoresPresetAndRoomFromPortableFiles() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        fun client(name: String) = CoreClient(
            object : ContextWrapper(app) {
                override fun getFilesDir() = directory.resolve(name).apply { mkdirs() }
            },
            scope,
        )
        fun command(op: String) = JSONObject().put("op", op)
        val source = client("source")
        val target = client("target")
        try {
            val preset = source.query(command("create_preset").put("name", "Portable fixture")
                .put("open", true).put("interview", false)).getString("preset")
            source.send(command("select_chat").put("preset", preset))
            val loaded = withTimeout(10_000) { source.events.first { it.optString("kind") == "loaded" } }
            val room = loaded.getJSONObject("chat").getString("id")
            source.send(command("system_message").put("chat", room).put("preset", preset).put("text", "Persistent Room note"))
            withTimeout(10_000) { source.events.first { it.optString("kind") == "system" } }

            suspend fun export(type: String): File {
                val file = directory.resolve("portable.minirp-$type")
                val result = source.query(
                    command("archive_export").put("type", type).put("preset", preset)
                        .put("room", room).put("path", file.path),
                )
                assertEquals("archive_exported", result.getString("kind"))
                return file
            }
            val roomFile = export("room")
            val db = "conversations/$preset/$room/agents/chat.sqlite"
            ZipFile(roomFile).use {
                assertNotNull(it.getEntry("conversations/$preset/$room.json"))
                assertNotNull(it.getEntry(db))
            }
            suspend fun restore(type: String) {
                val file = export(type)
                val result = target.query(command("archive_import").put("type", type).put("path", file.path))
                assertEquals("archive_imported", result.getString("kind"))
                assertEquals(preset, result.getString("preset"))
            }
            restore("preset")
            assertFalse(directory.resolve("target/workspace/$db").exists())
            restore("save")
            assertTrue(directory.resolve("target/workspace/$db").isFile)
            target.send(command("select_chat").put("preset", preset).put("id", room))
            val restored = withTimeout(10_000) { target.events.first { it.optString("kind") == "loaded" } }
            assertEquals("Persistent Room note",
                restored.getJSONObject("chat").getJSONArray("blocks").getJSONObject(0).getString("content"))
        } finally {
            source.close()
            target.close()
            scope.cancel()
            directory.deleteRecursively()
        }
    }

    @Test
    fun renamedAppKeepsItsLauncherComponent() {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val launcher = app.packageManager.getLaunchIntentForPackage(app.packageName)
        assertEquals("MiniRP", app.getString(R.string.app_name))
        assertEquals("com.minichat.app.MainActivity", launcher?.component?.className)
    }

    @Test
    fun presetEditorUsesItsOwnSessionWithoutCreatingAChat() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val root =
            object : ContextWrapper(app) {
                override fun getFilesDir(): File = directory
            }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val core = CoreClient(root, scope)
        try {
            val initial =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "ready" } }
            assertEquals("", initial.getString("preset"))
            val created =
                core.query(
                    JSONObject()
                        .put("op", "create_preset")
                        .put("open", true)
                        .put("name", "Preset fixture")
                )
            assertEquals("preset_opened", created.getString("kind"))
            assertTrue(created.getBoolean("created"))
            val preset = created.getString("preset")
            assertEquals(preset, UUID.fromString(preset).toString())
            core.send(
                JSONObject()
                    .put("op", "system_message")
                    .put("agent", "preset")
                    .put("preset", preset)
                    .put("text", "Preset-only note")
            )
            withTimeout(10_000) { core.events.first { it.optString("kind") == "system" } }
            val reopened = core.query(JSONObject().put("op", "open_preset").put("preset", preset))
            assertEquals(
                "Preset-only note",
                reopened.getJSONArray("blocks").getJSONObject(0).getString("content"),
            )
            assertFalse(directory.resolve("workspace/conversations").exists())
            core.query(
                JSONObject().put("op", "delete_preset").put("id", preset).put("preset", preset)
            )
            val home = withTimeout(10_000) { core.events.first { it.optString("kind") == "home" } }
            assertEquals("", home.getString("preset"))
            val page = core.query(JSONObject().put("op", "library_page"))
            assertEquals(0, page.getJSONArray("items").length())
        } finally {
            core.close()
            scope.cancel()
        }
    }

    @Test
    fun signedPluginSettingIconLoadsThroughTheNativeAssetStream() = runBlocking {
        val fixture = InstrumentationRegistry.getArguments().getString("settingIconFixture")
        assumeTrue("Pass the signed room-status example with -e settingIconFixture", fixture != null)
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val core = CoreClient(context, scope)
        try {
            withTimeout(25_000) {
                core.query(JSONObject().put("op", "plugin_install").put("path", fixture))
                val settings = core.query(JSONObject().put("op", "plugin_settings")).getJSONArray("items")
                val setting = (0 until settings.length()).map(settings::getJSONObject)
                    .single { it.getString("key") == "about-status" }
                val url = setting.getString("icon")
                assertEquals("minirp://assets/example.minichat.roomstatus/settings-icon", url)
                val png = core.readAsset(url, 256 * 1024)
                val image = requireNotNull(BitmapFactory.decodeByteArray(png, 0, png.size))
                assertEquals(48, image.width)
                assertEquals(48, image.height)
            }
        } finally {
            core.close()
            scope.cancel()
        }
    }

    @Test
    fun signedPluginToolEmitsBothPresentationsThroughTheNativeCore() = runBlocking {
        val fixture = InstrumentationRegistry.getArguments().getString("settingIconFixture")
        assumeTrue("Pass the signed room-status example with -e settingIconFixture", fixture != null)
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val core = CoreClient(context, scope, seedDefaultPreset = true)
        val line = "Gate closed."
        val tool = "plugin_room-status_report"
        fun stream(delta: JSONObject) = MockResponse.Builder()
            .setHeader("Content-Type", "text/event-stream")
            .body("data: " + JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta))) + "\n\ndata: [DONE]\n\n")
            .build()
        fun call(id: String, name: String, arguments: JSONObject) = stream(
            JSONObject().put("tool_calls", JSONArray().put(JSONObject()
                .put("index", 0).put("id", id).put("type", "function")
                .put("function", JSONObject().put("name", name).put("arguments", arguments.toString())))),
        )
        try {
            MockWebServer().use { server ->
                server.start()
                server.enqueue(call("activate", "activate_skill", JSONObject().put("name", "room-status-status")))
                server.enqueue(call("report", tool, JSONObject().put("line", line)))
                server.enqueue(stream(JSONObject().put("content", "Report saved.")))
                val preset = seededPreset(core)
                core.query(JSONObject().put("op", "plugin_install").put("path", fixture))
                core.query(JSONObject().put("op", "plugin_locale").put("locale", "zh-Hans"))
                core.send(JSONObject().put("op", "configure")
                    .put("provider", JSONObject().put("protocol", "openai")
                        .put("url", server.url("/v1").toString()).put("model", "fixture").put("key", "fixture"))
                    .put("agents", JSONObject().put("title", JSONObject())))
                core.send(JSONObject().put("op", "select_chat").put("preset", preset))
                core.send(JSONObject().put("op", "message").put("text", "Verify the station.").put("locale", "zh-Hans"))
                var arguments: JSONObject? = null
                val result = withTimeout(30_000) {
                    core.events.first { event ->
                        check(event.optString("kind") != "error") { event.toString() }
                        if (event.optString("name") != tool) return@first false
                        if (event.optString("kind") == "tool_start") arguments = event.getJSONObject("tool_arguments_ui")
                        event.optString("kind") == "tool_end"
                    }.getJSONObject("tool_result_ui")
                }
                val input = requireNotNull(arguments)
                assertEquals("上报的状态", input.getString("title"))
                assertEquals("已保存的状态", result.getString("title"))
                assertEquals(line, input.getJSONObject("state").getJSONObject("data").getString("line"))
                assertEquals(line, result.getJSONObject("state").getJSONObject("data").getJSONObject("value").getString("line"))
                assertNotEquals(input.getString("program"), result.getString("program"))
            }
        } finally {
            core.close()
            scope.cancel()
        }
    }

    @Test
    fun installedModuleSuppliesAnAssetThroughTheNativeCore() = runBlocking {
        val fixture = InstrumentationRegistry.getArguments().getString("pluginFixture")
        assumeTrue("Pass the signed stream-assets example with -e pluginFixture", fixture != null)
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val core = CoreClient(context, scope)
        try {
            withTimeout(25_000) {
                val installed = core.query(JSONObject().put("op", "plugin_install").put("path", fixture))
                assertEquals("stream-example", installed.getJSONObject("item").getString("id"))
                val bytes = core.readAsset("minirp://assets/example.minichat.stream/sample", 2 * 1024 * 1024)
                assertEquals(1_000_003, bytes.size)
                assertTrue(bytes.all { it == 42.toByte() })
            }
        } finally {
            core.close()
            scope.cancel()
        }
    }

    @Test
    fun nativePackageVerifiesSignerAndStreamsAssets() = runBlocking {
        val fixture = InstrumentationRegistry.getArguments().getString("pluginFixture")
        assumeTrue("Pass a signed stream-assets example with -e pluginFixture", fixture != null)
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val root =
            object : ContextWrapper(app) {
                override fun getFilesDir(): File = directory
            }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val core = CoreClient(root, scope, seedDefaultPreset = true)
        try {
            val initialPreset = seededPreset(core)
            val builtIns = core.query(JSONObject().put("op", "plugins_list")).getJSONArray("items")
            assertEquals(1, builtIns.length())
            assertEquals("base", builtIns.getJSONObject(0).getString("name"))
            val licensePage =
                core
                    .query(
                        JSONObject()
                            .put("op", "plugin_page")
                            .put("id", "base")
                            .put("key", "notices")
                    )
                    .getJSONObject("item")
            val licenseUrl =
                licensePage.getJSONArray("items").getJSONObject(1).getString("assetUrl")
            assertEquals("minirp://assets/minirp.base/project-license", licenseUrl)
            assertTrue(
                core.readAsset(licenseUrl, 64 * 1024).decodeToString().contains("MIT License")
            )

            val packageInfo =
                core
                    .query(JSONObject().put("op", "plugin_install").put("path", fixture))
                    .getJSONObject("item")
            assertEquals("example.minichat.stream", packageInfo.getString("packageName"))
            val signer = packageInfo.getString("signer")
            assertEquals(64, signer.length)
            val docsUrl = "minirp://assets/example.minichat.stream/about"
            val second =
                core
                    .query(JSONObject().put("op", "create_preset").put("open", true).put("interview", false))
                    .getString("preset")
            fun selectScope(
                mode: String,
                ids: List<String> = emptyList(),
                id: String = "stream-example",
            ) =
                JSONObject()
                    .put("op", "plugin_scope")
                    .put("id", id)
                    .put("scope", JSONObject().put("mode", mode).put("presets", JSONArray(ids)))
            suspend fun home(id: String) {
                core.send(JSONObject().put("op", "home").put("preset", id))
                withTimeout(10_000) {
                    core.events.first { event ->
                        check(event.optString("kind") != "error") { event.optString("message") }
                        event.optString("kind") == "home" && event.optString("preset") == id
                    }
                }
            }
            assertTrue(
                runCatching { core.query(selectScope("include", listOf("default"))) }.isFailure
            )
            core.query(selectScope("include", listOf(initialPreset)))
            assertTrue(runCatching { core.readAsset(docsUrl, 64 * 1024) }.isFailure)
            home(initialPreset)
            val docs = core.readAsset(docsUrl, 64 * 1024)
            assertTrue(docs.decodeToString().contains("Stream assets example"))
            assertArrayEquals(
                docs,
                core.readAsset("minichat://assets/example.minichat.stream/about", 64 * 1024),
            )
            core.query(selectScope("exclude", listOf(initialPreset)))
            assertTrue(runCatching { core.readAsset(docsUrl, 64 * 1024) }.isFailure)
            home(second)
            assertArrayEquals(docs, core.readAsset(docsUrl, 64 * 1024))
            assertTrue(
                runCatching { core.query(selectScope("exclude", listOf(second), "base")) }.isFailure
            )
            core.query(selectScope("global"))
            home(initialPreset)
            val chunks =
                core.readAsset("minirp://assets/example.minichat.stream/sample", 2 * 1024 * 1024)
            assertEquals(1_000_003, chunks.size)
            assertTrue(chunks.all { it == 42.toByte() })

            val open =
                core
                    .query(JSONObject().put("op", "asset_open").put("url", docsUrl))
                    .getJSONObject("result")
                    .getString("handle")
            val pull = { JSONObject().put("op", "asset_pull").put("handle", open) }
            val firstPart = core.query(pull()).getJSONObject("result").getString("transfer")
            assertArrayEquals(docs, core.takeBinary(firstPart))
            core.query(JSONObject().put("op", "plugin_install").put("path", fixture))
            assertTrue(runCatching { core.query(pull()) }.isFailure)
            assertArrayEquals(docs, core.readAsset(docsUrl, 64 * 1024))

            core.send(JSONObject().put("op", "system_message").put("text", "Fixture context"))
            val loaded =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "loaded" } }
            val chat = loaded.getJSONObject("chat").getString("id")
            core.send(JSONObject().put("op", "plugin_command").put("id", "stream-example"))
            val database =
                directory.resolve(
                    "workspace/plugins/data/example.minichat.stream.$signer/chat/$chat.sqlite"
                )
            val wal = File(database.parentFile, database.name + "-wal")
            val snapshot = directory.resolve("plugin-db-check.sqlite")
            withTimeout(20_000) {
                var bytes: String? = null
                while (bytes == null) {
                    delay(80)
                    if (database.isFile && !wal.exists()) {
                        // Android SQLite and HarnessCore bundle separate SQLite libraries.
                        // Inspect a checkpointed copy so they never share live WAL files.
                        database.copyTo(snapshot, overwrite = true)
                        bytes =
                            try {
                                android.database.sqlite.SQLiteDatabase.openDatabase(
                                        snapshot.absolutePath,
                                        null,
                                        android.database.sqlite.SQLiteDatabase.OPEN_READONLY,
                                    )
                                    .use { db ->
                                        db.rawQuery(
                                                "SELECT content FROM entries WHERE key = ?",
                                                arrayOf("stream-example/last-size"),
                                            )
                                            .use { if (it.moveToFirst()) it.getString(0) else null }
                                    }
                            } catch (_: android.database.sqlite.SQLiteException) {
                                null
                            }
                    }
                }
                assertEquals("1000003", bytes)
            }
        } finally {
            core.close()
            scope.cancel()
        }
    }

    @Test
    fun nativeCoreLoadsLegacyChatAndRunsBuiltInV8Plugin() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        File(directory, "workspace/presets")
            .apply { mkdirs() }
            .resolve("default.json")
            .writeText("""{"name":"Default"}""")
        val parent = File(directory, "workspace/conversations/default").apply { mkdirs() }
        File(parent, "legacy.json")
            .writeText(
                """{"name":"Old","chat":{"history":[{"role":"user","content":"旧记录🙂"}]},"trace":[false,null],"blocks":{}}"""
            )
        val context =
            object : ContextWrapper(app) {
                override fun getFilesDir(): File = directory
            }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val core = CoreClient(context, scope, seedDefaultPreset = true)
        try {
            val preset = seededPreset(core)
            val migrated =
                core
                    .query(JSONObject().put("op", "library_page").put("preset", preset))
                    .getJSONArray("items")
                    .getJSONObject(0)
                    .getString("id")
            assertEquals(migrated, UUID.fromString(migrated).toString())
            core.send(
                JSONObject().put("op", "select_chat").put("preset", preset).put("id", migrated)
            )
            val loaded =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "loaded" } }
            assertEquals(
                "旧记录🙂",
                loaded
                    .getJSONObject("chat")
                    .getJSONArray("blocks")
                    .getJSONObject(0)
                    .getString("content"),
            )
        } finally {
            core.close()
            scope.cancel()
        }
    }

    @Test
    fun nativeCoreStreamsToolsAndPersistsUnicodeMemory() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val context =
            object : ContextWrapper(app) {
                override fun getFilesDir(): File = directory
            }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val server = MockWebServer()
        server.start()
        val tool =
            JSONObject()
                .put("index", 0)
                .put("id", "memory-write")
                .put("type", "function")
                .put(
                    "function",
                    JSONObject()
                        .put("name", "db_write")
                        .put("arguments", """{"key":"probe/answer","content":"完整正文🙂"}"""),
                )
        fun event(delta: JSONObject) =
            "data: " +
                JSONObject()
                    .put("choices", org.json.JSONArray().put(JSONObject().put("delta", delta))) +
                "\n\ndata: [DONE]\n\n"
        server.enqueue(
            MockResponse.Builder()
                .setHeader("Content-Type", "text/event-stream")
                .body(
                    event(
                        JSONObject()
                            .put("reasoning_content", "记住任务")
                            .put("tool_calls", org.json.JSONArray().put(tool))
                    )
                )
                .build()
        )
        server.enqueue(
            MockResponse.Builder()
                .setHeader("Content-Type", "text/event-stream")
                .body(event(JSONObject().put("content", "已保存🙂")))
                .build()
        )
        val core = CoreClient(context, scope, seedDefaultPreset = true)
        try {
            val preset = seededPreset(core)
            core.send(
                JSONObject()
                    .put("op", "configure")
                    .put(
                        "provider",
                        JSONObject()
                            .put("protocol", "openai")
                            .put("url", server.url("/v1").toString())
                            .put("model", "fixture")
                            .put("key", "test"),
                    )
            )
            core.send(JSONObject().put("op", "select_chat").put("preset", preset))
            core.send(JSONObject().put("op", "rename").put("name", "Branch fixture"))
            val attachment =
                directory.resolve("workspace/attachments/notes").apply {
                    parentFile!!.mkdirs()
                    writeText("%PDF-1.7")
                }
            core.send(
                JSONObject()
                    .put("op", "message")
                    .put("text", "记住任务")
                    .put(
                        "attachments",
                        org.json
                            .JSONArray()
                            .put(
                                JSONObject()
                                    .put("id", attachment.name)
                                    .put("name", "notes.pdf")
                                    .put("mime", "application/pdf")
                            ),
                    )
            )
            var toolFinished = false
            var thoughts = false
            var original = ""
            var source = ""
            val completed =
                withTimeout(30_000) {
                    core.events.first { event ->
                        check(event.optString("kind") != "error") { event.toString() }
                        if (event.optString("kind") == "tool_end") toolFinished = true
                        if (event.optString("type") == "reasoning") thoughts = true
                        if (event.optString("kind") == "loaded")
                            original = event.getJSONObject("chat").getString("id")
                        if (event.optString("kind") == "user") source = event.getString("block")
                        event.optString("kind") == "completed"
                    }
                }
            assertEquals("已保存🙂", completed.getString("text"))
            assertTrue(toolFinished)
            assertTrue(thoughts)
            val footer =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "reply_end" } }
            assertEquals("已保存🙂", footer.getString("reply"))
            core.send(
                JSONObject()
                    .put("op", "memory")
                    .put("tool", "db_read")
                    .put("arguments", JSONObject().put("key", "probe/answer"))
            )
            val memory =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "memory" } }
            assertEquals("完整正文🙂", memory.getJSONObject("result").getString("content"))

            core.send(
                JSONObject()
                    .put("op", "branch")
                    .put("chat", original)
                    .put("block", source)
                    .put("mode", "edit")
            )
            val edited =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "loaded" } }
            val draft = edited.getJSONObject("draft")
            assertEquals("记住任务", draft.getString("text"))
            assertEquals(
                "notes.pdf",
                draft.getJSONArray("attachments").getJSONObject(0).getString("name"),
            )
            assertEquals(0, edited.getJSONObject("chat").getJSONArray("blocks").length())

            core.send(
                JSONObject().put("op", "select_chat").put("preset", preset).put("id", original)
            )
            withTimeout(10_000) { core.events.first { it.optString("kind") == "loaded" } }
            core.send(
                JSONObject()
                    .put("op", "branch")
                    .put("chat", original)
                    .put("block", footer.getString("id"))
                    .put("mode", "reply")
            )
            val continued =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "loaded" } }
            assertTrue(continued.isNull("draft"))
            val blocks = continued.getJSONObject("chat").getJSONArray("blocks")
            assertEquals(
                footer.getString("id"),
                blocks.getJSONObject(blocks.length() - 1).getString("id"),
            )
            withTimeout(10_000) {
                core.events.first { it.optString("kind") == "busy" && !it.optBoolean("value") }
            }
            assertEquals(2, server.requestCount)
        } finally {
            core.close()
            scope.cancel()
            server.close()
        }
    }

    @Test
    fun webViewCapturesCompleteMarkdown() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val server = MockWebServer()
        server.start()
        val html =
            "<html><head><title>Local capture</title></head><body><h1>Article</h1><p>" +
                "完整正文 retained text. ".repeat(10_000) +
                "</p><a href='/end'>FINALMARKER</a></body></html>"
        repeat(3) {
            server.enqueue(
                MockResponse.Builder()
                    .setHeader("Content-Type", "text/html; charset=utf-8")
                    .body(html)
                    .build()
            )
        }
        val browser = BrowserHost(app)
        fun request(tool: String, arguments: JSONObject = JSONObject()) =
            JSONObject().put("job", "probe").put("tool", tool).put("arguments", arguments)
        try {
            browser.execute(
                request("browser_navigate", JSONObject().put("url", server.url("/").toString()))
            )
            val snapshot = browser.execute(request("browser_snapshot")).getString("snapshot")
            assertTrue(snapshot.contains("FINALMARKER — " + server.url("/end")))
            val page = browser.execute(request("capture_page"))
            assertTrue(page.getString("content").length > 150_000)
            assertTrue(page.getString("content").contains("FINALMARKER"))
            assertTrue(page.getString("content").contains(server.url("/end").toString()))
        } finally {
            withContext(Dispatchers.Main) { browser.close() }
            server.close()
        }
    }

    private suspend fun seededPreset(core: CoreClient): String =
        withTimeout(10_000) { core.events.first { it.optString("kind") == "ready" } }
            .getString("preset")
            .also { assertEquals(it, UUID.fromString(it).toString()) }
}
