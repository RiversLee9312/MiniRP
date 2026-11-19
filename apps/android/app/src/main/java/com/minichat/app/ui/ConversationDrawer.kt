package com.minichat.app.ui

import androidx.compose.animation.AnimatedContent
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
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.paging.LoadState
import androidx.paging.compose.LazyPagingItems
import androidx.paging.compose.collectAsLazyPagingItems
import androidx.paging.compose.itemContentType
import androidx.paging.compose.itemKey
import com.minichat.app.R

@Composable
internal fun ConversationDrawer(
    state: ChatState,
    model: ChatViewModel,
    drawer: DrawerState,
    open: Boolean,
    onSelect: (String, String) -> Unit,
    onCreatePreset: () -> Unit,
) {
    val library by model.chatLibrary.collectAsStateWithLifecycle()
    val preset = library.preset
    val presets = model.presets.collectAsLazyPagingItems()
    val chats = library.pages.collectAsLazyPagingItems()
    val presetScroll = rememberLazyListState()
    val chatScroll = key(preset) { rememberLazyListState() }
    var presetName by rememberSaveable { mutableStateOf("") }
    var picking by rememberSaveable { mutableStateOf(false) }
    var deletion by remember { mutableStateOf<Pair<Choice, Boolean>?>(null) }
    LaunchedEffect(open) {
        if (!open) picking = false
    }
    ModalDrawerSheet(drawerState = drawer, modifier = Modifier.widthIn(max = 360.dp)) {
        Row(
            Modifier.fillMaxWidth().padding(start = 12.dp, end = 8.dp),
            verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
        ) {
            TextButton(onClick = { picking = !picking }, modifier = Modifier.weight(1f)) {
                Text(
                    if (preset.isBlank()) stringResource(R.string.preset)
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
        val enabled = state.ready && !state.busy && !state.preparing
        DrawerLibrary(
            picking = picking,
            enabled = enabled,
            onNewChat = { onSelect(preset, "") },
            onCreatePreset = onCreatePreset,
            modifier = Modifier.weight(1f),
            hasPreset = preset.isNotBlank(),
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
                    onDelete = { deletion = it to showPresets },
                ) { item ->
                    if (showPresets) {
                        model.selectPreset(item.id)
                        presetName = item.name
                        picking = false
                    } else {
                        onSelect(preset, item.id)
                    }
                }
            }
        }
    }
    deletion?.let { (item, isPreset) ->
        DeleteLibraryChoice(item, isPreset, onDismiss = { deletion = null }) {
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
    content: @Composable (Boolean, PaddingValues) -> Unit,
) {
    val motion = MaterialTheme.motionScheme
    val createPreset = picking || !hasPreset
    val actionLabel =
        stringResource(if (createPreset) R.string.create_preset else R.string.new_chat)
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
            content(
                showPresets,
                PaddingValues(start = 12.dp, top = 8.dp, end = 12.dp, bottom = 88.dp),
            )
        }
        ExtendedFloatingActionButton(
            onClick = { if (enabled) (if (createPreset) onCreatePreset else onNewChat)() },
            modifier =
                Modifier.align(Alignment.BottomStart)
                    .padding(16.dp)
                    .alpha(if (enabled) 1f else 0.56f)
                    .semantics {
                        contentDescription = actionLabel
                        if (!enabled) disabled()
                    },
            icon = { Symbol("add") },
            text = {
                AnimatedContent(
                    targetState = createPreset,
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
                ) { createPreset ->
                    Text(
                        stringResource(
                            if (createPreset) R.string.create_preset else R.string.new_chat
                        )
                    )
                }
            },
        )
    }
}

private fun LazyListScope.pagedChoices(
    rows: LazyPagingItems<Choice>,
    selected: String,
    enabled: Boolean = true,
    onDelete: (Choice) -> Unit,
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
            LibraryChoiceRow(row, row.id == selected, enabled, { onClick(row) }, { onDelete(row) })
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
) {
    val label = choice.name.ifBlank { stringResource(R.string.new_chat) }
    var menu by remember { mutableStateOf(false) }
    val delete = stringResource(R.string.delete)
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
                    .combinedClickable(
                        enabled = enabled,
                        role = Role.Button,
                        onLongClickLabel = delete,
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
    onConfirm: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                stringResource(
                    R.string.delete_named,
                    choice.name.ifBlank { stringResource(R.string.new_chat) },
                )
            )
        },
        text = if (isPreset) ({ Text(stringResource(R.string.delete_preset_detail)) }) else null,
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) }
        },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                Text(stringResource(if (isPreset) R.string.delete_preset else R.string.delete_chat))
            }
        },
    )
}
