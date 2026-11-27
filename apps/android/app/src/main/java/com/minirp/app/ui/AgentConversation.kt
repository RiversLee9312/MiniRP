package com.minirp.app.ui

import android.content.ClipData
import android.widget.Toast
import androidx.annotation.StringRes
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.scrollable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.DisableSelection
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.mapSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.onSizeChanged
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
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.mikepenz.markdown.compose.MarkdownElement
import com.mikepenz.markdown.m3.Markdown
import com.mikepenz.markdown.model.MarkdownAnimations
import com.mikepenz.markdown.model.State
import com.mikepenz.markdown.model.rememberMarkdownState
import com.mikepenz.markdown.model.rememberStreamingMarkdownState
import com.minirp.app.R
import com.minirp.app.data.AttachmentFile
import com.minirp.app.data.AttachmentStore
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
                modifier.windowInsetsPadding(WindowInsets.safeDrawing.only(WindowInsetsSides.Bottom)),
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
                it.role == agent && it.subagentRole == null
            }
        }
    val draft = if (agent == AgentRole.Preset) state.presetBranchDraft else state.branchDraft?.takeIf { it.agent == agent }
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
    val wallpaperCard = background != null
    PresetBackdropLayout(background, modifier, home = home, blur = state.appearance.blurBackground) { contrast ->
        Column(Modifier.fillMaxSize()) {
            Box(Modifier.weight(1f).fillMaxWidth()) {
                if (home) {
                    HomeGreeting(
                        state.presetName,
                        Modifier.fillMaxSize(),
                        onOpenPreset,
                        enabled = state.ready && !state.busy && !state.preparing,
                        contrast = contrast,
                        onHintSeen = onPresetHintSeen,
                    )
                } else {
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
            }
            val scheme = MaterialTheme.colorScheme
            Surface(
                modifier = Modifier.fillMaxWidth(),
                color = if (!wallpaperCard && agent == AgentRole.Chat) scheme.surfaceContainer else Color.Transparent,
                contentColor = scheme.onSurface,
            ) {
                Column(Modifier.windowInsetsPadding(WindowInsets.safeDrawing.only(WindowInsetsSides.Bottom))) {
                    if (home && sending)
                        ConversationStatus(R.string.sending, Modifier.padding(horizontal = 16.dp))
                    composerStatus?.invoke()
                    ComposerCard(wallpaperCard) { fieldInteraction ->
                        Column {
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
                                cardInputInteraction = fieldInteraction,
                                wallpaperActions = if (fieldInteraction == null) null else { actionsModifier ->
                                    AgentModelPickers(state, agent, onModels, actionsModifier, besideActions = true)
                                },
                            )
                            if (fieldInteraction == null) {
                                AgentModelPickers(state, agent, onModels,
                                    modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp))
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun AgentModelPickers(
    state: ChatState,
    agent: AgentRole,
    onModels: (AgentRole) -> Unit,
    modifier: Modifier,
    besideActions: Boolean = false,
) {
    val scheme = MaterialTheme.colorScheme
    val presetModels = agent == AgentRole.Preset
    val modelsLayout =
        if (presetModels && !besideActions) Modifier else Modifier.horizontalScroll(rememberScrollState())
    Row(
        modifier.testTag("agent-models").then(modelsLayout),
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
                border = AssistChipDefaults.assistChipBorder(
                    enabled = !state.preparing,
                    borderColor = scheme.outline,
                ),
                label = {
                    if (presetModels) {
                        Column(Modifier.padding(vertical = 4.dp)) {
                            Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                name,
                                style = MaterialTheme.typography.labelSmall,
                                color = scheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                    } else {
                        Text("$label · $name", maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                },
                trailingIcon = { Symbol("expand_more", size = 16) },
                modifier = (if (presetModels && !besideActions) Modifier.weight(1f)
                    else Modifier.widthIn(max = if (besideActions) 164.dp else 290.dp)).testTag("agent-model:${role.id}"),
            )
        }
    }
}

@Composable
private fun ComposerCard(
    wallpaper: Boolean,
    content: @Composable (MutableInteractionSource?) -> Unit,
) {
    if (!wallpaper) {
        content(null)
        return
    }
    val scheme = MaterialTheme.colorScheme
    val field = remember { MutableInteractionSource() }
    val focused by field.collectIsFocusedAsState()
    Surface(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp).testTag("composer-card"),
        shape = MaterialTheme.shapes.extraLarge,
        color = scheme.surfaceContainerLow,
        border = BorderStroke(1.dp, if (focused) scheme.primary else scheme.outline),
        contentColor = scheme.onSurface,
    ) {
        content(field)
    }
}

@Composable
internal fun HomeGreeting(
    presetName: String,
    modifier: Modifier,
    onPresetAction: (() -> Unit)?,
    enabled: Boolean,
    contrast: HomeWallpaperContrast? = null,
    onHintSeen: (() -> Unit)? = null,
) {
    val hasPreset = presetName.isNotBlank()
    Box(modifier.padding(horizontal = 24.dp), contentAlignment = Alignment.Center) {
        Column(Modifier.testTag("preset-home-heading"), horizontalAlignment = Alignment.CenterHorizontally) {
            HomeWallpaperText(
                stringResource(
                    if (hasPreset) R.string.welcome_to_preset else R.string.no_presets_yet
                ),
                contrast = contrast,
                style = MaterialTheme.typography.headlineSmall.copy(textAlign = TextAlign.Center),
            )
            PresetEntryHint(stringResource(R.string.preset_home_hint), onHintSeen) {
                HomeWallpaperText(
                    if (hasPreset) presetName else stringResource(R.string.create_preset),
                    contrast = contrast,
                    name = true,
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
                    style = MaterialTheme.typography.headlineMedium.copy(
                        textAlign = TextAlign.Center, textDecoration = TextDecoration.Underline,
                    ),
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
    expandActivity: Boolean,
): List<TimelineItem> = buildList {
    val activity = mutableListOf<TraceBlock>()
    fun flush() {
        if (activity.isEmpty()) return
        val id = "activity:${activity.first().id}"
        val hasReasoning = activity.any { it.reasoning.isNotBlank() }
        val group =
            TraceHeader(
                id = id,
                running = activity.any { it.running || it.streaming },
                tools = activity.count { it.tool.isNotBlank() },
                expanded = expansion[id] ?: (expandActivity || defaultExpanded && hasReasoning),
                hasReasoning = hasReasoning,
            )
        add(TimelineItem.ActivityHeader(group))
        if (group.expanded) activity.forEach { add(TimelineItem.ActivityStep(it, group)) }
        activity.clear()
    }
    blocks.forEach { block ->
        block.replyEnd?.let {
            flush()
            add(TimelineItem.ReplyActions(block, it.text))
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

private data class PinnedTrace(val item: TimelineItem, val offset: Int, val height: Int)

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
    expandActivity: Boolean = false,
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
        remember(blocks, streamed, expandThoughts, expanded, expandActivity) {
            timeline(blocks, streamed, expandThoughts, expanded, expandActivity)
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
    val groupEnds = remember(entries) {
        buildMap {
            entries.forEachIndexed { index, entry ->
                when (entry) {
                    is TimelineItem.ActivityHeader -> put(entry.id, index)
                    is TimelineItem.ActivityStep -> put(entry.group.id, index)
                    else -> Unit
                }
            }
        }
    }
    val headingHeight = with(LocalDensity.current) { traceHeadingMinHeight.roundToPx() }
    val headingHeights = remember(headingHeight) { mutableStateMapOf<String, Int>() }
    fun measureHeading(id: String) = Modifier.onSizeChanged { size ->
        if (size.height == headingHeight) headingHeights.remove(id)
        else headingHeights[id] = size.height
    }
    val pinned by
        remember(list, itemsByKey, groupEnds, expanded, headingHeights, headingHeight) {
            derivedStateOf {
                val layout = list.layoutInfo
                val edge = layout.viewportStartOffset
                val visible = layout.visibleItemsInfo
                val top = visible.firstOrNull { it.offset + it.size > edge }
                    ?: return@derivedStateOf emptyList()
                val entry = itemsByKey[top.key]
                val group = when (entry) {
                    is TimelineItem.ActivityStep -> entry.group
                    is TimelineItem.ActivityHeader -> entry.header.takeIf { it.expanded && top.offset < edge }
                    else -> null
                }
                val stackHeight = group?.let { headingHeights[it.id] ?: headingHeight } ?: 0
                val toolRow = visible.firstOrNull { it.offset + it.size > edge + stackHeight }
                val tool = toolRow?.let { row ->
                    (itemsByKey[row.key] as? TimelineItem.ActivityStep)?.takeIf {
                        expanded[it.id] == true && it.group.id == group?.id && row.offset < edge + stackHeight
                    }
                }
                buildList {
                    var anchor = 0
                    var covered = 0
                    fun pin(item: TimelineItem, lastIndex: Int) {
                        val height = headingHeights[item.id] ?: headingHeight
                        val last = visible.firstOrNull { it.index == lastIndex }
                        val offset = if (last == null) anchor else minOf(anchor, last.offset + last.size - edge - height)
                        if (offset + height > covered) {
                            add(PinnedTrace(item, offset, height))
                            covered = offset + height
                        }
                        anchor += height
                    }
                    if (group != null) pin(TimelineItem.ActivityHeader(group), groupEnds.getValue(group.id))
                    if (toolRow != null && tool != null) pin(tool, toolRow.index)
                }
            }
        }
    val pinnedTool = pinned.lastOrNull()?.takeIf { it.item is TimelineItem.ActivityStep }
    fun toggleTrace(header: TraceHeader, pinnedHeader: Boolean = false) {
        if (pinnedHeader && list.canScrollForward) {
            val remaining =
                timeline(blocks, streamed, expandThoughts, expanded + (header.id to false), expandActivity)
            val index = remaining.indexOfFirst { it.id == header.id }
            if (index >= 0) list.requestScrollToItem((index - 1).coerceAtLeast(0))
        }
        expansion[header.id] = !header.expanded
    }
    fun toggleStep(item: TimelineItem.ActivityStep) {
        val open = expansion[item.id] == true
        if (open && pinnedTool?.item?.id == item.id) {
            val index = entries.indexOfFirst { it.id == item.id }
            if (index >= 0) list.requestScrollToItem(index, -pinned.dropLast(1).sumOf { it.height })
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
        // Share a selection across prose rows while retaining the lazy timeline and its stable keys.
        ReplySelection(onQuote) {
            LazyColumn(
                Modifier.fillMaxSize().testTag("transcript"),
                state = list,
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp),
                verticalArrangement = Arrangement.Bottom,
            ) {
                items(entries, key = { it.id }, contentType = { it.type }) { item ->
                    val spacing = when (item) {
                        is TimelineItem.ActivityHeader, is TimelineItem.ActivityStep, is TimelineItem.Speaker -> 0.dp
                        is TimelineItem.Paragraph -> if (item.last) 10.dp else 0.dp
                        else -> 10.dp
                    }
                    val textBlock = when (item) {
                        is TimelineItem.Message -> item.block.takeUnless { it.user }
                        is TimelineItem.Paragraph -> item.block
                        else -> null
                    }
                    val row: @Composable () -> Unit = {
                        Box(Modifier.fillMaxWidth().padding(bottom = spacing)) {
                            when (item) {
                                is TimelineItem.ActivityHeader -> TraceHeading(
                                    item.header,
                                    obscured = pinned.any { it.item.id == item.id },
                                    modifier = measureHeading(item.id),
                                ) { toggleTrace(item.header) }
                                is TimelineItem.ActivityStep -> TraceStep(
                                    item.step,
                                    quoteDepth = 1,
                                    details = expanded[item.id] == true,
                                    toggleDetails = { toggleStep(item) },
                                    openUiPages = openUiPages,
                                    obscureHeading = pinnedTool?.item?.id == item.id,
                                    headingModifier = measureHeading(item.id),
                                )
                                is TimelineItem.InteractiveReply -> OpenUiReply(item.block, openUi, openUiPages, displayAssets)
                                is TimelineItem.Speaker -> Text(
                                    item.block.speaker,
                                    style = MaterialTheme.typography.labelMedium,
                                    color = MaterialTheme.colorScheme.primary,
                                    modifier = Modifier.padding(bottom = 4.dp),
                                )
                                is TimelineItem.ReplyActions -> ReplyActions(
                                    item.text,
                                    item.block.id,
                                    item.block.version.isNotBlank(),
                                    onBranchReply,
                                )
                                is TimelineItem.Message -> if (item.block.user) {
                                    UserMessage(item.block, onBranch, loadThumbnail)
                                } else {
                                    Text(item.block.content, style = MaterialTheme.typography.bodyMedium)
                                }
                                is TimelineItem.Paragraph -> ReplyParagraph(item, displayAssets)
                            }
                        }
                    }
                    when {
                        textBlock == null -> DisableSelection(content = row)
                        textBlock.user || textBlock.system -> ReplySelection(null, content = row)
                        else -> row()
                    }
                }
                item(key = "latest-edge", contentType = "status") {
                    DisableSelection {
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
            }
        }
        if (pinned.isNotEmpty()) {
            Box(Modifier.matchParentSize().clipToBounds()) {
                // Draw ancestors above departing descendants as each reaches its own region boundary.
                pinned.asReversed().forEach { header ->
                    key(header.item.id) {
                        Surface(
                            modifier = Modifier.offset { IntOffset(0, header.offset) }
                                .fillMaxWidth()
                                .scrollable(list, Orientation.Vertical, reverseDirection = true),
                            color = MaterialTheme.colorScheme.surface,
                        ) {
                            Box(Modifier.padding(horizontal = 16.dp)) {
                                when (val item = header.item) {
                                    is TimelineItem.ActivityHeader -> TraceHeading(item.header, pinned = true) {
                                        toggleTrace(item.header, pinnedHeader = true)
                                    }
                                    is TimelineItem.ActivityStep -> TraceStepHeading(
                                        item.step,
                                        expanded = true,
                                        modifier = Modifier.testTag("pinned:${item.id}")
                                            .traceQuote(1),
                                        toggle = { toggleStep(item) },
                                    )
                                    else -> Unit
                                }
                            }
                        }
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
private fun ReplyParagraph(item: TimelineItem.Paragraph, displayAssets: DisplayAssets?) {
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
            typography = chatMarkdownTypography(),
            animations = stillMarkdown,
            annotator = chatMathAnnotator,
            inlineContent = chatMathInlineContent(item.document.content, item.node),
            components = replyMarkdownComponents,
            success = { state, components, _ -> MarkdownElement(item.node, components, state.content) },
        )
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
    val stream = rememberStreamingMarkdownState(flavour = ChatMathFlavour)
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
    val context = LocalContext.current
    val store = remember(context) { AttachmentStore(context) }
    var preview by remember(block.id) { mutableStateOf<AttachmentFile?>(null) }
    preview?.let { AttachmentPreviewDialog(it) { preview = null } }
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
                    block.attachments.forEach { attachment ->
                        key(attachment.id) {
                            val file = remember(store, attachment.id, attachment.name, attachment.mime) {
                                store.saved(attachment.id, attachment.name, attachment.mime)
                            }
                            val thumbnail = rememberAttachmentThumbnail(attachment.thumbnail, loadThumbnail)
                            AttachmentChip(attachment.name, file, { preview = file }, preview = thumbnail,
                                modifier = Modifier.padding(bottom = 5.dp))
                        }
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
