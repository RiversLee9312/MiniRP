package com.minirp.app

import android.content.Context
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.Clipboard
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipe
import androidx.compose.ui.test.swipeDown
import androidx.test.core.app.ApplicationProvider
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import com.minirp.app.ui.AgentConversation
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatState
import com.minirp.app.ui.TraceBlock
import com.minirp.app.ui.TraceLog
import com.minirp.app.ui.TraceStep
import com.minirp.app.ui.formatTracePayload
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class TraceInteractionTest {
    @get:Rule val compose = createComposeRule()

    private fun label(id: Int) = ApplicationProvider.getApplicationContext<Context>().getString(id)

    @Composable
    private fun StandaloneStep(step: TraceBlock) {
        var expanded by remember { mutableStateOf(false) }
        TraceStep(step, 0, expanded, { expanded = !expanded })
    }

    @Test
    fun toolTraceFormatsJsonAndKeepsPayloadsIndependentlyExpandableAndCopyable() {
        val detail = "Long detail ".repeat(25) + "final argument"
        val arguments = """{"scope":"room","details":"$detail","tail":"short sibling"}"""
        val result = "Output detail\n".repeat(20) + "final result"
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
            MaterialExpressiveTheme {
                CompositionLocalProvider(LocalClipboard provides testClipboard) {
                    Column(Modifier.verticalScroll(rememberScrollState())) {
                        StandaloneStep(TraceBlock("reason", reasoning = "Explanation in prose"))
                        StandaloneStep(TraceBlock("tool", tool = "fixture_tool", arguments = arguments, result = result))
                    }
                }
            }
        }
        compose.onNodeWithText("fixture tool").performClick()
        val argumentField = checkNotNull(formatTracePayload(arguments).json).single()
        val argsToggle = compose.onNodeWithTag("trace-json-field:${argumentField.start}")
        val resultToggle = compose.onNodeWithTag("trace-toggle:result")
        val argsStart = compose.onNode(
            hasAnyAncestor(hasTestTag("trace-json:arguments")) and hasText("\n  \"scope\": \"room\",", substring = true),
            useUnmergedTree = true,
        )
        val output = compose.onNodeWithTag("trace-payload:result", useUnmergedTree = true)
        compose.waitUntil(5_000) { argsToggle.isDisplayed() }
        argsStart.assertTextContains("\n  \"scope\": \"room\",", substring = true)
        compose.onNodeWithText("short sibling", substring = true).assertIsDisplayed()
        compose.onNodeWithText("final argument", substring = true).assertDoesNotExist()
        compose.onNodeWithText("final result", substring = true).assertDoesNotExist()
        val toolLayouts = mutableListOf<TextLayoutResult>()
        argsStart.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(toolLayouts) }
        val proseLayouts = mutableListOf<TextLayoutResult>()
        compose.onNodeWithText("Explanation in prose")
            .performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(proseLayouts) }
        assertEquals(FontFamily.Monospace, toolLayouts.single().layoutInput.style.fontFamily)
        assertTrue(proseLayouts.single().layoutInput.style.fontFamily != FontFamily.Monospace)
        compose.onNodeWithTag("trace-copy:arguments").performClick()
        compose.runOnIdle { assertEquals(arguments, copied) }
        argsToggle.performClick()
        compose.onNodeWithText("final argument", substring = true).assertExists()
        compose.onNodeWithText("short sibling", substring = true).assertExists()
        compose.onNodeWithText("final result", substring = true).assertDoesNotExist()
        argsToggle.performClick()
        compose.waitUntil(5_000) {
            compose.onAllNodesWithText("final argument", substring = true).fetchSemanticsNodes().isEmpty()
        }
        resultToggle.performClick()
        output.assertTextContains("final result", substring = true)
        compose.onNodeWithText("final argument", substring = true).assertDoesNotExist()
        compose.onNodeWithTag("trace-copy:result").performClick()
        compose.runOnIdle { assertEquals(result, copied) }
    }

    @Test
    fun manyShortJsonFieldsStartCollapsedAndScrollInsideTheirCard() {
        val result = (0..90).joinToString(prefix = "{", postfix = "}") { index ->
            if (index == 90) "\"tail\":\"End of payload\"" else "\"field$index\":$index"
        }
        compose.setContent {
            MaterialExpressiveTheme {
                StandaloneStep(TraceBlock("large-json", tool = "fixture_tool", result = result))
            }
        }
        compose.onNodeWithText("fixture tool").performClick()
        val toggle = compose.onNodeWithTag("trace-toggle:result")
        val json = compose.onNodeWithTag("trace-json:result", useUnmergedTree = true)
        val tail = compose.onNodeWithText("End of payload", substring = true)
        compose.waitUntil(5_000) { toggle.isDisplayed() }
        json.assertDoesNotExist()
        tail.assertDoesNotExist()
        toggle.performClick()
        compose.waitUntil(5_000) { json.isDisplayed() }
        val body = compose.onNodeWithTag("trace-payload-body:result")
        assertTrue(body.fetchSemanticsNode().boundsInRoot.height <= with(compose.density) { 280.dp.toPx() } + 1)
        body.performSemanticsAction(SemanticsActions.ScrollBy) { it(0f, 20_000f) }
        compose.waitUntil(5_000) {
            val scroll = body.fetchSemanticsNode().config[SemanticsProperties.VerticalScrollAxisRange]
            scroll.maxValue() > 0f && scroll.value() >= scroll.maxValue()
        }
        tail.assertIsDisplayed()
        toggle.performClick()
        json.assertDoesNotExist()
    }

    @Test
    fun traceJsonFormattingPreservesNumbersAndDuplicateNamesAndLeavesInvalidTextAlone() {
        val raw = """{"n":123456789012345678901,"n":-0.40,"nested":[true,null,{},[]]}"""
        val formatted = formatTracePayload(raw)
        assertTrue(formatted.json != null)
        assertTrue(formatted.value.contains("\n  \"n\": 123456789012345678901,\n  \"n\": -0.40"))
        assertTrue(formatted.value.contains("\n    true,\n    null,\n    {},\n    []"))
        for (source in listOf("{notJson: true}", "{} trailing", "[1,]", "A plain result")) {
            val presentation = formatTracePayload(source)
            assertEquals(source, presentation.value)
            assertTrue(presentation.json == null)
        }
    }

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
                    onSend = { false },
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
            list = rememberLazyListState(initialFirstVisibleItemIndex = Int.MAX_VALUE)
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
            assertTrue(list.canScrollForward)
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
    fun anExpandedToolCanBeClosedFromItsPinnedHeading() {
        val blocks = listOf(
            TraceBlock("sticky-tool", tool = "fixture_tool", result = "Output detail\n".repeat(30)),
        ) + List(80) { TraceBlock("following-$it", content = "Following speech $it") }
        val list = LazyListState()
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(blocks, expandThoughts = true, list = list, standalonePluginTrace = true)
            }
        }
        compose.onNodeWithText("fixture tool").performClick()
        val offset = with(compose.density) { 72.dp.roundToPx() }
        compose.runOnIdle { list.requestScrollToItem(1, offset) }
        val heading = compose.onNodeWithTag("pinned:activity-step:sticky-tool")
        compose.waitUntil(5_000) { heading.isDisplayed() }
        compose.onAllNodesWithText("fixture tool").assertCountEquals(1)
        heading.performClick()
        heading.assertDoesNotExist()
        compose.onNodeWithTag("trace-payload-body:result").assertDoesNotExist()
        compose.onNodeWithText("fixture tool").assertIsDisplayed()
    }

    @Test
    fun sendingTakesPrecedenceUntilTheMessageIsAccepted() {
        val state = mutableStateOf(ChatState(
            ready = true, preset = "fixture", chatId = "fixture-chat",
            generating = AgentRole.Chat, submitting = AgentRole.Chat,
            blocks = listOf(TraceBlock("user", user = true, content = "Hello")),
        ))
        compose.setContent {
            MaterialExpressiveTheme {
                AgentConversation(
                    state.value, AgentRole.Chat,
                    onCancel = {}, onSend = { false }, onModels = {},
                )
            }
        }
        compose.onNodeWithText(label(R.string.sending)).assertIsDisplayed()
        compose.onNodeWithText(label(R.string.running)).assertDoesNotExist()
        compose.runOnIdle { state.value = state.value.copy(submitting = null) }
        compose.onNodeWithText(label(R.string.sending)).assertDoesNotExist()
        compose.onNodeWithText(label(R.string.running)).assertIsDisplayed()
    }

    @Test
    fun growingVisibleTraceKeepsItsReadingPositionUntilReturningToLatest() {
        val trace = TraceBlock("live-trace", reasoning = "Thinking line\n".repeat(160), streaming = true)
        val blocks = mutableStateOf(listOf(trace))
        val list = LazyListState(firstVisibleItemIndex = Int.MAX_VALUE)
        compose.setContent {
            MaterialExpressiveTheme { TraceLog(blocks.value, expandThoughts = true, list = list) }
        }
        val transcript = compose.onNodeWithTag("transcript")
        transcript.performTouchInput { swipeDown(durationMillis = 600) }
        fun traceOffset() = compose.runOnIdle {
            assertTrue(list.canScrollForward)
            list.layoutInfo.visibleItemsInfo.single { it.key == "activity-step:live-trace" }.offset
        }
        val offset = traceOffset()
        repeat(3) {
            compose.runOnIdle {
                blocks.value = listOf(trace.copy(reasoning = trace.reasoning + "New line\n".repeat(8 * (it + 1))))
            }
            assertEquals(offset, traceOffset())
        }
        val latest = compose.onNodeWithContentDescription(label(R.string.latest_message))
        latest.performClick()
        compose.waitUntil(5_000) { !latest.isDisplayed() }
        compose.runOnIdle {
            blocks.value = listOf(trace.copy(reasoning = trace.reasoning + "More at the end\n".repeat(30)))
        }
        compose.runOnIdle { assertTrue(!list.canScrollForward) }
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
                    onSend = { false },
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
