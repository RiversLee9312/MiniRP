package com.minirp.app

import android.content.Context
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.Clipboard
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextReplacement
import androidx.test.core.app.ApplicationProvider
import com.minirp.app.ui.BranchDraft
import com.minirp.app.ui.Composer
import com.minirp.app.ui.ReplyEnd
import com.minirp.app.ui.TraceBlock
import com.minirp.app.ui.TraceLog
import com.minirp.app.ui.withLegacyReplyActions
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class BranchActionsTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun loadingAnOlderReplyKeepsCopyWithoutDuplicatingAModernFooter() {
        val blocks =
            listOf(
                TraceBlock("old-input", user = true),
                TraceBlock("old-answer", content = "Older answer"),
                TraceBlock("input", user = true),
                TraceBlock("partial", content = "Checking"),
                TraceBlock("steer", user = true),
                TraceBlock("final", content = "New answer"),
                TraceBlock("end", version = "saved", replyEnd = ReplyEnd("New answer", "input")),
            )
        val actions = blocks.withLegacyReplyActions().filter { it.replyEnd != null }
        assertEquals(listOf("Older answer", "New answer"), actions.map { it.replyEnd!!.text })
        assertEquals(listOf("", "saved"), actions.map { it.version })
    }

    @Test
    fun replyFooterCopiesTheCompletedReplyAndBranchesAtItsOwnCheckpoint() {
        val blocks = mutableStateOf(listOf(TraceBlock("response", content = "Visible reply")))
        var copied = ""
        var branched = ""
        compose.setContent {
            val clipboard = LocalClipboard.current
            val testClipboard =
                remember(clipboard) {
                    object : Clipboard by clipboard {
                        override suspend fun setClipEntry(clipEntry: ClipEntry?) {
                            copied = clipEntry?.clipData?.getItemAt(0)?.text?.toString().orEmpty()
                        }
                    }
                }
            MaterialExpressiveTheme {
                CompositionLocalProvider(LocalClipboard provides testClipboard) {
                    TraceLog(blocks.value, false, onBranchReply = { branched = it })
                }
            }
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val copy = compose.onNodeWithContentDescription(context.getString(R.string.copy))
        val branch = compose.onNodeWithContentDescription(context.getString(R.string.branch_chat))
        copy.assertDoesNotExist()
        branch.assertDoesNotExist()
        compose.runOnIdle {
            blocks.value +=
                TraceBlock(
                    "end",
                    version = "saved",
                    replyEnd = ReplyEnd("**Visible reply**", "user"),
                )
        }
        copy.performClick()
        branch.performClick()
        compose.runOnIdle {
            assertEquals("**Visible reply**", copied)
            assertEquals("end", branched)
        }
    }

    @Test
    fun branchDraftIsEditableAndFilesSendOnlyWhenTheUserSubmits() {
        val source = "saved-attachment:attachment"
        val draft =
            mutableStateOf<BranchDraft?>(
                BranchDraft(
                    "Original prompt",
                    mapOf(source to JSONObject().put("name", "notes.pdf")),
                    null,
                )
            )
        val submitted = mutableListOf<Pair<String, List<String>>>()
        compose.setContent {
            MaterialExpressiveTheme {
                key(draft.value != null) {
                    Composer(
                        busy = false,
                        preparing = false,
                        onCancel = {},
                        onSubmit = { text, files, _, _ ->
                            submitted += text to files
                            true
                        },
                        draft = draft.value,
                    )
                }
            }
        }
        val input = compose.onNodeWithTag("composer")
        val send = compose.onNodeWithTag("send")
        input.assertTextContains("Original prompt")
        compose.onNodeWithText("notes.pdf").assertIsDisplayed()
        input.performTextReplacement("Edited prompt")
        compose.runOnIdle { assertEquals(emptyList<Pair<String, List<String>>>(), submitted) }
        send.performClick()
        compose.runOnIdle {
            assertEquals(listOf("Edited prompt" to listOf(source)), submitted)
            draft.value = null
        }
        send.assertIsNotEnabled()
        compose.onNodeWithText("notes.pdf").assertDoesNotExist()
    }
}
