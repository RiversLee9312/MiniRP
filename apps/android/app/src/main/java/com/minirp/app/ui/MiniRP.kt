package com.minirp.app.ui

import android.widget.FrameLayout
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandHorizontally
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkHorizontally
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavDestination.Companion.hasRoute
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.toRoute
import com.minirp.app.R
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable

internal sealed interface AppRoute {
    @Serializable data object Chat : AppRoute

    @Serializable data class EnsembleChat(val room: String, val id: String) : AppRoute

    @Serializable data object Settings : AppRoute

    @Serializable data object Providers : AppRoute

    @Serializable data object Models : AppRoute

    @Serializable data object Plugins : AppRoute

    @Serializable data class PluginDetails(val id: String) : AppRoute

    @Serializable data class PluginPage(val plugin: String, val key: String) : AppRoute
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MiniRP(model: ChatViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    val view = LocalView.current
    DisposableEffect(model, view) {
        model.setPreviewWindow(view.rootView.findViewById<FrameLayout>(android.R.id.content))
        onDispose { model.setPreviewWindow(null) }
    }
    val background = rememberPresetBackground(model, state)
    MiniRPTheme(seedColor = state.appearance.themeColor?.let(::Color) ?: background?.seedColor) {
        if (state.showOnboarding) {
            FirstRunGuide(state, model)
            return@MiniRPTheme
        }
        val navigation = rememberNavController()
        val destination by navigation.currentBackStackEntryAsState()
        val active = destination?.destination
        val isChat = active?.hasRoute<AppRoute.Chat>() != false
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
        val archives = rememberArchivePicker(model)
        val assetDownloader = rememberAssetDownloader(model, snackbars)
        val actions =
            remember(context, navigation) { ActionsRouter(context, ::open) { language = true } }
        fun openLink(setting: PluginSetting) {
            if (setting.plugin != "base" || !actions.open(setting.url))
                model.openSettingLink(setting)
        }
        LaunchedEffect(actions) { for (url in model.actions) actions.open(url) }
        if (language) LegacyLanguageSheet { language = false }
        LaunchedEffect(isChat, state.chatId) {
            if (isChat) model.leaveEnsembleChat()
        }
        LaunchedEffect(destination, state.ready, state.chatId) {
            val entry = destination ?: return@LaunchedEffect
            if (
                state.ready &&
                    entry.destination.hasRoute<AppRoute.EnsembleChat>() &&
                    entry.toRoute<AppRoute.EnsembleChat>().room != state.chatId
            ) {
                navigation.popBackStack<AppRoute.Chat>(inclusive = false)
            }
        }
        PluginPickerHost(model.picker)
        LaunchedEffect(state.error, activePanel) {
            state.error?.let {
                snackbars.showSnackbar(it)
                model.clearError()
            }
        }
        LaunchedEffect(state.archiveSaved) {
            if (state.archiveSaved) {
                snackbars.showSnackbar(context.getString(R.string.saved))
                model.clearArchiveSaved()
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
                    onSelectPreset = { preset ->
                        model.selectPreset(preset)
                        scope.launch { drawer.close() }
                    },
                    onCreateConversation = { preset, ensemble ->
                        if (ensemble) model.createEnsembleRoom(preset) else model.createChat(preset)
                        scope.launch { drawer.close() }
                    },
                    onCreatePreset = {
                        scope.launch {
                            drawer.close()
                            model.openPresetEditor(create = true)
                        }
                    },
                    onArchiveExport = { type, name, preset, id ->
                        scope.launch {
                            drawer.close()
                            archives.export(type, name, preset, id)
                        }
                    },
                    onArchiveImport = { type ->
                        scope.launch {
                            drawer.close()
                            archives.importArchive(type)
                        }
                    },
                )
            },
        ) {
            Surface(Modifier.fillMaxSize()) {
                val back: () -> Unit = { navigation.popBackStack() }
                PageNavHost(
                    navigation,
                    startDestination = AppRoute.Chat,
                    modifier = Modifier.fillMaxSize(),
                ) {
                    page<AppRoute.Settings> { entry ->
                        SettingsPage(stringResource(R.string.settings), back, snackbars) {
                            DeclaredSettingsScreen(
                                state.pluginSettings,
                                state,
                                model,
                                onPage = {
                                    if (navigation.currentBackStackEntry == entry) open(it)
                                },
                                onLink = {
                                    if (navigation.currentBackStackEntry == entry) openLink(it)
                                },
                            )
                        }
                    }
                    page<AppRoute.Providers> {
                        SettingsPage(stringResource(R.string.providers), back, snackbars) {
                            ProvidersScreen(state, model, Modifier.fillMaxSize())
                        }
                    }
                    page<AppRoute.Plugins> {
                        SettingsPage(stringResource(R.string.plugins), back, snackbars) {
                            PluginsScreen(state, model, Modifier.fillMaxSize()) { plugin ->
                                navigation.navigate(AppRoute.PluginDetails(plugin.id)) { launchSingleTop = true }
                            }
                        }
                    }
                    page<AppRoute.PluginDetails> { entry ->
                        val id = entry.toRoute<AppRoute.PluginDetails>().id
                        val plugin = state.installedPlugins.firstOrNull { it.id == id }
                        val loaded = state.installedPlugins.isNotEmpty()
                        LaunchedEffect(plugin, loaded) {
                            if (plugin == null && loaded && navigation.currentBackStackEntry == entry) back()
                        }
                        SettingsPage(plugin?.name.orEmpty(), back, snackbars) {
                            if (plugin != null) PluginDetailsScreen(plugin, state, model) {
                                archives.export(ArchiveType.Plugin, plugin.name, "", plugin.id)
                            }
                        }
                    }
                    page<AppRoute.PluginPage> { entry ->
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
                                    onPage = {
                                        if (navigation.currentBackStackEntry == entry) open(it)
                                    },
                                    onLink = {
                                        if (navigation.currentBackStackEntry == entry) openLink(it)
                                    },
                                )
                            }
                        }
                    }
                    page<AppRoute.Models> {
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
                    page<AppRoute.Chat> {
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
                            if (state.ensembleChats != null) {
                                EnsembleChats(state.ensembleChats.orEmpty()) {
                                    open(AppRoute.EnsembleChat(state.chatId, it))
                                }
                            } else if (
                                state.chatId.isBlank() &&
                                    (state.chatMode == "ensemble" || state.chatGreeting)
                            ) {
                                val ensemble = state.chatMode == "ensemble"
                                Box(Modifier.fillMaxSize()) {
                                    PresetBackdrop(
                                        background, conversation = false, blur = false,
                                        modifier = Modifier.matchParentSize(),
                                    )
                                    Column(
                                        Modifier.fillMaxSize().padding(24.dp),
                                        verticalArrangement =
                                            Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
                                        horizontalAlignment = Alignment.CenterHorizontally,
                                    ) {
                                        HomeGreeting(
                                            state.presetName,
                                            Modifier.fillMaxWidth(),
                                            { model.openPresetEditor() },
                                            enabled = state.ready && !state.busy && !state.preparing,
                                            onBackground = background != null,
                                        )
                                        Button(
                                            onClick = {
                                                if (ensemble) model.createEnsembleRoom()
                                                else model.createChat()
                                            },
                                            enabled = state.ready && !state.busy && !state.preparing,
                                        ) {
                                            Text(
                                                stringResource(
                                                    if (ensemble) R.string.new_room
                                                    else R.string.new_chat
                                                )
                                            )
                                        }
                                    }
                                }
                            } else
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
                                        background = background,
                                        displayAssets = remember(model, assetDownloader, state.preset, state.chatId) {
                                            DisplayAssets(model, assetDownloader, state.preset, state.chatId)
                                        },
                                        openUi =
                                            remember(model, state.chatId) {
                                                val chatId = state.chatId
                                                OpenUiInteractions(
                                                    reply = { id, text, data ->
                                                        model.respondToOpenUi(
                                                            chatId,
                                                            id,
                                                            text,
                                                            data,
                                                        )
                                                    },
                                                    restore = { id ->
                                                        model.openUiDraft(chatId, id)
                                                    },
                                                    save = { id, value ->
                                                        model.saveOpenUiDraft(chatId, id, value)
                                                    },
                                                )
                                            },
                                    )
                                }
                        }
                    }
                    page<AppRoute.EnsembleChat> { entry ->
                        val (room, conversation) = entry.toRoute<AppRoute.EnsembleChat>()
                        val info = state.ensembleChats?.firstOrNull { it.id == conversation }
                        LaunchedEffect(room, conversation) {
                            model.openEnsembleChat(room, conversation)
                        }
                        val focus = LocalFocusManager.current
                        PageScaffold(
                            topBar = {
                                ChatToolbar(
                                    state,
                                    onMenu = {},
                                    onBack = back,
                                    conversationTitle = info?.name.orEmpty(),
                                    onSettings = {
                                        focus.clearFocus()
                                        open(AppRoute.Settings)
                                    },
                                    onRename = {},
                                    onPanel = {
                                        if (it == "preset") model.openPresetEditor() else panel = it
                                    },
                                )
                            },
                            snackbarHost = { if (activePanel.isBlank()) SnackbarHost(snackbars) },
                        ) {
                            if (state.chatId == room && state.ensembleChatId == conversation)
                                screens.SaveableStateProvider("ensemble:$room:$conversation") {
                                    AgentConversation(
                                        state.copy(blocks = state.ensembleBlocks),
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
                                        loadThumbnail = { model.thumbnail(AgentRole.Chat, it) },
                                        background = background,
                                        displayAssets = remember(model, assetDownloader, state.preset, room, conversation) {
                                            DisplayAssets(model, assetDownloader, state.preset, room, conversation)
                                        },
                                        openUi =
                                            remember(model, room) {
                                                OpenUiInteractions(
                                                    reply = { id, text, data ->
                                                        model.respondToOpenUi(room, id, text, data)
                                                    },
                                                    restore = { id -> model.openUiDraft(room, id) },
                                                    save = { id, value ->
                                                        model.saveOpenUiDraft(room, id, value)
                                                    },
                                                )
                                            },
                                    )
                                }
                            else
                                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                                    CircularProgressIndicator()
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
                MemoryScreen(
                    state.chatId,
                    model,
                    Modifier.weight(1f).fillMaxWidth(),
                    state.participants,
                    state.agenticSearch,
                )
            }
        } else if (activePanel.isNotEmpty()) {
            val role = if (activePanel == "env") AgentRole.World else AgentRole.Preset
            FullSheet(stringResource(role.label), ::closePanel) {
                var stage by rememberSaveable { mutableStateOf(false) }
                if (role == AgentRole.World)
                    FilterChip(
                        stage,
                        { stage = !stage },
                        label = { Text(stringResource(R.string.advance_story)) },
                        modifier = Modifier.padding(horizontal = 16.dp),
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
                        displayAssets = remember(model, assetDownloader, state.preset, state.chatId, role) {
                            DisplayAssets(model, assetDownloader, state.preset, if (role == AgentRole.Preset) "" else state.chatId)
                        },
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

@Composable
internal fun EnsembleChats(chats: List<EnsembleChat>, open: (String) -> Unit) {
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(vertical = 8.dp)) {
        item {
            Text(
                stringResource(R.string.conversations),
                modifier = Modifier.padding(start = 20.dp, top = 12.dp, bottom = 8.dp),
                style = MaterialTheme.typography.titleMedium,
            )
        }
        items(chats, key = { it.id }, contentType = { "conversation" }) { chat ->
            ListItem(
                content = { Text(chat.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                supportingContent = {
                    Text(
                        chat.members.joinToString(" · "),
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                },
                trailingContent = { Symbol("chevron_right") },
                modifier = Modifier.clickable { open(chat.id) },
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
    onBack: (() -> Unit)? = null,
    conversationTitle: String? = null,
) {
    val hasChat = state.chatId.isNotEmpty()
    val motion = MaterialTheme.motionScheme
    TopAppBar(
        title = {
            if (hasChat) {
                Text(
                    conversationTitle
                        ?: state.chatName.ifBlank {
                            stringResource(
                                if (state.ensembleChats == null) R.string.new_chat
                                else R.string.new_room
                            )
                        },
                    modifier =
                        Modifier.clickable(
                            enabled = !state.busy && conversationTitle == null,
                            onClickLabel = stringResource(R.string.rename),
                            onClick = onRename,
                        ),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        },
        navigationIcon = {
            IconButton(onClick = onBack ?: onMenu) {
                if (onBack == null) Symbol("menu", stringResource(R.string.open_menu))
                else Symbol("arrow_back", stringResource(R.string.back))
            }
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
    val caption = if (state.ensembleChats == null) R.string.chat_name else R.string.rename
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(caption)) },
        text = {
            Column {
                OutlinedTextField(
                    title,
                    { title = it.take(140) },
                    Modifier.fillMaxWidth(),
                    singleLine = true,
                    label = { Text(stringResource(caption)) },
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
