package com.minichat.app.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.minichat.app.R
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun Minichat(model: ChatViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    MaterialExpressiveTheme(
        colorScheme = if (isSystemInDarkTheme()) darkColorScheme() else expressiveLightColorScheme()
    ) {
        var page by rememberSaveable { mutableStateOf("chat") }
        var panel by rememberSaveable { mutableStateOf("") }
        var editTitle by rememberSaveable { mutableStateOf(false) }
        var modelPicker by rememberSaveable { mutableStateOf<AgentRole?>(null) }
        val screens = rememberSaveableStateHolder()
        val drawer = rememberDrawerState(DrawerValue.Closed)
        val scope = rememberCoroutineScope()
        val snackbars = remember { SnackbarHostState() }
        fun back() {
            page = if (page == "settings") "chat" else "settings"
        }
        BackHandler(enabled = page != "chat") { back() }
        LaunchedEffect(state.error, panel) {
            state.error?.let {
                snackbars.showSnackbar(it)
                model.clearError()
            }
        }
        ModalNavigationDrawer(
            drawerState = drawer,
            gesturesEnabled = page == "chat",
            drawerContent = {
                ConversationDrawer(
                    state,
                    model,
                    drawer.currentValue == DrawerValue.Open,
                    onSettings = {
                        page = "settings"
                        scope.launch { drawer.close() }
                    },
                    onSelect = { preset, id ->
                        model.chooseChat(preset, id)
                        scope.launch { drawer.close() }
                    },
                )
            },
        ) {
            Scaffold(
                topBar = {
                    TopAppBar(
                        title = {
                            Text(
                                when (page) {
                                    "settings" -> stringResource(R.string.settings)
                                    "providers" -> stringResource(R.string.providers)
                                    "models" -> stringResource(R.string.models)
                                    else ->
                                        state.chatName.ifBlank { stringResource(R.string.new_chat) }
                                },
                                modifier =
                                    Modifier.clickable(
                                        enabled =
                                            page == "chat" &&
                                                state.chatId.isNotEmpty() &&
                                                !state.busy,
                                        onClickLabel = stringResource(R.string.rename),
                                        onClick = { editTitle = true },
                                    ),
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                style = MaterialTheme.typography.titleLarge,
                            )
                        },
                        navigationIcon = {
                            IconButton(
                                onClick = {
                                    if (page == "chat") scope.launch { drawer.open() } else back()
                                }
                            ) {
                                Symbol(
                                    if (page == "chat") "menu" else "arrow_back",
                                    stringResource(
                                        if (page == "chat") R.string.open_menu else R.string.back
                                    ),
                                )
                            }
                        },
                        actions = {
                            if (page == "chat") ConversationActions(state, open = { panel = it })
                        },
                    )
                },
                snackbarHost = { if (panel.isBlank()) SnackbarHost(snackbars) },
            ) { padding ->
                val body =
                    Modifier.fillMaxSize()
                        .padding(padding)
                        .consumeWindowInsets(padding)
                        .imePadding()
                when (page) {
                    "settings" -> SettingsScreen(state, model, body, onPage = { page = it })
                    "providers" -> ProvidersScreen(state, model, body)
                    "models" ->
                        ModelsScreen(
                            state,
                            model,
                            body,
                            onAssign = { modelPicker = it },
                            onProviders = { page = "providers" },
                        )
                    else ->
                        screens.SaveableStateProvider("chat:${state.chatId}") {
                            AgentConversation(
                                state,
                                AgentRole.Chat,
                                body,
                                model::cancel,
                                { text, files -> model.send(text, files, AgentRole.Chat) },
                                { modelPicker = it },
                                model::branch,
                            )
                        }
                }
            }
        }
        if (editTitle) {
            TitleDialog(
                state,
                onDismiss = { editTitle = false },
                onSave = {
                    model.rename(it)
                    editTitle = false
                },
                onGenerate = {
                    model.generateTitle()
                    editTitle = false
                },
            )
        }
        if (panel == "memory") {
            FullSheet(stringResource(R.string.memory), { panel = "" }) {
                MemoryScreen(state.chatId, model, Modifier.weight(1f).fillMaxWidth())
            }
        } else if (panel.isNotEmpty()) {
            val role = if (panel == "env") AgentRole.World else AgentRole.Preset
            FullSheet(stringResource(role.label), { panel = "" }) {
                var stage by rememberSaveable { mutableStateOf(false) }
                if (role == AgentRole.World)
                    FilterChip(
                        stage,
                        { stage = !stage },
                        label = { Text(stringResource(R.string.advance_story)) },
                        modifier = Modifier.padding(horizontal = 16.dp),
                    )
                if (role == AgentRole.Preset)
                    ListItem(
                        content = { Text(stringResource(R.string.agentic_search)) },
                        supportingContent = { Text(stringResource(R.string.agentic_search_hint)) },
                        trailingContent = {
                            Switch(
                                state.agenticSearch,
                                model::searchMode,
                                enabled = !state.busy && !state.preparing,
                            )
                        },
                    )
                val session = if (role == AgentRole.Preset) state.preset else state.chatId
                screens.SaveableStateProvider("${role.id}:$session") {
                    AgentConversation(
                        state,
                        role,
                        Modifier.weight(1f).fillMaxWidth(),
                        model::cancel,
                        { text, files ->
                            if (text.trim() == "/close" && files.isEmpty()) {
                                panel = ""
                                true
                            } else model.send(text, files, role, stage)
                        },
                        { modelPicker = it },
                    )
                }
                SnackbarHost(snackbars)
            }
        }
        modelPicker?.let { role ->
            AssignmentSheet(
                role,
                state,
                onDismiss = { modelPicker = null },
                onChoose = {
                    model.assign(role, it)
                    modelPicker = null
                },
                onManage = {
                    modelPicker = null
                    panel = ""
                    page = if (state.providers.isEmpty()) "providers" else "models"
                },
            )
        }
    }
}

