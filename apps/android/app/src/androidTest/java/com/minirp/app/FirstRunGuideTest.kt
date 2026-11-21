package com.minirp.app

import android.content.ContextWrapper
import androidx.activity.ComponentActivity
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.Clipboard
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextReplacement
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.minirp.app.data.Preferences
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
import com.minirp.app.ui.usesMainlandChina
import java.io.File
import java.util.Locale
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class FirstRunGuideTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun freshInstallCanCopyHelpConnectAndChooseADefaultModel() {
        val root = temporary.newFolder()
        val app = compose.activity.application
        val workspace = object : ContextWrapper(app) {
            override fun getFilesDir(): File = root
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val auth = AtomicReference<String>()
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (request.url.encodedPath != "/v1/models") return MockResponse.Builder().code(404).build()
                    auth.set(request.headers["Authorization"])
                    return MockResponse.Builder().setHeader("Content-Type", "application/json")
                        .body("""{"data":[{"id":"guide-model","displayName":"Guide model","effort":{"supported_levels":["low","max","medium"]}}]}""").build()
                }
            }
            server.start()
            try {
                val preferences = Preferences(PreferenceDataStoreFactory.create(scope = scope) {
                    root.resolve("onboarding.preferences_pb")
                })
                val model = compose.runOnIdle {
                    ViewModelProvider.create(compose.activity, viewModelFactory {
                        initializer { ChatViewModel(app, workspace, preferences) }
                    })[ChatViewModel::class.java]
                }
                var copied = ""
                compose.setContent {
                    val clipboard = LocalClipboard.current
                    val testClipboard = remember(clipboard) {
                        object : Clipboard by clipboard {
                            override suspend fun setClipEntry(clipEntry: ClipEntry?) {
                                copied = clipEntry?.clipData?.getItemAt(0)?.text?.toString().orEmpty()
                            }
                        }
                    }
                    CompositionLocalProvider(LocalClipboard provides testClipboard) { MiniRP(model) }
                }
                fun label(id: Int) = compose.activity.getString(id)
                fun text(id: Int) = compose.onNodeWithText(label(id))
                fun assertSubscriptionGuidance() {
                    assertTrue(copied.contains(label(R.string.setup_help_research)))
                    assertTrue(copied.contains("coding plan", ignoreCase = true))
                    assertTrue(copied.contains("agent plan", ignoreCase = true))
                }
                compose.waitUntil(10_000) { text(R.string.setup_welcome).isDisplayed() }
                text(R.string.setup_begin).performClick()
                text(R.string.setup_unsure).performClick()
                text(R.string.setup_help_title).assertIsDisplayed()
                text(R.string.setup_copy_prompt).performClick()
                text(R.string.copied).assertIsDisplayed()
                compose.runOnIdle {
                    assertTrue(copied.contains("MiniRP") && copied.contains("Base URL"))
                    assertTrue(copied.contains("https://github.com/RiversLee9312/MiniRP/blob/main/README.md"))
                    assertSubscriptionGuidance()
                }
                text(R.string.setup_help_web).performClick()
                text(R.string.setup_copy_prompt).performClick()
                text(R.string.copied).assertIsDisplayed()
                compose.runOnIdle {
                    assertTrue(copied.contains("real_search") && copied.contains("real_fetch"))
                    assertTrue(copied.contains("Mwmbl") && copied.contains("DeepSeek"))
                    assertTrue(listOf("Tavily", "Exa", "Brave Search").all(copied::contains))
                    assertSubscriptionGuidance()
                }
                text(R.string.close).performClick()
                text(R.string.add_provider).performClick()
                val providerUrl = server.url("/v1").toString()
                text(R.string.provider_name).performTextReplacement("Guide provider")
                text(R.string.provider_name).assertTextContains("Guide provider")
                text(R.string.base_url).performTextReplacement(providerUrl)
                text(R.string.base_url).assertTextContains(providerUrl)
                text(R.string.api_key).performTextReplacement("guide-key")
                text(R.string.save).assertIsEnabled().performClick()

                val choice = compose.onNodeWithText("Guide model")
                compose.waitUntil(5_000) { model.state.value.providers.any { it.name == "Guide provider" } }
                val providerId = model.state.value.providers.single().id
                compose.waitUntil(10_000) { model.state.value.modelCatalogs[providerId]?.loading == false }
                assertEquals(null, model.state.value.modelCatalogs[providerId]?.error)
                compose.waitUntil(10_000) { choice.isDisplayed() }
                assertEquals("Bearer guide-key", auth.get())
                text(R.string.setup_next).assertIsNotEnabled()
                choice.performClick()
                text(R.string.setup_next).performClick()
                text(R.string.setup_effort_detail).assertIsDisplayed()
                val effort = compose.onNodeWithTag("model-thinking-effort")
                effort.assertTextContains("max").performClick()
                compose.onNodeWithText("low").performClick()
                text(R.string.setup_use_model).performClick()

                compose.waitUntil(10_000) { text(R.string.create_preset).isDisplayed() }
                assertEquals("guide-model", model.state.value.assigned(AgentRole.Chat)?.choice?.id)
                assertEquals("low", model.state.value.assigned(AgentRole.Chat)?.reasoningEffort)
                assertEquals("", model.state.value.chatId)
                compose.waitUntil(5_000) { runBlocking { preferences.load().second.optBoolean("onboarding_complete") } }
            } finally {
                scope.cancel()
            }
        }
    }

    @Test
    fun assistantSuggestionsUseTheDeviceRegionAndFallBackToLanguage() {
        fun locale(tag: String) = Locale.forLanguageTag(tag)
        assertTrue(usesMainlandChina(locale("en"), locale("en-CN")))
        assertTrue(usesMainlandChina(locale("en-US"), locale("en-US-u-rg-cnzzzz")))
        assertTrue(usesMainlandChina(locale("zh-Hans"), locale("zh")))
        assertFalse(usesMainlandChina(locale("zh-Hans"), locale("en-US")))
        assertFalse(usesMainlandChina(locale("zh-Hant"), locale("zh-TW")))
        assertFalse(usesMainlandChina(locale("en"), locale("en")))
    }
}
