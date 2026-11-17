package com.minichat.app.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.minichat.app.R
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun Minichat(model: ChatViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    val dark = isSystemInDarkTheme()
    MaterialExpressiveTheme(
        colorScheme = if (dark) darkColorScheme() else expressiveLightColorScheme()
    ) {
        var page by rememberSaveable { mutableStateOf("chat") }
        val savedScreens = rememberSaveableStateHolder()
        BackHandler(enabled = page != "chat") { page = "chat" }
        var panel by rememberSaveable { mutableStateOf("") }
        var modelPicker by remember { mutableStateOf(false) }
        val drawer = rememberDrawerState(DrawerValue.Closed)
        val scope = rememberCoroutineScope()
        LaunchedEffect(state.ready) {
            if (state.ready && state.chatId.isBlank())
                state.presets.firstOrNull()?.let { model.choosePreset(it.id) }
        }
        val snackbars = remember { SnackbarHostState() }
        LaunchedEffect(state.error) {
            state.error?.let {
                snackbars.showSnackbar(it)
                model.clearError()
            }
        }
        ModalNavigationDrawer(
            drawerState = drawer,
            drawerContent = {
                ModalDrawerSheet {
                    Spacer(Modifier.height(20.dp))
                    Text(
                        stringResource(R.string.presets),
                        Modifier.padding(20.dp),
                        style = MaterialTheme.typography.titleLarge,
                    )
                    state.presets.forEach { preset ->
                        NavigationDrawerItem(
                            label = { Text(preset.name) },
                            selected = state.preset == preset.id,
                            onClick = {
                                model.choosePreset(preset.id)
                                scope.launch { drawer.close() }
                            },
                            modifier = Modifier.padding(horizontal = 12.dp),
                        )
                    }
                    TextButton(
                        onClick = {
                            model.choosePreset(state.preset)
                            scope.launch { drawer.close() }
                        },
                        modifier = Modifier.padding(12.dp),
                    ) {
                        Text(stringResource(R.string.conversations))
                    }
                }
            },
        ) {
            Scaffold(
                topBar = {
                    TopAppBar(
                        title = {
                            Column {
                                Text(
                                    if (page == "settings") stringResource(R.string.settings)
                                    else state.chatName.ifBlank { stringResource(R.string.chat) },
                                    maxLines = 1,
                                    style = MaterialTheme.typography.titleMedium,
                                )
                                if (page == "chat")
                                    TextButton(
                                        onClick = {
                                            if (state.provider == null) page = "settings"
                                            else {
                                                model.refreshModels()
                                                modelPicker = true
                                            }
                                        },
                                        contentPadding = PaddingValues(0.dp),
                                    ) {
                                        Text(
                                            state.provider?.model?.ifBlank {
                                                stringResource(R.string.select_model)
                                            } ?: stringResource(R.string.select_model),
                                            maxLines = 1,
                                        )
                                        Symbol("expand_more")
                                    }
                            }
                        },
                        navigationIcon = {
                            IconButton(
                                onClick = {
                                    if (page == "chat") scope.launch { drawer.open() }
                                    else page = "chat"
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
                            if (page == "chat") {
                                var menu by remember { mutableStateOf(false) }
                                IconButton(onClick = { menu = true }) {
                                    Symbol("more_horiz", stringResource(R.string.more_options))
                                }
                                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.edit_facts)) },
                                        onClick = {
                                            panel = "env"
                                            menu = false
                                        },
                                    )
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.edit_preset)) },
                                        onClick = {
                                            panel = "preset"
                                            menu = false
                                        },
                                    )
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.memory)) },
                                        onClick = {
                                            panel = "memory"
                                            model.memory("", "chat")
                                            menu = false
                                        },
                                    )
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.settings)) },
                                        onClick = {
                                            page = "settings"
                                            menu = false
                                        },
                                    )
                                }
                            }
                        },
                    )
                },
                snackbarHost = { SnackbarHost(snackbars) },
                bottomBar = {
                    if (page == "chat")
                        Column(Modifier.navigationBarsPadding().imePadding()) {
                            savedScreens.SaveableStateProvider("composer:${state.chatId}") {
                                Composer(state.busy, model::cancel) { model.send(it) }
                            }
                        }
                },
            ) { padding ->
                savedScreens.SaveableStateProvider("body:${state.chatId}:$page") {
                    if (page == "settings") SettingsScreen(state, model, Modifier.padding(padding))
                    else
                        TraceLog(
                            state.blocks.filter {
                                it.agent !in setOf("env", "preset", "EnvAgent", "PresetAgent")
                            },
                            state.expandThoughts,
                            Modifier.fillMaxSize().padding(padding),
                            streaming = state.busy,
                        )
                }
            }
        }
        if (state.chatPicker)
            ModalBottomSheet(onDismissRequest = model::dismissChatPicker) {
                Text(
                    stringResource(R.string.conversations),
                    Modifier.padding(24.dp),
                    style = MaterialTheme.typography.headlineSmall,
                )
                TextButton(
                    onClick = { model.chooseChat() },
                    modifier = Modifier.padding(horizontal = 16.dp),
                ) {
                    Symbol("add")
                    Spacer(Modifier.width(8.dp))
                    Text(stringResource(R.string.new_chat))
                }
                LazyColumn(Modifier.heightIn(max = 420.dp)) {
                    items(state.chats, key = { it.id }) { chat ->
                        ListItem(
                            content = {
                                Text(chat.name.ifBlank { stringResource(R.string.new_chat) })
                            },
                            modifier =
                                Modifier.fillMaxWidth().clickable { model.chooseChat(chat.id) },
                            trailingContent = {
                                TextButton(onClick = { model.chooseChat(chat.id) }) {
                                    Symbol("chevron_right", stringResource(R.string.selected))
                                }
                            },
                        )
                    }
                }
                Spacer(Modifier.navigationBarsPadding())
            }
        if (panel.isNotEmpty())
            ModalBottomSheet(onDismissRequest = { panel = "" }, modifier = Modifier.imePadding()) {
                val memory = panel == "memory"
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 20.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    Text(
                        stringResource(
                            if (memory) R.string.memory
                            else if (panel == "env") R.string.world else R.string.preset
                        ),
                        style = MaterialTheme.typography.headlineSmall,
                    )
                    IconButton(onClick = { panel = "" }) {
                        Symbol("close", stringResource(R.string.close))
                    }
                }
                if (memory) {
                    var query by remember { mutableStateOf("") }
                    OutlinedTextField(
                        query,
                        {
                            query = it
                            model.memory(it, "chat")
                        },
                        label = { Text(stringResource(R.string.search_entries)) },
                        modifier = Modifier.fillMaxWidth().padding(20.dp),
                    )
                    Text(state.memory, Modifier.padding(20.dp))
                } else {
                    var stage by remember { mutableStateOf(false) }
                    if (panel == "env")
                        Row(Modifier.padding(horizontal = 20.dp)) {
                            FilterChip(
                                selected = stage,
                                onClick = { stage = !stage },
                                label = { Text(stringResource(R.string.advance_story)) },
                            )
                        }
                    savedScreens.SaveableStateProvider("panel:${state.chatId}:$panel") {
                        TraceLog(
                            state.blocks.filter {
                                it.agent == panel ||
                                    it.agent == if (panel == "env") "EnvAgent" else "PresetAgent"
                            },
                            state.expandThoughts,
                            Modifier.fillMaxWidth()
                                .heightIn(min = 120.dp, max = 420.dp)
                                .weight(1f, fill = false),
                            streaming = state.busy,
                        )
                        Composer(state.busy, model::cancel) {
                            if (it.trim() == "/close") panel = "" else model.send(it, panel, stage)
                        }
                    }
                }
                Spacer(Modifier.navigationBarsPadding())
            }
        if (modelPicker) ModelPicker(state, model) { modelPicker = false }
    }
}