@Composable
private fun ConversationActions(state: ChatState, open: (String) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    IconButton(onClick = { menu = true }) {
        Symbol("more_horiz", stringResource(R.string.more_options))
    }
    DropdownMenu(menu, { menu = false }) {
        listOf("env" to R.string.world, "preset" to R.string.preset, "memory" to R.string.memory)
            .forEach { (panel, label) ->
                DropdownMenuItem(
                    text = { Text(stringResource(label)) },
                    enabled = state.chatId.isNotEmpty(),
                    onClick = {
                        menu = false
                        open(panel)
                    },
                )
            }
    }
}

@Composable
private fun TitleDialog(
    state: ChatState,
    onDismiss: () -> Unit,
    onSave: (String) -> Unit,
    onGenerate: () -> Unit,
) {
    var title by rememberSaveable(state.chatId) { mutableStateOf(state.chatName) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.chat_name)) },
        text = {
            Column {
                OutlinedTextField(
                    title,
                    { title = it.take(140) },
                    Modifier.fillMaxWidth(),
                    singleLine = true,
                    label = { Text(stringResource(R.string.chat_name)) },
                )
                TextButton(
                    onClick = onGenerate,
                    enabled =
                        state.assigned(AgentRole.Title) != null &&
                            state.blocks.any { it.user && it.role == AgentRole.Chat },
                ) {
                    Text(stringResource(R.string.generate_title))
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) }
        },
        confirmButton = {
            Button(
                onClick = { onSave(title) },
                enabled = title.isNotBlank() && title.trim() != state.chatName,
            ) {
                Text(stringResource(R.string.save))
            }
        },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun FullSheet(
    title: String,
    onDismiss: () -> Unit,
    content: @Composable ColumnScope.() -> Unit,
) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState =
            rememberBottomSheetState(
                initialValue = SheetValue.Hidden,
                enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded),
            ),
    ) {
        Column(Modifier.fillMaxSize().imePadding()) {
            Row(
                Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp),
                verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
            ) {
                Text(title, Modifier.weight(1f), style = MaterialTheme.typography.headlineSmall)
                IconButton(onClick = onDismiss) { Symbol("close", stringResource(R.string.close)) }
            }
            content()
        }
    }
}
