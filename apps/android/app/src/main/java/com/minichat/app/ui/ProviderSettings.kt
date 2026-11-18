package com.minichat.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.minichat.app.R
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

@Composable
internal fun ProvidersScreen(state: ChatState, model: ChatViewModel, modifier: Modifier) {
    var editor by remember { mutableStateOf<ProviderConfig?>(null) }
    LazyColumn(modifier, contentPadding = PaddingValues(horizontal = 16.dp)) {
        items(state.providers, key = { it.id }) { provider ->
            ListItem(
                content = { Text(provider.name) },
                supportingContent = {
                    Text(provider.url, maxLines = 1, overflow = TextOverflow.Ellipsis)
                },
                trailingContent = { Symbol("chevron_right") },
                modifier = Modifier.clickable { editor = provider },
            )
        }
        item {
            TextButton(onClick = { editor = ProviderConfig() }) {
                Symbol("add")
                Text(stringResource(R.string.add_provider))
            }
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
            onDelete =
                if (state.providers.any { it.id == provider.id })
                    ({
                        model.deleteProvider(provider.id)
                        editor = null
                    })
                else null,
            onDismiss = { editor = null },
        )
    }
}

@Composable
private fun ProviderEditor(
    initial: ProviderConfig,
    templates: List<ProviderConfig>,
    onSave: (ProviderConfig) -> Unit,
    onDelete: (() -> Unit)?,
    onDismiss: () -> Unit,
) {
    var provider by remember(initial.id) { mutableStateOf(initial) }
    var showKey by remember { mutableStateOf(false) }
    var delete by remember { mutableStateOf(false) }
    val protocols =
        mapOf(
            "openai" to "OpenAI Chat Completions",
            "responses" to "OpenAI Responses",
            "anthropic" to "Anthropic Messages",
            "gemini" to "Gemini",
            "ollama" to "Ollama",
        )
    FullSheet(
        stringResource(if (onDelete == null) R.string.add_provider else R.string.edit_provider),
        onDismiss,
    ) {
        LazyColumn(
            Modifier.weight(1f),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                var showTemplates by remember { mutableStateOf(false) }
                Box {
                    OutlinedButton(onClick = { showTemplates = true }) {
                        Text(stringResource(R.string.provider_type))
                        Symbol("expand_more", size = 18)
                    }
                    DropdownMenu(
                        showTemplates,
                        { showTemplates = false },
                        Modifier.heightIn(max = 360.dp),
                    ) {
                        templates.forEach { template ->
                            DropdownMenuItem(
                                text = {
                                    Text(
                                        if (template.url.isBlank())
                                            stringResource(R.string.custom_provider)
                                        else template.name
                                    )
                                },
                                onClick = {
                                    provider =
                                        provider.copy(
                                            name = template.name,
                                            protocol = template.protocol,
                                            url = template.url,
                                        )
                                    showTemplates = false
                                },
                            )
                        }
                    }
                }
            }
            item {
                OutlinedTextField(
                    provider.name,
                    { provider = provider.copy(name = it) },
                    Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.provider_name)) },
                    singleLine = true,
                )
            }
            item {
                var showProtocols by remember { mutableStateOf(false) }
                Box {
                    OutlinedButton(onClick = { showProtocols = true }) {
                        Text(
                            stringResource(R.string.protocol) +
                                " · " +
                                protocols.getValue(provider.protocol)
                        )
                        Symbol("expand_more", size = 18)
                    }
                    DropdownMenu(showProtocols, { showProtocols = false }) {
                        protocols.forEach { (id, name) ->
                            DropdownMenuItem(
                                text = { Text(name) },
                                onClick = {
                                    provider = provider.copy(protocol = id)
                                    showProtocols = false
                                },
                            )
                        }
                    }
                }
            }
            item {
                OutlinedTextField(
                    provider.url,
                    { provider = provider.copy(url = it) },
                    Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.base_url)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                )
            }
            item {
                OutlinedTextField(
                    provider.key,
                    { provider = provider.copy(key = it) },
                    Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.api_key)) },
                    singleLine = true,
                    visualTransformation =
                        if (showKey) VisualTransformation.None else PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    trailingIcon = {
                        IconButton(onClick = { showKey = !showKey }) {
                            Symbol(
                                if (showKey) "visibility_off" else "visibility",
                                stringResource(
                                    if (showKey) R.string.hide_key else R.string.show_key
                                ),
                            )
                        }
                    },
                )
            }
            item {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    if (onDelete != null)
                        TextButton(onClick = { delete = true }) {
                            Text(stringResource(R.string.delete_provider))
                        }
                    Spacer(Modifier.weight(1f))
                    Button(
                        enabled =
                            provider.name.isNotBlank() &&
                                provider.url.trim().toHttpUrlOrNull() != null,
                        onClick = {
                            onSave(
                                provider.copy(
                                    name = provider.name.trim(),
                                    url = provider.url.trim().trimEnd('/'),
                                    key = provider.key.trim(),
                                )
                            )
                        },
                    ) {
                        Text(stringResource(R.string.save))
                    }
                }
            }
        }
    }
    if (delete)
        AlertDialog(
            onDismissRequest = { delete = false },
            text = { Text(stringResource(R.string.delete_provider_question, initial.name)) },
            dismissButton = {
                TextButton(onClick = { delete = false }) { Text(stringResource(R.string.cancel)) }
            },
            confirmButton = {
                TextButton(onClick = { onDelete?.invoke() }) {
                    Text(stringResource(R.string.delete))
                }
            },
        )
}
