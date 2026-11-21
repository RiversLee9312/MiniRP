package com.minirp.app

import android.content.Context
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipe
import androidx.test.core.app.ApplicationProvider
import com.minirp.app.ui.AgentConversation
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatState
import com.minirp.app.ui.TraceBlock
import com.minirp.app.ui.TraceLog
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class TraceInteractionTest {
    @get:Rule val compose = createComposeRule()

    private fun label(id: Int) = ApplicationProvider.getApplicationContext<Context>().getString(id)

    @Test
    fun searchTraceRequiresItsOwnExpansionInsideAnExpandedChatTrace() {
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(
                    listOf(
                        TraceBlock("chat", reasoning = "Chat reasoning"),
                        TraceBlock("search", "SearchAgent", reasoning = "Search reasoning"),
                        TraceBlock("fetch", "FetchAgent", content = "Fetch detail"),
                    ),
                    expandThoughts = true,
                )
            }
        }
        compose.onNodeWithText("Chat reasoning").assertIsDisplayed()
        compose.onNodeWithText("Search reasoning").assertDoesNotExist()
        compose.onNodeWithText("Fetch detail").assertDoesNotExist()
        val search = compose.onNodeWithText(label(R.string.search_agent))
        search.performClick()
        compose.onNodeWithText("Search reasoning").assertIsDisplayed()
        compose.onNodeWithText("Fetch detail").assertIsDisplayed()
        search.performClick()
        compose.onNodeWithText("Chat reasoning").assertIsDisplayed()
        compose.onNodeWithText("Search reasoning").assertDoesNotExist()
    }

    @Test
    fun presetPluginRunsExpandIndependentlyAndKeepTheirResultsInTheTrace() {
        compose.setContent {
            MaterialExpressiveTheme {
                AgentConversation(
                    ChatState(
                        preset = "fixture",
                        expandThoughts = true,
                        blocks = listOf(
                            TraceBlock("preset-thought", "PresetAgent", reasoning = "Delegating two projects"),
                            TraceBlock("first", "PluginAgent", pluginRun = "first", pluginAgentName = "Writer", pluginStatus = "finished"),
                            TraceBlock("second", "PluginAgent", pluginRun = "second", pluginAgentName = "Reader", pluginStatus = "finished"),
                            TraceBlock("first-tool", "PluginAgent", tool = "write_manifest", pluginRun = "first"),
                            TraceBlock("preset-result", "PresetAgent", content = "Both projects are done."),
                            TraceBlock("second-reply", "PluginAgent", content = "Schema read", pluginRun = "second"),
                            TraceBlock("first-reply", "PluginAgent", content = "Plugin published", pluginRun = "first"),
                        ),
                    ),
                    AgentRole.Preset,
                    onCancel = {},
                    onSend = { _, _, _, _ -> false },
                    onModels = {},
                )
            }
        }
        compose.onNodeWithText("Both projects are done.").assertIsDisplayed()
        compose.onNodeWithText("Plugin published").assertDoesNotExist()
        compose.onNodeWithText("Schema read").assertDoesNotExist()
        compose.onNodeWithText("Writer").performClick()
        compose.onAllNodesWithText("Writer").assertCountEquals(1)
        compose.onNodeWithText("write manifest").assertIsDisplayed()
        compose.onNodeWithText("Plugin published").assertIsDisplayed()
        compose.onNodeWithText("Schema read").assertDoesNotExist()
        compose.onNodeWithText("Writer").performClick()
        compose.onNodeWithText("Reader").performClick()
        compose.onNodeWithText("Plugin published").assertDoesNotExist()
        compose.onNodeWithText("Schema read").assertIsDisplayed()
    }

    @Test
    fun expandedSearchTracePinsBothHeadersAndKeepsItsReadingPositionWhileStreaming() {
        val blocks =
            mutableStateOf(
                listOf(TraceBlock("chat", reasoning = "Chat reasoning")) +
                    List(110) { index ->
                        TraceBlock("search-$index", "SearchAgent", reasoning = "Search row $index")
                    }
            )
        lateinit var list: LazyListState
        compose.setContent {
            list = rememberLazyListState()
            MaterialExpressiveTheme { TraceLog(blocks.value, expandThoughts = true, list = list) }
        }
        compose.onNodeWithText(label(R.string.search_agent)).performClick()
        val root = compose.onNodeWithTag("pinned:activity:chat")
        val search = compose.onNodeWithTag("pinned:search-activity:search-0")
        compose.waitUntil(5_000) { root.isDisplayed() && search.isDisplayed() }
        val viewport = compose.onNodeWithTag("transcript")
        assertTrue(
            root.fetchSemanticsNode().boundsInRoot.top <=
                viewport.fetchSemanticsNode().boundsInRoot.top + 1
        )
        root.performTouchInput {
            swipe(
                center,
                center + Offset(0f, height * 7.5f),
                durationMillis = 600,
            )
        }
        compose.waitUntil(5_000) { search.isDisplayed() }
        fun readingAnchor() = compose.runOnIdle {
            assertTrue(list.canScrollBackward)
            val first =
                list.layoutInfo.visibleItemsInfo.single { it.index == list.firstVisibleItemIndex }
            first.key to list.firstVisibleItemScrollOffset
        }
        val anchor = readingAnchor()
        compose.runOnIdle {
            blocks.value +=
                TraceBlock("streaming", "SearchAgent", reasoning = "More results", streaming = true)
        }
        assertEquals(anchor, readingAnchor())
        search.assertIsDisplayed()
        search.performClick()
        search.assertDoesNotExist()
        compose.onNodeWithText("Chat reasoning").assertIsDisplayed()
        compose.onNodeWithText("More results").assertDoesNotExist()
    }

    @Test
    fun thinkingPlaceholderDoesNotReturnDuringAToolOrBetweenToolAndReply() {
        val user = TraceBlock("user", user = true, content = "Find an answer")
        val running = label(R.string.running)
        val state =
            mutableStateOf(
                ChatState(
                    chatId = "fixture",
                    busy = true,
                    generating = AgentRole.Chat,
                    blocks = listOf(user),
                )
            )
        compose.setContent {
            MaterialExpressiveTheme {
                AgentConversation(
                    state.value,
                    AgentRole.Chat,
                    onCancel = {},
                    onSend = { _, _, _, _ -> false },
                    onModels = {},
                )
            }
        }
        compose.onAllNodesWithText(running).assertCountEquals(1)
        val search = TraceBlock("search", tool = "search", running = true)
        val done = TraceBlock("fetch", "SearchAgent", tool = "real_fetch", result = "Done")
        compose.runOnIdle { state.value = state.value.copy(blocks = listOf(user, search, done)) }
        compose.onAllNodesWithText(running).assertCountEquals(1)
        compose.runOnIdle {
            state.value =
                state.value.copy(blocks = listOf(user, search.copy(running = false), done))
        }
        compose.onAllNodesWithText(running).assertCountEquals(0)
    }
}
