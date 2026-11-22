package com.minirp.app.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.hideFromAccessibility
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.paging.LoadState
import androidx.paging.compose.LazyPagingItems
import androidx.paging.compose.collectAsLazyPagingItems
import androidx.paging.compose.itemContentType
import androidx.paging.compose.itemKey
import com.minirp.app.R

private val DrawerFabHeight = 56.dp
private val DrawerFabInset = 16.dp
private val DrawerFabGap = 8.dp

@Composable
internal fun ConversationDrawer(
    state: ChatState,
    model: ChatViewModel,
    drawer: DrawerState,
    open: Boolean,
    onSelect: (String, String) -> Unit,
    onSelectPreset: (String) -> Unit,
    onCreateConversation: (String, Boolean) -> Unit,
    onCreatePreset: () -> Unit,
    onArchiveExport: (ArchiveType, String, String, String) -> Unit,
    onArchiveImport: (ArchiveType) -> Unit,
    presetsFirst: Boolean = false,
) {
    val library by model.chatLibrary.collectAsStateWithLifecycle()
    val preset = library.preset
    val presets = model.presets.collectAsLazyPagingItems()
    val chats = library.pages.collectAsLazyPagingItems()
    val presetScroll = rememberLazyListState()
    val chatScroll = key(preset) { rememberLazyListState() }
    var presetName by rememberSaveable { mutableStateOf("") }
    var selectedEnsemble by rememberSaveable { mutableStateOf(false) }
    val ensemble = if (preset == state.preset) state.chatMode == "ensemble" else selectedEnsemble
    var picking by rememberSaveable { mutableStateOf(false) }
    var deletion by remember { mutableStateOf<Pair<Choice, Boolean>?>(null) }
    val workLocked = presetsFirst && state.presetWorking
    LaunchedEffect(open) {
        picking = open && presetsFirst
    }
    LaunchedEffect(workLocked) {
        if (workLocked) deletion = null
    }
    ModalDrawerSheet(drawerState = drawer, modifier = Modifier.widthIn(max = 360.dp)) {
        Row(
            Modifier.fillMaxWidth().padding(start = 12.dp, end = 8.dp),
            verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
        ) {
            TextButton(onClick = { picking = !picking }, modifier = Modifier.weight(1f).testTag("library-presets")) {
                Text(
                    if (picking || preset.isBlank()) stringResource(R.string.choose_preset)
                    else if (preset == state.preset)
                        state.presetName.ifBlank { presetName.ifBlank { preset } }
                    else presetName.ifBlank { preset },
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Symbol(if (picking) "expand_less" else "expand_more")
            }
        }
        HorizontalDivider(Modifier.padding(horizontal = 16.dp))
        val enabled = state.ready && !state.busy && !state.preparing && !state.archiveBusy && !workLocked
        DrawerLibrary(
            picking = picking,
            enabled = enabled,
            onNewChat = { onCreateConversation(preset, ensemble) },
            onCreatePreset = onCreatePreset,
            modifier = Modifier.weight(1f),
            hasPreset = preset.isNotBlank(),
            newConversationLabel = if (ensemble) R.string.new_room else R.string.new_chat,
            onArchiveImport = onArchiveImport,
            drawerOpen = open,
        ) { showPresets, padding ->
            LazyColumn(
                Modifier.fillMaxSize(),
                state = if (showPresets) presetScroll else chatScroll,
                contentPadding = padding,
            ) {
                pagedChoices(
                    if (showPresets) presets else chats,
                    if (showPresets) preset
                    else state.chatId.takeIf { preset == state.preset }.orEmpty(),
                    enabled = enabled && showPresets == picking,
                    fallback =
                        if (!showPresets && ensemble) R.string.new_room else R.string.new_chat,
                    onDelete = { deletion = it to showPresets },
                    actions = { item ->
                        if (showPresets) listOf(
                            R.string.export_preset to { onArchiveExport(ArchiveType.Preset, item.name, item.id, "") },
                            R.string.export_save to { onArchiveExport(ArchiveType.Save, item.name, item.id, "") },
                        )
                        else listOf(
                            (if (ensemble) R.string.export_archive else R.string.export_chat) to
                                { onArchiveExport(ArchiveType.Room, item.name, preset, item.id) },
                        )
                    },
                ) { item ->
                    if (showPresets) {
                        onSelectPreset(item.id)
                        presetName = item.name
                        selectedEnsemble = item.ensemble
                        picking = false
                    } else {
                        onSelect(preset, item.id)
                    }
                }
            }
        }
    }
    deletion?.let { (item, isPreset) ->
        DeleteLibraryChoice(
            item,
            isPreset,
            onDismiss = { deletion = null },
            isRoom = !isPreset && ensemble,
        ) {
            if (!presetsFirst || !model.state.value.presetWorking)
                model.deleteChoice(if (isPreset) item.id else preset, item.id, isPreset)
            if (isPreset) {
                presetName = ""
                picking = false
            }
            deletion = null
        }
    }
}

@Composable
internal fun DrawerLibrary(
    picking: Boolean,
    enabled: Boolean,
    onNewChat: () -> Unit,
    onCreatePreset: () -> Unit,
    modifier: Modifier = Modifier,
    hasPreset: Boolean = true,
    newConversationLabel: Int = R.string.new_chat,
    onArchiveImport: ((ArchiveType) -> Unit)? = null,
    drawerOpen: Boolean = true,
    content: @Composable (Boolean, PaddingValues) -> Unit,
) {
    val motion = MaterialTheme.motionScheme
    val createPreset = picking || !hasPreset
    val showImport = createPreset && onArchiveImport != null
    val actionLabelId = if (createPreset) R.string.create_preset else newConversationLabel
    val actionLabel = stringResource(actionLabelId)
    val importSlideDistance = with(LocalDensity.current) { DrawerFabGap.roundToPx() }
    Box(modifier.fillMaxWidth()) {
        AnimatedContent(
            targetState = picking,
            modifier = Modifier.fillMaxSize(),
            transitionSpec = {
                val direction = if (targetState) 1 else -1
                (fadeIn(motion.defaultEffectsSpec()) +
                        slideInVertically(motion.defaultSpatialSpec()) { -direction * it / 10 })
                    .togetherWith(
                        fadeOut(motion.fastEffectsSpec()) +
                            slideOutVertically(motion.defaultSpatialSpec()) { direction * it / 10 }
                    )
            },
            label = "Drawer list",
        ) { showPresets ->
            val importPadding = if ((showPresets || !hasPreset) && onArchiveImport != null)
                DrawerFabGap + DrawerFabHeight else 0.dp
            content(
                showPresets,
                PaddingValues(
                    start = 12.dp, top = 8.dp, end = 12.dp,
                    bottom = DrawerFabInset * 2 + DrawerFabHeight + importPadding,
                ),
            )
        }
        ExtendedFloatingActionButton(
            onClick = { if (enabled) (if (createPreset) onCreatePreset else onNewChat)() },
            modifier =
                Modifier.align(Alignment.BottomEnd)
                    .padding(DrawerFabInset)
                    .height(DrawerFabHeight)
                    .alpha(if (enabled) 1f else 0.56f)
                    .semantics {
                        contentDescription = actionLabel
                        if (!enabled) disabled()
                    },
            icon = { Symbol("add") },
            text = {
                AnimatedContent(
                    targetState = actionLabelId,
                    modifier = Modifier.clearAndSetSemantics {},
                    transitionSpec = {
                        (fadeIn(motion.defaultEffectsSpec()) +
                                slideInVertically(motion.fastSpatialSpec()) { it / 2 })
                            .togetherWith(
                                fadeOut(motion.fastEffectsSpec()) +
                                    slideOutVertically(motion.fastSpatialSpec()) { -it / 2 }
                            )
                    },
                    label = "Drawer action",
                ) { label ->
                    Text(
                        stringResource(label),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            },
        )
        if (onArchiveImport != null) {
            AnimatedVisibility(
                visible = showImport,
                // The Material menu already supplies the edge inset around its anchor.
                modifier = Modifier.align(Alignment.BottomEnd).padding(bottom = DrawerFabHeight + DrawerFabGap),
                enter = fadeIn(motion.defaultEffectsSpec()) +
                    slideInVertically(motion.fastSpatialSpec()) { importSlideDistance },
                exit = fadeOut(motion.fastEffectsSpec()) +
                    slideOutVertically(motion.fastSpatialSpec()) { importSlideDistance },
                label = "Drawer import action",
            ) {
                ArchiveImportMenu(
                    enabled = enabled,
                    active = showImport && drawerOpen,
                    onImport = onArchiveImport,
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun ArchiveImportMenu(
    enabled: Boolean,
    active: Boolean,
    onImport: (ArchiveType) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    var help by remember { mutableStateOf(false) }
    val canOperate = enabled && active
    val menuExpanded = expanded && canOperate
    val label = stringResource(R.string.import_menu)
    val buttonLabel = if (menuExpanded) stringResource(R.string.close) else label
    LaunchedEffect(canOperate, active) {
        if (!canOperate) expanded = false
        if (!active) help = false
    }
    BackHandler(menuExpanded) { expanded = false }
    fun choose(type: ArchiveType) {
        expanded = false
        if (canOperate) onImport(type)
    }
    FloatingActionButtonMenu(
        expanded = menuExpanded,
        modifier = if (active) Modifier else Modifier.clearAndSetSemantics { hideFromAccessibility() },
        horizontalAlignment = Alignment.End,
        button = {
            ExtendedFloatingActionButton(
                expanded = !menuExpanded,
                onClick = { if (canOperate) expanded = !menuExpanded },
                modifier = Modifier.height(DrawerFabHeight)
                    .testTag("drawer-import-fab")
                    .alpha(if (enabled) 1f else 0.56f)
                    .semantics {
                        contentDescription = buttonLabel
                        if (!enabled) disabled()
                    },
                icon = { Symbol(if (menuExpanded) "close" else "download") },
                text = {
                    Text(
                        label,
                        modifier = Modifier.clearAndSetSemantics {},
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                },
            )
        },
    ) {
        FloatingActionButtonMenuItem(
            onClick = { choose(ArchiveType.Preset) },
            icon = { Symbol("extension") },
            text = { Text(stringResource(R.string.import_preset)) },
        )
        FloatingActionButtonMenuItem(
            onClick = { choose(ArchiveType.Save) },
            icon = { Symbol("download") },
            text = { Text(stringResource(R.string.import_save)) },
        )
        FloatingActionButtonMenuItem(
            onClick = {
                expanded = false
                if (canOperate) help = true
            },
            icon = { Symbol("more_horiz") },
            text = { Text(stringResource(R.string.import_help)) },
        )
    }
    if (help && active) {
        AlertDialog(
            onDismissRequest = { help = false },
            title = { Text(stringResource(R.string.import_help)) },
            text = {
                Column(
                    modifier = Modifier.verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Text(stringResource(R.string.import_preset_detail))
                    Text(stringResource(R.string.import_save_detail))
                }
            },
            confirmButton = { TextButton(onClick = { help = false }) { Text(stringResource(R.string.close)) } },
        )
    }
}

private fun LazyListScope.pagedChoices(
    rows: LazyPagingItems<Choice>,
    selected: String,
    enabled: Boolean = true,
    fallback: Int = R.string.new_chat,
    onDelete: (Choice) -> Unit,
    actions: (Choice) -> List<Pair<Int, () -> Unit>>,
    onClick: (Choice) -> Unit,
) {
    val before = rows.loadState.prepend
    if (before is LoadState.Loading)
        item(key = "prepend-progress", contentType = "progress") {
            LinearProgressIndicator(Modifier.fillMaxWidth().padding(12.dp))
        }
    if (before is LoadState.Error)
        item(key = "prepend-retry", contentType = "retry") {
            TextButton(onClick = rows::retry) { Text(stringResource(R.string.retry)) }
        }
    items(
        rows.itemCount,
        key = rows.itemKey { "choice:${it.id}" },
        contentType = rows.itemContentType { "choice" },
    ) { index ->
        rows[index]?.let { row ->
            LibraryChoiceRow(
                row,
                row.id == selected,
                enabled,
                { onClick(row) },
                { onDelete(row) },
                fallback,
                actions(row),
            )
        }
    }
    pagingStatus(rows)
}

internal fun LazyListScope.pagingStatus(rows: LazyPagingItems<Choice>) {
    when {
        rows.loadState.refresh is LoadState.Loading || rows.loadState.append is LoadState.Loading ->
            item {
                LinearProgressIndicator(Modifier.fillMaxWidth().padding(12.dp))
            }
        rows.loadState.refresh is LoadState.Error || rows.loadState.append is LoadState.Error ->
            item {
                TextButton(onClick = rows::retry) { Text(stringResource(R.string.retry)) }
            }
    }
}

@Composable
internal fun LibraryChoiceRow(
    choice: Choice,
    selected: Boolean,
    enabled: Boolean,
    onOpen: () -> Unit,
    onDelete: () -> Unit,
    fallback: Int = R.string.new_chat,
    actions: List<Pair<Int, () -> Unit>> = emptyList(),
) {
    val label = choice.name.ifBlank { stringResource(fallback) }
    var menu by remember { mutableStateOf(false) }
    val delete = stringResource(R.string.delete)
    val longClickLabel = if (actions.isEmpty()) delete else stringResource(R.string.more_options)
    Box {
        Surface(
            shape = MaterialTheme.shapes.large,
            color =
                if (selected) MaterialTheme.colorScheme.secondaryContainer
                else androidx.compose.ui.graphics.Color.Transparent,
        ) {
            Row(
                Modifier.fillMaxWidth()
                    .heightIn(min = 48.dp)
                    .testTag("library-choice-${choice.id}")
                    .combinedClickable(
                        enabled = enabled,
                        role = Role.Button,
                        onLongClickLabel = longClickLabel,
                        onClick = onOpen,
                        onLongClick = { menu = true },
                    )
                    .padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        DropdownMenu(menu, { menu = false }) {
            actions.forEach { (title, run) ->
                DropdownMenuItem(
                    text = { Text(stringResource(title)) },
                    onClick = { menu = false; run() },
                )
            }
            DropdownMenuItem(
                text = { Text(delete) },
                onClick = {
                    menu = false
                    onDelete()
                },
            )
        }
    }
}

@Composable
internal fun DeleteLibraryChoice(
    choice: Choice,
    isPreset: Boolean,
    onDismiss: () -> Unit,
    isRoom: Boolean = false,
    onConfirm: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                stringResource(
                    R.string.delete_named,
                    choice.name.ifBlank {
                        stringResource(if (isRoom) R.string.new_room else R.string.new_chat)
                    },
                )
            )
        },
        text = if (isPreset) ({ Text(stringResource(R.string.delete_preset_detail)) }) else null,
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) }
        },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                Text(
                    stringResource(
                        when {
                            isPreset -> R.string.delete_preset
                            isRoom -> R.string.delete
                            else -> R.string.delete_chat
                        }
                    )
                )
            }
        },
    )
}
