package com.minichat.app.ui

import android.content.ClipData
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.scrollable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.mapSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.mikepenz.markdown.compose.MarkdownElement
import com.mikepenz.markdown.m3.Markdown
import com.mikepenz.markdown.model.MarkdownAnimations
import com.mikepenz.markdown.model.State
import com.mikepenz.markdown.model.rememberMarkdownState
import com.mikepenz.markdown.model.rememberStreamingMarkdownState
import com.minichat.app.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.conflate
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.intellij.markdown.MarkdownTokenTypes
import org.intellij.markdown.ast.ASTNode

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun AgentConversation(
    state: ChatState,
    agent: AgentRole,
    modifier: Modifier = Modifier,
    onCancel: () -> Unit,
    onSend: suspend (String, List<String>, Boolean, PluginAttachmentType?) -> Boolean,
    onModels: (AgentRole) -> Unit,
    onBranch: ((String) -> Unit)? = null,
    loadThumbnail: (suspend (AttachmentThumbnail) -> ByteArray)? = null,
    openUi: OpenUiInteractions? = null,
    onOpenPreset: (() -> Unit)? = null,
    onBranchReply: ((String) -> Unit)? = null,
    onCreatePreset: (() -> Unit)? = null,
) {
    val home = state.chatId.isBlank() && agent == AgentRole.Chat
    if (home && state.preset.isBlank()) {
        if (state.ready) {
            HomeGreeting(
                "",
                modifier,
                onCreatePreset,
                enabled = !state.busy && !state.preparing,
            )
        } else {
            Spacer(modifier)
        }
        return
    }
    val blocks =
        remember(state.blocks, agent) {
            state.blocks.filter {
                it.role == agent || agent == AgentRole.Chat && it.isSearchTrace
            }
        }
    Column(modifier) {
        if (home) {
            HomeGreeting(
                state.presetName,
                Modifier.weight(1f).fillMaxWidth(),
                onOpenPreset,
                enabled = state.ready && !state.busy && !state.preparing,
            )
        } else
            TraceLog(
                blocks,
                state.expandThoughts,
                Modifier.weight(1f).fillMaxWidth(),
                onBranch = onBranch.takeUnless { state.busy || state.preparing },
                onBranchReply = onBranchReply.takeUnless { state.busy || state.preparing },
                waiting =
                    state.generating == agent &&
                        blocks.lastOrNull()?.user != false &&
                        blocks.none { it.running || it.streaming },
                replyError = state.replyError?.takeIf { it.agent == agent }?.message,
                loadThumbnail = loadThumbnail,
                openUi = openUi,
            )
        Composer(
            state.busy,
            state.preparing,
            onCancel,
            onSend,
            steering = state.generating == agent,
            suggestions = if (home) state.homeSuggestions else emptyList(),
            pluginTypes =
                state.installedPlugins
                    .filter { it.scope.allows(state.preset) }
                    .flatMap { it.attachments },
            draft = state.branchDraft.takeIf { agent == AgentRole.Chat },
        )
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

@Composable
private fun HomeGreeting(
    presetName: String,
    modifier: Modifier,
    onPresetAction: (() -> Unit)?,
    enabled: Boolean,
) {
    val hasPreset = presetName.isNotBlank()
    Box(modifier.padding(horizontal = 24.dp), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                stringResource(
                    if (hasPreset) R.string.welcome_to_preset else R.string.no_presets_yet
                ),
                style = MaterialTheme.typography.headlineSmall,
                textAlign = TextAlign.Center,
            )
            Text(
                if (hasPreset) presetName else stringResource(R.string.create_preset),
                modifier =
                    Modifier.semantics { heading() }
                        .then(
                            if (onPresetAction == null) Modifier
                            else
                                Modifier.clickable(
                                        enabled = enabled,
                                        onClickLabel =
                                            if (hasPreset) stringResource(R.string.edit_preset)
                                            else null,
                                        role = Role.Button,
                                        onClick = onPresetAction,
                                    )
                                    .heightIn(min = 48.dp)
                                    .wrapContentHeight(Alignment.CenterVertically)
                        ),
                style = MaterialTheme.typography.headlineMedium,
                textDecoration = TextDecoration.Underline,
                textAlign = TextAlign.Center,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

private sealed interface TimelineItem {
    val id: String
    val type: String

    data class Message(val block: TraceBlock) : TimelineItem {
        override val id: String
            get() = "message:${block.id}"

        override val type: String
            get() = if (block.user) "user" else "assistant"
    }

    data class ActivityHeader(val header: TraceHeader) : TimelineItem {
        override val id: String
            get() = header.id

        override val type: String
            get() = "activity-header"
    }

    data class ActivityStep(
        val step: TraceBlock,
        val group: TraceHeader,
        val search: TraceHeader?,
    ) : TimelineItem {
        override val id: String
            get() = "activity-step:${step.id}"

        override val type: String
            get() = "activity-step"
    }

    data class InteractiveReply(val block: TraceBlock) : TimelineItem {
        override val id: String
            get() = "open-ui:${block.id}"

        override val type: String
            get() = "open-ui"
    }

    data class ReplyActions(val block: TraceBlock, val text: String) : TimelineItem {
        override val id: String
            get() = "actions:${block.id}"

        override val type: String
            get() = "actions"
    }

    data class Paragraph(
        val block: TraceBlock,
        val document: State.Success,
        val node: ASTNode,
        val last: Boolean,
    ) : TimelineItem {
        override val id: String
            get() = "message:${block.id}:${node.startOffset}"

        override val type: String
            get() = "markdown:${node.type}"
    }
}

private fun timeline(
    blocks: List<TraceBlock>,
    streamed: Map<String, MarkdownParts>,
    defaultExpanded: Boolean,
    expansion: Map<String, Boolean>,
): List<TimelineItem> = buildList {
    val activity = mutableListOf<TraceBlock>()
    fun flush() {
        if (activity.isEmpty()) return
        val id = "activity:${activity.first().id}"
        val group =
            TraceHeader(
                id = id,
                parent = null,
                running = activity.any { it.running || it.streaming },
                tools = activity.count { it.tool.isNotBlank() },
                expanded =
                    expansion[id]
                        ?: (defaultExpanded && activity.any { it.reasoning.isNotBlank() }),
            )
        add(TimelineItem.ActivityHeader(group))
        if (group.expanded) {
            var start = 0
            while (start < activity.size) {
                val step = activity[start]
                if (!step.isSearchTrace) {
                    add(TimelineItem.ActivityStep(step, group, null))
                    start++
                    continue
                }
                var end = start + 1
                while (end < activity.size && activity[end].isSearchTrace) end++
                val searchSteps = activity.subList(start, end)
                val searchId = "search-activity:${step.id}"
                val search =
                    TraceHeader(
                        id = searchId,
                        parent = group,
                        running = searchSteps.any { it.running || it.streaming },
                        tools = searchSteps.count { it.tool.isNotBlank() },
                        expanded = expansion[searchId] == true,
                        agent =
                            if (searchSteps.any { it.role == AgentRole.Search }) AgentRole.Search
                            else AgentRole.Fetch,
                    )
                add(TimelineItem.ActivityHeader(search))
                if (search.expanded)
                    searchSteps.forEach { add(TimelineItem.ActivityStep(it, group, search)) }
                start = end
            }
        }
        activity.clear()
    }
    blocks.forEach { block ->
        block.replyEnd?.let {
            flush()
            add(TimelineItem.ReplyActions(block, it.text))
            return@forEach
        }
        if (block.isSearchTrace) {
            if (
                block.tool.isNotBlank() ||
                    block.reasoning.isNotBlank() ||
                    block.content.isNotBlank()
            )
                activity += block
            return@forEach
        }
        if (block.tool.isNotBlank() || block.reasoning.isNotBlank()) activity += block
        if (!block.openUi.isNullOrBlank()) {
            flush()
            add(TimelineItem.InteractiveReply(block))
        }
        if (block.user || block.content.isNotBlank()) {
            flush()
            val parts =
                block.markdown?.let { MarkdownParts(it, it.node.children) } ?: streamed[block.id]
            val nodes = parts?.nodes?.filter { it.type != MarkdownTokenTypes.EOL }
            if (parts != null && !nodes.isNullOrEmpty()) {
                nodes.forEachIndexed { index, node ->
                    add(
                        TimelineItem.Paragraph(
                            block,
                            parts.document,
                            node,
                            last = index == nodes.lastIndex,
                        )
                    )
                }
            } else if (!block.streaming) {
                add(TimelineItem.Message(block))
            }
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
    waiting: Boolean = false,
    replyError: String? = null,
    list: LazyListState = rememberLazyListState(),
    loadThumbnail: (suspend (AttachmentThumbnail) -> ByteArray)? = null,
    openUi: OpenUiInteractions? = null,
    onBranchReply: ((String) -> Unit)? = null,
) {
    val streamed = buildMap {
        blocks.forEach { block ->
            if (block.streaming && block.content.isNotBlank())
                key(block.id) {
                    rememberStreamingParagraphs(block.content)?.let { put(block.id, it) }
                }
        }
    }
    val expansion =
        rememberSaveable(
            saver =
                mapSaver(
                    save = { it.toMap() },
                    restore = { values ->
                        mutableStateMapOf(
                            *values.map { (id, value) -> id to (value == true) }.toTypedArray()
                        )
                    },
                )
        ) {
            mutableStateMapOf<String, Boolean>()
        }
    val expanded = expansion.toMap()
    val entries =
        remember(blocks, streamed, expandThoughts, expanded) {
            timeline(blocks, streamed, expandThoughts, expanded)
        }
    val itemsByKey = remember(entries) { entries.associateBy { it.id } }
    val pinned by
        remember(list, itemsByKey) {
            derivedStateOf {
                val layout = list.layoutInfo
                val top =
                    layout.visibleItemsInfo
                        .filter { it.offset < layout.viewportEndOffset }
                        .maxByOrNull { it.offset } ?: return@derivedStateOf emptyList()
                val entry = itemsByKey[top.key]
                when (entry) {
                    is TimelineItem.ActivityStep -> listOfNotNull(entry.group, entry.search)
                    is TimelineItem.ActivityHeader ->
                        buildList {
                            entry.header.parent?.let(::add)
                            if (
                                entry.header.expanded &&
                                    top.offset + top.size > layout.viewportEndOffset
                            )
                                add(entry.header)
                        }
                    else -> emptyList()
                }
            }
        }
    fun toggleTrace(header: TraceHeader, pinnedHeader: Boolean = false) {
        if (pinnedHeader && list.canScrollBackward) {
            val remaining =
                timeline(blocks, streamed, expandThoughts, expanded + (header.id to false))
            val index = remaining.indexOfFirst { it.id == header.id }
            if (index >= 0) list.requestScrollToItem(index + 1)
        }
        expansion[header.id] = !header.expanded
    }
    val canReturn by remember {
        derivedStateOf { list.canScrollBackward && list.layoutInfo.totalItemsCount > 1 }
    }
    val scope = rememberCoroutineScope()
    val openUiPages = remember(openUi) { OpenUiPageCache() }
    DisposableEffect(openUiPages) { onDispose(openUiPages::close) }
    Box(modifier) {
        LazyColumn(
            Modifier.fillMaxSize().testTag("transcript"),
            state = list,
            reverseLayout = true,
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.Bottom,
        ) {
            // Keep a permanent bottom item. Stable keys let LazyColumn hold this anchor at the
            // latest edge and preserve the user's reading position elsewhere without a scroll jump.
            item(key = "latest-edge", contentType = "status") {
                Box(Modifier.fillMaxWidth().heightIn(min = 1.dp)) {
                    if (replyError != null)
                        Text(
                            replyError,
                            color = MaterialTheme.colorScheme.error,
                            modifier = Modifier.padding(vertical = 8.dp),
                        )
                    else if (waiting)
                        Text(
                            stringResource(R.string.running),
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(vertical = 8.dp),
                        )
                }
            }
            items(entries, key = { it.id }, contentType = { it.type }) { item ->
                val spacing =
                    if (
                        item is TimelineItem.ActivityHeader ||
                            item is TimelineItem.ActivityStep ||
                            item is TimelineItem.Paragraph && !item.last
                    )
                        0.dp
                    else 10.dp
                Box(Modifier.fillMaxWidth().padding(bottom = spacing)) {
                    when (item) {
                        is TimelineItem.ActivityHeader ->
                            TraceHeading(item.header, obscured = pinned.any { it.id == item.id }) {
                                toggleTrace(item.header)
                            }
                        is TimelineItem.ActivityStep ->
                            TraceStep(item.step, if (item.search == null) 1 else 2)
                        is TimelineItem.InteractiveReply ->
                            OpenUiReply(item.block, openUi, openUiPages)
                        is TimelineItem.ReplyActions ->
                            ReplyActions(
                                item.text,
                                item.block.id,
                                item.block.version.isNotBlank(),
                                onBranchReply,
                            )
                        is TimelineItem.Message ->
                            if (item.block.user) UserMessage(item.block, onBranch, loadThumbnail)
                            else
                                SelectionContainer(Modifier.fillMaxWidth()) {
                                    Text(
                                        item.block.content,
                                        style = MaterialTheme.typography.bodyMedium,
                                    )
                                }
                        is TimelineItem.Paragraph ->
                            SelectionContainer(Modifier.fillMaxWidth()) {
                                Markdown(
                                    item.document,
                                    modifier = Modifier.fillMaxWidth(),
                                    animations = stillMarkdown,
                                    success = { state, components, _ ->
                                        MarkdownElement(item.node, components, state.content)
                                    },
                                )
                            }
                    }
                }
            }
        }
        if (pinned.isNotEmpty()) {
            Surface(
                modifier =
                    Modifier.align(Alignment.TopCenter)
                        .fillMaxWidth()
                        .scrollable(list, Orientation.Vertical),
                color = MaterialTheme.colorScheme.surface,
            ) {
                Column(Modifier.padding(horizontal = 16.dp)) {
                    pinned.forEach { header ->
                        TraceHeading(header, pinned = true) {
                            toggleTrace(header, pinnedHeader = true)
                        }
                    }
                }
            }
        }
        if (canReturn)
            FilledTonalIconButton(
                onClick = {
                    scope.launch { list.animateScrollToItem(0) }
                },
                modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 8.dp),
            ) {
                Symbol("arrow_downward", stringResource(R.string.latest_message))
            }
    }
}

@Composable
private fun ReplyActions(
    text: String,
    id: String,
    canBranch: Boolean,
    onBranch: ((String) -> Unit)?,
) {
    val clipboard = LocalClipboard.current
    val scope = rememberCoroutineScope()
    val copy = stringResource(R.string.copy)
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        IconButton(
            enabled = text.isNotBlank(),
            onClick = {
                scope.launch {
                    clipboard.setClipEntry(ClipEntry(ClipData.newPlainText(copy, text)))
                }
            },
        ) {
            Symbol("content_copy", copy, size = 20)
        }
        if (canBranch) {
            IconButton(onClick = { onBranch?.invoke(id) }, enabled = onBranch != null) {
                Symbol("fork_right", stringResource(R.string.branch_chat), size = 20)
            }
        }
    }
}

private val stillMarkdown =
    object : MarkdownAnimations {
        override val animateTextSize: Modifier.() -> Modifier = { this }
    }

private data class MarkdownParts(val document: State.Success, val nodes: List<ASTNode>)

@Composable
private fun rememberStreamingParagraphs(text: String): MarkdownParts? {
    val stream = rememberStreamingMarkdownState()
    val source by rememberUpdatedState(text)
    var parts by remember { mutableStateOf<MarkdownParts?>(null) }
    LaunchedEffect(stream) {
        var consumed = 0
        snapshotFlow { source }
            .conflate()
            .collect { current ->
                if (current.length > consumed) {
                    val snapshot =
                        withContext(Dispatchers.Default) {
                            stream.append(current.substring(consumed))
                        }
                    consumed = current.length
                    val nodes = snapshot.stableAst + snapshot.unstableAstTail
                    val root = nodes.firstOrNull() ?: return@collect
                    parts =
                        MarkdownParts(
                            State.Success(root, current, true, stream.referenceLinkHandler),
                            nodes,
                        )
                }
            }
    }
    return parts
}

@Composable
private fun UserMessage(
    block: TraceBlock,
    branch: ((String) -> Unit)?,
    loadThumbnail: (suspend (AttachmentThumbnail) -> ByteArray)?,
) {
    var menu by remember { mutableStateOf(false) }
    val branchLabel = stringResource(R.string.branch_edit_message)
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
                    if (block.system)
                        Text(
                            stringResource(R.string.system_message),
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.primary,
                        )
                    if (loadThumbnail != null)
                        block.thumbnails.forEach { thumbnail ->
                            val image by
                                produceState<android.graphics.Bitmap?>(null, thumbnail) {
                                    value =
                                        try {
                                            val data = loadThumbnail(thumbnail)
                                            withContext(Dispatchers.Default) {
                                                val bounds =
                                                    android.graphics.BitmapFactory.Options().apply {
                                                        inJustDecodeBounds = true
                                                    }
                                                android.graphics.BitmapFactory.decodeByteArray(
                                                    data,
                                                    0,
                                                    data.size,
                                                    bounds,
                                                )
                                                if (
                                                    bounds.outWidth !in 1..1280 ||
                                                        bounds.outHeight !in 1..1280
                                                )
                                                    null
                                                else
                                                    android.graphics.BitmapFactory.decodeByteArray(
                                                        data,
                                                        0,
                                                        data.size,
                                                    )
                                            }
                                        } catch (cancelled: CancellationException) {
                                            throw cancelled
                                        } catch (_: Exception) {
                                            null
                                        }
                                }
                            Box(
                                Modifier.size(width = 180.dp, height = 120.dp),
                                contentAlignment = Alignment.Center,
                            ) {
                                image?.let {
                                    Image(it.asImageBitmap(), null, Modifier.fillMaxSize())
                                }
                            }
                        }
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
                    leadingIcon = { Symbol("edit", size = 20) },
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
