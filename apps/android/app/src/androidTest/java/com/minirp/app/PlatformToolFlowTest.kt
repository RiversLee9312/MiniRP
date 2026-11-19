package com.minirp.app

import android.content.ContextWrapper
import android.app.Activity
import android.content.Intent
import android.net.Uri
import androidx.activity.ComponentActivity
import androidx.activity.compose.LocalActivityResultRegistryOwner
import androidx.activity.result.ActivityResultRegistry
import androidx.activity.result.ActivityResultRegistryOwner
import androidx.activity.result.contract.ActivityResultContract
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performImeAction
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.core.app.ActivityOptionsCompat
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

class PlatformToolFlowTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun newPresetDelegatesFirstSigningDebuggingAndPublishingThenRollsDice() {
        val app = compose.activity.application
        val directory = File(app.cacheDir, "tool-flow-${System.nanoTime()}").apply { mkdirs() }
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
                send(SetupRequest, inPresetEditor = true)
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == SetupReply } &&
                        model.state.value.installedPlugins.any { it.name == ProjectName }
                }
                compose.onNodeWithContentDescription(label(R.string.close)).performClick()
                waitFor { !model.state.value.presetEditorOpen }
                send(DiceRequest)
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == DiceReply }
                }
                assertTrue(model.state.value.chatId.isNotBlank())
                val preset = model.state.value.preset
                val room = model.state.value.chatId
                val plugin = model.state.value.installedPlugins.single { it.name == ProjectName }
                val menu = compose.onNodeWithContentDescription(label(R.string.open_menu))
                fun pickFromLibrary(id: String, option: Int, file: File) {
                    val row = compose.onNodeWithTag("library-choice-$id")
                    waitFor { row.isDisplayed() }
                    row.performTouchInput { longClick() }
                    document.set(file)
                    compose.onNodeWithText(label(option)).performClick()
                    waitFor { !model.state.value.archiveBusy && file.isFile && file.length() > 0 }
                }
                val exportedRoom = directory.resolve("portable.minirp-room")
                menu.performClick()
                pickFromLibrary(room, R.string.export_chat, exportedRoom)
                ZipFile(exportedRoom).use { assertNotNull(it.getEntry("conversations/$preset/$room.json")) }
                val exported = directory.resolve("portable.minirp-preset")
                menu.performClick()
                compose.onNodeWithTag("library-presets").performClick()
                pickFromLibrary(preset, R.string.export_preset, exported)
                val saved = directory.resolve("portable.minirp-save")
                menu.performClick()
                compose.onNodeWithTag("library-presets").performClick()
                pickFromLibrary(preset, R.string.export_save, saved)
                menu.performClick()
                compose.onNodeWithTag("library-presets").performClick()
                document.set(exported)
                compose.onNodeWithText(label(R.string.import_preset)).performClick()
                waitFor { !model.state.value.archiveBusy && model.state.value.chatId.isBlank() }
                assertEquals(preset, model.state.value.preset)
                val standalone = directory.resolve("standalone.plugin")
                compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
                val plugins = compose.onNodeWithText(label(R.string.plugins))
                waitFor { plugins.isDisplayed() }
                plugins.performClick()
                val more = compose.onNodeWithContentDescription(label(R.string.more_options))
                waitFor { more.isDisplayed() }
                more.performClick()
                document.set(standalone)
                compose.onNodeWithText(label(R.string.export_archive)).performClick()
                waitFor { !model.state.value.archiveBusy && standalone.isFile && standalone.length() > 0 }
                assertArrayEquals(directory.resolve("workspace/plugins/${plugin.id}.plugin").readBytes(), standalone.readBytes())
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
                system.startsWith("You are PluginAgent working on") -> workerReply(result)
                !body.has("tools") -> json("content" to "Tabletop test")
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
            1 -> tools(tool("activate_skill", "name" to "base-plugin-development"))
            2 -> tools(tool("plugin_base_plugin_spawn", "name" to ProjectName, "message" to "Debug and publish the draft."))
            3 -> tools(tool("plugin_base_plugin_wait", "ids" to JSONArray().put(JSONObject(result).getString("id")), "seconds" to 20))
            4 -> {
                val child = JSONArray(result).getJSONObject(0)
                assertEquals(child.optString("result"), "finished", child.getString("status"))
                assertEquals("Plugin published.", child.getString("result"))
                tools(tool("finish_preset_setup"))
            }
            5 -> {
                assertTrue(JSONObject(result).getBoolean("completed"))
                json("content" to SetupReply)
            }
            else -> error("Unexpected PresetAgent round")
        }

        private fun workerReply(result: String): JSONObject = when (workerRound.getAndIncrement()) {
            0 -> tools(tool("activate_skill", "name" to "base-plugin-project"))
            1 -> tools(tool("plugin_base_project_read", "path" to "manifest.json"))
            2 -> {
                manifest = JSONObject(JSONObject(result).getString("text"))
                fixtureHandler = "minirp://actions/${manifest.getString("packageName")}/hooks/probe"
                manifest.put("actions", json("handlers" to json("probe" to "probe")))
                tools(
                    tool("plugin_base_project_write", "path" to "manifest.json", "overwrite" to true, "text" to manifest.toString()),
                    tool("plugin_base_project_write", "path" to "index.js", "overwrite" to true,
                        "text" to "globalThis.handlers = { probe: event => ({ echoed: event.message }) };"),
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
                tools(tool("plugin_base_project_publish"))
            }
            5 -> {
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
                json("content" to DiceReply)
            }
            else -> error("Unexpected ChatAgent round")
        }

        private fun tools(vararg calls: JSONObject) =
            json("tool_calls" to JSONArray().apply { calls.forEachIndexed { i, call -> put(call.put("index", i)) } })

        private fun tool(name: String, vararg arguments: Pair<String, Any>) =
            json("id" to "call-${callId.incrementAndGet()}", "type" to "function",
                "function" to json("name" to name, "arguments" to json(*arguments).toString()))
    }

    private companion object {
        const val Interview = "What should your tabletop preset do?"
        const val ProjectName = "Signing flow fixture"
        const val SetupRequest = "Create a tabletop preset and delegate a small plugin. Debug and publish it."
        const val SetupReply = "The tabletop preset and plugin are ready."
        const val DiceRequest = "Roll thirty dice with a modifier of minus seven."
        const val DiceReply = "The dice results were checked."

        fun json(vararg pairs: Pair<String, Any>) = JSONObject().apply { pairs.forEach { (key, value) -> put(key, value) } }
    }
}
