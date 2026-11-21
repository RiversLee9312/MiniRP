package com.minirp.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.minirp.app.R
import kotlinx.coroutines.CancellationException

@Composable
internal fun ModelsScreen(
    state: ChatState,
    model: ChatViewModel,
    modifier: Modifier,
    onAssign: (AgentRole) -> Unit,
    onProviders: () -> Unit,
) {
    var providerId by rememberSaveable { mutableStateOf("") }
    var query by rememberSaveable { mutableStateOf("") }
    var editingModel by rememberSaveable { mutableStateOf<String?>(null) }
    val provider = state.providers.find { it.id == providerId } ?: state.providers.firstOrNull()
    LaunchedEffect(provider?.id) {
        if (provider != null && provider.id !in state.modelCatalogs) model.refreshModels(provider)
    }
    val catalog = state.modelCatalogs[provider?.id] ?: ModelCatalog()
    val models =
        remember(catalog.choices, state.savedModels, provider, query) {
            (catalog.choices +
                    state.savedModels.filter { it.provider == provider?.id }.map { it.choice })
                .distinctBy { it.id }
                .filter { it.name.contains(query, true) || it.id.contains(query, true) }
        }
    LazyColumn(modifier.testTag("models-list"), contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp)) {
        item { SectionLabel(R.string.agent_models) }
        items(AgentRole.entries.filterNot { it == AgentRole.Plugin }, key = { "assignment:${it.id}" }) { role ->
            val selection = state.assigned(role)
            ListItem(
                content = { Text(stringResource(role.label)) },
                supportingContent = {
                    Text(
                        selection?.choice?.name ?: stringResource(R.string.select_model),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                },
                trailingContent = { Symbol("chevron_right") },
                modifier = Modifier.clickable { onAssign(role) },
            )
        }
        item {
            HorizontalDivider(Modifier.padding(vertical = 8.dp))
            SectionLabel(R.string.available_models)
        }
        if (provider == null) {
            item {
                TextButton(onClick = onProviders) { Text(stringResource(R.string.add_provider)) }
            }
        } else {
            item {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    var show by remember { mutableStateOf(false) }
                    Box(Modifier.weight(1f)) {
                        TextButton(onClick = { show = true }) {
                            Text(provider.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Symbol("expand_more", size = 18)
                        }
                        DropdownMenu(show, { show = false }) {
                            state.providers.forEach { item ->
                                DropdownMenuItem(
                                    text = { Text(item.name) },
                                    onClick = {
                                        providerId = item.id
                                        show = false
                                    },
                                )
                            }
                        }
                    }
                    TextButton(
                        onClick = { model.refreshModels(provider) },
                        enabled = !catalog.loading,
                    ) {
                        Text(stringResource(R.string.refresh_models))
                    }
                }
                if (catalog.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
                catalog.error?.let {
                    Text(
                        it,
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(12.dp),
                    )
                }
                OutlinedTextField(
                    query,
                    { query = it },
                    Modifier.fillMaxWidth(),
                    singleLine = true,
                    label = { Text(stringResource(R.string.search_models)) },
                )
            }
            if (models.isEmpty() && !catalog.loading && catalog.error == null)
                item { SectionLabel(R.string.no_models) }
            items(models, key = { "model:${provider.id}:${it.id}" }) { choice ->
                val saved = SavedModel(provider.id, choice)
                val configured = state.savedModels.find { it.sameModel(saved) }
                val checked = configured != null
                ListItem(
                    content = { Text(choice.name) },
                    supportingContent =
                        if (choice.name == choice.id) null else ({ Text(choice.id) }),
                    trailingContent = {
                        Row {
                            if (checked) {
                                IconButton(onClick = { editingModel = choice.id }) {
                                    Symbol("settings", description = stringResource(R.string.model_settings))
                                }
                            }
                            Switch(checked, { model.enableModel(saved, it) })
                        }
                    },
                    modifier = Modifier.clickable { model.enableModel(saved, !checked) },
                )
            }
        }
    }
    val selected = state.savedModels.find { it.provider == provider?.id && it.choice.id == editingModel }
    if (selected != null && provider != null) {
        ModelSettingsDialog(
            selected,
            provider,
            catalog.loading,
            fetchEffortLevels = { model.fetchModelEffortLevels(provider, selected.choice.id) },
            onDismiss = { editingModel = null },
            onSave = { compaction, effort ->
                model.saveModelSettings(selected, compaction, effort)
                editingModel = null
            },
        )
    }
}

@Composable
private fun ModelSettingsDialog(
    model: SavedModel,
    provider: ProviderConfig,
    catalogLoading: Boolean,
    fetchEffortLevels: suspend () -> List<String>,
    onDismiss: () -> Unit,
    onSave: (CompactionSettings, String) -> Unit,
) {
    var value by rememberSaveable(model.provider, model.choice.id) {
        mutableStateOf(model.compaction.threshold.toString())
    }
    var background by rememberSaveable(model.provider, model.choice.id) {
        mutableStateOf(model.compaction.background)
    }
    var startValue by rememberSaveable(model.provider, model.choice.id) {
        mutableStateOf(model.compaction.backgroundThreshold.toString())
    }
    var requestedEffort by rememberSaveable(model.provider, model.choice.id) { mutableStateOf(model.reasoningEffort) }
    val fetchDetails = provider.protocol == "ollama" && model.choice.effortLevels.isEmpty()
    var fetchedLevels by remember(provider, model.choice.id) { mutableStateOf<List<String>?>(null) }
    var loadFailed by remember(provider, model.choice.id) { mutableStateOf(false) }
    LaunchedEffect(provider, model.choice.id, fetchDetails) {
        if (fetchDetails) {
            try {
                fetchedLevels = fetchEffortLevels()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                loadFailed = true
            }
        }
    }
    val effortLevels = when {
        fetchDetails -> fetchedLevels
        catalogLoading && model.choice.effortLevels.isEmpty() -> null
        else -> model.choice.effortLevels
    }
    val effort = requestedEffort.takeIf { effortLevels == null || it in effortLevels }.orEmpty()
    val percent = value.toIntOrNull()?.takeIf { it in 1..100 }
    val start = startValue.toIntOrNull()?.takeIf { it in 1..99 && percent != null && it < percent }
    val context = model.choice.contextWindow
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.model_settings)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(model.choice.name, style = MaterialTheme.typography.titleSmall)
                ThinkingEffortField(effortLevels, effort, loadFailed) { requestedEffort = it }
                Text(stringResource(R.string.compaction_threshold), style = MaterialTheme.typography.titleSmall)
                Text(
                    if (context > 0) stringResource(R.string.compaction_model_context, context)
                    else stringResource(R.string.compaction_fallback_context, provider.contextWindow)
                )
                OutlinedTextField(
                    value,
                    { if (it.length <= 3 && it.all(Char::isDigit)) value = it },
                    Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.compaction_context_percentage)) },
                    suffix = { Text("%") },
                    supportingText = { Text(stringResource(R.string.compaction_percentage_range)) },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    singleLine = true,
                    isError = value.isNotEmpty() && percent == null,
                )
                ListItem(
                    content = { Text(stringResource(R.string.background_compaction)) },
                    trailingContent = { Switch(background, { background = it }) },
                    modifier = Modifier.clickable { background = !background },
                )
                if (background) {
                    OutlinedTextField(
                        startValue,
                        { if (it.length <= 2 && it.all(Char::isDigit)) startValue = it },
                        Modifier.fillMaxWidth(),
                        label = { Text(stringResource(R.string.background_compaction_threshold)) },
                        suffix = { Text("%") },
                        supportingText = { Text(stringResource(R.string.background_compaction_range)) },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        singleLine = true,
                        isError = startValue.isNotEmpty() && start == null,
                    )
                }
            }
        },
        confirmButton = {
            TextButton(
                onClick = {
                    if (percent != null) {
                        onSave(CompactionSettings(percent, background, start ?: model.compaction.backgroundThreshold), effort)
                    }
                },
                enabled = percent != null && (!background || start != null),
            ) {
                Text(stringResource(R.string.save))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ThinkingEffortField(
    levels: List<String>?,
    effort: String,
    loadFailed: Boolean,
    onSelect: (String) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    val label = stringResource(R.string.thinking_effort)
    val default = stringResource(R.string.thinking_effort_default)
    if (levels.isNullOrEmpty()) {
        Text(label, style = MaterialTheme.typography.titleSmall)
        Text(
            stringResource(
                when {
                    loadFailed -> R.string.thinking_effort_load_failed
                    levels == null -> R.string.thinking_effort_loading
                    else -> R.string.thinking_effort_unavailable
                }
            ),
            style = MaterialTheme.typography.bodySmall,
        )
        return
    }
    ExposedDropdownMenuBox(expanded, { expanded = it }) {
        OutlinedTextField(
            value = effort.ifEmpty { default },
            onValueChange = {},
            modifier = Modifier.fillMaxWidth().testTag("model-thinking-effort")
                .menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable),
            readOnly = true,
            label = { Text(label) },
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded) },
        )
        ExposedDropdownMenu(expanded, onDismissRequest = { expanded = false }) {
            (listOf("") + levels).forEach { value ->
                DropdownMenuItem(
                    text = { Text(value.ifEmpty { default }) },
                    onClick = {
                        onSelect(value)
                        expanded = false
                    },
                )
            }
        }
    }
}

