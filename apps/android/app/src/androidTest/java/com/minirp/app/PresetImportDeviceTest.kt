package com.minirp.app

import android.app.Activity
import android.content.ContextWrapper
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import androidx.activity.ComponentActivity
import androidx.activity.compose.LocalActivityResultRegistryOwner
import androidx.activity.result.ActivityResultRegistry
import androidx.activity.result.ActivityResultRegistryOwner
import androidx.activity.result.contract.ActivityResultContract
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextReplacement
import androidx.core.app.ActivityOptionsCompat
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.data.Preferences
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.io.File
import java.util.Base64
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.CRC32
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
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PresetImportDeviceTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun welcomeCanImportADocumentAndPngCardUsingContainerAndResearch() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("livePresetResearch") == "true")
        val app = compose.activity.application
        val directory = temporary.newFolder()
        val workspace = object : ContextWrapper(app) {
            override fun getFilesDir(): File = directory
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val document = directory.resolve("harbor-setting.txt").apply { writeText(Setting) }
        val card = directory.resolve("mira-card.png").apply { writeCharacterCard(this) }
        val selected = AtomicReference<File>()
        MockWebServer().use { server ->
            server.start()
            val fixture = PresetModel(server.url("/download").toString(), server.url("/lore").toString())
            server.dispatcher = fixture
            try {
                val preferences = Preferences(PreferenceDataStoreFactory.create(scope = scope) {
                    directory.resolve("preset-import.preferences_pb")
                })
                runBlocking {
                    preferences.save(JSONArray().put(json(
                        "id" to "fixture", "name" to "Fixture", "protocol" to "openai",
                        "model" to "fixture", "url" to server.url("/v1").toString(),
                    )), JSONObject())
                }
                val model = compose.runOnIdle {
                    ViewModelProvider.create(compose.activity, viewModelFactory {
                        initializer { ChatViewModel(app, workspace, preferences) }
                    })[ChatViewModel::class.java]
                }
                val registry = object : ActivityResultRegistry(), ActivityResultRegistryOwner {
                    override val activityResultRegistry: ActivityResultRegistry get() = this

                    override fun <I, O> onLaunch(
                        requestCode: Int, contract: ActivityResultContract<I, O>, input: I,
                        options: ActivityOptionsCompat?,
                    ) {
                        val intent = contract.createIntent(compose.activity, input)
                        assertEquals(Intent.ACTION_OPEN_DOCUMENT, intent.action)
                        assertEquals("*/*", intent.type)
                        val file = checkNotNull(selected.getAndSet(null))
                        dispatchResult(requestCode, Activity.RESULT_OK, Intent().setData(Uri.fromFile(file)))
                    }
                }
                compose.setContent {
                    CompositionLocalProvider(LocalActivityResultRegistryOwner provides registry) { MiniRP(model) }
                }
                fun label(id: Int) = compose.activity.getString(id)
                fun waitFor(timeout: Long = 20_000, condition: () -> Boolean) {
                    compose.waitUntil(timeout) {
                        fixture.failure.get()?.let { throw AssertionError("Preset import tool failed", it) }
                        model.state.value.replyError?.let { throw AssertionError(it.message) }
                        condition()
                    }
                }
                val create = compose.onNodeWithText(label(R.string.create_preset))
                waitFor { model.state.value.ready && create.isDisplayed() }
                create.performClick()
                waitFor {
                    model.state.value.presetEditorOpen && !model.state.value.busy &&
                        model.state.value.blocks.any { it.content == Welcome }
                }
                val preset = directory.resolve("workspace/presets/${model.state.value.preset}.json")
                val inEditor = hasAnyAncestor(isDialog())
                fun send(file: File, instruction: String) {
                    selected.set(file)
                    compose.onNode(hasContentDescription(label(R.string.add_attachment)) and inEditor).performClick()
                    compose.onNodeWithText(label(R.string.choose_files)).performClick()
                    waitFor { compose.onNodeWithText(file.name).isDisplayed() }
                    compose.onNode(hasTestTag("composer") and inEditor).performTextReplacement(instruction)
                    compose.onNode(hasContentDescription(label(R.string.send)) and inEditor).performClick()
                }

                send(document, ImportSetting)
                waitFor { !model.state.value.busy && model.state.value.blocks.any { it.content == SettingDone } }
                assertEquals(Setting, JSONObject(preset.readText()).getJSONObject("world").getString("summary"))

                send(card, ImportCard)
                waitFor(175_000) { !model.state.value.busy && model.state.value.blocks.any { it.content == CardDone } }
                val imported = JSONObject(preset.readText())
                assertEquals(Setting, imported.getJSONObject("world").getString("summary"))
                val agent = imported.getJSONObject("chat_agent")
                assertEquals(CharacterDescription, agent.getString("system_prompt"))
                assertEquals(CharacterGreeting, agent.getString("greeting_system_message"))
                assertEquals("", model.state.value.chatId)
                assertFalse(directory.resolve("workspace/conversations").exists())
                assertEquals(3, fixture.fetchRounds.get())

                val presetId = model.state.value.preset
                val close = compose.onNode(hasContentDescription(label(R.string.close)) and inEditor)
                close.performClick()
                waitFor { !model.state.value.presetEditorOpen }
                compose.onNodeWithTag("composer").assertDoesNotExist()
                compose.runOnIdle { model.selectPreset(presetId) }
                waitFor {
                    model.state.value.presetEditorOpen && model.state.value.presetSetupPending &&
                        model.state.value.blocks.any { it.content == CardDone }
                }
                compose.onNode(hasTestTag("composer") and inEditor).performTextReplacement(CompleteSetup)
                compose.onNode(hasContentDescription(label(R.string.send)) and inEditor).performClick()
                waitFor {
                    !model.state.value.busy && !model.state.value.presetSetupPending &&
                        model.state.value.blocks.any { it.content == SetupDone }
                }
                assertEquals("complete", JSONObject(preset.readText()).getString("setup_status"))
                close.performClick()
                compose.runOnIdle { model.chooseChat(presetId) }
                waitFor { !model.state.value.presetEditorOpen && compose.onNodeWithText(label(R.string.new_chat)).isDisplayed() }
            } finally {
                scope.cancel()
            }
        }
    }

    private class PresetModel(private val download: String, private val lore: String) : Dispatcher() {
        val failure = AtomicReference<Throwable>()
        val fetchRounds = AtomicInteger()
        private val settingRounds = AtomicInteger()
        private val cardRounds = AtomicInteger()
        private val setupRounds = AtomicInteger()
        private val callIds = AtomicInteger()
        private lateinit var character: JSONObject

        override fun dispatch(request: RecordedRequest): MockResponse = try {
            when (request.url.encodedPath) {
                "/download" -> MockResponse.Builder().setHeader("Content-Type", "text/plain").body(CurlMarker).build()
                "/lore" -> MockResponse.Builder().setHeader("Content-Type", "text/html; charset=utf-8")
                    .body("<html><head><title>Harbor lore</title></head><body><main><h1>Harbor lore</h1><p>$WebMarker</p></main></body></html>").build()
                "/v1/chat/completions" -> model(checkNotNull(request.body).utf8())
                else -> MockResponse.Builder().code(404).build()
            }
        } catch (problem: Throwable) {
            failure.compareAndSet(null, problem)
            MockResponse.Builder().code(500).body("Preset import fixture failed").build()
        }

        private fun model(content: String): MockResponse {
            val body = JSONObject(content)
            val messages = body.getJSONArray("messages")
            val history = (0 until messages.length()).map(messages::getJSONObject)
            val user = history.lastOrNull { it.optString("role") == "user" && it.optString("content").isNotBlank() }
                ?.optString("content").orEmpty()
            val result = history.lastOrNull { it.optString("role") == "tool" }?.optString("content").orEmpty()
            val delta = when {
                history.first().getString("content").startsWith("You are FetchAgent") -> when (fetchRounds.getAndIncrement()) {
                    0 -> { assertTrue(user.contains(lore)); tool("browser_navigate", "url" to lore) }
                    1 -> { assertTrue(result.contains(WebMarker)); tool("capture_page") }
                    2 -> { assertTrue(result.contains(WebMarker)); tool("finish_fetch", "complete" to true) }
                    else -> error("Unexpected browser step")
                }
                user.startsWith(ImportSetting) -> setting(user, result)
                user.startsWith(ImportCard) -> card(user, result)
                user == CompleteSetup -> when (setupRounds.getAndIncrement()) {
                    0 -> tool("configure_experience", "mode" to "character", "agentic_search" to true)
                    1 -> { assertTrue(JSONObject(result).getBoolean("staged")); tool("finish_preset_setup") }
                    2 -> { assertTrue(JSONObject(result).getBoolean("completed")); json("content" to SetupDone) }
                    else -> error("Unexpected setup completion step")
                }
                else -> {
                    assertTrue(history.any { it.optString("role") == "system" &&
                        it.optString("content").contains("SillyTavern") &&
                        it.optString("content").contains("automatically import") })
                    json("content" to Welcome)
                }
            }
            val response = json("choices" to JSONArray().put(json("delta" to delta)))
            return MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                .body("data: $response\n\ndata: [DONE]\n\n").build()
        }

        private fun tool(name: String, vararg args: Pair<String, Any>): JSONObject =
            json("tool_calls" to JSONArray().put(json(
                "index" to 0, "id" to "preset-import-${callIds.incrementAndGet()}", "type" to "function",
                "function" to json("name" to name, "arguments" to json(*args).toString()),
            )))

        private fun setting(user: String, result: String): JSONObject = when (settingRounds.getAndIncrement()) {
            0 -> tool("activate_skill", "name" to "view_asset")
            1 -> tool("plugin_base_grep", "url" to asset(user), "pattern" to "^")
            2 -> {
                val text = JSONObject(result).getJSONArray("lines").getJSONObject(0).getString("text")
                assertEquals(Setting, text)
                tool("set_value", "path" to "world.summary", "value" to text)
            }
            3 -> { assertTrue(JSONObject(result).getBoolean("staged")); json("content" to SettingDone) }
            else -> error("Unexpected setting import step")
        }

        private fun card(user: String, result: String): JSONObject = when (cardRounds.getAndIncrement()) {
            0 -> tool("activate_skill", "name" to "container")
            1 -> tool("export_asset", "url" to asset(user), "path" to "mira-card.png")
            2 -> { assertEquals("mira-card.png", JSONObject(result).getString("path")); tool("shell", "command" to ReadPngCard) }
            3 -> {
                val output = JSONObject(result)
                assertEquals(0, output.getInt("exit_code"))
                character = JSONObject(output.getString("output").trim()).getJSONObject("data")
                assertEquals("Mira", character.getString("name"))
                assertEquals(CharacterDescription, character.getString("description"))
                tool("set_value", "path" to "chat_agent.system_prompt", "value" to character.getString("description"))
            }
            4 -> {
                assertTrue(JSONObject(result).getBoolean("staged"))
                tool("set_value", "path" to "chat_agent.greeting_system_message", "value" to character.getString("first_mes"))
            }
            5 -> { assertTrue(JSONObject(result).getBoolean("staged")); tool("activate_skill", "name" to "curl") }
            6 -> tool("curl", "args" to JSONArray().put("--fail").put("--silent").put(download))
            7 -> {
                val transfer = JSONObject(result)
                assertEquals(0, transfer.getInt("exit_code"))
                assertEquals(CurlMarker.toByteArray().size, transfer.getInt("bytes"))
                tool("export_asset", "url" to transfer.getString("asset_url"), "path" to "tide-ledger.txt")
            }
            8 -> {
                assertEquals("tide-ledger.txt", JSONObject(result).getString("path"))
                tool("shell", "command" to "cat tide-ledger.txt")
            }
            9 -> {
                val downloaded = JSONObject(result)
                assertEquals(0, downloaded.getInt("exit_code"))
                assertEquals(CurlMarker, downloaded.getString("output").trim())
                tool("real_search", "query" to "open source search engine", "limit" to 5)
            }
            10 -> {
                val hits = JSONObject(result).getJSONArray("results")
                assertTrue("Expected a live web search result: $result",
                    hits.length() > 0 && hits.getJSONObject(0).getString("url").startsWith("http"))
                tool("real_fetch", "url" to lore, "backend" to "local")
            }
            11 -> {
                assertTrue(JSONObject(result).getString("content").contains(WebMarker))
                tool("cache_page", "ref" to JSONObject(result).getString("ref"))
            }
            12 -> { assertTrue(JSONObject(result).getBoolean("cached")); json("content" to CardDone) }
            else -> error("Unexpected card import step")
        }

        private fun asset(user: String): String =
            checkNotNull(Regex("minirp://assets/minirp[.]attachments/[a-f0-9-]+").find(user)).value
    }

    companion object {
        private const val Welcome = "Share a setting document or a SillyTavern character card and I can import it. What would you like to create?"
        private const val ImportSetting = "Import this setting document."
        private const val ImportCard = "Import this character card and check the public lore."
        private const val Setting = "Keep the harbor lights blue after sunset."
        private const val CharacterDescription = "Mira guards the harbor with a copper lantern."
        private const val CharacterGreeting = "Mira raises her lantern at the dock."
        private const val SettingDone = "I imported your harbor setting."
        private const val CardDone = "I imported Mira and checked the lore."
        private const val CompleteSetup = "Confirm a single character with delegated search and finish setup."
        private const val SetupDone = "The preset is ready to play."
        private const val CurlMarker = "Curl downloaded the tide ledger."
        private const val WebMarker = "Brown sails indicate a low tide."

        private val ReadPngCard = """
            python3 - <<'PY'
            import base64
            import struct
            from pathlib import Path
            png = Path('mira-card.png').read_bytes()
            assert png.startswith(b'\x89PNG\r\n\x1a\n')
            position = 8
            while position + 12 <= len(png):
                length, kind = struct.unpack_from('>I4s', png, position)
                start = position + 8
                data = png[start:start + length]
                if kind == b'tEXt' and data.startswith(b'chara\x00'):
                    print(base64.b64decode(data.split(b'\x00', 1)[1]).decode('utf-8'))
                    break
                position += length + 12
            else:
                raise ValueError('missing character data')
            PY
        """.trimIndent()

        private fun json(vararg pairs: Pair<String, Any>) =
            JSONObject().apply { pairs.forEach { (key, value) -> put(key, value) } }

        private fun writeCharacterCard(file: File) {
            val artwork = Bitmap.createBitmap(2, 2, Bitmap.Config.ARGB_8888)
            val png = ByteArrayOutputStream().use { bytes ->
                try { check(artwork.compress(Bitmap.CompressFormat.PNG, 100, bytes)); bytes.toByteArray() }
                finally { artwork.recycle() }
            }
            val character = json(
                "spec" to "chara_card_v2", "spec_version" to "2.0",
                "data" to json("name" to "Mira", "description" to CharacterDescription, "first_mes" to CharacterGreeting),
            )
            val payload = ("chara\u0000" + Base64.getEncoder().encodeToString(character.toString().toByteArray())).toByteArray()
            val kind = "tEXt".toByteArray(Charsets.US_ASCII)
            val crc = CRC32().apply { update(kind); update(payload) }
            DataOutputStream(file.outputStream()).use { output ->
                val end = png.size - 12
                output.write(png, 0, end)
                output.writeInt(payload.size)
                output.write(kind)
                output.write(payload)
                output.writeInt(crc.value.toInt())
                output.write(png, end, 12)
            }
        }
    }
}
