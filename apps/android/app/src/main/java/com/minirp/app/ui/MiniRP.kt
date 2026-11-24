package com.minirp.app.ui

import android.Manifest
import android.content.pm.PackageManager
import android.widget.FrameLayout
import android.widget.Toast
import android.os.Build
import android.window.OnBackInvokedCallback
import android.window.OnBackInvokedDispatcher
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandHorizontally
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkHorizontally
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogWindowProvider
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavDestination.Companion.hasRoute
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.toRoute
import com.minirp.app.R
import com.minirp.app.core.TurnProtection
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable

internal sealed interface AppRoute {
    @Serializable data object Chat : AppRoute

    @Serializable data class EnsembleChat(val room: String, val id: String) : AppRoute

    @Serializable data object Settings : AppRoute

    @Serializable data object Providers : AppRoute

    @Serializable data object Models : AppRoute

    @Serializable data object Research : AppRoute

    @Serializable data object BackgroundTurns : AppRoute

    @Serializable data object Plugins : AppRoute

    @Serializable data class PluginDetails(val id: String) : AppRoute

    @Serializable data class PluginPage(val plugin: String, val key: String) : AppRoute
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MiniRP(model: ChatViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    val appLocale = LocalConfiguration.current.locales[0].toLanguageTag()
    LaunchedEffect(model, appLocale) { model.refreshPluginLocale(appLocale) }
    val protection = model.turnProtection?.state?.collectAsStateWithLifecycle()?.value
    val permissionContext = LocalContext.current
    var requestingNotifications by rememberSaveable { mutableStateOf(false) }
    val requestNotifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        requestingNotifications = false
        model.authorizeBackgroundTurns(it)
    }
    val authorizeBackgroundTurns: (Boolean) -> Unit = { enabled ->
        if (enabled && Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(permissionContext, Manifest.permission.POST_NOTIFICATIONS) !=
                PackageManager.PERMISSION_GRANTED
        ) {
            requestingNotifications = true
            requestNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        } else {
            model.authorizeBackgroundTurns(enabled)
        }
    }
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
        if (state.ready && protection?.prompt != null && !requestingNotifications) {
            BackgroundTurnsPrompt(protection.prompt, authorizeBackgroundTurns)
        }
        val navigation = rememberNavController()
        val destination by navigation.currentBackStackEntryAsState()
        val active = destination?.destination
        val isChat = active?.hasRoute<AppRoute.Chat>() != false
        var panel by rememberSaveable { mutableStateOf("") }
        val isConversation = isChat || active.hasRoute<AppRoute.EnsembleChat>()
        val activePanel = if (state.presetEditorOpen && isConversation) "preset" else panel
        fun closePanel(): Boolean {
            if (model.state.value.presetEditorOpen && !model.closePresetEditor()) return false
            panel = ""
            return true
        }
        var editTitle by rememberSaveable { mutableStateOf(false) }
        var modelPicker by rememberSaveable { mutableStateOf<AgentRole?>(null) }
        var language by rememberSaveable { mutableStateOf(false) }
        val screens = rememberSaveableStateHolder()
        val drawer = rememberDrawerState(DrawerValue.Closed)
        val showPresetHints = state.ready && state.preset.isNotBlank() && !state.presetSetupPending &&
            !state.busy && !state.preparing && activePanel.isBlank() && drawer.isClosed &&
            modelPicker == null && !editTitle
        val homeHint: (() -> Unit)? =
            if (showPresetHints && isChat && state.chatId.isBlank() && !state.presetHomeHintSeen)
                ({ model.markPresetHintSeen(inChat = false) }) else null
        val chatHint: (() -> Unit)? =
            if (showPresetHints && isConversation && state.chatId.isNotBlank() && !state.presetChatHintSeen)
                ({ model.markPresetHintSeen(inChat = true) }) else null
        val scope = rememberCoroutineScope()
        val snackbars = remember { SnackbarHostState() }
        fun open(route: AppRoute) {
            navigation.navigate(route) {
                // Nested declared pages share a destination type and need distinct back-stack entries.
                launchSingleTop = route !is AppRoute.PluginPage
            }
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
        val library: @Composable (DrawerState, Boolean) -> Unit = { sheet, presetsFirst ->
            fun canNavigate() = !presetsFirst || !model.state.value.presetWorking
            ConversationDrawer(
                state,
                model,
                sheet,
                sheet.currentValue == DrawerValue.Open,
                onSelect = { preset, id ->
                    if (canNavigate()) model.chooseChat(preset, id)
                    scope.launch { sheet.close() }
                },
                onSelectPreset = { preset ->
                    if (canNavigate()) model.selectPreset(preset)
                    scope.launch { sheet.close() }
                },
                onCreateConversation = { preset, ensemble ->
                    if (canNavigate()) {
                        if (ensemble) model.createEnsembleRoom(preset) else model.createChat(preset)
                    }
                    scope.launch { sheet.close() }
                },
                onCreatePreset = {
                    scope.launch {
                        sheet.close()
                        if (canNavigate()) model.openPresetEditor(create = true)
                    }
                },
                onArchiveExport = { type, name, preset, id ->
                    scope.launch {
                        sheet.close()
                        if (canNavigate()) archives.export(type, name, preset, id)
                    }
                },
                onArchiveImport = { type ->
                    scope.launch {
                        sheet.close()
                        if (canNavigate()) archives.importArchive(type)
                    }
                },
                presetsFirst = presetsFirst,
            )
        }
        ModalNavigationDrawer(
            drawerState = drawer,
            gesturesEnabled = isChat && activePanel.isBlank(),
            drawerContent = { library(drawer, false) },
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
                                state.pluginSettings.filter { it.plugin == "base" },
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
                    page<AppRoute.Research> {
                        SettingsPage(stringResource(R.string.providers), back, snackbars) {
                            ResearchSettingsScreen(state.research) {
                                model.researchSettings(it)
                                back()
                            }
                        }
                    }
                    page<AppRoute.BackgroundTurns> {
                        SettingsPage(stringResource(R.string.background_turns), back, snackbars) {
                            BackgroundTurnsSettingsScreen(
                                protection?.enabled == true,
                                authorizeBackgroundTurns.takeIf { protection != null && !requestingNotifications },
                            )
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
                            if (plugin != null) {
                                PluginDetailsScreen(
                                    plugin,
                                    state,
                                    model,
                                    onExport = { archives.export(ArchiveType.Plugin, plugin.name, "", plugin.id) },
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
                    page<AppRoute.PluginPage> { entry ->
                        val (plugin, key) = entry.toRoute<AppRoute.PluginPage>()
                        val setting by
                            produceState(
                                state.pluginSettings.findSetting(plugin, key),
                                plugin,
                                key,
                                appLocale,
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
                                    if (content.markdown.isBlank() && content.localizedMarkdown.isBlank() && content.assetUrl.isBlank())
                                        content.children
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
                                    onExportTranscript = {
                                        archives.exportTranscript(state.chatName, TranscriptTarget(state.preset, AgentRole.Chat, state.chatId))
                                    }.takeIf { state.chatId.isNotBlank() && state.ensembleChats == null },
                                    onPresetHintSeen = chatHint.takeIf { isChat },
                                )
                            },
                            snackbarHost = { if (activePanel.isBlank()) SnackbarHost(snackbars) },
                        ) {
                            if (state.ensembleChats != null) {
                                EnsembleChats(state.ensembleChats.orEmpty()) {
                                    open(AppRoute.EnsembleChat(state.chatId, it))
                                }
                            } else if (state.chatId.isBlank() && state.presetSetupPending) {
                                HomeGreeting(
                                    state.presetName,
                                    Modifier.fillMaxSize(),
                                    { model.openPresetEditor() },
                                    enabled = state.ready && !state.busy && !state.preparing,
                                )
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
                                            onHintSeen = homeHint,
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
                                        onPresetHintSeen = homeHint,
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
                                    onExportTranscript = {
                                        archives.exportTranscript(info?.name.orEmpty(),
                                            TranscriptTarget(state.preset, AgentRole.Chat, room, conversation))
                                    }.takeIf { state.chatId == room && state.ensembleChatId == conversation },
                                    onPresetHintSeen = chatHint.takeUnless { isChat },
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
        state.roomLoading?.let { RoomLoadingScreen(it, model::cancel) }
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
            val session = if (role == AgentRole.Preset) state.preset else state.chatId
            val assets = remember(model, assetDownloader, state.preset, state.chatId, role) {
                DisplayAssets(model, assetDownloader, state.preset, if (role == AgentRole.Preset) "" else state.chatId)
            }
            val presetActions = if (role == AgentRole.Preset) remember(model, context, session) {
                OpenUiInteractions(
                    reply = { _, _, _ -> false },
                    restore = { id -> model.openUiDraft(session, id) },
                    save = { id, value -> model.saveOpenUiDraft(session, id, value) },
                    hostAction = { id, _ ->
                        val result = model.returnHomeFromPresetCard(session, id)
                        val message = when (result) {
                            PresetHomeResult.SetupPending -> R.string.preset_return_home_setup_pending
                            PresetHomeResult.Working -> R.string.preset_return_home_working
                            else -> null
                        }
                        if (message != null) Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
                        result == PresetHomeResult.Returned
                    },
                )
            } else null
            val conversation: @Composable (Modifier) -> Unit = { modifier ->
                screens.SaveableStateProvider("${role.id}:$session") {
                    AgentConversation(
                        state,
                        role,
                        modifier,
                        model::cancel,
                        { text, files, system, type ->
                            if (!system && text.trim() == "/close" && files.isEmpty()) {
                                closePanel()
                            } else model.sendFromPanel(text, files, role, system, type)
                        },
                        { modelPicker = it },
                        loadThumbnail = { model.thumbnail(role, it) },
                        displayAssets = assets,
                        openUi = presetActions,
                    )
                }
            }
            if (role == AgentRole.Preset) {
                key(session) {
                    PresetPluginPanel(
                        state.blocks,
                        assets,
                        ::closePanel,
                        locked = state.presetExitLocked,
                        canSwitchPreset = !state.presetWorking,
                        canExportTranscript = state.canExportTranscript,
                        onExportTranscript = { name, run ->
                            val targetRole = if (run.isEmpty()) AgentRole.Preset else AgentRole.Plugin
                            archives.exportTranscript(name.ifBlank { state.presetName },
                                TranscriptTarget(state.preset, targetRole, run = run))
                        },
                        onSettings = { open(AppRoute.Settings) },
                        library = { library(it, true) },
                        snackbarHost = { SnackbarHost(snackbars) },
                        conversation = conversation,
                    )
                }
            } else {
                FullSheet(stringResource(role.label), ::closePanel, actions = {
                    ExportTranscriptButton(
                        onClick = {
                            archives.exportTranscript(state.chatName,
                                TranscriptTarget(state.preset, AgentRole.World, state.chatId))
                        },
                        enabled = state.canExportTranscript && state.chatId.isNotBlank(),
                    )
                }) {
                    conversation(Modifier.weight(1f).fillMaxWidth())
                    SnackbarHost(snackbars)
                }
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
    onPresetHintSeen: (() -> Unit)? = null,
    onExportTranscript: (() -> Unit)? = null,
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
                ConversationActions(hasChat, open = onPanel, onPresetHintSeen = onPresetHintSeen,
                    onExportTranscript = onExportTranscript, canExportTranscript = state.canExportTranscript)
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
private fun ConversationActions(
    enabled: Boolean,
    open: (String) -> Unit,
    onPresetHintSeen: (() -> Unit)? = null,
    onExportTranscript: (() -> Unit)? = null,
    canExportTranscript: Boolean = false,
) {
    var menu by remember { mutableStateOf(false) }
    LaunchedEffect(enabled) { if (!enabled) menu = false }
    PresetEntryHint(stringResource(R.string.preset_chat_hint), onPresetHintSeen.takeUnless { menu }) {
        IconButton(
            onClick = {
                onPresetHintSeen?.invoke()
                menu = true
            },
            enabled = enabled,
        ) {
            Symbol("more_horiz", stringResource(R.string.more_options))
        }
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
        if (onExportTranscript != null) {
            DropdownMenuItem(
                text = { Text(stringResource(R.string.export_transcript)) },
                leadingIcon = { Symbol("share") },
                enabled = canExportTranscript,
                onClick = {
                    menu = false
                    onExportTranscript()
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
    onBack: (() -> Unit)? = null,
    draftControls: DraftPresetControls? = null,
    actions: @Composable () -> Unit = {},
    content: @Composable ColumnScope.() -> Unit,
) {
    val locked = draftControls?.locked == true
    val state =
        rememberBottomSheetState(
            initialValue = SheetValue.Hidden,
            enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded),
        )
    val scope = rememberCoroutineScope()
    val dismiss by rememberUpdatedState(onDismiss)
    val currentLock by rememberUpdatedState(locked)
    ModalBottomSheet(
        onDismissRequest = {
            if (currentLock) scope.launch { state.show() } else dismiss()
        },
        sheetState = state,
        sheetGesturesEnabled = !locked,
        properties = ModalBottomSheetProperties(
            shouldDismissOnBackPress = !locked && onBack == null,
            shouldDismissOnClickOutside = !locked,
        ),
        dragHandle = if (draftControls == null) ({ BottomSheetDefaults.DragHandle() }) else null,
        contentWindowInsets = { WindowInsets.safeDrawing },
    ) {
        val body: @Composable () -> Unit = {
            BackHandler(locked && onBack == null && draftControls.drawer.isClosed) {}
            if (onBack != null) SheetPageBackHandler(onBack)
            Column(Modifier.fillMaxSize()) {
                if (draftControls != null) {
                    AnimatedVisibility(
                        visible = !locked,
                        enter = expandVertically() + fadeIn(),
                        exit = shrinkVertically() + fadeOut(),
                    ) {
                        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                            BottomSheetDefaults.DragHandle(Modifier.testTag("sheet-drag-handle"))
                        }
                    }
                }
                SheetHeading(title, onBack, draftControls, actions) {
                    scope.launch {
                        state.hide()
                        if (currentLock) state.show() else dismiss()
                    }
                }
                content()
            }
        }
        if (draftControls == null) body()
        else ModalNavigationDrawer(
            drawerState = draftControls.drawer,
            gesturesEnabled = locked && draftControls.canSwitchPreset,
            drawerContent = draftControls.library,
            content = body,
        )
    }
}

@Composable
private fun SheetPageBackHandler(onBack: () -> Unit) {
    val window = (LocalView.current.parent as? DialogWindowProvider)?.window
    if (Build.VERSION.SDK_INT < 33 || window == null) {
        BackHandler(onBack = onBack)
        return
    }
    val back by rememberUpdatedState(onBack)
    // Handle the page inside the sheet's own window while sheet dismissal is disabled.
    DisposableEffect(window) {
        val dispatcher = window.onBackInvokedDispatcher
        val callback = OnBackInvokedCallback { back() }
        dispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_OVERLAY, callback)
        onDispose { dispatcher.unregisterOnBackInvokedCallback(callback) }
    }
}

internal data class DraftPresetControls(
    val locked: Boolean,
    val canSwitchPreset: Boolean,
    val drawer: DrawerState,
    val library: @Composable () -> Unit,
    val openDrawer: () -> Unit,
    val settings: () -> Unit,
)

@Composable
private fun SheetHeading(
    title: String,
    onBack: (() -> Unit)?,
    draftControls: DraftPresetControls?,
    actions: @Composable () -> Unit,
    close: () -> Unit,
) {
    val locked = draftControls?.locked == true
    val motion = MaterialTheme.motionScheme
    Row(
        Modifier.fillMaxWidth().padding(start = if (onBack == null) 12.dp else 4.dp, end = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (onBack != null) {
            IconButton(onClick = onBack) {
                Symbol("arrow_back", stringResource(R.string.back))
            }
        } else {
            AnimatedVisibility(
                visible = locked && draftControls.canSwitchPreset,
                enter = expandHorizontally(motion.fastSpatialSpec()) + fadeIn(motion.fastEffectsSpec()),
                exit = shrinkHorizontally(motion.fastSpatialSpec()) + fadeOut(motion.fastEffectsSpec()),
            ) {
                IconButton(onClick = { draftControls?.openDrawer?.invoke() }) {
                    Symbol("menu", stringResource(R.string.open_menu))
                }
            }
        }
        Text(
            title,
            Modifier.weight(1f).padding(start = if (onBack == null) 8.dp else 0.dp).semantics { heading() },
            style = MaterialTheme.typography.headlineSmall,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        actions()
        if (onBack == null) {
            IconButton(onClick = if (locked) checkNotNull(draftControls).settings else close) {
                AnimatedContent(
                    locked,
                    transitionSpec = { fadeIn(motion.fastEffectsSpec()) togetherWith fadeOut(motion.fastEffectsSpec()) },
                    label = "Preset editor action",
                ) { showSettings ->
                    if (showSettings) Symbol("settings", stringResource(R.string.settings))
                    else Symbol("close", stringResource(R.string.close))
                }
            }
        }
    }
}
