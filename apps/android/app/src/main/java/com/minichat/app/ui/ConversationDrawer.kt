package com.minichat.app.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.paging.LoadState
import androidx.paging.compose.LazyPagingItems
import androidx.paging.compose.collectAsLazyPagingItems
import androidx.paging.compose.itemKey
import com.minichat.app.R

@Composable
internal fun ConversationDrawer(
    state: ChatState,
    model: ChatViewModel,
    open: Boolean,
    onSettings: () -> Unit,
    onSelect: (String, String) -> Unit,
) {
    val preset by model.libraryPreset.collectAsStateWithLifecycle()
    var presetName by rememberSaveable { mutableStateOf("") }
    var picking by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(open) {
        if (!open) picking = false
    }
    ModalDrawerSheet(Modifier.widthIn(max = 360.dp)) {
        Row(
            Modifier.fillMaxWidth().padding(start = 12.dp, end = 8.dp),
            verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
        ) {
            TextButton(onClick = { picking = !picking }, modifier = Modifier.weight(1f)) {
                Text(
                    if (preset == state.preset)
                        state.presetName.ifBlank { presetName.ifBlank { preset } }
                    else presetName.ifBlank { preset },
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Symbol(if (picking) "chevron_right" else "expand_more")
            }
            TextButton(onClick = onSettings) { Text(stringResource(R.string.settings)) }
        }
        if (picking) {
            val presets = model.presets.collectAsLazyPagingItems()
            LazyColumn(Modifier.weight(1f), contentPadding = PaddingValues(horizontal = 12.dp)) {
                pagedChoices(presets, preset) { item ->
                    model.libraryPreset.value = item.id
                    presetName = item.name
                    picking = false
                }
            }
        } else {
            TextButton(
                onClick = { onSelect(preset, "") },
                enabled = state.ready && !state.busy && !state.preparing,
                modifier = Modifier.padding(horizontal = 12.dp),
            ) {
                Symbol("add")
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.new_chat))
            }
            HorizontalDivider(Modifier.padding(horizontal = 16.dp))
            key(preset) {
                val chats = remember(preset) { model.chatsFor(preset) }.collectAsLazyPagingItems()
                LazyColumn(
                    Modifier.weight(1f),
                    contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
                ) {
                    pagedChoices(
                        chats,
                        state.chatId.takeIf { preset == state.preset }.orEmpty(),
                        enabled = !state.busy && !state.preparing,
                    ) { item ->
                        onSelect(preset, item.id)
                    }
                }
            }
        }
    }
}

private fun LazyListScope.pagedChoices(
    rows: LazyPagingItems<Choice>,
    selected: String,
    enabled: Boolean = true,
    onClick: (Choice) -> Unit,
) {
    items(rows.itemCount, key = rows.itemKey { it.id }) { index ->
        rows[index]?.let { row ->
            NavigationDrawerItem(
                label = {
                    Text(
                        row.name.ifBlank { stringResource(R.string.new_chat) },
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                },
                selected = row.id == selected,
                onClick = { if (enabled) onClick(row) },
            )
        }
    }
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
