package com.minirp.app

import android.content.Context
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeDown
import androidx.compose.ui.test.swipeUp
import androidx.test.core.app.ApplicationProvider
import com.mikepenz.markdown.model.State as MarkdownState
import com.mikepenz.markdown.model.parseMarkdown
import com.minirp.app.ui.Choice
import com.minirp.app.ui.Composer
import com.minirp.app.ui.DeleteLibraryChoice
import com.minirp.app.ui.FullSheet
import com.minirp.app.ui.LibraryChoiceRow
import com.minirp.app.ui.TraceBlock
import com.minirp.app.ui.TraceLog
import kotlinx.coroutines.CompletableDeferred
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
            ApplicationProvider.getApplicationContext<Context>().getString(R.string.branch_edit_message)
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

    @Test
    fun longPressSendUsesSystemRoleWhileGenerating() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        var sent: Pair<String, Boolean>? = null
        compose.setContent {
            MaterialExpressiveTheme {
                Composer(
                    busy = true,
                    preparing = false,
                    onCancel = {},
                    allowAttachments = false,
                    onSubmit = { text, _, system, _ ->
                        sent = text to system
                        true
                    },
                )
            }
        }
        compose.onNodeWithTag("composer").performTextInput("Keep it short")
        compose.onNodeWithContentDescription(context.getString(R.string.steer)).assertIsDisplayed()
        compose.onNodeWithTag("send").performTouchInput { longClick() }
        compose.onNodeWithText(context.getString(R.string.send_as_system)).performClick()
        compose.runOnIdle { assertEquals("Keep it short" to true, sent) }
    }

    @Test
    fun longPressChatAndPresetRequiresConfirmation() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val selected = mutableStateOf<Pair<Choice, Boolean>?>(null)
        val deleted = mutableListOf<String>()
        compose.setContent {
            MaterialExpressiveTheme {
                androidx.compose.foundation.layout.Column {
                    val chat = Choice("chat", "A library chat")
                    val preset = Choice("preset", "A library preset")
                    LibraryChoiceRow(chat, false, true, {}, { selected.value = chat to false })
                    LibraryChoiceRow(preset, false, true, {}, { selected.value = preset to true })
                }
                selected.value?.let { (choice, preset) ->
                    DeleteLibraryChoice(choice, preset, { selected.value = null }) {
                        deleted += choice.id
                        selected.value = null
                    }
                }
            }
        }
        compose.onNodeWithText("A library chat").performTouchInput { longClick() }
        compose.onNodeWithText(context.getString(R.string.delete)).performClick()
        compose.onNodeWithText(context.getString(R.string.cancel)).performClick()
        compose.runOnIdle { assertTrue(deleted.isEmpty()) }
        compose.onNodeWithText("A library chat").performTouchInput { longClick() }
        compose.onNodeWithText(context.getString(R.string.delete)).performClick()
        compose.onNodeWithText(context.getString(R.string.delete_chat)).performClick()
        compose.onNodeWithText("A library preset").performTouchInput { longClick() }
        compose.onNodeWithText(context.getString(R.string.delete)).performClick()
        compose.onNodeWithText(context.getString(R.string.delete_preset_detail)).assertIsDisplayed()
        compose.onNodeWithText(context.getString(R.string.delete_preset)).performClick()
        compose.runOnIdle { assertEquals(listOf("chat", "preset"), deleted) }
    }

    @Test
    fun scrollingAwayStaysAwayWhenANewStreamStartsAndButtonReturnsToLatest() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val blocks =
            mutableStateOf(
                List(50) { index ->
                    TraceBlock("old-$index", user = true, content = "Message $index")
                }
            )
        compose.setContent { MaterialExpressiveTheme { TraceLog(blocks.value, false) } }
        compose.onNodeWithTag("transcript").performTouchInput { swipeDown(durationMillis = 140) }
        val latest =
            compose.onNodeWithContentDescription(context.getString(R.string.latest_message))
        latest.assertIsDisplayed()
        compose.runOnIdle {
            blocks.value =
                blocks.value +
                    TraceBlock("new", content = "Newest streamed line\n\n", streaming = true)
        }
        latest.assertIsDisplayed()
        compose.runOnIdle {
            val stream = blocks.value.last()
            blocks.value =
                blocks.value.dropLast(1) +
                    stream.copy(content = stream.content + "Streaming off screen.\n\n".repeat(12))
        }
        latest.assertIsDisplayed()
        latest.performClick()
        compose.waitUntil(5_000) { !latest.isDisplayed() }
    }

    @Test
    fun reversingAFlingAcrossFinishedMarkdownKeepsTheSameReadingAnchor() {
        val messages =
            List(30) { index ->
                val text = buildString {
                    appendLine("# Reply $index")
                    appendLine()
                    repeat(6) { paragraph ->
                        appendLine(
                            "Paragraph $paragraph **bold**. " +
                                "Extra words for wrapping. ".repeat(paragraph * 4)
                        )
                        appendLine()
                    }
                }
                TraceBlock(
                    "reply-$index",
                    content = text,
                    markdown = parseMarkdown(text) as MarkdownState.Success,
                )
            }
        lateinit var list: LazyListState
        compose.setContent {
            MaterialExpressiveTheme {
                list =
                    rememberLazyListState(
                        initialFirstVisibleItemIndex = 60,
                        initialFirstVisibleItemScrollOffset = 12,
                    )
                TraceLog(messages, false, list = list)
            }
        }
        val transcript = compose.onNodeWithTag("transcript")
        fun geometry() = compose.runOnIdle {
            list.layoutInfo.visibleItemsInfo.map { Triple(it.key, it.offset, it.size) }
        }
        val before = geometry()
        assertTrue(before.size > 1)
        try {
            compose.mainClock.autoAdvance = false
            transcript.performTouchInput { swipeDown(durationMillis = 65) }
            compose.mainClock.advanceTimeBy(48)
            val older = compose.runOnIdle { list.firstVisibleItemIndex }
            assertTrue(older > 60)
            transcript.performTouchInput { swipeUp(durationMillis = 65) }
            compose.mainClock.advanceTimeBy(48)
            assertTrue(compose.runOnIdle { list.firstVisibleItemIndex } < older)
        } finally {
            compose.mainClock.autoAdvance = true
        }
        compose.runOnIdle { list.requestScrollToItem(60, 12) }
        assertEquals(before, geometry())
    }

    @Test
    fun composerKeepsDraftUntilTheInputIsAccepted() {
        val acceptance = CompletableDeferred<Boolean>()
        compose.setContent {
            MaterialExpressiveTheme {
                Composer(
                    false,
                    false,
                    {},
                    allowAttachments = false,
                    onSubmit = { _, _, _, _ ->
                        acceptance.await()
                    },
                )
            }
        }
        compose.onNodeWithTag("composer").performTextInput("Pending reply")
        compose.onNodeWithTag("send").performClick()
        compose.onNodeWithText("Pending reply").assertIsDisplayed()
        acceptance.complete(false)
        compose.onNodeWithText("Pending reply").assertIsDisplayed()
    }
}
