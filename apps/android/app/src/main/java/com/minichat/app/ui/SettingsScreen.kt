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
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import com.minichat.app.R

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun SettingsScreen(
    state: ChatState,
    model: ChatViewModel,
    modifier: Modifier = Modifier,
    onPage: (String) -> Unit,
) {
    var prompt by remember { mutableStateOf(false) }
    var language by remember { mutableStateOf(false) }
    val context = LocalContext.current
    LazyColumn(
        modifier.fillMaxSize(),
        contentPadding = PaddingValues(20.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        items(listOf("providers" to R.string.providers, "models" to R.string.models)) {
            (page, label) ->
            ListItem(
                content = { Text(stringResource(label)) },
                trailingContent = { Symbol("chevron_right") },
                modifier = Modifier.clickable { onPage(page) },
            )
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
