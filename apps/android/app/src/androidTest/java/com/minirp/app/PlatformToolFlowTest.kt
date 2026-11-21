package com.minirp.app

import android.content.ContextWrapper
import android.app.Activity
import android.content.Intent
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.pdf.PdfDocument
import android.net.Uri
import androidx.activity.ComponentActivity
import androidx.activity.compose.LocalActivityResultRegistryOwner
import androidx.activity.result.ActivityResultRegistry
import androidx.activity.result.ActivityResultRegistryOwner
import androidx.activity.result.contract.ActivityResultContract
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performImeAction
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.printToString
import androidx.core.app.ActivityOptionsCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.minirp.app.data.Preferences
import com.minirp.app.core.CoreClient
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
import java.io.File
import java.security.MessageDigest
import java.util.Base64
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.ZipFile
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PlatformToolFlowTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun newPresetDelegatesFirstSigningDebuggingAndPublishingThenRollsDice() {
        val app = compose.activity.application
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) {
            override fun getFilesDir(): File = directory
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val fixture = ToolModel(directory.resolve("workspace"))
        MockWebServer().use { server ->
            server.dispatcher = fixture
            server.start()
            try {
                val preferences = Preferences(
                    PreferenceDataStoreFactory.create(scope = scope) {
                        directory.resolve("flow.preferences_pb")
                    }
                )
                runBlocking {
                    preferences.save(
                        JSONArray().put(json(
                            "id" to "fixture",
                            "name" to "Fixture",
                            "protocol" to "openai",
                            "model" to "fixture",
                            "url" to server.url("/v1").toString(),
                        )),
                        JSONObject(),
                    )
                }
                val model = compose.runOnIdle {
                    ViewModelProvider.create(
                        compose.activity,
                        viewModelFactory { initializer { ChatViewModel(app, context, preferences) } },
                    )[ChatViewModel::class.java]
                }
                val document = AtomicReference<File>()
                val registry = object : ActivityResultRegistry(), ActivityResultRegistryOwner {
                    override val activityResultRegistry: ActivityResultRegistry get() = this

                    override fun <I, O> onLaunch(
                        requestCode: Int, contract: ActivityResultContract<I, O>, input: I,
                        options: ActivityOptionsCompat?,
                    ) {
                        val selected = checkNotNull(document.getAndSet(null))
                        val chooser = contract.createIntent(compose.activity, input)
                        if (chooser.action == Intent.ACTION_CREATE_DOCUMENT) {
                            assertEquals(selected.extension, chooser.getStringExtra(Intent.EXTRA_TITLE)?.substringAfterLast('.'))
                        } else {
                            assertEquals(Intent.ACTION_OPEN_DOCUMENT, chooser.action)
                        }
                        dispatchResult(requestCode, Activity.RESULT_OK, Intent().setData(Uri.fromFile(selected)))
                    }
                }
                compose.setContent {
                    CompositionLocalProvider(LocalActivityResultRegistryOwner provides registry) { MiniRP(model) }
                }
                fun label(id: Int) = compose.activity.getString(id)
                fun waitFor(condition: () -> Boolean) {
                    compose.waitUntil(30_000) {
                        fixture.failure.get()?.let { throw AssertionError("Tool flow failed", it) }
                        condition()
                    }
                }
                fun send(message: String, inPresetEditor: Boolean = false) {
                    val composer = hasTestTag("composer")
                    val field = compose.onNode(
                        if (inPresetEditor) composer and hasAnyAncestor(isDialog()) else composer
                    )
                    field.performTextInput(message)
                    field.performImeAction()
                }

                val create = compose.onNodeWithText(label(R.string.create_preset))
                waitFor { model.state.value.ready && create.isDisplayed() }
                assertEquals("", model.state.value.preset)
                create.performClick()
                waitFor {
                    model.state.value.presetEditorOpen && !model.state.value.busy &&
                        model.state.value.blocks.any { it.content == Interview }
                }
                val savedPreset = directory.resolve("workspace/presets/${model.state.value.preset}.json")
                assertEquals("real", JSONObject(savedPreset.readText()).getString("date_mode"))
                send(SetupRequest, inPresetEditor = true)
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == SetupReply } &&
                        model.state.value.installedPlugins.any { it.name == ProjectName }
                }
                val calendar = JSONObject(savedPreset.readText())
                assertEquals("plugin", calendar.getString("date_mode"))
                assertEquals("2032-02-29", calendar.getString("sim_date"))
                val worker = model.state.value.blocks.single { it.pluginStatus == "finished" }
                assertEquals(ProjectName, worker.pluginAgentName)
                assertTrue(model.state.value.blocks.any { it.pluginRun == worker.pluginRun && it.content == "Plugin published." })
                compose.onNodeWithContentDescription(label(R.string.close)).performClick()
                waitFor { !model.state.value.presetEditorOpen }
                send(DiceRequest)
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == DiceReply }
                }
                val pdf = directory.resolve("asset-probe.pdf")
                val fixturePdf = PdfDocument()
                try {
                    val page = fixturePdf.startPage(PdfDocument.PageInfo.Builder(100, 200, 1).create())
                    page.canvas.drawColor(Color.RED)
                    fixturePdf.finishPage(page)
                    pdf.outputStream().use(fixturePdf::writeTo)
                } finally { fixturePdf.close() }
                document.set(pdf)
                compose.onNodeWithContentDescription(label(R.string.add_attachment)).performClick()
                compose.onNodeWithText(label(R.string.choose_files)).performClick()
                waitFor { compose.onNodeWithText(pdf.name).isDisplayed() }
                send(AssetRequest)
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == AssetReply }
                }
                assertTrue(model.state.value.chatId.isNotBlank())
                val preset = model.state.value.preset
                val room = model.state.value.chatId
                val plugin = model.state.value.installedPlugins.single { it.name == ProjectName }
                val menu = compose.onNodeWithContentDescription(label(R.string.open_menu))
                val presetSelector = compose.onNodeWithTag("library-presets")
                val importPreset = compose.onNodeWithText(label(R.string.import_preset))
                fun openLibrary() {
                    try {
                        compose.runOnIdle {
                            val window = compose.activity.window
                            WindowCompat.getInsetsController(window, window.decorView)
                                .hide(WindowInsetsCompat.Type.ime())
                        }
                        compose.waitForIdle()
                        if (presetSelector.isDisplayed()) return
                        waitFor { menu.isDisplayed() }
                        menu.performTouchInput { click() }
                        waitFor { presetSelector.isDisplayed() }
                    } catch (error: Throwable) {
                        throw AssertionError(compose.onRoot(useUnmergedTree = true).printToString(), error)
                    }
                }
                fun openPresetList() {
                    openLibrary()
                    if (!importPreset.isDisplayed()) presetSelector.performClick()
                    waitFor { importPreset.isDisplayed() }
                }
                fun pickFromLibrary(id: String, option: Int, file: File) {
                    val row = compose.onNodeWithTag("library-choice-$id")
                    waitFor { row.isDisplayed() }
                    row.performTouchInput { longClick() }
                    document.set(file)
                    compose.onNodeWithText(label(option)).performClick()
                    waitFor { !model.state.value.archiveBusy && file.isFile && file.length() > 0 }
                }
                val exportedRoom = directory.resolve("portable.minirp-room")
                openLibrary()
                pickFromLibrary(room, R.string.export_chat, exportedRoom)
                ZipFile(exportedRoom).use { assertNotNull(it.getEntry("conversations/$preset/$room.json")) }
                val exported = directory.resolve("portable.minirp-preset")
                openPresetList()
                pickFromLibrary(preset, R.string.export_preset, exported)
                val saved = directory.resolve("portable.minirp-save")
                openPresetList()
                pickFromLibrary(preset, R.string.export_save, saved)
                val overlay = "plugins/writable-assets/${plugin.packageName}.${plugin.signer}/$preset/notes"
                ZipFile(saved).use { archive ->
                    val edited = archive.getInputStream(archive.getEntry(overlay)).reader().use { it.readText() }
                    assertEquals("Intro\n中文 TOKEN-44\nClosing\n", edited.removePrefix("\uFEFF"))
                }
                openPresetList()
                document.set(exported)
                importPreset.performClick()
                waitFor { !model.state.value.archiveBusy && model.state.value.chatId.isBlank() }
                assertEquals(preset, model.state.value.preset)
                val standalone = directory.resolve("standalone.plugin")
                compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
                val plugins = compose.onNodeWithText(label(R.string.plugins))
                waitFor { plugins.isDisplayed() }
                plugins.performClick()
                val baseRow = compose.onNodeWithTag("plugin:base")
                waitFor { baseRow.isDisplayed() }
                baseRow.performClick()
                compose.onNodeWithTag("plugin-export:base").assertDoesNotExist()
                compose.onNodeWithTag("plugin-enabled:base").assertDoesNotExist()
                compose.onNodeWithTag("plugin-uninstall:base").assertDoesNotExist()
                val back = compose.onNodeWithContentDescription(label(R.string.back))
                back.performClick()
                val row = compose.onNodeWithTag("plugin:${plugin.id}")
                waitFor { row.isDisplayed() }
                val externalExport = compose.onNodeWithTag("plugin-export:${plugin.id}")
                externalExport.assertDoesNotExist()
                row.performClick()
                val enabled = compose.onNodeWithTag("plugin-enabled:${plugin.id}")
                waitFor { enabled.isDisplayed() }
                enabled.assertIsOn().performClick()
                waitFor { model.state.value.installedPlugins.any { it.id == plugin.id && !it.enabled } }
                enabled.assertIsOff()
                waitFor { externalExport.isDisplayed() }
                document.set(standalone)
                externalExport.performClick()
                waitFor { !model.state.value.archiveBusy && standalone.isFile && standalone.length() > 0 }
                assertArrayEquals(directory.resolve("workspace/plugins/${plugin.id}.plugin").readBytes(), standalone.readBytes())
                enabled.performClick()
                waitFor { model.state.value.installedPlugins.any { it.id == plugin.id && it.enabled } }
                enabled.assertIsOn()
                val uninstall = compose.onNodeWithTag("plugin-uninstall:${plugin.id}")
                uninstall.performClick()
                compose.onNode(hasText(label(R.string.cancel)) and hasAnyAncestor(isDialog())).performClick()
                uninstall.performClick()
                compose.onNode(hasText(label(R.string.remove_plugin)) and hasAnyAncestor(isDialog())).performClick()
                waitFor { baseRow.isDisplayed() && model.state.value.installedPlugins.none { it.id == plugin.id } }
                row.assertDoesNotExist()
                val restored = CoreClient(object : ContextWrapper(app) {
                    override fun getFilesDir() = directory.resolve("restored").apply { mkdirs() }
                }, scope)
                try {
                    val imported = runBlocking {
                        restored.query(json("op" to "archive_import", "type" to "save", "path" to saved.path))
                    }
                    assertEquals(preset, imported.getString("preset"))
                    assertArrayEquals(standalone.readBytes(),
                        directory.resolve("restored/workspace/plugins/${plugin.id}.plugin").readBytes())
                    assertTrue(directory.resolve("restored/workspace/conversations/$preset/$room.json").isFile)
                    assertTrue(directory.resolve("restored/workspace/$overlay").readText().contains("TOKEN-44"))
                } finally {
                    restored.close()
                }
                assertNull(fixture.failure.get())
            } finally {
                scope.cancel()
            }
        }
    }

    private class ToolModel(private val workspace: File) : Dispatcher() {
        val failure = AtomicReference<Throwable>()
        private val presetRound = AtomicInteger()
        private val workerRound = AtomicInteger()
        private val diceRound = AtomicInteger()
        private val assetRound = AtomicInteger()
        private var pageSample = ""
        private val callId = AtomicInteger()
        private lateinit var draft: File
        private lateinit var manifest: JSONObject
        private lateinit var signer: String
        private lateinit var fixtureHandler: String

        override fun dispatch(request: RecordedRequest): MockResponse = try {
            val body = JSONObject(request.body?.utf8().orEmpty())
            val messages = body.getJSONArray("messages")
            val system = messages.getJSONObject(0).getString("content")
            val history = (0 until messages.length()).map(messages::getJSONObject)
            val result = history.lastOrNull { it.optString("role") == "tool" }?.optString("content").orEmpty()
            val delta = when {
                system.startsWith("You are PluginAgent working on") -> workerReply(result, history)
                !body.has("tools") -> json("content" to "Tabletop test")
                history.any { it.optString("content").startsWith(AssetRequest) } -> assetReply(result, history)
                history.any { it.optString("content") == DiceRequest } -> diceReply(result)
                history.any { it.optString("content") == SetupRequest } -> presetReply(result)
                else -> json("content" to Interview)
            }
            val data = json("choices" to JSONArray().put(json("delta" to delta)))
            MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                .body("data: $data\n\ndata: [DONE]\n\n").build()
        } catch (problem: Throwable) {
            failure.compareAndSet(null, problem)
            MockResponse.Builder().code(500).body("Unexpected tool flow").build()
        }

        private fun presetReply(result: String): JSONObject = when (presetRound.getAndIncrement()) {
            0 -> tools(tool("configure_experience", "mode" to "tabletop", "agentic_search" to false))
            1 -> tools(tool("configure_date", "mode" to "plugin", "initial_date" to "2032-02-29"))
            2 -> {
                assertTrue(JSONObject(result).getBoolean("staged"))
                tools(tool("activate_skill", "name" to "base-plugin-development"))
            }
            3 -> tools(tool("plugin_base_plugin_spawn", "name" to ProjectName, "message" to "Debug the draft, iterate on its card, then publish it."))
            4 -> tools(tool("plugin_base_plugin_wait", "ids" to JSONArray().put(JSONObject(result).getString("id")), "seconds" to 20))
            5 -> {
                val child = JSONArray(result).getJSONObject(0)
                assertEquals(child.optString("result"), "finished", child.getString("status"))
                assertEquals("Plugin published.", child.getString("result"))
                tools(tool("finish_preset_setup"))
            }
            6 -> {
                assertTrue(JSONObject(result).getBoolean("completed"))
                json("content" to SetupReply)
            }
            else -> error("Unexpected PresetAgent round")
        }

        private fun workerReply(result: String, history: List<JSONObject>): JSONObject = when (workerRound.getAndIncrement()) {
            0 -> tools(tool("activate_skill", "name" to "base-plugin-project"))
            1 -> tools(tool("plugin_base_project_read", "path" to "manifest.json"))
            2 -> {
                manifest = JSONObject(JSONObject(result).getString("text"))
                fixtureHandler = "minirp://actions/${manifest.getString("packageName")}/hooks/probe"
                manifest.put("actions", json("handlers" to json("probe" to "probe")))
                    .put("permissions", (manifest.optJSONArray("permissions") ?: JSONArray()).put("harness.read"))
                    .put("assets", JSONArray().put(json(
                        "id" to "notes", "type" to "file", "path" to "notes.txt", "mimeType" to "text/plain", "writable" to true,
                    )))
                    .put("skills", JSONArray().put(json(
                        "name" to "session", "description" to "Read the current session budget.",
                        "agents" to JSONArray().put("chat"), "tools" to JSONArray().put(json(
                            "name" to "session_info", "description" to "Read model and token usage.",
                            "handler" to fixtureHandler, "parameters" to json("type" to "object"),
                        )).put(json(
                            "name" to "notes", "description" to "Return the text fixture.", "returnType" to "asset-url",
                            "handler" to fixtureHandler, "parameters" to json("type" to "object"),
                        )),
                    )))
                    .put("stateEntries", JSONArray().put(json(
                        "key" to "gate", "scope" to "room", "description" to "Gate display",
                        "initial" to json("line" to "Gate open.", "steps" to 1),
                    )))
                    .put("cards", JSONArray().put(json(
                        "id" to "gate", "title" to "Gate", "state" to "gate",
                        "program" to CardProgram.replace("Title(", "WrongTitle("),
                    )))
                tools(
                    tool("plugin_base_project_write", "path" to "manifest.json", "overwrite" to true, "text" to manifest.toString()),
                    tool("plugin_base_project_write", "path" to "notes.txt", "text" to "Intro\n中文 TOKEN-42\nClosing\n"),
                    tool("plugin_base_project_write", "path" to "index.js", "overwrite" to true,
                        "text" to """
                            globalThis.handlers = { probe: async (event, api) => {
                                if (event.tool?.endsWith("_notes")) return api.assets.url("notes");
                                if (event.name === "skill.tool") return api.harness.getSessionInfo();
                                if (event.line) {
                                    const state = await api.state.get({ key: "gate" });
                                    await api.state.set({ key: "gate", expected_revision: state.revision,
                                        value: { line: event.line, steps: 1 } });
                                }
                                return { echoed: event.message };
                            } };
                        """.trimIndent()),
                )
            }
            3 -> {
                assertTrue(JSONObject(result).getBoolean("written"))
                draft = workspace.resolve("preset_sessions").listFiles()!!.single()
                    .resolve("plugin_agents").listFiles()!!.single()
                assertFalse(draft.resolve("signer.json").exists())
                tools(tool("plugin_base_project_debug", "handler" to fixtureHandler, "event" to json("message" to "debug fixture")))
            }
            4 -> {
                val debug = JSONObject(result)
                assertTrue(result, debug.getBoolean("ok"))
                assertEquals("debug fixture", debug.getJSONObject("result").getString("echoed"))
                val certificate = JSONObject(draft.resolve("signer.json").readText()).getString("certificate")
                signer = MessageDigest.getInstance("SHA-256").digest(Base64.getDecoder().decode(certificate))
                    .joinToString("") { "%02X".format(it) }
                tools(tool("plugin_base_project_card_catalog", "query" to "Button(label:", "length" to 800))
            }
            5 -> {
                val catalog = JSONObject(result)
                assertTrue(catalog.getBoolean("available"))
                assertTrue(result, catalog.getString("text").contains("Button(label:"))
                tools(tool("plugin_base_project_card_preview", "id" to "gate"))
            }
            6 -> {
                val invalid = JSONObject(result)
                assertTrue(result, invalid.getBoolean("available"))
                assertFalse(result, invalid.getBoolean("ok"))
                val errors = invalid.getJSONArray("diagnostics")
                assertTrue(result, (0 until errors.length()).map(errors::getJSONObject).any {
                    it.getString("code") == "unknown-component" && it.getString("component") == "WrongTitle"
                })
                assertFalse(workspace.resolve("plugins/${manifest.getString("id")}.plugin").exists())
                tools(tool("plugin_base_project_edit", "path" to "manifest.json", "before" to "WrongTitle(", "after" to "Title("))
            }
            7 -> {
                assertTrue(result, JSONObject(result).getBoolean("edited"))
                tools(tool("plugin_base_project_card_preview",
                    "id" to "gate", "handler" to fixtureHandler, "ensemble" to true, "screenshot" to true,
                    "event" to json("message" to "room fixture", "line" to "Gate closed."), "width" to 320,
                    "steps" to JSONArray().put(json("control" to "Step count", "value" to "5"))
                        .put(json("control" to "Add step")).put(json("control" to "Inspect gate"))
                        .put(json("control" to "Manual"))))
            }
            8 -> {
                val preview = JSONObject(result)
                assertTrue(result, preview.getBoolean("ok"))
                assertEquals("room fixture", preview.getJSONObject("handler_result").getString("echoed"))
                assertTrue(result, preview.getJSONObject("initial").getString("text").contains("Gate closed."))
                val view = preview.getJSONObject("view")
                assertTrue(result, view.getString("text").contains("Steps 6"))
                assertEquals(320, view.getInt("width"))
                val actions = preview.getJSONArray("actions")
                assertEquals(2, actions.length())
                val action = actions.getJSONObject(0)
                assertEquals("reply", action.getString("type"))
                assertEquals("Check the gate", action.getString("text"))
                assertEquals(6, action.getJSONObject("context").getInt("steps"))
                val link = actions.getJSONObject(1)
                assertEquals("link", link.getString("type"))
                assertEquals("https://example.invalid/manual", link.getString("url"))
                val screenshot = preview.getJSONObject("screenshot")
                assertTrue(result, screenshot.getBoolean("captured"))
                val media = history.last().getJSONArray("content")
                val imageUrl = (0 until media.length()).map(media::getJSONObject)
                    .single { it.getString("type") == "image_url" }.getJSONObject("image_url").getString("url")
                assertTrue(imageUrl.startsWith("data:image/png;base64,"))
                val png = Base64.getDecoder().decode(imageUrl.substringAfter(','))
                val image = assertNotNullImage(png)
                try {
                    assertEquals(screenshot.getInt("width"), image.width)
                    assertEquals(screenshot.getInt("height"), image.height)
                    val colors = mutableSetOf<Int>()
                    for (y in 0 until image.height step 4) {
                        for (x in 0 until image.width step 4) colors.add(image.getPixel(x, y))
                    }
                    assertTrue("The renderer returned a blank screenshot.", colors.size > 10)
                } finally { image.recycle() }
                tools(tool("plugin_base_project_card_preview", "id" to "gate", "ensemble" to true,
                    "viewport" to json("x" to 0, "y" to 0, "width" to 128, "height" to 80)))
            }
            9 -> {
                val clean = JSONObject(result)
                assertTrue(result, clean.getBoolean("ok"))
                val text = clean.getJSONObject("view").getString("text")
                assertTrue(result, text.contains("Gate open.") && text.contains("Steps 1"))
                assertEquals(0, clean.getJSONArray("actions").length())
                val screenshot = clean.getJSONObject("screenshot")
                assertTrue(result, screenshot.getBoolean("captured") && screenshot.getBoolean("cropped"))
                assertEquals(128, screenshot.getInt("width"))
                assertEquals(80, screenshot.getInt("height"))
                assertTrue(draft.listFiles()!!.none { it.extension == "preview" })
                tools(tool("plugin_base_project_publish"))
            }
            10 -> {
                val published = JSONObject(result)
                assertEquals(result, manifest.getString("id"), published.getString("id"))
                assertEquals(signer, published.getString("signer"))
                assertEquals("include", published.getJSONObject("scope").getString("mode"))
                assertEquals(manifest.getString("presetId"), published.getString("presetId"))
                assertEquals(manifest.getString("presetId"),
                    published.getJSONObject("scope").getJSONArray("presets").getString(0))
                assertEquals(signer, JSONObject(draft.resolve("deployment.json").readText()).getString("signer"))
                assertTrue(workspace.resolve("plugins/${manifest.getString("id")}.plugin").isFile)
                json("content" to "Plugin published.")
            }
            else -> error("Unexpected PluginAgent round")
        }

        private fun assertNotNullImage(bytes: ByteArray) =
            checkNotNull(BitmapFactory.decodeByteArray(bytes, 0, bytes.size)) { "The model received an invalid PNG." }

        private fun assetReply(result: String, history: List<JSONObject>): JSONObject {
            val input = history.single { it.optString("content").startsWith(AssetRequest) }.getString("content")
            val url = checkNotNull(Regex("minirp://assets/minirp.attachments/[a-f0-9-]+").find(input)).value
            return when (assetRound.getAndIncrement()) {
                0 -> tools(tool("activate_skill", "name" to "view_asset"))
                1 -> {
                    val names = JSONObject(result).getJSONArray("tools")
                    assertEquals(12, names.length())
                    tools(tool("plugin_base_view_url_metadata", "url" to url))
                }
                2 -> {
                    val metadata = JSONObject(result)
                    assertEquals("application/pdf", metadata.getString("mime"))
                    assertEquals("asset-probe.pdf", metadata.getString("name"))
                    tools(tool("plugin_base_view_pdf_metadata", "url" to url))
                }
                3 -> {
                    val metadata = JSONObject(result).getJSONObject("metadata")
                    assertEquals(1, metadata.getInt("page_count"))
                    assertEquals(100, metadata.getInt("first_page_width_points"))
                    tools(tool("plugin_base_view_pdf_page", "url" to url, "page" to 1))
                }
                4 -> {
                    val part = history.last().getJSONArray("content").getJSONObject(1)
                    val data = part.getJSONObject("image_url").getString("url")
                    val bitmap = assertNotNullImage(Base64.getDecoder().decode(data.substringAfter(',')))
                    try { assertEquals(Color.RED, bitmap.getPixel(bitmap.width / 2, bitmap.height / 2)) }
                    finally { bitmap.recycle() }
                    pageSample = JSONObject(result).getString("sample_url")
                    tools(tool("plugin_base_view_image_metadata", "url" to pageSample))
                }
                5 -> {
                    val metadata = JSONObject(result).getJSONObject("metadata")
                    assertEquals(700, metadata.getInt("width"))
                    assertEquals(1400, metadata.getInt("height"))
                    tools(tool("plugin_base_view_image", "url" to pageSample,
                        "viewport" to json("x" to 50, "y" to 100, "width" to 80, "height" to 120)))
                }
                6 -> {
                    val crop = JSONObject(result)
                    assertEquals(80, crop.getInt("width"))
                    assertEquals(120, crop.getInt("height"))
                    tools(tool("plugin_base_raw_grep", "url" to url, "pattern" to "25 50 44 46 2d", "literal" to true, "limit" to 1))
                }
                7 -> {
                    val row = JSONObject(result).getJSONArray("lines").getJSONObject(0)
                    assertEquals(0, row.getInt("match_offset"))
                    assertTrue(row.getString("text").startsWith("25 50 44 46 2D "))
                    assertTrue(row.getString("text").contains(" | "))
                    json("content" to AssetReply)
                }
                else -> error("Unexpected Asset preview round")
            }
        }

        private fun diceReply(result: String): JSONObject = when (diceRound.getAndIncrement()) {
            0 -> tools(tool("activate_skill", "name" to "tabletop"))
            1 -> tools(tool("roll_dice", "count" to 0, "sides" to 6))
            2 -> {
                assertTrue(result, JSONObject(result).has("error"))
                tools(tool("roll_dice", "count" to 30, "sides" to 1000, "modifier" to -7))
            }
            3 -> {
                val outcome = JSONObject(result)
                val rolls = outcome.getJSONArray("rolls")
                assertEquals(30, rolls.length())
                val values = (0 until rolls.length()).map(rolls::getInt)
                assertTrue(values.all { it in 1..1000 })
                assertEquals(values.sum() - 7, outcome.getInt("total"))
                tools(tool("activate_skill", "name" to "${manifest.getString("id")}-session"))
            }
            4 -> tools(tool("plugin_${manifest.getString("id")}_session_info"))
            5 -> {
                val info = JSONObject(result)
                assertEquals("fixture", info.getString("model"))
                assertEquals("fixture", info.getString("provider"))
                assertTrue(info.getInt("usedTokens") > 0)
                assertEquals(info.getInt("contextWindow") - info.getInt("usedTokens"), info.getInt("remainingTokens"))
                tools(tool("plugin_${manifest.getString("id")}_notes"))
            }
            6 -> tools(tool("activate_skill", "name" to "view_asset"))
            7 -> {
                val url = "minirp://assets/${manifest.getString("packageName")}/notes"
                tools(tool("plugin_base_grep", "url" to url, "pattern" to "[[:upper:]]+-[0-9]+", "context" to 1))
            }
            8 -> {
                val lines = JSONObject(result).getJSONArray("lines")
                assertEquals("Intro", lines.getJSONObject(0).getString("text"))
                assertEquals("中文 TOKEN-42", lines.getJSONObject(1).getString("text"))
                assertEquals(4, lines.getJSONObject(1).getInt("column"))
                assertEquals("Closing", lines.getJSONObject(2).getString("text"))
                tools(tool("activate_skill", "name" to "edit_asset"))
            }
            9 -> tools(tool("plugin_base_text_edit", "url" to notesUrl(), "old_string" to "TOKEN-42", "new_string" to "TOKEN-43"))
            10 -> {
                assertEquals(result, 1, JSONObject(result).optInt("changes"))
                tools(tool("plugin_base_raw_grep", "url" to notesUrl(), "pattern" to "34 33", "literal" to true))
            }
            11 -> {
                val offset = JSONObject(result).getJSONArray("lines").getJSONObject(0).getInt("match_offset")
                tools(tool("plugin_base_bin_patch", "url" to notesUrl(), "offset" to offset, "old_hex" to "34 33", "new_hex" to "34 34"))
            }
            12 -> {
                assertEquals(result, 1, JSONObject(result).optInt("changes"))
                tools(tool("plugin_base_grep", "url" to notesUrl(), "pattern" to "TOKEN-44"))
            }
            13 -> {
                assertEquals("中文 TOKEN-44", JSONObject(result).getJSONArray("lines").getJSONObject(0).getString("text"))
                json("content" to DiceReply)
            }
            else -> error("Unexpected ChatAgent round")
        }

        private fun notesUrl() = "minirp://assets/${manifest.getString("packageName")}/notes"

        private fun tools(vararg calls: JSONObject) =
            json("tool_calls" to JSONArray().apply { calls.forEachIndexed { i, call -> put(call.put("index", i)) } })

        private fun tool(name: String, vararg arguments: Pair<String, Any>) =
            json("id" to "call-${callId.incrementAndGet()}", "type" to "function",
                "function" to json("name" to name, "arguments" to json(*arguments).toString()))
    }

    private companion object {
        const val Interview = "What should your tabletop preset do?"
        const val ProjectName = "Signing flow fixture"
        const val SetupRequest = "Create a tabletop preset whose plugin-managed calendar starts on 2032-02-29. Delegate, debug, and publish a small plugin."
        const val SetupReply = "The tabletop preset and plugin are ready."
        const val DiceRequest = "Roll thirty dice with a modifier of minus seven."
        const val DiceReply = "The dice results were checked."
        const val AssetRequest = "Inspect the attached PDF using its Asset URL."
        const val AssetReply = "The PDF asset and page sample were checked."
        val CardProgram = """
            root = Col([Title("Gate monitor", "md"), Text(%line), Text("Steps " + %steps), slider, add, inspect, manual])
            slider = Slider(%steps, 0, 10, 1, "Step count")
            add = Button("Add step", @Set(%steps, %steps + 1))
            inspect = Button("Inspect gate", @ToAssistant("Check the gate", {steps: %steps}))
            manual = Button("Manual", @OpenUrl("https://example.invalid/manual"))
            %line = "Missing fixture"
            %steps = 0
        """.trimIndent().replace('%', '$')

        fun json(vararg pairs: Pair<String, Any>) = JSONObject().apply { pairs.forEach { (key, value) -> put(key, value) } }
    }
}