@Composable
private fun SectionLabel(label: Int) {
    Text(
        stringResource(label),
        Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun AssignmentSheet(
    role: AgentRole,
    state: ChatState,
    onDismiss: () -> Unit,
    onChoose: (SavedModel?) -> Unit,
    onManage: () -> Unit,
) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState =
            rememberBottomSheetState(
                initialValue = SheetValue.Hidden,
                enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded),
            ),
    ) {
        var query by rememberSaveable { mutableStateOf("") }
        Text(
            stringResource(role.label),
            Modifier.padding(start = 20.dp, bottom = 12.dp),
            style = MaterialTheme.typography.headlineSmall,
        )
        if (state.savedModels.isNotEmpty())
            OutlinedTextField(
                query,
                { query = it },
                Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                singleLine = true,
                label = { Text(stringResource(R.string.search_models)) },
            )
        LazyColumn(
            Modifier.heightIn(max = 460.dp).fillMaxWidth(),
            contentPadding = PaddingValues(vertical = 8.dp),
        ) {
            if (role != AgentRole.Chat)
                item {
                    ListItem(
                        content = {
                            Text(stringResource(
                                if (role == AgentRole.Plugin) R.string.use_preset_model else R.string.use_chat_model
                            ))
                        },
                        trailingContent = {
                            RadioButton(role !in state.assignments, onClick = null)
                        },
                        modifier = Modifier.clickable { onChoose(null) },
                    )
                }
            items(
                state.savedModels.filter {
                    it.choice.name.contains(query, true) || it.choice.id.contains(query, true)
                },
                key = { "${it.provider}:${it.choice.id}" },
            ) { entry ->
                val provider = state.providers.find { it.id == entry.provider }
                ListItem(
                    content = { Text(entry.choice.name) },
                    supportingContent = { Text(provider?.name.orEmpty()) },
                    trailingContent = {
                        RadioButton(entry.sameModel(state.assignments[role]), onClick = null)
                    },
                    modifier = Modifier.clickable { onChoose(entry) },
                )
            }
        }
        if (state.savedModels.isEmpty())
            TextButton(onClick = onManage, modifier = Modifier.padding(horizontal = 16.dp)) {
                Text(stringResource(R.string.manage_models))
            }
    }
}
