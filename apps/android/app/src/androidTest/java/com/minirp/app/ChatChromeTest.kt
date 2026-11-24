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
import androidx.compose.ui.MotionDurationScale
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.click
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
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
import com.minirp.app.ui.RoomLoading
import com.minirp.app.ui.RoomLoadingScreen
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class ChatChromeTest {
    private val motionScale = object : MotionDurationScale {
        override var scaleFactor = 1f
    }
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>(effectContext = motionScale)

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
                        { false },
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
                            { false },
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

    @Test
    fun roomPreparationShowsProgressRotatesWorldCopyAndCanBeStopped() {
        val firstGrapheme = "👩🏽‍🚀"
        val first = "$firstGrapheme Navigation lights drift past the window."
        val second = "The next shift checks the gate."
        val loading = mutableStateOf(RoomLoading("fixture", ready = 0, total = 2, progress = 0.25f, messages = listOf(first, second)))
        var cancellations = 0
        compose.mainClock.autoAdvance = false
        compose.setContent {
            MaterialExpressiveTheme {
                RoomLoadingScreen(loading.value) { cancellations++ }
            }
        }
        compose.mainClock.advanceTimeByFrame()
        compose.onNodeWithContentDescription(first).assertIsDisplayed()
        assertEquals(firstGrapheme.length, hiddenCaptionStart(first))
        compose.onNodeWithText("0/2").assertDoesNotExist()
        assertRoomProgressForTalkBack(0.25f)
        val initial = displayedRoomProgress()
        compose.mainClock.advanceTimeBy(1_600)
        val firstAdvance = displayedRoomProgress()
        assertTrue(firstAdvance > initial)
        assertEquals(null, hiddenCaptionStart(first))
        compose.mainClock.advanceTimeBy(1_600)
        val secondAdvance = displayedRoomProgress()
        assertTrue(secondAdvance > firstAdvance && secondAdvance - firstAdvance < firstAdvance - initial)
        compose.onNodeWithContentDescription(first).assertDoesNotExist()
        compose.onNodeWithContentDescription(second).assertIsDisplayed()
        assertRoomProgressForTalkBack(0.25f)
        compose.runOnIdle { loading.value = loading.value.copy(ready = 1, progress = 1f) }
        compose.mainClock.advanceTimeBy(350)
        assertRoomProgressForTalkBack(0.98f)
        assertEquals(0.98f, displayedRoomProgress(), 0.0001f)
        compose.onNodeWithText("1/2").assertDoesNotExist()
        compose.runOnIdle { loading.value = loading.value.copy(ready = 2) }
        compose.mainClock.advanceTimeBy(350)
        assertRoomProgressForTalkBack(1f)
        assertEquals(1f, displayedRoomProgress(), 0.0001f)
        compose.onNodeWithText(compose.activity.getString(R.string.stop)).performClick()
        compose.runOnIdle { assertEquals(1, cancellations) }
    }

    @Test
    fun roomPreparationUsesConfirmedProgressAndWholeCaptionsWithAnimationsDisabled() {
        val caption = "The next shift checks the gate."
        motionScale.scaleFactor = 0f
        compose.setContent {
            MaterialExpressiveTheme {
                RoomLoadingScreen(RoomLoading("fixture", ready = 1, total = 2, progress = 0.65f, messages = listOf(caption))) {}
            }
        }
        compose.onNodeWithContentDescription(caption).assertIsDisplayed()
        assertEquals(null, hiddenCaptionStart(caption))
        compose.mainClock.advanceTimeBy(5_000)
        assertRoomProgressForTalkBack(0.65f)
        assertEquals(0.65f, displayedRoomProgress(), 0.0001f)
    }

    private fun assertRoomProgressForTalkBack(progress: Float) {
        compose.onNode(
            SemanticsMatcher.expectValue(SemanticsProperties.ProgressBarRangeInfo, ProgressBarRangeInfo(progress, 0f..1f))
        ).assertIsDisplayed()
    }

    private fun displayedRoomProgress(): Float =
        compose.onNodeWithTag("room-loading-track", useUnmergedTree = true)
            .fetchSemanticsNode().config[SemanticsProperties.ProgressBarRangeInfo].current

    private fun hiddenCaptionStart(caption: String): Int? {
        val layouts = mutableListOf<TextLayoutResult>()
        compose.onNodeWithText(caption, useUnmergedTree = true)
            .performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
        return layouts.single().layoutInput.text.spanStyles.singleOrNull()?.start
    }

    private fun assertEllipsized(text: String) {
        val layout = mutableListOf<TextLayoutResult>()
        compose.onNodeWithText(text).performSemanticsAction(SemanticsActions.GetTextLayoutResult) {
            it(layout)
        }
        assertTrue(layout.single().lineCount == 1 && layout.single().isLineEllipsized(0))
    }
}
