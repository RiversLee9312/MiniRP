package com.minirp.app.ui

import android.app.LocaleManager
import android.content.ClipData
import android.content.Context
import android.content.res.Resources
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.minirp.app.R
import java.util.Locale
import kotlinx.coroutines.launch

private enum class SetupStep { Welcome, Provider, Model, Effort }

private const val MiniRPDocs = "https://github.com/RiversLee9312/MiniRP/blob/main/README.md"
private const val MiniRPReleases = "https://github.com/RiversLee9312/MiniRP/releases/latest"

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun FirstRunGuide(state: ChatState, model: ChatViewModel) {
    val provider = state.providers.lastOrNull()
    val uriHandler = LocalUriHandler.current
    var step by rememberSaveable {
        mutableStateOf(if (provider == null) SetupStep.Welcome else SetupStep.Model)
    }
    var editing by rememberSaveable { mutableStateOf(false) }
    var help by rememberSaveable { mutableStateOf(false) }
    var modelSearch by rememberSaveable(provider?.id) { mutableStateOf("") }
    var selectedModel by rememberSaveable(provider?.id) { mutableStateOf("") }
    var manualId by rememberSaveable(provider?.id) { mutableStateOf("") }
    var addingById by rememberSaveable(provider?.id) { mutableStateOf(false) }
    var requestedEffort by rememberSaveable(provider?.id, selectedModel.ifBlank { manualId.trim() }) { mutableStateOf("") }
    val snackbars = remember { SnackbarHostState() }

    val catalog = state.modelCatalogs[provider?.id]
    val choice = catalog?.choices?.find { it.id == selectedModel }
        ?: manualId.trim().takeIf { it.isNotEmpty() }?.let {
            Choice(it, it)
        }
    val (effortLevels, effortLoadFailed) = rememberThinkingEffortOptions(
        provider, choice.takeIf { step == SetupStep.Effort }, catalog?.loading == true, model::fetchModelEffortLevels,
    )

    LaunchedEffect(step, provider?.id) {
        if ((step == SetupStep.Model || step == SetupStep.Effort) && provider != null && catalog == null)
            model.refreshModels(provider)
    }
    LaunchedEffect(state.error) {
        state.error?.let {
            snackbars.showSnackbar(it)
            model.clearError()
        }
    }

    fun back() {
        step = when (step) {
            SetupStep.Effort -> SetupStep.Model
            SetupStep.Model -> SetupStep.Provider
            else -> SetupStep.Welcome
        }
    }
    BackHandler(enabled = step != SetupStep.Welcome && !editing && !help, onBack = ::back)

    val title = when (step) {
        SetupStep.Welcome -> R.string.setup_welcome
        SetupStep.Provider -> R.string.setup_connect_provider
        SetupStep.Model -> R.string.setup_choose_model
        SetupStep.Effort -> R.string.setup_effort_title
    }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.app_name)) },
                navigationIcon = {
                    if (step != SetupStep.Welcome)
                        IconButton(onClick = ::back) { Symbol("arrow_back", stringResource(R.string.back)) }
                },
            )
        },
        bottomBar = {
            if (step == SetupStep.Model || step == SetupStep.Effort) {
                val choosingModel = step == SetupStep.Model
                Surface(tonalElevation = 2.dp) {
                    Box(Modifier.fillMaxWidth().navigationBarsPadding().imePadding().padding(16.dp)) {
                        Button(
                            enabled = choice != null && provider != null && catalog?.loading == false &&
                                (choosingModel || effortLevels != null || effortLoadFailed),
                            onClick = {
                                if (choosingModel) step = SetupStep.Effort
                                else if (choice != null && provider != null) {
                                    val selected = requestedEffort.takeIf { it in effortLevels.orEmpty() }.orEmpty()
                                    model.finishOnboarding(SavedModel(
                                        provider.id, choice.copy(effortLevels = effortLevels.orEmpty()), reasoningEffort = selected,
                                    ))
                                }
                            },
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text(stringResource(if (choosingModel) R.string.setup_next else R.string.setup_use_model))
                        }
                    }
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbars) },
    ) { padding ->
        LazyColumn(
            Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding).imePadding().selectableGroup(),
            contentPadding = PaddingValues(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            item {
                Text(
                    stringResource(title),
                    style = MaterialTheme.typography.headlineMedium,
                    modifier = Modifier.semantics { heading() },
                )
            }
            when (step) {
                SetupStep.Welcome -> {
                    item { Text(stringResource(R.string.setup_welcome_detail)) }
                    item {
                        Column {
                            Text(stringResource(R.string.setup_download_notice), style = MaterialTheme.typography.bodyMedium)
                            TextButton(onClick = { uriHandler.openUri(MiniRPReleases) }) {
                                Text(stringResource(R.string.setup_official_download))
                            }
                        }
                    }
                    item {
                        Button(onClick = { step = SetupStep.Provider }) {
                            Text(stringResource(R.string.setup_begin))
                        }
                    }
                }
                SetupStep.Provider -> {
                    item { Text(stringResource(R.string.setup_provider_detail)) }
                    item {
                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            if (provider != null) {
                                Text(provider.name, style = MaterialTheme.typography.titleMedium)
                                Button(onClick = { step = SetupStep.Model }) {
                                    Text(stringResource(R.string.setup_choose_model))
                                }
                            }
                            OutlinedButton(onClick = { editing = true }) {
                                Text(stringResource(if (provider == null) R.string.add_provider else R.string.edit_provider))
                            }
                            TextButton(onClick = { help = true }) {
                                Text(stringResource(R.string.setup_unsure))
                            }
                        }
                    }
                }
                SetupStep.Model -> {
                    item {
                        Text(stringResource(R.string.setup_model_detail, provider?.name.orEmpty()))
                    }
                    if (catalog == null || catalog.loading) {
                        item {
                            LinearProgressIndicator(Modifier.fillMaxWidth())
                        }
                    } else if (catalog.choices.isEmpty()) {
                        item {
                            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                Text(
                                    stringResource(
                                        if (catalog.error == null) R.string.no_models else R.string.model_fetch_failed,
                                    ),
                                    style = MaterialTheme.typography.titleMedium,
                                )
                                Text(stringResource(R.string.setup_model_fallback))
                                OutlinedTextField(
                                    value = manualId,
                                    onValueChange = { manualId = it },
                                    label = { Text(stringResource(R.string.model_id)) },
                                    singleLine = true,
                                    modifier = Modifier.fillMaxWidth(),
                                )
                                if (provider != null)
                                    TextButton(onClick = { model.refreshModels(provider) }) {
                                        Text(stringResource(R.string.retry))
                                    }
                                TextButton(onClick = { help = true }) {
                                    Text(stringResource(R.string.setup_unsure))
                                }
                            }
                        }
                    } else {
                        item {
                            OutlinedTextField(
                                value = modelSearch,
                                onValueChange = { modelSearch = it },
                                label = { Text(stringResource(R.string.search_models)) },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            TextButton(onClick = { addingById = true }) {
                                Text(stringResource(R.string.add_model_by_id))
                            }
                            if (selectedModel.isEmpty() && manualId.isNotBlank()) {
                                Text(manualId, style = MaterialTheme.typography.bodyMedium)
                            }
                        }
                        items(
                            catalog.choices.filter { it.name.contains(modelSearch, true) || it.id.contains(modelSearch, true) },
                            key = { it.id },
                        ) { entry ->
                            val checked = choice?.id == entry.id
                            ListItem(
                                content = { Text(entry.name) },
                                supportingContent = if (entry.name != entry.id) { { Text(entry.id) } } else null,
                                trailingContent = { RadioButton(selected = checked, onClick = null) },
                                colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
                                modifier = Modifier.selectable(checked, role = Role.RadioButton) {
                                    selectedModel = entry.id
                                    manualId = ""
                                },
                            )
                        }
                    }
                }
                SetupStep.Effort -> {
                    item { Text(stringResource(R.string.setup_effort_detail)) }
                    item {
                        ThinkingEffortField(effortLevels, requestedEffort, effortLoadFailed) { requestedEffort = it }
                    }
                }
            }
        }
    }
    if (addingById) {
        AddModelByIdDialog(onDismiss = { addingById = false }) { id ->
            selectedModel = catalog?.choices?.find { it.id == id }?.id.orEmpty()
            manualId = if (selectedModel.isEmpty()) id else ""
            addingById = false
        }
    }
    if (editing) {
        val initial = remember(provider) { provider ?: ProviderConfig() }
        ProviderEditor(
            initial = initial,
            templates = state.catalog,
            onSave = {
                model.saveProvider(it)
                editing = false
                step = SetupStep.Model
            },
            onDelete = null,
            onDismiss = { editing = false },
        )
    }
    if (help) SetupHelpDialog { help = false }
}

