package com.minichat.app

import android.content.Context
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.test.core.app.ApplicationProvider
import com.minichat.app.ui.Composer
import com.minichat.app.ui.FullSheet
import com.minichat.app.ui.TraceBlock
import com.minichat.app.ui.TraceLog
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class ComposerTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun thoughtsExpandAndCollapseOnSuccessiveTaps() {
        val label =
            ApplicationProvider.getApplicationContext<Context>().getString(R.string.reasoning)
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(listOf(TraceBlock("fold", "EnvAgent", reasoning = "Trace details")), false)
            }
        }
        compose.onNodeWithText("Trace details").assertDoesNotExist()
        compose.onNodeWithText(label).performClick()
        compose.onNodeWithText("Trace details").assertIsDisplayed()
        compose.onNodeWithText(label).performClick()
        compose.onNodeWithText("Trace details").assertDoesNotExist()
    }

    @Test
    fun inputGrowsThenKeepsItsHeightWhileTextScrolls() {
        compose.setContent { MaterialExpressiveTheme { Composer(false, {}, {}) } }
        compose.waitForIdle()
        val input = compose.onNodeWithTag("composer")
        val original = input.fetchSemanticsNode().boundsInRoot.height
        input.performTextInput("Line\n".repeat(4))
        compose.waitForIdle()
        val expanded = input.fetchSemanticsNode().boundsInRoot.height
        input.performTextInput("Line\n".repeat(20))
        compose.waitForIdle()
        val maximum = input.fetchSemanticsNode().boundsInRoot.height
        input.performTextInput("Line\n".repeat(30))
        compose.waitForIdle()
        val afterMoreInput = input.fetchSemanticsNode().boundsInRoot.height
        assertTrue(expanded > original)
        assertTrue(maximum > expanded)
        assertTrue(kotlin.math.abs(afterMoreInput - maximum) < 1f)
    }

    @Test
    fun markdownKeepsStructureWhileTheResponseIsStreaming() {
        val block =
            mutableStateOf(
                TraceBlock("stream", content = "# Heading\n\n**Bold words**\n\n", streaming = true)
            )
        compose.setContent { MaterialExpressiveTheme { TraceLog(listOf(block.value), false) } }
        compose.waitUntil(5_000) { compose.onNodeWithText("Heading").isDisplayed() }
        compose.onNodeWithText("Bold words").assertIsDisplayed()
        compose.onNodeWithText("# Heading", substring = true).assertDoesNotExist()
        compose.runOnIdle {
            block.value =
                block.value.copy(content = block.value.content + "A second paragraph.\n\n")
        }
        compose.waitUntil(5_000) { compose.onNodeWithText("A second paragraph.").isDisplayed() }
        compose.onNodeWithText("Heading").assertIsDisplayed()
    }

    @Test
    fun agentSheetOpensExpandedAndBranchUsesThePressedMessage() {
        var branched = ""
        val label =
            ApplicationProvider.getApplicationContext<Context>().getString(R.string.branch_chat)
        compose.setContent {
            MaterialExpressiveTheme {
                FullSheet("Conversation", {}) {
                    TraceLog(
                        listOf(
                            TraceBlock(
                                "selected",
                                content = "Checkpoint message",
                                user = true,
                                version = "v1",
                            )
                        ),
                        false,
                        modifier = androidx.compose.ui.Modifier.weight(1f),
                        onBranch = { branched = it },
                    )
                    Text("End of conversation")
                }
            }
        }
        val title = compose.onNodeWithText("Conversation").fetchSemanticsNode().boundsInRoot
        val end = compose.onNodeWithText("End of conversation").fetchSemanticsNode().boundsInRoot
        val windowHeight =
            ApplicationProvider.getApplicationContext<Context>()
                .resources
                .displayMetrics
                .heightPixels
        assertTrue(end.bottom - title.top > windowHeight * 0.75f)
        compose.onNodeWithText("Checkpoint message").performTouchInput { longClick() }
        compose.onNodeWithText(label).performClick()
        compose.runOnIdle { assertEquals("selected", branched) }
    }
}
