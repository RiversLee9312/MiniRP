package com.minichat.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.mikepenz.markdown.m3.Markdown
import com.mikepenz.markdown.model.rememberMarkdownState
import com.mikepenz.markdown.model.rememberStreamingMarkdownState
import com.minichat.app.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.conflate
import kotlinx.coroutines.withContext

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun AgentConversation(
    state: ChatState,
    agent: AgentRole,
    modifier: Modifier = Modifier,
    onCancel: () -> Unit,
    onSend: suspend (String, List<String>) -> Boolean,
    onModels: (AgentRole) -> Unit,
    onBranch: ((String) -> Unit)? = null,
) {
    val blocks =
        remember(state.blocks, agent) {
            state.blocks.filter {
                it.role == agent || agent == AgentRole.Chat && it.isSearchTrace
            }
        }
    Column(modifier) {
        TraceLog(
            blocks,
            state.expandThoughts,
            Modifier.weight(1f).fillMaxWidth(),
            onBranch = onBranch.takeUnless { state.busy || state.preparing },
        )
        Composer(state.busy, state.preparing, onCancel, onSend)
        FlowRow(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            val roles =
                if (agent == AgentRole.Chat && state.agenticSearch) listOf(agent, AgentRole.Search)
                else listOf(agent)
            roles.forEach { role ->
                val label = stringResource(role.label)
                val name =
                    state.assigned(role)?.choice?.name ?: stringResource(R.string.select_model)
                AssistChip(
                    onClick = { onModels(role) },
                    enabled = !state.preparing,
                    label = {
                        Text("$label · $name", maxLines = 1, overflow = TextOverflow.Ellipsis)
                    },
                    trailingIcon = { Symbol("expand_more", size = 16) },
                    modifier = Modifier.widthIn(max = 290.dp),
                )
            }
        }
    }
}

private sealed interface TimelineItem {
    val id: String

    data class Message(val block: TraceBlock) : TimelineItem {
        override val id: String
            get() = "message:${block.id}"
    }

    data class Activity(val steps: List<TraceBlock>) : TimelineItem {
        override val id: String
            get() = "activity:${steps.first().id}"
    }
}

private fun timeline(blocks: List<TraceBlock>): List<TimelineItem> = buildList {
    val activity = mutableListOf<TraceBlock>()
    fun flush() {
        if (activity.isNotEmpty()) add(TimelineItem.Activity(activity.toList()))
        activity.clear()
    }
    blocks.forEach { block ->
        if (block.tool.isNotBlank() || block.reasoning.isNotBlank()) activity += block
        if (block.user || block.content.isNotBlank()) {
            flush()
            add(TimelineItem.Message(block))
        }
    }
    flush()
}
    .asReversed()

@Composable
fun TraceLog(
    blocks: List<TraceBlock>,
    expandThoughts: Boolean,
    modifier: Modifier = Modifier,
    onBranch: ((String) -> Unit)? = null,
) {
    val entries = remember(blocks) { timeline(blocks) }
    val list = rememberLazyListState()
    var followBottom by remember { mutableStateOf(!list.canScrollBackward) }
    LaunchedEffect(list) {
        snapshotFlow { list.isScrollInProgress to list.canScrollBackward }
            .collect { (scrolling, awayFromBottom) ->
                if (scrolling) followBottom = !awayFromBottom
            }
    }
    LaunchedEffect(entries.firstOrNull()?.id) {
        if (entries.isNotEmpty() && followBottom && !list.isScrollInProgress) {
            list.requestScrollToItem(0)
        }
    }
    LazyColumn(
        modifier,
        state = list,
        reverseLayout = true,
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        items(entries, key = { it.id }) { item ->
            when (item) {
                is TimelineItem.Activity -> ActivityGroup(item.steps, expandThoughts)
                is TimelineItem.Message ->
                    if (item.block.user) UserMessage(item.block, onBranch)
                    else
                        SelectionContainer(Modifier.fillMaxWidth().searchIndent(item.block)) {
                            MessageMarkdown(item.block.content, item.block.streaming)
                        }
            }
        }
    }
}

