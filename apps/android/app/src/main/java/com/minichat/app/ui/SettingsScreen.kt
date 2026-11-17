package com.minichat.app.ui

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import com.minichat.app.R

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun SettingsScreen(state: ChatState, model: ChatViewModel, modifier: Modifier = Modifier) {
    var editor by remember { mutableStateOf<ProviderConfig?>(null) }
    var prompt by remember { mutableStateOf(false) }
    var language by remember { mutableStateOf(false) }
    val context = LocalContext.current
    LazyColumn(
        modifier.fillMaxSize(),
        contentPadding = PaddingValues(20.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Text(stringResource(R.string.providers), style = MaterialTheme.typography.titleLarge)
        }
        items(state.providers, key = { it.id }) { p ->
            ListItem(
                content = { Text(p.name) },
                supportingContent = { Text(p.model.ifBlank { p.url }, maxLines = 1) },
                modifier = Modifier.clickable { editor = p },
                trailingContent = {
                    RadioButton(
                        selected = state.provider?.id == p.id,
                        onClick = { model.selectProvider(p.id) },
                    )
                },
            )
        }
        item {
            TextButton(onClick = { editor = ProviderConfig() }) {
                Symbol("add")
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.add_provider))
            }
        }
        item { HorizontalDivider() }
        item {
            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.expand_thoughts), Modifier.weight(1f))
                Switch(state.expandThoughts, model::thoughts)
            }
        }
        item {
            Text(stringResource(R.string.fetch_mode), style = MaterialTheme.typography.titleMedium)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(
                        "api" to R.string.fetch_api,
                        "local" to R.string.fetch_browser,
                        "api_then_local" to R.string.fetch_fallback,
                    )
                    .forEach { (mode, label) ->
                        FilterChip(
                            selected = state.fetchMode == mode,
                            onClick = { model.fetchMode(mode) },
                            label = { Text(stringResource(label)) },
                        )
                    }
            }
        }
        item {
            ListItem(
                content = { Text(stringResource(R.string.system_prompt)) },
                trailingContent = { Symbol("chevron_right") },
                modifier = Modifier.clickable { prompt = true },
            )
        }
        item {
            ListItem(
                content = { Text(stringResource(R.string.language)) },
                trailingContent = { Symbol("chevron_right") },
                modifier =
                    Modifier.clickable {
                        if (Build.VERSION.SDK_INT >= 33)
                            context.startActivity(
                                Intent(
                                    Settings.ACTION_APP_LOCALE_SETTINGS,
                                    Uri.parse("package:${context.packageName}"),
                                )
                            )
                        else language = true
                    },
            )
        }
    }
    editor?.let { provider ->
        ProviderEditor(
            provider,
            state.catalog,
            onSave = {
                model.saveProvider(it)
                editor = null
            },
            onDelete = {
                model.deleteProvider(provider.id)
                editor = null
            },
            onDismiss = { editor = null },
        )
    }
    if (prompt) {
        var text by remember { mutableStateOf(state.systemPrompt) }
        AlertDialog(
            onDismissRequest = { prompt = false },
            title = { Text(stringResource(R.string.system_prompt)) },
            text = {
                OutlinedTextField(
                    text,
                    { text = it },
                    modifier = Modifier.heightIn(max = 360.dp),
                    minLines = 4,
                    maxLines = 12,
                )
            },
            dismissButton = {
                TextButton(onClick = { prompt = false }) { Text(stringResource(R.string.cancel)) }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        model.prompt(text)
                        prompt = false
                    }
                ) {
                    Text(stringResource(R.string.save))
                }
            },
        )
    }
    if (language)
        ModalBottomSheet(onDismissRequest = { language = false }) {
            val tags =
                listOf(
                    "",
                    "en",
                    "zh-Hans",
                    "zh-Hant",
                    "ja",
                    "ko",
                    "fr",
                    "de",
                    "es",
                    "pt-BR",
                    "it",
                    "ru",
                    "ar",
                    "he",
                    "fa",
                    "hi",
                    "id",
                    "vi",
                    "th",
                    "tr",
                    "pl",
                    "uk",
                    "nl",
                    "sv",
                    "da",
                    "nb",
                    "fi",
                    "cs",
                    "ro",
                    "hu",
                    "el",
                )
            LazyColumn {
                items(tags) { tag ->
                    ListItem(
                        content = {
                            Text(
                                if (tag.isEmpty()) stringResource(R.string.system_default)
                                else
                                    java.util.Locale.forLanguageTag(tag).let {
                                        it.getDisplayName(it)
                                    }
                            )
                        },
                        modifier =
                            Modifier.clickable {
                                AppCompatDelegate.setApplicationLocales(
                                    LocaleListCompat.forLanguageTags(tag)
                                )
                                language = false
                            },
                    )
                }
            }
        }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ProviderEditor(
    initial: ProviderConfig,
    catalog: List<ProviderConfig>,
    onSave: (ProviderConfig) -> Unit,
    onDelete: () -> Unit,
    onDismiss: () -> Unit,
) {
    var value by remember(initial.id) { mutableStateOf(initial) }
    var visible by remember { mutableStateOf(false) }
    var choose by remember { mutableStateOf(false) }
    var delete by remember { mutableStateOf(false) }
    var advanced by remember { mutableStateOf(false) }
    var contextSize by remember(initial.id) { mutableStateOf(initial.contextWindow.toString()) }
    var maxOutput by remember(initial.id) { mutableStateOf(initial.maxOutput.toString()) }
    ModalBottomSheet(onDismissRequest = onDismiss, modifier = Modifier.imePadding()) {
        LazyColumn(
            Modifier.fillMaxWidth(),
            contentPadding = PaddingValues(20.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                Text(
                    stringResource(R.string.edit_provider),
                    style = MaterialTheme.typography.headlineSmall,
                )
            }
            item {
                TextButton(onClick = { choose = true }) {
                    Text(value.name.ifBlank { stringResource(R.string.provider_type) })
                    Symbol("expand_more")
                }
                DropdownMenu(choose, { choose = false }) {
                    catalog.forEach { template ->
                        DropdownMenuItem(
                            text = { Text(template.name) },
                            onClick = {
                                value =
                                    value.copy(
                                        name = template.name,
                                        protocol = template.protocol,
                                        url = template.url,
                                    )
                                choose = false
                            },
                        )
                    }
                }
            }
            item {
                OutlinedTextField(
                    value.name,
                    { value = value.copy(name = it) },
                    label = { Text(stringResource(R.string.provider_name)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                OutlinedTextField(
                    value.url,
                    { value = value.copy(url = it) },
                    label = { Text(stringResource(R.string.base_url)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                OutlinedTextField(
                    value.key,
                    { value = value.copy(key = it) },
                    label = { Text(stringResource(R.string.api_key)) },
                    visualTransformation =
                        if (visible) VisualTransformation.None else PasswordVisualTransformation(),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    trailingIcon = {
                        IconButton(onClick = { visible = !visible }) {
                            Symbol(
                                if (visible) "visibility_off" else "visibility",
                                stringResource(
                                    if (visible) R.string.hide_key else R.string.show_key
                                ),
                            )
                        }
                    },
                )
            }
            item {
                OutlinedTextField(
                    value.model,
                    { value = value.copy(model = it) },
                    label = { Text(stringResource(R.string.model_id)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            item {
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Text(stringResource(R.string.thinking), Modifier.weight(1f))
                    Switch(value.thinking, { value = value.copy(thinking = it) })
                }
            }
            item {
                TextButton(onClick = { advanced = !advanced }) {
                    Text(stringResource(R.string.more_options))
                }
            }
            if (advanced) {
                item {
                    OutlinedTextField(
                        contextSize,
                        { contextSize = it },
                        label = { Text(stringResource(R.string.context_size)) },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
                item {
                    OutlinedTextField(
                        maxOutput,
                        { maxOutput = it },
                        label = { Text(stringResource(R.string.max_output)) },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
            item {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    if (initial.name.isNotEmpty())
                        TextButton(onClick = { delete = true }) {
                            Text(stringResource(R.string.delete))
                        }
                    else Spacer(Modifier.width(1.dp))
                    Button(
                        enabled =
                            value.name.isNotBlank() &&
                                (value.url.startsWith("https://") ||
                                    value.url.startsWith("http://")) &&
                                (contextSize.toIntOrNull() ?: 0) >= 2048 &&
                                (maxOutput.toIntOrNull() ?: 0) > 0,
                        onClick = {
                            onSave(
                                value.copy(
                                    contextWindow = contextSize.toInt(),
                                    maxOutput = maxOutput.toInt(),
                                )
                            )
                        },
                    ) {
                        Text(stringResource(R.string.save))
                    }
                }
            }
            item { Spacer(Modifier.navigationBarsPadding()) }
        }
    }
    if (delete)
        AlertDialog(
            onDismissRequest = { delete = false },
            text = { Text(stringResource(R.string.delete_provider_question, value.name)) },
            dismissButton = {
                TextButton(onClick = { delete = false }) { Text(stringResource(R.string.cancel)) }
            },
            confirmButton = {
                TextButton(onClick = onDelete) { Text(stringResource(R.string.delete)) }
            },
        )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ModelPicker(state: ChatState, model: ChatViewModel, onDismiss: () -> Unit) {
    var query by remember { mutableStateOf("") }
    ModalBottomSheet(onDismissRequest = onDismiss) {
        if (state.modelsLoading) LinearProgressIndicator(Modifier.fillMaxWidth())
        OutlinedTextField(
            query,
            { query = it },
            label = { Text(stringResource(R.string.search_models)) },
            modifier = Modifier.fillMaxWidth().padding(20.dp),
            singleLine = true,
        )
        LazyColumn(Modifier.heightIn(max = 500.dp)) {
            items(
                state.models.filter { it.id.contains(query, ignoreCase = true) },
                key = { it.id },
            ) { item ->
                ListItem(
                    content = { Text(item.name) },
                    supportingContent = { if (item.name != item.id) Text(item.id) },
                    modifier =
                        Modifier.clickable {
                            model.selectModel(item.id)
                            onDismiss()
                        },
                )
            }
            if (query.isNotBlank() && state.models.none { it.id == query })
                item {
                    ListItem(
                        content = { Text(query) },
                        modifier =
                            Modifier.clickable {
                                model.selectModel(query)
                                onDismiss()
                            },
                    )
                }
        }
        Spacer(Modifier.navigationBarsPadding())
    }
}
