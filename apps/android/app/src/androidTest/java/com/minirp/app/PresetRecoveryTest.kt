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
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
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
import java.util.concurrent.atomic.AtomicInteger
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

    private fun fixturePreset(root: File, setup: String, message: String): String {
        val preset = UUID.randomUUID().toString()
        root.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json").writeText(
            JSONObject().put("name", "Recovery preset").put("setup_status", setup).toString(),
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

        fun preferences(preset: String, endpoint: String? = null) = runBlocking {
            val providers = JSONArray()
            if (endpoint != null) providers.put(JSONObject().put("id", "fixture").put("model", "fixture")
                .put("protocol", "openai").put("url", endpoint))
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