@Composable
private fun UserMessage(block: TraceBlock, branch: ((String) -> Unit)?) {
    var menu by remember { mutableStateOf(false) }
    val branchLabel = stringResource(R.string.branch_chat)
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
        Box(Modifier.fillMaxWidth(0.86f), contentAlignment = Alignment.CenterEnd) {
            Surface(
                color = MaterialTheme.colorScheme.surfaceContainerHigh,
                shape = MaterialTheme.shapes.large,
                modifier =
                    if (branch != null && block.version.isNotBlank())
                        Modifier.combinedClickable(
                            onClick = {},
                            onLongClickLabel = branchLabel,
                            onLongClick = { menu = true },
                        )
                    else Modifier,
            ) {
                Column(Modifier.padding(horizontal = 14.dp, vertical = 10.dp)) {
                    block.attachmentNames.forEach { name ->
                        Text(
                            name,
                            style = MaterialTheme.typography.labelMedium,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            color = MaterialTheme.colorScheme.primary,
                        )
                    }
                    if (block.content.isNotBlank()) Text(block.content)
                }
            }
            DropdownMenu(menu, { menu = false }) {
                DropdownMenuItem(
                    text = { Text(branchLabel) },
                    onClick = {
                        menu = false
                        branch?.invoke(block.id)
                    },
                )
            }
        }
    }
}

private val TraceBlock.isSearchTrace: Boolean
    get() = role == AgentRole.Search || role == AgentRole.Fetch

private fun Modifier.searchIndent(block: TraceBlock, nested: Boolean = false): Modifier =
    if (block.isSearchTrace && !nested) padding(start = 16.dp) else this

@Composable
private fun ActivityGroup(steps: List<TraceBlock>, defaultExpanded: Boolean) {
    val tools = steps.count { it.tool.isNotBlank() }
    val running = steps.any { it.running || it.streaming }
    var expansionOverride by rememberSaveable { mutableStateOf<Boolean?>(null) }
    val expanded = expansionOverride ?: (defaultExpanded && steps.any { it.reasoning.isNotBlank() })
    val nested = steps.all { it.isSearchTrace }
    val label =
        when {
            running -> stringResource(R.string.running)
            tools > 0 -> pluralStringResource(R.plurals.trace_actions, tools, tools)
            else -> stringResource(R.string.reasoning)
        }
    Column(if (nested) Modifier.padding(start = 16.dp) else Modifier) {
        ActivityRow(label, expanded) { expansionOverride = !expanded }
        if (expanded) {
            VerticalDividerGroup {
                steps.forEach { step ->
                    key(step.id) {
                        if (step.tool.isBlank()) {
                            SelectionContainer(Modifier.searchIndent(step, nested)) {
                                Text(
                                    step.reasoning,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier = Modifier.padding(vertical = 4.dp),
                                )
                            }
                        } else {
                            var details by rememberSaveable { mutableStateOf(false) }
                            val agent =
                                if (step.isSearchTrace) stringResource(step.role.label) + " · "
                                else ""
                            Column(Modifier.searchIndent(step, nested)) {
                                ActivityRow(agent + step.tool.replace('_', ' '), details) {
                                    details = !details
                                }
                                if (details)
                                    SelectionContainer {
                                        Text(
                                            listOf(step.arguments, step.result)
                                                .filter(String::isNotBlank)
                                                .joinToString("\n\n"),
                                            Modifier.padding(start = 12.dp, bottom = 6.dp),
                                            style = MaterialTheme.typography.bodySmall,
                                        )
                                    }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun VerticalDividerGroup(content: @Composable ColumnScope.() -> Unit) {
    Row(Modifier.padding(start = 9.dp).height(IntrinsicSize.Min)) {
        Box(
            Modifier.width(1.dp)
                .fillMaxHeight()
                .background(MaterialTheme.colorScheme.outlineVariant)
        )
        Column(Modifier.padding(start = 10.dp), content = content)
    }
}

@Composable
private fun ActivityRow(label: String, expanded: Boolean, toggle: () -> Unit) {
    Row(
        Modifier.fillMaxWidth()
            .clickable(
                onClickLabel = stringResource(if (expanded) R.string.collapse else R.string.expand),
                onClick = toggle,
            )
            .heightIn(min = 40.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Symbol(if (expanded) "expand_more" else "chevron_right", size = 18)
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
internal fun MessageMarkdown(text: String, streaming: Boolean = false) {
    if (streaming) {
        val state = rememberStreamingMarkdownState()
        val source by rememberUpdatedState(text)
        LaunchedEffect(state) {
            var consumed = 0
            snapshotFlow { source }
                .conflate()
                .collect { current ->
                    if (current.length > consumed) {
                        withContext(Dispatchers.Default) {
                            state.append(current.substring(consumed))
                        }
                        consumed = current.length
                    }
                }
        }
        Markdown(streamingMarkdownState = state, modifier = Modifier.fillMaxWidth())
    } else {
        Markdown(
            rememberMarkdownState(text, retainState = true),
            modifier = Modifier.fillMaxWidth(),
        )
    }
}
