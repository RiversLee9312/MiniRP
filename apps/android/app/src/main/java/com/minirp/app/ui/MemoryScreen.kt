package com.minirp.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.minirp.app.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import org.json.JSONObject

private data class Note(val key: String, val body: String = "", val revision: Int = 0)

private sealed interface MemoryRoute {
    @Serializable data object Notes : MemoryRoute

    @Serializable data object Editor : MemoryRoute
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun MemoryScreen(
    chatId: String,
    model: ChatViewModel,
    modifier: Modifier,
    participants: List<Choice> = emptyList(),
    agenticSearch: Boolean = true,
) {
    val navigation = rememberNavController()
    var role by rememberSaveable(chatId) { mutableStateOf(AgentRole.Chat) }
    var participant by rememberSaveable(chatId) { mutableStateOf("") }
    val selected = participants.firstOrNull { it.id == participant } ?: participants.firstOrNull()
    val actor = selected?.takeIf { role == AgentRole.Chat || role == AgentRole.Search }
    var query by rememberSaveable(chatId) { mutableStateOf("") }
    var entries by remember { mutableStateOf(emptyList<Note>()) }
    var note by remember { mutableStateOf<Note?>(null) }
    var original by remember { mutableStateOf<Note?>(null) }
    var nextKey by remember { mutableStateOf("") }
    var nextOffset by remember { mutableIntStateOf(0) }
    var loading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var revision by remember { mutableIntStateOf(0) }
    var confirmDelete by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    suspend fun memory(tool: String, args: JSONObject) =
        model.memory(role, tool, args, actor?.id.orEmpty())

    fun edit(draft: Note) {
        note = draft
        original = draft.takeIf { it.revision > 0 }
        navigation.navigate(MemoryRoute.Editor) { launchSingleTop = true }
    }

    fun closeEditor() {
        navigation.popBackStack<MemoryRoute.Editor>(inclusive = true)
    }

    suspend fun load(append: Boolean) {
        val search = query.isNotBlank()
        val result =
            memory(
                if (search) "db_search" else "db_list",
                JSONObject()
                    .put("notes_only", true)
                    .put("query", query.trim())
                    .put("limit", 30)
                    .put("after", if (append) nextKey else "")
                    .put("offset", if (append) nextOffset else 0),
            )
        val page =
            result.getJSONArray(if (search) "matches" else "entries").objects().map {
                Note(it.getString("key"), it.optString("snippet"), it.optInt("revision"))
            }
        entries = if (append) (entries + page).distinctBy { it.key } else page
        nextKey = if (result.isNull("next_after")) "" else result.optString("next_after")
        nextOffset = result.optInt("next_offset")
    }

    suspend fun work(action: suspend () -> Unit) {
        loading = true
        error = null
        try {
            action()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            error = failure.message
        } finally {
            loading = false
        }
    }

    fun open(key: String) = scope.launch {
        work {
            var offset = 0
            var noteRevision = 0
            val body = buildString {
                do {
                    val result =
                        memory(
                            "db_read",
                            JSONObject().put("key", key).put("start", offset).put("length", 64000),
                        )
                    val currentRevision = result.getInt("revision")
                    check(offset == 0 || currentRevision == noteRevision) {
                        "The note changed while loading."
                    }
                    noteRevision = currentRevision
                    append(result.getString("content"))
                    offset = result.optInt("next_offset")
                } while (offset > 0)
            }
            edit(Note(key, body, noteRevision))
        }
    }

    LaunchedEffect(chatId, role, actor?.id, query, revision) {
        entries = emptyList()
        nextKey = ""
        nextOffset = 0
        work {
            if (query.isNotBlank()) delay(200)
            load(false)
        }
    }
    Column(modifier) {
        if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        error?.let { message ->
            Text(message, Modifier.padding(16.dp), color = MaterialTheme.colorScheme.error)
        }
        NavHost(navigation, startDestination = MemoryRoute.Notes, modifier = Modifier.weight(1f)) {
            composable<MemoryRoute.Editor> {
                val current = note
                if (current == null) {
                    LaunchedEffect(navigation) { closeEditor() }
                    return@composable
                }
                Surface(
                    color = BottomSheetDefaults.ContainerColor,
                    modifier = Modifier.fillMaxSize(),
                ) {
                    Column {
                        val existing = current.revision > 0
                        val dirty = current != original
                        Row(
                            Modifier.fillMaxWidth().padding(horizontal = 8.dp),
                            verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
                        ) {
                            IconButton(
                                onClick = ::closeEditor,
                                enabled = !loading,
                            ) {
                                Symbol("arrow_back", stringResource(R.string.back))
                            }
                            Spacer(Modifier.weight(1f))
                            if (existing)
                                TextButton(onClick = { confirmDelete = true }, enabled = !loading) {
                                    Text(stringResource(R.string.delete))
                                }
                            Button(
                                onClick = {
                                    scope.launch {
                                        work {
                                            memory(
                                                "db_write",
                                                JSONObject()
                                                    .put("key", current.key.trim())
                                                    .put("content", current.body)
                                                    .put("expected_revision", current.revision),
                                            )
                                            closeEditor()
                                            revision++
                                        }
                                    }
                                },
                                enabled =
                                    !loading &&
                                        dirty &&
                                        current.key.isNotBlank() &&
                                        !current.key.startsWith('_'),
                            ) {
                                Text(stringResource(R.string.save))
                            }
                        }
                        Column(
                            Modifier.weight(1f)
                                .verticalScroll(rememberScrollState())
                                .padding(16.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            if (existing)
                                Text(current.key, style = MaterialTheme.typography.titleLarge)
                            else
                                OutlinedTextField(
                                    current.key,
                                    { note = current.copy(key = it) },
                                    Modifier.fillMaxWidth(),
                                    singleLine = true,
                                    label = { Text(stringResource(R.string.entry_key)) },
                                )
                            TextField(
                                current.body,
                                { note = current.copy(body = it) },
                                Modifier.fillMaxWidth(),
                                minLines = 12,
                                placeholder = { Text(stringResource(R.string.entry_content)) },
                            )
                        }
                    }
                }
            }
            composable<MemoryRoute.Notes> {
                Surface(
                    color = BottomSheetDefaults.ContainerColor,
                    modifier = Modifier.fillMaxSize(),
                ) {
                    Column {
                        Row(
                            Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                        ) {
                            var roles by remember { mutableStateOf(false) }
                            Box {
                                TextButton(
                                    onClick = { roles = true },
                                    enabled = !loading || entries.isEmpty(),
                                ) {
                                    Text(stringResource(role.label))
                                    Symbol("expand_more", size = 18)
                                }
                                DropdownMenu(roles, { roles = false }) {
                                    AgentRole.entries
                                        .filter {
                                            it != AgentRole.Fetch &&
                                                it != AgentRole.Title &&
                                                (agenticSearch || it != AgentRole.Search)
                                        }
                                        .forEach { candidate ->
                                            DropdownMenuItem(
                                                text = { Text(stringResource(candidate.label)) },
                                                onClick = {
                                                    role = candidate
                                                    roles = false
                                                },
                                            )
                                        }
                                }
                            }
                            TextButton(
                                onClick = { edit(Note("")) },
                                enabled = !loading,
                            ) {
                                Symbol("add")
                                Text(stringResource(R.string.new_note))
                            }
                        }
                        if (actor != null) {
                            var choosing by remember { mutableStateOf(false) }
                            Box(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
                                TextButton(onClick = { choosing = true }) {
                                    Text(actor.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    Symbol("expand_more", size = 18)
                                }
                                DropdownMenu(choosing, { choosing = false }) {
                                    participants.forEach { choice ->
                                        DropdownMenuItem(
                                            text = {
                                                Text(
                                                    choice.name,
                                                    maxLines = 1,
                                                    overflow = TextOverflow.Ellipsis,
                                                )
                                            },
                                            onClick = {
                                                participant = choice.id
                                                choosing = false
                                            },
                                        )
                                    }
                                }
                            }
                        }
                        OutlinedTextField(
                            query,
                            { query = it },
                            Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                            singleLine = true,
                            label = { Text(stringResource(R.string.search_entries)) },
                        )
                        LazyColumn(
                            Modifier.weight(1f),
                            contentPadding = PaddingValues(vertical = 8.dp),
                        ) {
                            if (entries.isEmpty() && !loading && error == null)
                                item {
                                    Text(
                                        stringResource(R.string.database_empty),
                                        Modifier.padding(24.dp),
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                            items(entries, key = { it.key }) { entry ->
                                ListItem(
                                    content = {
                                        Text(
                                            entry.key,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                    },
                                    supportingContent = {
                                        Text(
                                            entry.body,
                                            maxLines = 2,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                    },
                                    modifier =
                                        Modifier.clickable(enabled = !loading) { open(entry.key) },
                                )
                            }
                            if (nextKey.isNotEmpty() || nextOffset > 0)
                                item {
                                    TextButton(
                                        onClick = { scope.launch { work { load(true) } } },
                                        enabled = !loading,
                                    ) {
                                        Text(stringResource(R.string.load_more))
                                    }
                                }
                        }
                    }
                }
            }
        }
    }
    if (confirmDelete)
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            text = { Text(stringResource(R.string.delete_note_question)) },
            dismissButton = {
                TextButton(onClick = { confirmDelete = false }) {
                    Text(stringResource(R.string.cancel))
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        confirmDelete = false
                        val current = note ?: return@TextButton
                        scope.launch {
                            work {
                                memory(
                                    "db_delete",
                                    JSONObject()
                                        .put("key", current.key)
                                        .put("expected_revision", current.revision),
                                )
                                closeEditor()
                                revision++
                            }
                        }
                    }
                ) {
                    Text(stringResource(R.string.delete))
                }
            },
        )
}