private fun Locale.regionCode(): String =
    getUnicodeLocaleType("rg")?.removeSuffix("zzzz")?.uppercase(Locale.ROOT)
        ?.takeIf { it.length == 2 } ?: country

internal fun usesMainlandChina(appLocale: Locale, deviceLocale: Locale): Boolean {
    val region = deviceLocale.regionCode().ifBlank { appLocale.regionCode() }
    return if (region.isNotBlank()) region == "CN"
    else appLocale.language == "zh" && appLocale.script != "Hant"
}

internal fun Context.deviceLocale(): Locale =
    if (Build.VERSION.SDK_INT >= 33)
        getSystemService(LocaleManager::class.java)?.systemLocales?.get(0)
            ?: Resources.getSystem().configuration.locales[0]
    else Resources.getSystem().configuration.locales[0]

@Composable
internal fun SetupHelpDialog(close: () -> Unit) {
    var webTools by rememberSaveable { mutableStateOf(false) }
    var showPrompt by rememberSaveable { mutableStateOf(false) }
    val clipboard = LocalClipboard.current
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val locale = LocalConfiguration.current.locales[0]
    val deviceLocale = context.deviceLocale()
    val assistants = stringResource(
        if (usesMainlandChina(locale, deviceLocale)) R.string.setup_assistants_china
        else R.string.setup_assistants_global,
    )
    val language = locale.getDisplayName(locale)
    val region = deviceLocale.regionCode().ifBlank { locale.regionCode() }
        .ifBlank { stringResource(R.string.setup_unknown_region) }
    val promptIntro = stringResource(R.string.setup_help_intro, language, region, MiniRPDocs)
    val prompt = listOf(
        stringResource(R.string.setup_help_harness),
        promptIntro,
        stringResource(if (webTools) R.string.setup_help_web_prompt else R.string.setup_help_model_prompt),
        stringResource(R.string.setup_help_research),
        stringResource(R.string.setup_help_coaching),
        stringResource(R.string.setup_help_privacy),
    ).joinToString("\n\n")
    val copy = stringResource(R.string.setup_copy_prompt)
    var copied by remember(webTools) { mutableStateOf(false) }

    AlertDialog(
        onDismissRequest = close,
        title = { Text(stringResource(R.string.setup_help_title)) },
        text = {
            Column(
                Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text(stringResource(R.string.setup_help_detail, assistants))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(!webTools, { webTools = false }, { Text(stringResource(R.string.models)) })
                    FilterChip(webTools, { webTools = true }, { Text(stringResource(R.string.setup_help_web)) })
                }
                SelectionContainer { Text(if (showPrompt) prompt else promptIntro) }
                TextButton(onClick = { showPrompt = !showPrompt }) {
                    Text(stringResource(if (showPrompt) R.string.collapse else R.string.expand))
                }
            }
        },
        confirmButton = {
            Button(onClick = {
                scope.launch {
                    clipboard.setClipEntry(ClipEntry(ClipData.newPlainText(copy, prompt)))
                    copied = true
                }
            }) {
                Text(if (copied) stringResource(R.string.copied) else copy)
            }
        },
        dismissButton = { TextButton(onClick = close) { Text(stringResource(R.string.close)) } },
    )
}
