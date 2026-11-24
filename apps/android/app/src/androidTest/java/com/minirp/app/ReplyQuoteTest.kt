package com.minirp.app

import androidx.compose.foundation.text.contextmenu.data.TextContextMenuItem
import androidx.compose.foundation.text.contextmenu.data.TextContextMenuSession
import androidx.compose.foundation.text.contextmenu.provider.LocalTextContextMenuToolbarProvider
import androidx.compose.foundation.text.contextmenu.provider.TextContextMenuDataProvider
import androidx.compose.foundation.text.contextmenu.provider.TextContextMenuProvider
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.Clipboard
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.performTextReplacement
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.text.TextLayoutResult
import androidx.test.platform.app.InstrumentationRegistry
import com.mikepenz.markdown.model.State
import com.mikepenz.markdown.model.parseMarkdown
import com.minirp.app.ui.AgentConversation
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.BranchDraft
import com.minirp.app.ui.ChatState
import com.minirp.app.ui.ComposerSubmission
import com.minirp.app.ui.QuoteAttachment
import com.minirp.app.ui.TraceBlock
import com.minirp.app.ui.branchDraft
import com.minirp.app.ui.decodeBlock
import com.minirp.app.ui.toJson
import kotlinx.coroutines.CompletableDeferred
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class ReplyQuoteTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun selectionMenuCopiesAndAttachesOnlyTheSelectedWordToTheCurrentDraft() {
        val firstRoom = "first-room"
        val activeRoom = mutableStateOf(firstRoom)
        val source = "Before **selected** after"
        val reply = TraceBlock("reply", content = source, markdown = parseMarkdown(source) as State.Success)
        val file = "saved-attachment:notes"
        val toolbar = TestSelectionToolbar()
        var copied: String? = null
        var submitted: ComposerSubmission? = null
        compose.setContent {
            val stateHolder = rememberSaveableStateHolder()
            val clipboard = LocalClipboard.current
            val testClipboard = remember(clipboard) {
                object : Clipboard by clipboard {
                    override suspend fun setClipEntry(clipEntry: ClipEntry?) {
                        copied = clipEntry?.clipData?.getItemAt(0)?.text?.toString()
                    }
                }
            }
            MaterialExpressiveTheme {
                CompositionLocalProvider(
                    LocalClipboard provides testClipboard,
                    LocalTextContextMenuToolbarProvider provides toolbar,
                ) {
                    val room = activeRoom.value
                    stateHolder.SaveableStateProvider(room) {
                        val draft = if (room == firstRoom) {
                            BranchDraft("Branch text", mapOf(file to JSONObject().put("name", "Notes.pdf")), null)
                        } else BranchDraft("Other room draft", emptyMap(), null)
                        AgentConversation(
                            state = ChatState(ready = true, chatId = room, blocks = listOf(reply), branchDraft = draft),
                            agent = AgentRole.Chat,
                            onCancel = {},
                            onSend = { message ->
                                submitted = message
                                true
                            },
                            onModels = {},
                        )
                    }
                }
            }
        }

        val instrumentation = InstrumentationRegistry.getInstrumentation()
        fun openMenu() {
            val answer = compose.onNodeWithText("Before selected after", useUnmergedTree = true)
            val layouts = mutableListOf<TextLayoutResult>()
            answer.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { assertTrue(it(layouts)) }
            val position = layouts.single().getBoundingBox("Before se".length).center
            answer.performTouchInput { longClick(position) }
        }
        fun chooseMenuItem(label: String) {
            compose.waitUntil(5_000) { toolbar.open }
            compose.runOnIdle { toolbar.choose(label) }
            compose.waitUntil(5_000) { !toolbar.open }
        }

        val input = compose.onNodeWithTag("composer")
        input.performTextReplacement("Keep my text")
        openMenu()
        chooseMenuItem(instrumentation.targetContext.getString(android.R.string.copy))
        compose.runOnIdle { assertEquals("selected", copied) }
        input.assertTextContains("Keep my text")

        openMenu()
        chooseMenuItem(instrumentation.targetContext.getString(R.string.quote_reply))
        input.assertTextContains("Keep my text")
        val quote = compose.onNodeWithText("selected")
        quote.assertIsDisplayed()
        compose.onNodeWithText("Notes.pdf").assertIsDisplayed()
        compose.runOnIdle {
            assertNull(submitted)
            activeRoom.value = "another-room"
        }
        input.assertTextContains("Other room draft")
        quote.assertDoesNotExist()
        compose.runOnIdle { activeRoom.value = firstRoom }
        input.assertTextContains("Keep my text")
        quote.assertIsDisplayed()
        compose.onNodeWithText("Notes.pdf").assertIsDisplayed()
        compose.onNodeWithTag("send").performClick()
        compose.runOnIdle {
            val message = checkNotNull(submitted)
            assertEquals("Keep my text", message.text)
            assertEquals(listOf(file), message.files)
            assertEquals("selected", message.quotes.single().text)
        }
        quote.assertDoesNotExist()
    }

    private class TestSelectionToolbar : TextContextMenuProvider {
        @Volatile private var menu: TextContextMenuDataProvider? = null
        private var dismissal: CompletableDeferred<Unit>? = null
        val open: Boolean get() = menu != null

        override suspend fun showTextContextMenu(dataProvider: TextContextMenuDataProvider) {
            val finished = CompletableDeferred<Unit>()
            dismissal = finished
            menu = dataProvider
            try {
                finished.await()
            } finally {
                dismissal = null
                menu = null
            }
        }

        fun choose(label: String) {
            val items = checkNotNull(menu).data().components.filterIsInstance<TextContextMenuItem>()
            val item = checkNotNull(items.firstOrNull { it.label == label }) {
                "Expected menu item $label. Available items are ${items.map { it.label }}"
            }
            val finished = checkNotNull(dismissal)
            item.onClick(object : TextContextMenuSession {
                override fun close() { finished.complete(Unit) }
            })
        }
    }

    @Test
    fun quotedAttachmentIsRestoredInSentMessagesAndBranchesWithoutBecomingAFile() {
        val quote = QuoteAttachment("b4c346e2-896a-487e-a2c0-711451689f67", "First\n\nSecond")
        val packet = JSONObject().put("id", "message").put("kind", "user").put("text", "Draft")
            .put("attachments", JSONArray().put(quote.toJson()).put(JSONObject().put("id", "file").put("name", "Notes.txt")))

        val branch = packet.branchDraft()
        assertEquals(listOf(quote), branch.quotes)
        assertEquals(setOf("saved-attachment:file"), branch.attachments.keys)
        val message = decodeBlock(packet)
        assertEquals(listOf(quote), message.quotes)
        assertEquals(listOf("Notes.txt"), message.attachmentNames)
    }
}
