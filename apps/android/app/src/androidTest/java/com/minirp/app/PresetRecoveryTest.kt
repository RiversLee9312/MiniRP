package com.minirp.app

import android.app.Application
import android.content.ContextWrapper
import androidx.activity.ComponentActivity
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.minirp.app.data.Preferences
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
import java.io.File
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockResponseBody
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import okio.BufferedSink
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PresetRecoveryTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun aFreshModelRestoresTheOpenEditorForAnEstablishedPreset() {
        val root = temporary.newFolder()
        val preset = fixturePreset(root, "complete", "Previous preset suggestion.")
        Fixture(compose.activity.application, root).use { fixture ->
            fixture.preferences(preset)
            var store = ViewModelStore()
            val model = mutableStateOf(compose.runOnIdle { fixture.model(store) })
            compose.setContent { MiniRP(model.value) }
            val home = compose.onNode(hasText("Recovery preset") and hasClickAction() and
                SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading))
            val previous = compose.onNodeWithText("Previous preset suggestion.")
            compose.waitUntil(12_000) { home.isDisplayed() }
            home.performClick()
            compose.waitUntil(12_000) { previous.isDisplayed() }
            compose.waitUntil(5_000) { fixture.editorWasSaved() }

            compose.runOnIdle {
                store.clear()
                store = ViewModelStore()
                model.value = fixture.model(store)
            }
            try {
                compose.waitUntil(12_000) { model.value.state.value.presetEditorOpen && previous.isDisplayed() }
                assertEquals(preset, model.value.state.value.preset)
                compose.onNodeWithContentDescription(compose.activity.getString(R.string.close)).performClick()
                compose.waitUntil(8_000) { !model.value.state.value.presetEditorOpen && !fixture.editorWasSaved() }
            } finally {
                compose.runOnIdle { store.clear() }
            }
        }
    }

    @Test
    fun openingPresetKeepsEarlyResponsesReasoningExpandableWhileTheReplyIsStillRunning() {
        val root = temporary.newFolder()
        val previousPreset = fixturePreset(root, "complete", "Previous preset suggestion.")
        val thought = "Checking the premise before writing."
        val answer = "What should the first scene be?"
        val finish = CountDownLatch(1)
        val delivered = CountDownLatch(1)
        fun reasoning(delta: String) = "data: " + JSONObject()
            .put("type", "response.reasoning_text.delta").put("output_index", 0)
            .put("content_index", 0).put("delta", delta) + "\n\n"
        val start = "data: {\"type\":\"response.created\"}\n\n" +
            reasoning("Checking the premise ") + reasoning("before writing.")
        val textDelta = JSONObject().put("type", "response.output_text.delta")
            .put("output_index", 0).put("content_index", 0).put("delta", answer)
        val message = JSONObject().put("type", "message").put("role", "assistant")
            .put("content", JSONArray().put(JSONObject().put("type", "output_text").put("text", answer)))
        val done = JSONObject().put("type", "response.completed").put("response", JSONObject()
            .put("status", "completed").put("output", JSONArray().put(message)))
        val completed = "data: $textDelta\n\ndata: $done\n\n"
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (!request.url.encodedPath.endsWith("/responses")) return MockResponse.Builder().code(404).build()
                    return MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body(object : MockResponseBody {
                            override val contentLength = (start + completed).toByteArray().size.toLong()

                            override fun writeTo(sink: BufferedSink) {
                                sink.writeUtf8(start).flush()
                                finish.await(20, TimeUnit.SECONDS)
                                sink.writeUtf8(completed).flush()
                                delivered.countDown()
                            }
                        }).build()
                }
            }
            server.start()
            Fixture(compose.activity.application, root).use { fixture ->
                fixture.preferences(previousPreset, server.url("/v1").toString(), protocol = "responses")
                val store = ViewModelStore()
                val model = compose.runOnIdle { fixture.model(store) }
                compose.setContent { MiniRP(model) }
                try {
                    compose.waitUntil(12_000) { model.state.value.ready && model.state.value.preset == previousPreset }
                    compose.runOnIdle { model.openPresetEditor(create = true) }
                    val runningThought = compose.onNodeWithText(compose.activity.getString(R.string.reasoning))
                    compose.waitUntil(12_000) { model.state.value.presetEditorOpen && runningThought.isDisplayed() }
                    runningThought.performClick()
                    compose.waitUntil(10_000) { compose.onNodeWithText(thought).isDisplayed() }
                    assertTrue(model.state.value.generating != null)
                    assertEquals(thought, model.state.value.blocks.single { it.reasoning.isNotBlank() }.reasoning)
                    finish.countDown()
                    assertTrue("The test server did not flush the final events.", delivered.await(5, TimeUnit.SECONDS))
                    compose.waitUntil(12_000) { model.state.value.blocks.any { it.content == answer } }
                    compose.waitUntil(12_000) { model.state.value.generating == null }
                } finally {
                    finish.countDown()
                    compose.runOnIdle { store.clear() }
                }
            }
        }
    }

    @Test
    fun anInterruptedDraftKeepsItsTraceAndOffersRetryAfterTheOpeningFails() {
        val root = temporary.newFolder()
        val preset = fixturePreset(root, "draft", "Partial draft work before restart.")
        val requests = AtomicInteger()
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (!request.url.encodedPath.endsWith("/chat/completions")) return MockResponse.Builder().code(404).build()
                    if (requests.getAndIncrement() == 0)
                        return MockResponse.Builder().code(400).body("Fixture opening failure").build()
                    return MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body("data: {\"choices\":[{\"delta\":{\"content\":\"The recovered interview can continue.\"}}]}\n\ndata: [DONE]\n\n")
                        .build()
                }
            }
            server.start()
            Fixture(compose.activity.application, root).use { fixture ->
                fixture.preferences(preset, server.url("/v1").toString())
                val store = ViewModelStore()
                val model = compose.runOnIdle { fixture.model(store) }
                compose.setContent { MiniRP(model) }
                try {
                    val retry = compose.onNodeWithText(compose.activity.getString(R.string.retry))
                    compose.waitUntil(15_000) { model.state.value.presetEditorOpen && retry.isDisplayed() }
                    assertTrue(model.state.value.blocks.any { it.content == "Partial draft work before restart." })
                    retry.performClick()
                    compose.waitUntil(15_000) {
                        compose.onNodeWithText("The recovered interview can continue.").isDisplayed() &&
                            model.state.value.generating == null
                    }
                    assertEquals(2, requests.get())
                    assertEquals(preset, model.state.value.preset)
                    assertEquals("", model.state.value.chatId)
                } finally {
                    compose.runOnIdle { store.clear() }
                }
            }
        }
    }

    @Test
    fun stoppingAStreamingPresetReplyKeepsItInTheEditorAndNextRequest() {
        val root = temporary.newFolder()
        val preset = fixturePreset(root, "complete", "An earlier suggestion.", requireNewChat = true)
        val input = "Develop the next scene."
        val partial = "The scene starts from an empty station."
        val followUp = "Continue from there."
        val finalReply = "The station light came on."
        fun frame(content: String) = "data: " +
            JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", JSONObject().put("content", content)))) +
            "\n\n"
        val streamed = frame(partial)
        val done = "data: [DONE]\n\n"
        val finishStream = CountDownLatch(1)
        MockWebServer().use { server ->
            server.enqueue(MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                .body(object : MockResponseBody {
                    override val contentLength = (streamed + done).toByteArray().size.toLong()

                    override fun writeTo(sink: BufferedSink) {
                        sink.writeUtf8(streamed).flush()
                        finishStream.await(20, TimeUnit.SECONDS)
                        sink.writeUtf8(done)
                    }
                }).build())
            server.enqueue(MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                .body(frame(finalReply) + done).build())
            server.start()
            Fixture(compose.activity.application, root).use { fixture ->
                fixture.preferences(preset, server.url("/v1").toString())
                val store = ViewModelStore()
                val model = compose.runOnIdle { fixture.model(store) }
                compose.setContent { MiniRP(model) }
                try {
                    val home = compose.onNode(hasText("Recovery preset") and hasClickAction() and
                        SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading))
                    val composer = compose.onNodeWithTag("composer")
                    val send = compose.onNodeWithTag("send")
                    val prefix = compose.onNodeWithText(partial)
                    compose.waitUntil(12_000) { home.isDisplayed() }
                    home.performClick()
                    compose.waitUntil(12_000) { model.state.value.presetEditorOpen && composer.isDisplayed() }
                    composer.performTextInput(input)
                    send.performClick()
                    compose.waitUntil(12_000) { prefix.isDisplayed() && model.state.value.generating != null }
                    compose.onNodeWithContentDescription(compose.activity.getString(R.string.stop)).performClick()
                    compose.waitUntil(12_000) { !model.state.value.busy && model.state.value.generating == null }
                    assertTrue("The stopped reply should remain in the editor's transcript.",
                        model.state.value.blocks.any { it.content == partial })

                    composer.performTextInput(followUp)
                    send.performClick()
                    compose.waitUntil(12_000) { !model.state.value.busy && compose.onNodeWithText(finalReply).isDisplayed() }
                    checkNotNull(server.takeRequest(2, TimeUnit.SECONDS))
                    val request = checkNotNull(server.takeRequest(2, TimeUnit.SECONDS))
                    val messages = JSONObject(checkNotNull(request.body).utf8()).getJSONArray("messages")
                    val conversation = List(messages.length(), messages::getJSONObject)
                        .filter { it.getString("role") in setOf("user", "assistant") }
                        .map { it.getString("content") }
                    assertEquals(listOf(input, partial, followUp), conversation)

                    compose.onNodeWithContentDescription(compose.activity.getString(R.string.close)).performClick()
                    compose.waitUntil(12_000) { !model.state.value.presetEditorOpen && home.isDisplayed() }
                    home.performClick()
                    compose.waitUntil(12_000) { model.state.value.presetEditorOpen && prefix.isDisplayed() }
                } finally {
                    finishStream.countDown()
                    compose.runOnIdle { store.clear() }
                }
            }
        }
    }

    private fun fixturePreset(root: File, setup: String, message: String, requireNewChat: Boolean = false): String {
        val preset = UUID.randomUUID().toString()
        val config = JSONObject().put("name", "Recovery preset").put("setup_status", setup)
        if (requireNewChat) config.put("chat_agent",
            JSONObject().put("greeting_system_message", "Open a new scene."))
        root.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json").writeText(
            config.toString(),
        )
        root.resolve("workspace/preset_sessions/$preset").apply { mkdirs() }.resolve("blocks.json").writeText(
            JSONArray().put(JSONObject().put("id", "previous-opening").put("agent", "PresetAgent").put("content", message)).toString(),
        )
        return preset
    }

    private class Fixture(private val app: Application, root: File) : AutoCloseable {
        private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        private val workspace = object : ContextWrapper(app) { override fun getFilesDir() = root }
        private val preferences = Preferences(PreferenceDataStoreFactory.create(scope = scope) {
            root.resolve("preset-recovery.preferences_pb")
        })

        fun preferences(preset: String, endpoint: String? = null, protocol: String = "openai") = runBlocking {
            val providers = JSONArray()
            if (endpoint != null) providers.put(JSONObject().put("id", "fixture").put("model", "fixture")
                .put("protocol", protocol).put("url", endpoint))
            preferences.save(providers,
                JSONObject().put("preset", preset).put("onboarding_complete", true).put("preset_home_hint_seen", true))
        }

        fun editorWasSaved() = runBlocking { preferences.load().second.optBoolean("preset_editor_open") }

        fun model(store: ViewModelStore): ChatViewModel = ViewModelProvider.create(store, viewModelFactory {
            initializer { ChatViewModel(app, workspace, preferences) }
        })[ChatViewModel::class.java]

        override fun close() { scope.cancel() }
    }
}
