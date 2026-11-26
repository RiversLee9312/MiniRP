package com.minirp.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import com.minirp.app.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

internal data class ArchivedHistoryPage(val blocks: List<TraceBlock>, val before: String?)

@Composable
internal fun ArchivedChatPicker(
    load: suspend () -> List<EnsembleChat>,
    close: () -> Unit,
    open: (EnsembleChat) -> Unit,
    scroll: LazyListState,
) {
    var attempt by remember { mutableIntStateOf(0) }
    val latestLoad by rememberUpdatedState(load)
    val result by produceState<Result<List<EnsembleChat>>?>(null, attempt) {
        value = null
        value = loadArchive { latestLoad() }
    }
    FullSheet(stringResource(R.string.ensemble_archived_chats), close) {
        val chats = result?.getOrNull().orEmpty()
        if (chats.isEmpty()) {
            ArchiveStatus(result, R.string.ensemble_no_archived_chats) { attempt++ }
        } else {
            LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = scroll) {
                items(chats, key = { it.id }, contentType = { "archived_chat" }) { chat ->
                    ListItem(
                        content = { Text(chat.name, maxLines = 2, overflow = TextOverflow.Ellipsis) },
                        leadingContent = { Symbol("archive") },
                        trailingContent = { Symbol("chevron_right") },
                        modifier = Modifier.clickable(role = Role.Button) { open(chat) },
                    )
                }
            }
        }
    }
}

@Composable
internal fun ArchivedChatHistory(
    load: suspend (String?) -> ArchivedHistoryPage,
    loadThumbnail: suspend (AttachmentThumbnail) -> ByteArray,
    displayAssets: DisplayAssets,
) {
    var attempt by remember { mutableIntStateOf(0) }
    val latestLoad by rememberUpdatedState(load)
    val first by produceState<Result<ArchivedHistoryPage>?>(null, attempt) {
        value = null
        value = loadArchive { latestLoad(null) }
    }
    val initial = first?.getOrNull()
    if (initial == null || initial.blocks.isEmpty()) {
        ArchiveStatus(first, R.string.ensemble_archive_empty_history) { attempt++ }
        return
    }
    var page by remember(initial) { mutableStateOf(initial) }
    var pending by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxSize()) {
        page.before?.let { before ->
            TextButton(
                modifier = Modifier.align(Alignment.CenterHorizontally),
                enabled = !pending,
                onClick = {
                    pending = true
                    scope.launch {
                        try {
                            loadArchive { latestLoad(before) }.onSuccess { earlier ->
                                page = earlier.copy(blocks = earlier.blocks + page.blocks)
                                failed = false
                            }.onFailure { failed = true }
                        } finally { pending = false }
                    }
                },
            ) {
                Text(stringResource(if (failed) R.string.retry else R.string.load_more))
            }
        }
        TraceLog(page.blocks, expandThoughts = false, modifier = Modifier.weight(1f).fillMaxWidth(),
            loadThumbnail = loadThumbnail, displayAssets = displayAssets)
    }
}

private suspend fun <T> loadArchive(load: suspend () -> T): Result<T> =
    try { Result.success(load()) }
    catch (cancelled: CancellationException) { throw cancelled }
    catch (error: Exception) { Result.failure(error) }

@Composable
private fun ArchiveStatus(result: Result<*>?, empty: Int, retry: () -> Unit) {
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally) {
        when {
            result == null -> CircularProgressIndicator()
            result.isFailure -> {
                Text(stringResource(R.string.connection_failed))
                TextButton(onClick = retry) { Text(stringResource(R.string.retry)) }
            }
            else -> Text(stringResource(empty))
        }
    }
}
