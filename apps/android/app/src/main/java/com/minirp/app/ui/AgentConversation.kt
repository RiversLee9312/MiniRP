package com.minirp.app.ui

import android.content.ClipData
import android.widget.Toast
import androidx.annotation.StringRes
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.scrollable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.mapSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.UriHandler
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
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
import com.minirp.app.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.conflate
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.intellij.markdown.MarkdownTokenTypes
import org.intellij.markdown.ast.ASTNode
import java.util.UUID

@Composable
internal fun AgentConversation(
    state: ChatState,
    agent: AgentRole,
    modifier: Modifier = Modifier,
    onCancel: () -> Unit,
    onSend: suspend (ComposerSubmission) -> Boolean,
    onModels: (AgentRole) -> Unit,
    onBranch: ((String) -> Unit)? = null,
    loadThumbnail: (suspend (AttachmentThumbnail) -> ByteArray)? = null,
    openUi: OpenUiInteractions? = null,
    onOpenPreset: (() -> Unit)? = null,
    onBranchReply: ((String) -> Unit)? = null,
    onCreatePreset: (() -> Unit)? = null,
    background: PresetBackground? = null,
    displayAssets: DisplayAssets? = null,
    onPresetHintSeen: (() -> Unit)? = null,
    onRetryPresetInterview: (() -> Unit)? = null,
    composerStatus: @Composable (() -> Unit)? = null,
    systemMessageMode: Boolean? = null,
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
    val draft = state.branchDraft.takeIf { agent == AgentRole.Chat }
    val draftText = rememberSaveable { mutableStateOf(draft?.text.orEmpty()) }
    val draftFiles = rememberDraftFiles(draft)
    val draftQuotes = rememberDraftQuotes(draft)
    val context = LocalContext.current
    val addQuote: ((String) -> Unit)? = if (systemMessageMode == true) null else { selection ->
        if (selection.length > maxQuoteLength || draftFiles.value.size + draftQuotes.value.size >= maxMessageAttachments) {
            Toast.makeText(context, R.string.attachment_too_large, Toast.LENGTH_SHORT).show()
        } else {
            draftQuotes.value = draftQuotes.value + QuoteAttachment(UUID.randomUUID().toString(), selection)
        }
    }
    val sending = state.submitting == agent
    val transcriptStatus = when {
        sending -> R.string.sending
        state.generating == agent && blocks.lastOrNull()?.user != false &&
            blocks.none { it.running || it.streaming } -> R.string.running
        else -> null
    }
    Column(modifier) {
        Box(Modifier.weight(1f).fillMaxWidth()) {
            PresetBackdrop(
                background, conversation = !home, blur = state.appearance.blurBackground,
                modifier = Modifier.matchParentSize(),
            )
            if (home) {
                HomeGreeting(
                    state.presetName,
                    Modifier.fillMaxSize(),
                    onOpenPreset,
                    enabled = state.ready && !state.busy && !state.preparing,
                    onBackground = background != null,
                    onHintSeen = onPresetHintSeen,
                )
            } else
                TraceLog(
                    blocks,
                    state.expandThoughts,
                    Modifier.fillMaxSize(),
                    onBranch = onBranch.takeUnless { state.busy || state.preparing },
                    onBranchReply = onBranchReply.takeUnless { state.busy || state.preparing },
                    status = transcriptStatus,
                    replyError = state.replyError?.takeIf { it.agent == agent }?.message,
                    onRetry = onRetryPresetInterview.takeIf {
                        agent == AgentRole.Preset && state.presetSetupPending && !state.presetWorking &&
                            state.replyError?.presetInterview == true
                    },
                    loadThumbnail = loadThumbnail,
                    openUi = openUi,
                    displayAssets = displayAssets,
                    onQuote = addQuote,
                )
        }
        if (home && sending)
            ConversationStatus(R.string.sending, Modifier.padding(horizontal = 16.dp))
        composerStatus?.invoke()
        Composer(
            state.busy,
            state.preparing,
            onCancel,
            onSend,
            steering = state.generating == agent,
            allowAttachments = systemMessageMode != true,
            pluginTypes =
                state.installedPlugins
                    .filter { it.availableIn(state.preset) }
                    .flatMap { it.attachments },
            draft = draft,
            textState = draftText,
            documentTypes = if (agent == AgentRole.Preset) arrayOf("*/*")
                else com.minirp.app.data.AttachmentStore.documentTypes,
            systemMessageMode = systemMessageMode,
            filesState = draftFiles,
            quotesState = draftQuotes,
        )
        val presetModels = agent == AgentRole.Preset
        val modelsLayout =
            if (presetModels) Modifier else Modifier.horizontalScroll(rememberScrollState())
        Row(
            Modifier.fillMaxWidth()
                .testTag("agent-models")
                .then(modelsLayout)
                .padding(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            val roles =
                when {
                    presetModels -> listOf(agent, AgentRole.Plugin)
                    agent == AgentRole.Chat && state.agenticSearch -> listOf(agent, AgentRole.Search)
                    else -> listOf(agent)
                }
            roles.forEach { role ->
                val label = stringResource(role.label)
                val name =
                    state.assigned(role)?.choice?.name ?: stringResource(R.string.select_model)
                AssistChip(
                    onClick = { onModels(role) },
                    enabled = !state.preparing,
                    label = {
                        if (presetModels) {
                            Column(Modifier.padding(vertical = 4.dp)) {
                                Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(
                                    name,
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                        } else {
                            Text("$label · $name", maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    },
                    trailingIcon = { Symbol("expand_more", size = 16) },
                    modifier = (if (presetModels) Modifier.weight(1f) else Modifier.widthIn(max = 290.dp))
                        .testTag("agent-model:${role.id}"),
                )
            }
        }
    }
}

@Composable
internal fun HomeGreeting(
    presetName: String,
    modifier: Modifier,
    onPresetAction: (() -> Unit)?,
    enabled: Boolean,
    onBackground: Boolean = false,
    onHintSeen: (() -> Unit)? = null,
) {
    val hasPreset = presetName.isNotBlank()
    val dark = LocalMiniRPDarkTheme.current
    val foreground = if (!onBackground) Color.Unspecified else if (dark) Color.White else Color.Black
    val shadow = if (onBackground) Shadow(
        color = (if (dark) Color.Black else Color.White).copy(alpha = 0.82f),
        blurRadius = with(LocalDensity.current) { 14.dp.toPx() },
    ) else null
    Box(modifier.padding(horizontal = 24.dp), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                stringResource(
                    if (hasPreset) R.string.welcome_to_preset else R.string.no_presets_yet
                ),
                color = foreground,
                style = MaterialTheme.typography.headlineSmall.copy(shadow = shadow),
                textAlign = TextAlign.Center,
            )
            PresetEntryHint(stringResource(R.string.preset_home_hint), onHintSeen) {
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
                                        ) {
                                            onHintSeen?.invoke()
                                            onPresetAction()
                                        }
                                        .heightIn(min = 48.dp)
                                        .wrapContentHeight(Alignment.CenterVertically)
                            ),
                    color = foreground,
                    style = MaterialTheme.typography.headlineMedium.copy(shadow = shadow),
                    textDecoration = TextDecoration.Underline,
                    textAlign = TextAlign.Center,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
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
        val child: TraceHeader?,
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

    data class Speaker(val block: TraceBlock) : TimelineItem {
        override val id: String
            get() = "speaker:${block.id}"

        override val type: String
            get() = "speaker"
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
    standalonePluginTrace: Boolean,
): List<TimelineItem> = buildList {
    val activity = mutableListOf<TraceBlock>()
    val pluginRuns = if (standalonePluginTrace) emptyMap()
        else blocks.filter { it.pluginRun.isNotBlank() }.groupBy { it.pluginRun }
    val seenRuns = mutableSetOf<String>()
    fun flush() {
        if (activity.isEmpty()) return
        val id = "activity:${activity.first().id}"
        val group =
            TraceHeader(
                id = id,
                parent = null,
                running = activity.any { it.running || it.streaming },
                tools = activity.sumOf { step ->
                    val run = pluginRuns[step.pluginRun]
                    if (run == null) (if (step.tool.isNotBlank()) 1 else 0)
                    else run.count { it.tool.isNotBlank() }
                },
                expanded =
                    expansion[id]
                        ?: (standalonePluginTrace || defaultExpanded && activity.any { step ->
                            step.reasoning.isNotBlank() ||
                                pluginRuns[step.pluginRun]?.any { it.reasoning.isNotBlank() } == true
                        }),
            )
        add(TimelineItem.ActivityHeader(group))
        if (group.expanded) {
            fun addChild(steps: List<TraceBlock>, id: String, agent: AgentRole? = null, name: String? = null) {
                val status = steps.firstOrNull { it.pluginStatus.isNotBlank() }?.pluginStatus
                val header = TraceHeader(
                    id = id,
                    parent = group,
                    running = status?.let { it == "running" } ?: steps.any { it.running || it.streaming },
                    tools = steps.count { it.tool.isNotBlank() },
                    expanded = expansion[id] == true,
                    agent = agent,
                    agentName = name,
                )
                add(TimelineItem.ActivityHeader(header))
                if (header.expanded) steps.filter { it.hasTraceContent }.forEach {
                    add(TimelineItem.ActivityStep(it, group, header))
                }
            }
            var start = 0
            while (start < activity.size) {
                val step = activity[start]
                val run = pluginRuns[step.pluginRun]
                if (run != null) {
                    addChild(run, "plugin-activity:${step.pluginRun}", agent = AgentRole.Plugin,
                        name = step.pluginAgentName.takeIf(String::isNotBlank))
                    start++
                } else if (step.isSearchTrace) {
                    var end = start + 1
                    while (end < activity.size && activity[end].isSearchTrace) end++
                    val steps = activity.subList(start, end)
                    val agent = if (steps.any { it.role == AgentRole.Search }) AgentRole.Search else AgentRole.Fetch
                    addChild(steps, "search-activity:${step.id}", agent = agent)
                    start = end
                } else {
                    add(TimelineItem.ActivityStep(step, group, null))
                    start++
                }
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
        if (block.pluginRun in pluginRuns) {
            if (seenRuns.add(block.pluginRun)) activity += block
            return@forEach
        }
        if (block.isSearchTrace) {
            if (block.hasTraceContent) activity += block
            return@forEach
        }
        if (block.compaction || block.tool.isNotBlank() || block.reasoning.isNotBlank()) activity += block
        if (!block.openUi.isNullOrBlank()) {
            flush()
            add(TimelineItem.InteractiveReply(block))
        }
        if (block.user || block.content.isNotBlank()) {
            flush()
            if (block.speaker.isNotBlank()) add(TimelineItem.Speaker(block))
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

@Composable
internal fun TraceLog(
    blocks: List<TraceBlock>,
    expandThoughts: Boolean,
    modifier: Modifier = Modifier,
    onBranch: ((String) -> Unit)? = null,
    @StringRes status: Int? = null,
    replyError: String? = null,
    list: LazyListState = rememberLazyListState(initialFirstVisibleItemIndex = Int.MAX_VALUE),
    loadThumbnail: (suspend (AttachmentThumbnail) -> ByteArray)? = null,
    openUi: OpenUiInteractions? = null,
    displayAssets: DisplayAssets? = null,
    onBranchReply: ((String) -> Unit)? = null,
    standalonePluginTrace: Boolean = false,
    onQuote: ((String) -> Unit)? = null,
    onRetry: (() -> Unit)? = null,
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
        remember(blocks, streamed, expandThoughts, expanded, standalonePluginTrace) {
            timeline(blocks, streamed, expandThoughts, expanded, standalonePluginTrace)
        }
    // Keep the visible row anchored at its top as it grows. Pin to the end only
    // when the reader was already there before the content changed.
    val content = Triple(entries, status, replyError)
    var previousContent by remember(list) { mutableStateOf(content) }
    var followingLatest by rememberSaveable { mutableStateOf(list.firstVisibleItemIndex == Int.MAX_VALUE) }
    val latestIndex by rememberUpdatedState(entries.size)
    LaunchedEffect(list) {
        var previousAnchor: Any? = null
        snapshotFlow {
            val anchor = list.layoutInfo.visibleItemsInfo.firstOrNull { it.index == list.firstVisibleItemIndex }?.key
            Triple(list.isScrollInProgress, list.canScrollForward, anchor)
        }.collect { (scrolling, away, anchor) ->
            when {
                scrolling -> followingLatest = false
                !away -> followingLatest = true
                anchor != previousAnchor -> followingLatest = false
                followingLatest -> list.requestScrollToItem(latestIndex)
            }
            previousAnchor = anchor
        }
    }
    SideEffect {
        if (previousContent != content) {
            if (followingLatest && !list.isScrollInProgress) {
                list.requestScrollToItem(entries.size)
            }
            previousContent = content
        }
    }
    val itemsByKey = remember(entries) { entries.associateBy { it.id } }
    val headingHeight = with(LocalDensity.current) { traceHeadingMinHeight.roundToPx() }
    val pinnedHeaders by
        remember(list, itemsByKey, expanded, headingHeight) {
            derivedStateOf {
                val layout = list.layoutInfo
                val top =
                    layout.visibleItemsInfo.firstOrNull { it.offset + it.size > layout.viewportStartOffset }
                        ?: return@derivedStateOf emptyList<TraceHeader>() to null
                val entry = itemsByKey[top.key]
                val groups = when (entry) {
                    is TimelineItem.ActivityStep -> listOfNotNull(entry.group, entry.child)
                    is TimelineItem.ActivityHeader ->
                        buildList {
                            entry.header.parent?.let(::add)
                            if (
                                entry.header.expanded &&
                                    top.offset < layout.viewportStartOffset
                            )
                                add(entry.header)
                        }
                    else -> emptyList()
                }
                val tool = (entry as? TimelineItem.ActivityStep)?.takeIf {
                    expanded[it.id] == true &&
                        top.offset < layout.viewportStartOffset + groups.size * headingHeight
                }
                groups to tool
            }
        }
    val (pinned, pinnedTool) = pinnedHeaders
    fun toggleTrace(header: TraceHeader, pinnedHeader: Boolean = false) {
        if (pinnedHeader && list.canScrollForward) {
            val remaining =
                timeline(blocks, streamed, expandThoughts, expanded + (header.id to false), standalonePluginTrace)
            val index = remaining.indexOfFirst { it.id == header.id }
            if (index >= 0) list.requestScrollToItem((index - 1).coerceAtLeast(0))
        }
        expansion[header.id] = !header.expanded
    }
    fun toggleStep(item: TimelineItem.ActivityStep) {
        val open = expansion[item.id] == true
        if (open && pinnedTool?.id == item.id) {
            val index = entries.indexOfFirst { it.id == item.id }
            if (index >= 0) list.requestScrollToItem(index, -pinned.size * headingHeight)
        }
        expansion[item.id] = !open
    }
    val canReturn by remember {
        derivedStateOf { list.canScrollForward && list.layoutInfo.totalItemsCount > 1 }
    }
    val scope = rememberCoroutineScope()
    val openUiPages = remember(openUi) { OpenUiPageCache() }
    DisposableEffect(openUiPages) { onDispose(openUiPages::close) }
    Box(modifier) {
        LazyColumn(
            Modifier.fillMaxSize().testTag("transcript"),
            state = list,
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.Bottom,
        ) {
            items(entries, key = { it.id }, contentType = { it.type }) { item ->
                val spacing =
                    if (
                        item is TimelineItem.ActivityHeader ||
                            item is TimelineItem.ActivityStep ||
                            item is TimelineItem.Speaker ||
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
                        is TimelineItem.ActivityStep -> {
                            val depth = if (item.child == null) 1 else 2
                            val nestedCard = item.child != null && !item.step.openUi.isNullOrBlank()
                            if (!nestedCard || item.step.reasoning.isNotBlank() || item.step.content.isNotBlank() ||
                                item.step.toolArgumentsUi != null || item.step.toolResultUi != null)
                                TraceStep(
                                    item.step,
                                    depth,
                                    details = expanded[item.id] == true,
                                    toggleDetails = { toggleStep(item) },
                                    openUiPages = openUiPages,
                                    obscureHeading = pinnedTool?.id == item.id,
                                )
                            if (nestedCard) {
                                Box(Modifier.traceQuote(depth)) { OpenUiReply(item.step, openUi, openUiPages, displayAssets) }
                            }
                        }
                        is TimelineItem.InteractiveReply ->
                            OpenUiReply(item.block, openUi, openUiPages, displayAssets)
                        is TimelineItem.Speaker ->
                            Text(
                                item.block.speaker,
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.padding(bottom = 4.dp),
                            )
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
                                ReplySelection(onQuote.takeUnless { item.block.system }) {
                                    Text(
                                        item.block.content,
                                        style = MaterialTheme.typography.bodyMedium,
                                    )
                                }
                        is TimelineItem.Paragraph ->
                            ReplySelection(onQuote.takeUnless { item.block.user || item.block.system }) {
                                val external = LocalUriHandler.current
                                val links = remember(external, displayAssets, item.block.id) {
                                    object : UriHandler {
                                        override fun openUri(uri: String) {
                                            if (isDisplayAssetUrl(uri)) displayAssets?.download(item.block.id, uri)
                                            else external.openUri(uri)
                                        }
                                    }
                                }
                                CompositionLocalProvider(LocalUriHandler provides links) {
                                    Markdown(
                                        item.document,
                                        modifier = Modifier.fillMaxWidth(),
                                        animations = stillMarkdown,
                                        components = chatMarkdownComponents,
                                        success = { state, components, _ ->
                                            MarkdownElement(item.node, components, state.content)
                                        },
                                    )
                                }
                            }
                    }
                }
            }
            item(key = "latest-edge", contentType = "status") {
                Box(Modifier.fillMaxWidth().heightIn(min = 1.dp)) {
                    if (replyError != null) {
                        Column(Modifier.padding(vertical = 8.dp)) {
                            Text(replyError, color = MaterialTheme.colorScheme.error)
                            if (onRetry != null) TextButton(onClick = onRetry) { Text(stringResource(R.string.retry)) }
                        }
                    } else if (status != null) ConversationStatus(status)
                }
            }
        }
        if (pinned.isNotEmpty()) {
            Surface(
                modifier =
                    Modifier.align(Alignment.TopCenter)
                        .fillMaxWidth()
                        .scrollable(list, Orientation.Vertical, reverseDirection = true),
                color = MaterialTheme.colorScheme.surface,
            ) {
                Column(Modifier.padding(horizontal = 16.dp)) {
                    pinned.forEach { header ->
                        TraceHeading(header, pinned = true) {
                            toggleTrace(header, pinnedHeader = true)
                        }
                    }
                    pinnedTool?.let { item ->
                        TraceStepHeading(
                            item.step,
                            expanded = true,
                            modifier = Modifier.testTag("pinned:${item.id}")
                                .traceQuote(if (item.child == null) 1 else 2),
                            toggle = { toggleStep(item) },
                        )
                    }
                }
            }
        }
        if (canReturn)
            FilledTonalIconButton(
                onClick = {
                    scope.launch {
                        list.animateScrollToItem(entries.size)
                        followingLatest = true
                        list.requestScrollToItem(latestIndex)
                    }
                },
                modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 8.dp),
            ) {
                Symbol("arrow_downward", stringResource(R.string.latest_message))
            }
    }
}

@Composable
private fun ConversationStatus(@StringRes status: Int, modifier: Modifier = Modifier) {
    Text(
        stringResource(status),
        modifier = modifier.padding(vertical = 8.dp).semantics { liveRegion = LiveRegionMode.Polite },
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
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
                    block.quotes.forEach { quote ->
                        key(quote.id) { QuoteAttachmentCard(quote) }
                    }
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

private val TraceBlock.hasTraceContent: Boolean
    get() = compaction || tool.isNotBlank() || reasoning.isNotBlank() || content.isNotBlank() || !openUi.isNullOrBlank()

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
        Markdown(
            streamingMarkdownState = state,
            modifier = Modifier.fillMaxWidth(),
            components = chatMarkdownComponents,
        )
    } else {
        Markdown(
            rememberMarkdownState(text, retainState = true),
            modifier = Modifier.fillMaxWidth(),
            components = chatMarkdownComponents,
        )
    }
}
