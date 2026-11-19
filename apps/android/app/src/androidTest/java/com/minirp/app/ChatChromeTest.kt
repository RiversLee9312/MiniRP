package com.minirp.app

import android.view.KeyEvent
import android.view.View
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.Scaffold
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.click
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.text.TextLayoutResult
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.ui.AgentConversation
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatState
import com.minirp.app.ui.ChatToolbar
import com.minirp.app.ui.FullSheet
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class ChatChromeTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun greetingAndChatTitleFitOneLineAndTheGearPrecedesTheChatMenu() {
        val preset = "A preset with a long title ".repeat(5).trimEnd()
        val title = "A conversation with a long title ".repeat(5).trimEnd()
        val state = mutableStateOf(ChatState(preset = "fixture", presetName = preset))
        compose.setContent {
            MaterialExpressiveTheme {
                Scaffold(topBar = { ChatToolbar(state.value, {}, {}, {}, {}) }) { padding ->
                    AgentConversation(
                        state.value,
                        AgentRole.Chat,
                        Modifier.fillMaxSize().padding(padding),
                        {},
                        { _, _, _, _ -> false },
                        {},
                    )
                }
            }
        }
        fun label(id: Int) = compose.activity.getString(id)
        val gear = compose.onNodeWithContentDescription(label(R.string.settings))
        val menu = compose.onNodeWithContentDescription(label(R.string.more_options))
        val greeting = compose.onNodeWithText(label(R.string.welcome_to_preset))
        gear.assertIsDisplayed()
        menu.assertDoesNotExist()
        compose.onNodeWithText(label(R.string.new_chat)).assertDoesNotExist()
        greeting.assertIsDisplayed()
        assertEllipsized(preset)
        compose.runOnIdle { state.value = state.value.copy(chatId = "fixture", chatName = title) }
        greeting.assertDoesNotExist()
        menu.assertIsDisplayed()
        assertTrue(
            gear.fetchSemanticsNode().boundsInRoot.left <
                menu.fetchSemanticsNode().boundsInRoot.left
        )
        assertEllipsized(title)
        compose.runOnIdle { state.value = state.value.copy(chatId = "") }
        greeting.assertIsDisplayed()
        menu.assertDoesNotExist()
    }

    @Test
    fun worldAndPresetComposersRecoverTheirBottomSpaceAfterImeCloses() {
        val role = mutableStateOf(AgentRole.World)
        lateinit var sheetView: View
        compose.setContent {
            MaterialExpressiveTheme {
                FullSheet("Agent conversation", {}) {
                    val view = LocalView.current
                    SideEffect { sheetView = view }
                    androidx.compose.runtime.key(role.value) {
                        AgentConversation(
                            ChatState(chatId = "fixture"),
                            role.value,
                            Modifier.weight(1f),
                            {},
                            { _, _, _, _ -> false },
                            {},
                        )
                    }
                }
            }
        }
        val input = compose.onNodeWithTag("composer")
        val ime = WindowInsetsCompat.Type.ime()
        for (agent in listOf(AgentRole.World, AgentRole.Preset)) {
            compose.runOnIdle { role.value = agent }
            input.assertIsDisplayed()
            val baseline = input.fetchSemanticsNode().boundsInWindow.bottom
            input.performTouchInput { click() }
            compose.waitUntil(8_000) {
                ViewCompat.getRootWindowInsets(sheetView)?.isVisible(ime) == true
            }
            compose.waitUntil(8_000) {
                input.fetchSemanticsNode().boundsInWindow.bottom < baseline - 80
            }
            input.performTextInput("Draft")
            if (agent == AgentRole.World) {
                InstrumentationRegistry.getInstrumentation()
                    .sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
            } else {
                compose.runOnIdle {
                    WindowInsetsControllerCompat(
                            (sheetView.parent as androidx.compose.ui.window.DialogWindowProvider)
                                .window,
                            sheetView,
                        )
                        .hide(ime)
                }
            }
            compose.waitUntil(8_000) {
                ViewCompat.getRootWindowInsets(sheetView)?.isVisible(ime) == false &&
                    kotlin.math.abs(input.fetchSemanticsNode().boundsInWindow.bottom - baseline) <=
                        2f
            }
            input.assertTextContains("Draft")
        }
    }

    private fun assertEllipsized(text: String) {
        val layout = mutableListOf<TextLayoutResult>()
        compose.onNodeWithText(text).performSemanticsAction(SemanticsActions.GetTextLayoutResult) {
            it(layout)
        }
        assertTrue(layout.single().lineCount == 1 && layout.single().isLineEllipsized(0))
    }
}
