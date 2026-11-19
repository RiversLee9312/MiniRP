package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.core.BrowserHost
import com.minirp.app.core.CoreClient
import java.io.File
import java.util.UUID
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NativeCoreTest {
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
        val directory = File(app.cacheDir, "preset-probe-${System.nanoTime()}").apply { mkdirs() }
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
    fun nativePackageVerifiesSignerAndStreamsAssets() = runBlocking {
        val fixture = InstrumentationRegistry.getArguments().getString("pluginFixture")
        assumeTrue("Pass a signed stream-assets example with -e pluginFixture", fixture != null)
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = File(app.cacheDir, "asset-probe-${System.nanoTime()}").apply { mkdirs() }
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
                    .query(JSONObject().put("op", "create_preset").put("open", true))
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
                withTimeout(10_000) { core.events.first { it.optString("kind") == "home" } }
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
        val directory = File(app.cacheDir, "legacy-probe-${System.nanoTime()}").apply { mkdirs() }
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
        val directory = File(app.cacheDir, "native-probe-${System.nanoTime()}").apply { mkdirs() }
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
