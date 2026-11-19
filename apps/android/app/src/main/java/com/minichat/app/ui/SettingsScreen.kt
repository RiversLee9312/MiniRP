package com.minichat.app.ui

import android.content.Context
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
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import com.minichat.app.R
import kotlinx.coroutines.CancellationException

@Composable
internal fun DeclaredSettingsScreen(
    items: List<PluginSetting>,
    state: ChatState,
    model: ChatViewModel,
    modifier: Modifier = Modifier,
    onPage: (AppRoute.PluginPage) -> Unit,
) {
    LazyColumn(modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp)) {
        fun androidx.compose.foundation.lazy.LazyListScope.rows(
            nodes: List<PluginSetting>,
            nested: Boolean,
        ) {
            nodes.forEach { setting ->
                if (setting.type == "group") {
                    item(setting.listKey, contentType = setting.type) {
                        if (!nested) HorizontalDivider(Modifier.padding(vertical = 8.dp))
                        Text(
                            SettingTitle(setting),
                            Modifier.padding(start = 16.dp, top = 8.dp, bottom = 6.dp),
                            style = MaterialTheme.typography.titleSmall,
                            color = MaterialTheme.colorScheme.primary,
                        )
                    }
                    rows(setting.children, true)
                } else
                    item(setting.listKey, contentType = setting.type) {
                        SettingItem(setting, state, model, onPage)
                    }
            }
        }
        rows(items, false)
    }
}

@Composable
private fun SettingItem(
    setting: PluginSetting,
    state: ChatState,
    model: ChatViewModel,
    open: (AppRoute.PluginPage) -> Unit,
) {
    val title = SettingTitle(setting)
    val value =
        when (setting.binding) {
            "thoughts" -> state.expandThoughts
            "fetch" -> state.fetchMode
            else ->
                if (setting.binding.startsWith("prompt/"))
                    state.agentPrompts[setting.binding.substringAfter('/')].orEmpty()
                else setting.value
        }
    when (setting.type) {
        "markdown" -> {
            val content by
                produceState(setting.markdown, setting.assetUrl, setting.markdown) {
                    if (setting.assetUrl.isNotBlank()) {
                        try {
                            this.value = model.settingMarkdown(setting.assetUrl)
                        } catch (cancelled: CancellationException) {
                            throw cancelled
                        } catch (failure: Exception) {
                            model.pluginFailure(failure)
                        }
                    }
                }
            if (content.isNotEmpty()) Box(Modifier.padding(12.dp)) { MessageMarkdown(content) }
        }
        "page",
        "link" ->
            ListItem(
                content = { Text(title) },
                trailingContent = { Symbol("chevron_right") },
                modifier =
                    Modifier.clickable {
                        if (setting.type == "page")
                            open(AppRoute.PluginPage(setting.plugin, setting.key))
                        else model.openSettingLink(setting)
                    },
            )
        "boolean" ->
            ListItem(
                content = { Text(title) },
                trailingContent = {
                    Switch(value == true, { model.updateSetting(setting, it) })
                },
            )
        "choice" -> {
            var choosing by remember { mutableStateOf(false) }
            val active = setting.options.firstOrNull { it.value == value }
            val selected = if (active == null) "" else SettingTitle(active)
            Box {
                ListItem(
                    content = { Text(title) },
                    supportingContent = { if (selected.isNotEmpty()) Text(selected) },
                    trailingContent = { Symbol("expand_more") },
                    modifier = Modifier.clickable { choosing = true },
                )
                DropdownMenu(choosing, { choosing = false }) {
                    setting.options.forEach { option ->
                        DropdownMenuItem(
                            text = { Text(SettingTitle(option)) },
                            onClick = {
                                choosing = false
                                model.updateSetting(setting, option.value)
                            },
                        )
                    }
                }
            }
        }
        "text" -> {
            var editing by rememberSaveable { mutableStateOf(false) }
            val enabled = !setting.binding.startsWith("prompt/") || state.preset.isNotBlank()
            val saved = value as? String ?: ""
            ListItem(
                content = { Text(title) },
                supportingContent = {
                    if (saved.isNotBlank())
                        Text(saved, maxLines = 2, overflow = TextOverflow.Ellipsis)
                },
                trailingContent = { Symbol("chevron_right") },
                modifier = Modifier.clickable(enabled = enabled) { editing = true },
            )
            if (editing && enabled) {
                var draft by rememberSaveable { mutableStateOf(saved) }
                AlertDialog(
                    onDismissRequest = { editing = false },
                    title = { Text(title) },
                    text = {
                        OutlinedTextField(
                            draft,
                            { draft = it.take(16000) },
                            Modifier.fillMaxWidth(),
                            singleLine = !setting.multiline,
                            minLines = if (setting.multiline) 5 else 1,
                            maxLines = if (setting.multiline) 12 else 1,
                        )
                    },
                    dismissButton = {
                        TextButton(onClick = { editing = false }) {
                            Text(stringResource(R.string.cancel))
                        }
                    },
                    confirmButton = {
                        Button(
                            onClick = {
                                model.updateSetting(setting, draft)
                                editing = false
                            }
                        ) {
                            Text(stringResource(R.string.save))
                        }
                    },
                )
            }
        }
    }
}

@Composable
internal fun SettingTitle(setting: PluginSetting): String =
    localTitle(setting.label, setting.localized(LocalConfiguration.current.locales[0]))

@Composable
private fun SettingTitle(option: SettingOption): String = localTitle(option.label, option.title)

@Composable
private fun localTitle(label: String, fallback: String): String {
    val context = LocalContext.current
    val res =
        remember(label) {
            if (label.isBlank()) 0
            else context.resources.getIdentifier(label, "string", context.packageName)
        }
    return if (res == 0) fallback else stringResource(res)
}

internal class ActionsRouter(
    context: Context,
    navigate: (AppRoute) -> Unit,
    fallbackLanguage: () -> Unit,
) {
    private val handlers: Map<String, () -> Unit> =
        mapOf(
            "minichat://actions/minichat.app/hooks/providers" to { navigate(AppRoute.Providers) },
            "minichat://actions/minichat.app/hooks/models" to { navigate(AppRoute.Models) },
            "minichat://actions/minichat.app/hooks/plugins" to { navigate(AppRoute.Plugins) },
            "minichat://actions/minichat.app/hooks/language" to
                {
                    if (Build.VERSION.SDK_INT >= 33)
                        context.startActivity(
                            Intent(
                                Settings.ACTION_APP_LOCALE_SETTINGS,
                                Uri.parse("package:${context.packageName}"),
                            )
                        )
                    else fallbackLanguage()
                },
        )

    fun open(url: String) {
        handlers[url]?.invoke()
    }
}

@Composable
internal fun LegacyLanguageSheet(close: () -> Unit) {
    FullSheet(stringResource(R.string.language), close) {
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
                            else java.util.Locale.forLanguageTag(tag).let { it.getDisplayName(it) }
                        )
                    },
                    modifier =
                        Modifier.clickable {
                            AppCompatDelegate.setApplicationLocales(
                                LocaleListCompat.forLanguageTags(tag)
                            )
                            close()
                        },
                )
            }
        }
    }
}
