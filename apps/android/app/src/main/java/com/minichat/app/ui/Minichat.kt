package com.minichat.app.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandHorizontally
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkHorizontally
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavDestination.Companion.hasRoute
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.toRoute
import com.minichat.app.R
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable

internal sealed interface AppRoute {
    @Serializable data object Chat : AppRoute

    @Serializable data object Settings : AppRoute

    @Serializable data object Providers : AppRoute

    @Serializable data object Models : AppRoute

    @Serializable data object Plugins : AppRoute

    @Serializable data class PluginPage(val plugin: String, val key: String) : AppRoute
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun Minichat(model: ChatViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    MinichatTheme {
        val navigation = rememberNavController()
        val destination by navigation.currentBackStackEntryAsState()
        val active = destination?.destination
        val isChat = active?.hasRoute<AppRoute.Chat>() != false
        val locale = LocalConfiguration.current.locales[0]
        var panel by rememberSaveable { mutableStateOf("") }
        val activePanel = if (state.presetEditorOpen) "preset" else panel
        fun closePanel() {
            if (state.presetEditorOpen) model.closePresetEditor()
            panel = ""
        }
        var editTitle by rememberSaveable { mutableStateOf(false) }
        var modelPicker by rememberSaveable { mutableStateOf<AgentRole?>(null) }
        var language by rememberSaveable { mutableStateOf(false) }
        val screens = rememberSaveableStateHolder()
        val drawer = rememberDrawerState(DrawerValue.Closed)
        val scope = rememberCoroutineScope()
        val snackbars = remember { SnackbarHostState() }
        fun open(route: AppRoute) {
            navigation.navigate(route) { launchSingleTop = true }
        }
        val context = LocalContext.current
        val actions =
            remember(context, navigation) { ActionsRouter(context, ::open) { language = true } }
        LaunchedEffect(actions) { for (url in model.actions) actions.open(url) }
        if (language) LegacyLanguageSheet { language = false }
        LaunchedEffect(
            isChat,
            state.chatId,
            state.preset,
            state.presetEditorOpen,
            state.assigned(AgentRole.Chat),
            locale,
        ) {
            if (isChat) model.requestHome()
        }
        PluginPickerHost(model.picker)
        LaunchedEffect(state.error, activePanel) {
            state.error?.let {
                snackbars.showSnackbar(it)
                model.clearError()
            }
        }
        ModalNavigationDrawer(
            drawerState = drawer,
            gesturesEnabled = isChat,
            drawerContent = {
                ConversationDrawer(
                    state,
                    model,
                    drawer,
                    drawer.currentValue == DrawerValue.Open,
                    onSelect = { preset, id ->
                        model.chooseChat(preset, id)
                        scope.launch { drawer.close() }
                    },
                    onCreatePreset = {
                        scope.launch {
                            drawer.close()
                            model.openPresetEditor(create = true)
                        }
                    },
                )
            },
        ) {
            Surface(Modifier.fillMaxSize()) {
                val back: () -> Unit = { navigation.popBackStack() }
                NavHost(
                    navigation,
                    startDestination = AppRoute.Chat,
                    modifier = Modifier.fillMaxSize(),
                ) {
                    composable<AppRoute.Settings> {
                        SettingsPage(stringResource(R.string.settings), back, snackbars) {
                            DeclaredSettingsScreen(
                                state.pluginSettings,
                                state,
                                model,
                                onPage = ::open,
                            )
                        }
                    }
                    composable<AppRoute.Providers> {
                        SettingsPage(stringResource(R.string.providers), back, snackbars) {
                            ProvidersScreen(state, model, Modifier.fillMaxSize())
                        }
                    }
                    composable<AppRoute.Plugins> {
                        SettingsPage(stringResource(R.string.plugins), back, snackbars) {
                            PluginsScreen(state, model, Modifier.fillMaxSize())
                        }
                    }
                    composable<AppRoute.PluginPage> { entry ->
                        val (plugin, key) = entry.toRoute<AppRoute.PluginPage>()
                        val setting by
                            produceState(
                                state.pluginSettings.findSetting(plugin, key),
                                plugin,
                                key,
                                state.pluginSettings,
                            ) {
                                try {
                                    value = model.pluginPage(plugin, key)
                                } catch (cancelled: kotlinx.coroutines.CancellationException) {
                                    throw cancelled
                                } catch (failure: Exception) {
                                    model.pluginFailure(failure)
                                }
                            }
                        SettingsPage(setting?.let { SettingTitle(it) }.orEmpty(), back, snackbars) {
                            setting?.let { content ->
                                DeclaredSettingsScreen(
                                    if (content.markdown.isBlank()) content.children
                                    else
                                        listOf(
                                            content.copy(type = "markdown", children = emptyList())
                                        ) + content.children,
                                    state,
                                    model,
                                    onPage = ::open,
                                )
                            }
                        }
                    }
                    composable<AppRoute.Models> {
                        SettingsPage(stringResource(R.string.models), back, snackbars) {
                            ModelsScreen(
                                state,
                                model,
                                Modifier.fillMaxSize(),
                                onAssign = { modelPicker = it },
                                onProviders = { open(AppRoute.Providers) },
                            )
                        }
                    }
                    composable<AppRoute.Chat> {
                        val focus = LocalFocusManager.current
                        PageScaffold(
                            topBar = {
                                ChatToolbar(
                                    state,
                                    onMenu = { scope.launch { drawer.open() } },
                                    onSettings = {
                                        focus.clearFocus()
                                        open(AppRoute.Settings)
                                    },
                                    onRename = { editTitle = true },
                                    onPanel = {
                                        if (it == "preset") model.openPresetEditor() else panel = it
                                    },
                                )
                            },
                            snackbarHost = { if (activePanel.isBlank()) SnackbarHost(snackbars) },
                        ) {
                            screens.SaveableStateProvider("chat:${state.conversationViewKey}") {
                                AgentConversation(
                                    state,
                                    AgentRole.Chat,
                                    Modifier.fillMaxSize(),
                                    model::cancel,
                                    { text, files, system, type ->
                                        model.send(
                                            text,
                                            files,
                                            AgentRole.Chat,
                                            system = system,
                                            pluginAttachment = type,
                                        )
                                    },
                                    { modelPicker = it },
                                    onBranch = { model.branch(it) },
                                    onBranchReply = { model.branch(it, afterReply = true) },
                                    loadThumbnail = { model.thumbnail(AgentRole.Chat, it) },
                                    onOpenPreset = { model.openPresetEditor() },
                                    onCreatePreset = { model.openPresetEditor(create = true) },
                                    openUi =
                                        remember(model, state.chatId) {
                                            val chatId = state.chatId
                                            OpenUiInteractions(
                                                reply = { id, text, data ->
                                                    model.respondToOpenUi(chatId, id, text, data)
                                                },
                                                restore = { id -> model.openUiDraft(chatId, id) },
                                                save = { id, value ->
                                                    model.saveOpenUiDraft(chatId, id, value)
                                                },
                                            )
                                        },
                                )
                            }
                        }
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
        if (activePanel == "memory") {
            FullSheet(stringResource(R.string.memory), { panel = "" }) {
                MemoryScreen(state.chatId, model, Modifier.weight(1f).fillMaxWidth())
            }
        } else if (activePanel.isNotEmpty()) {
            val role = if (activePanel == "env") AgentRole.World else AgentRole.Preset
            val title =
                if (role == AgentRole.Preset) state.presetName else stringResource(role.label)
            FullSheet(title, ::closePanel) {
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
                        { text, files, system, type ->
                            if (!system && text.trim() == "/close" && files.isEmpty()) {
                                closePanel()
                                true
                            } else model.send(text, files, role, stage, system, type)
                        },
                        { modelPicker = it },
                        loadThumbnail = { model.thumbnail(role, it) },
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
                    closePanel()
                    if (isChat) open(AppRoute.Settings)
                    open(if (state.providers.isEmpty()) AppRoute.Providers else AppRoute.Models)
                },
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ChatToolbar(
    state: ChatState,
    onMenu: () -> Unit,
    onSettings: () -> Unit,
    onRename: () -> Unit,
    onPanel: (String) -> Unit,
) {
    val hasChat = state.chatId.isNotEmpty()
    val motion = MaterialTheme.motionScheme
    TopAppBar(
        title = {
            if (hasChat) {
                Text(
                    state.chatName.ifBlank { stringResource(R.string.new_chat) },
                    modifier =
                        Modifier.clickable(
                            enabled = !state.busy,
                            onClickLabel = stringResource(R.string.rename),
                            onClick = onRename,
                        ),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        },
        navigationIcon = {
            IconButton(onClick = onMenu) { Symbol("menu", stringResource(R.string.open_menu)) }
        },
        actions = {
            IconButton(onClick = onSettings) {
                Symbol("settings", stringResource(R.string.settings))
            }
            AnimatedVisibility(
                visible = hasChat,
                enter =
                    expandHorizontally(motion.fastSpatialSpec(), expandFrom = Alignment.End) +
                        fadeIn(motion.fastEffectsSpec()),
                exit =
                    shrinkHorizontally(motion.fastSpatialSpec(), shrinkTowards = Alignment.End) +
                        fadeOut(motion.fastEffectsSpec()),
            ) {
                ConversationActions(hasChat, open = onPanel)
            }
        },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SettingsPage(
    title: String,
    onBack: () -> Unit,
    snackbars: SnackbarHostState,
    content: @Composable () -> Unit,
) {
    PageScaffold(
        topBar = {
            TopAppBar(
                title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Symbol("arrow_back", stringResource(R.string.back))
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbars) },
        content = content,
    )
}

@Composable
private fun PageScaffold(
    topBar: @Composable () -> Unit,
    snackbarHost: @Composable () -> Unit,
    content: @Composable () -> Unit,
) {
    Scaffold(topBar = topBar, snackbarHost = snackbarHost) { padding ->
        Box(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding).imePadding()) {
            content()
        }
    }
}

private fun List<PluginSetting>.findSetting(plugin: String, key: String): PluginSetting? {
    for (item in this) {
        if (item.plugin == plugin && item.key == key) return item
        item.children.findSetting(plugin, key)?.let {
            return it
        }
    }
    return null
}

@Composable
private fun ConversationActions(enabled: Boolean, open: (String) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    LaunchedEffect(enabled) { if (!enabled) menu = false }
    IconButton(onClick = { menu = true }, enabled = enabled) {
        Symbol("more_horiz", stringResource(R.string.more_options))
    }
    DropdownMenu(menu && enabled, { menu = false }) {
        listOf("env" to R.string.world, "preset" to R.string.preset, "memory" to R.string.memory)
            .forEach { (panel, label) ->
                DropdownMenuItem(
                    text = { Text(stringResource(label)) },
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
    val state =
        rememberBottomSheetState(
            initialValue = SheetValue.Hidden,
            enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded),
        )
    val scope = rememberCoroutineScope()
    val dismiss by rememberUpdatedState(onDismiss)
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = state,
        contentWindowInsets = { WindowInsets.safeDrawing },
    ) {
        Column(Modifier.fillMaxSize()) {
            Row(
                Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp),
                verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
            ) {
                Text(
                    title,
                    Modifier.weight(1f),
                    style = MaterialTheme.typography.headlineSmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                IconButton(
                    onClick = {
                        scope.launch {
                            state.hide()
                            dismiss()
                        }
                    }
                ) {
                    Symbol("close", stringResource(R.string.close))
                }
            }
            content()
        }
    }
}
