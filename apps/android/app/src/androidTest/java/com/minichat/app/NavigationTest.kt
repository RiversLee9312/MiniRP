package com.minichat.app

import android.view.KeyEvent
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.lifecycle.ViewModelProvider
import androidx.navigationevent.DirectNavigationEventInput
import androidx.navigationevent.NavigationEvent
import androidx.test.platform.app.InstrumentationRegistry
import com.minichat.app.ui.AgentConversation
import com.minichat.app.ui.AgentRole
import com.minichat.app.ui.ChatState
import com.minichat.app.ui.ChatViewModel
import com.minichat.app.ui.FullSheet
import com.minichat.app.ui.MemoryScreen
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class NavigationTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()

    @Test
    fun settingsActionAndPluginPageReturnToTheirParent() {
        fun label(resource: Int) = compose.activity.getString(resource)
        compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
        fun waitFor(resource: Int) {
            compose.waitUntil(10_000) { compose.onNodeWithText(label(resource)).isDisplayed() }
        }
        waitFor(R.string.providers)
        compose.onNodeWithText(label(R.string.providers)).performClick()
        waitFor(R.string.add_provider)
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText(label(R.string.providers)).assertIsDisplayed()
        compose.onNodeWithText(label(R.string.system_prompt)).performClick()
        waitFor(R.string.title_model)
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText(label(R.string.providers)).assertIsDisplayed()
    }

    @Test
    fun predictiveBackFinishesItsTransitionAfterReleaseAndRestoresOnCancel() {
        val settings = compose.activity.getString(R.string.settings)
        val providers = compose.activity.getString(R.string.providers)
        val gear = compose.onNodeWithContentDescription(settings)
        val composer = compose.onNodeWithTag("composer")
        composer.performTextInput("Predictive back draft")
        gear.performClick()
        val toolbar = compose.onNodeWithText(settings)
        val original = toolbar.fetchSemanticsNode().boundsInRoot
        val input = DirectNavigationEventInput()
        compose.runOnIdle {
            compose.activity.navigationEventDispatcher.addInput(input)
        }
        fun startGesture(edge: Int) {
            compose.runOnIdle {
                input.backStarted(NavigationEvent(swipeEdge = edge))
                input.backProgressed(NavigationEvent(progress = 0.35f, swipeEdge = edge))
            }
        }
        try {
            startGesture(NavigationEvent.EDGE_LEFT)
            compose.waitUntil(5_000) { gear.isDisplayed() }
            toolbar.assertIsDisplayed()
            compose.runOnIdle { input.backCancelled() }
            compose.onNodeWithText(providers).assertIsDisplayed()
            assertEquals(original, toolbar.fetchSemanticsNode().boundsInRoot)
            gear.assertDoesNotExist()
            startGesture(NavigationEvent.EDGE_RIGHT)
            compose.waitUntil(5_000) { gear.isDisplayed() }
            toolbar.assertIsDisplayed()
            compose.mainClock.autoAdvance = false
            compose.runOnUiThread { input.backCompleted() }
            compose.mainClock.advanceTimeByFrame()
            compose.mainClock.advanceTimeByFrame()
            toolbar.assertIsDisplayed()
            gear.assertIsDisplayed()
            compose.mainClock.autoAdvance = true
            gear.assertIsDisplayed()
            toolbar.assertDoesNotExist()
            composer.assertTextContains("Predictive back draft")
        } finally {
            compose.mainClock.autoAdvance = true
            compose.runOnIdle { compose.activity.navigationEventDispatcher.removeInput(input) }
        }
    }

    @Test
    fun settingsRoundTripPreservesTheComposerDraft() {
        val input = compose.onNodeWithTag("composer")
        val gear =
            compose.onNodeWithContentDescription(compose.activity.getString(R.string.settings))
        input.performTextInput("A draft to keep")
        gear.performClick()
        compose
            .onNodeWithContentDescription(compose.activity.getString(R.string.back))
            .performClick()
        input.assertTextContains("A draft to keep")
    }

    @Test
    fun homePresetTitleOpensItsAgentAndReturnsToHome() {
        val heading =
            compose.onNode(
                SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading) and hasClickAction()
            )
        val searchMode = compose.onNodeWithText(compose.activity.getString(R.string.agentic_search))
        val chatMenu =
            compose.onNodeWithContentDescription(compose.activity.getString(R.string.more_options))
        compose.waitUntil(10_000) { heading.isDisplayed() }
        heading.performClick()
        compose.waitUntil(10_000) { searchMode.isDisplayed() }
        chatMenu.assertDoesNotExist()
        compose
            .onNodeWithContentDescription(compose.activity.getString(R.string.close))
            .performClick()
        compose.waitUntil(5_000) { !searchMode.isDisplayed() }
        heading.assertIsDisplayed()
        chatMenu.assertDoesNotExist()
    }
}

class MemoryNavigationTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun backFromTheEditorReturnsToNotesBeforeClosingTheSheet() {
        val model = ViewModelProvider(compose.activity)[ChatViewModel::class.java]
        compose.setContent {
            MaterialExpressiveTheme {
                var open by remember { mutableStateOf(true) }
                if (open) {
                    FullSheet("Memory test", { open = false }) {
                        MemoryScreen("", model, Modifier.weight(1f).fillMaxWidth())
                    }
                }
            }
        }
        val newNote = compose.onNodeWithText(compose.activity.getString(R.string.new_note))
        val noteKey = compose.onNodeWithText(compose.activity.getString(R.string.entry_key))
        val sheet = compose.onNodeWithText("Memory test")
        compose.waitUntil(10_000) { newNote.isDisplayed() }
        newNote.performClick()
        noteKey.assertIsDisplayed()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
        compose.waitUntil(5_000) { newNote.isDisplayed() }
        noteKey.assertDoesNotExist()
        sheet.assertIsDisplayed()
        instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
        compose.waitUntil(5_000) { !sheet.isDisplayed() }
    }
}

class EmptyHomeTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun emptyHomeOnlyOffersToCreateAPresetInItsContent() {
        var created = false
        compose.setContent {
            MaterialExpressiveTheme {
                AgentConversation(
                    state = ChatState(ready = true, homeSuggestions = listOf("Old suggestion")),
                    agent = AgentRole.Chat,
                    modifier = Modifier.fillMaxSize(),
                    onCancel = {},
                    onSend = { _, _, _, _ -> error("An empty home must not send a message") },
                    onModels = {},
                    onCreatePreset = { created = true },
                )
            }
        }
        compose
            .onNodeWithText(compose.activity.getString(R.string.no_presets_yet))
            .assertIsDisplayed()
        compose.onNodeWithTag("composer").assertDoesNotExist()
        compose.onNodeWithText("Old suggestion").assertDoesNotExist()
        compose.onNodeWithText(compose.activity.getString(R.string.create_preset)).performClick()
        compose.runOnIdle { assertEquals(true, created) }
    }
}
