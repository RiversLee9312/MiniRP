package com.minirp.app.ui

import android.app.NotificationManager
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import androidx.lifecycle.compose.LifecycleResumeEffect
import com.minirp.app.R
import com.minirp.app.core.TurnProtection
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
internal fun DeclaredSettingsScreen(
    items: List<PluginSetting>,
    state: ChatState,
    model: ChatViewModel,
    modifier: Modifier = Modifier,
    onPage: (AppRoute.PluginPage) -> Unit,
    onLink: (PluginSetting) -> Unit,
) {
    LazyColumn(modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp)) {
        declaredSettingsRows(items, state, model, onPage, onLink)
    }
}

internal fun LazyListScope.declaredSettingsRows(
    items: List<PluginSetting>,
    state: ChatState,
    model: ChatViewModel,
    onPage: (AppRoute.PluginPage) -> Unit,
    onLink: (PluginSetting) -> Unit,
    nested: Boolean = false,
) {
    for (setting in items) {
        if (setting.plugin == "base" && setting.binding == "theme") continue
        if (setting.type == "group") {
            item(setting.listKey, contentType = setting.type) {
                if (!nested) HorizontalDivider(Modifier.padding(vertical = 8.dp))
                Row(
                    Modifier.padding(start = 16.dp, top = 8.dp, bottom = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    SettingIcon(setting, state, model)
                    Text(SettingTitle(setting), style = MaterialTheme.typography.titleSmall,
                        color = MaterialTheme.colorScheme.primary)
                }
            }
            declaredSettingsRows(setting.children, state, model, onPage, onLink, nested = true)
        } else {
            item(setting.listKey, contentType = setting.type) {
                SettingItem(setting, state, model, onPage, onLink)
            }
        }
    }
}

@Composable
internal fun BackgroundTurnsSettingsScreen(
    enabled: Boolean,
    onChange: ((Boolean) -> Unit)?,
) {
    val context = LocalContext.current
    var notificationsEnabled by remember { mutableStateOf(true) }
    LifecycleResumeEffect(context) {
        notificationsEnabled = context.getSystemService(NotificationManager::class.java).areNotificationsEnabled()
        onPauseOrDispose { }
    }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp)) {
        item("background-turns") {
            ListItem(
                content = { Text(stringResource(R.string.background_turns)) },
                leadingContent = { Symbol("schedule") },
                supportingContent = { Text(stringResource(R.string.background_turns_description)) },
                trailingContent = { Switch(enabled, onCheckedChange = null, enabled = onChange != null) },
                modifier = Modifier.toggleable(
                    value = enabled,
                    enabled = onChange != null,
                    role = Role.Switch,
                    onValueChange = { onChange?.invoke(it) },
                ),
            )
        }
        if (enabled) item("background-system-settings") { BackgroundTurnSystemSettings() }
        if (!notificationsEnabled) item("background-notifications") {
            BackgroundTurnSettingLink(
                R.string.background_turn_notifications,
                R.string.background_turn_notifications_description,
                "notifications",
            ) {
                context.openTurnSettings(context.notificationSettings())
            }
        }
    }
}

@Composable
private fun BackgroundTurnSystemSettings() {
    val context = LocalContext.current
    var batteryExempt by remember { mutableStateOf(false) }
    var promoted by remember { mutableStateOf(false) }
    LifecycleResumeEffect(context) {
        batteryExempt = context.getSystemService(PowerManager::class.java)
            .isIgnoringBatteryOptimizations(context.packageName)
        promoted = Build.VERSION.SDK_INT >= 36 &&
            context.getSystemService(NotificationManager::class.java).canPostPromotedNotifications()
        onPauseOrDispose { }
    }
    BackgroundTurnSettingLink(
        R.string.background_turn_battery,
        if (batteryExempt) R.string.background_turn_battery_exempt else R.string.background_turn_battery_description,
        "battery_saver",
    ) {
        context.openTurnSettings(
            Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
                .putExtra(Intent.EXTRA_PACKAGE_NAME, context.packageName),
        )
    }
    BackgroundTurnSettingLink(R.string.background_turn_app_info, R.string.background_turn_app_info_description, "info") {
        context.openTurnSettings(context.appDetails())
    }
    if (Build.VERSION.SDK_INT >= 36) {
        BackgroundTurnSettingLink(
            R.string.background_turn_live_updates,
            if (promoted) R.string.background_turn_live_updates_on else R.string.background_turn_live_updates_description,
            "notifications",
        ) {
            context.openTurnSettings(
                context.notificationSettings(Settings.ACTION_APP_NOTIFICATION_PROMOTION_SETTINGS),
                context.notificationSettings(),
            )
        }
    }
}

@Composable
private fun BackgroundTurnSettingLink(title: Int, description: Int, icon: String, onClick: () -> Unit) {
    ListItem(
        content = { Text(stringResource(title)) },
        leadingContent = { Symbol(icon) },
        supportingContent = { Text(stringResource(description)) },
        trailingContent = { Symbol("chevron_right") },
        modifier = Modifier.clickable(onClick = onClick),
    )
}

private fun Context.notificationSettings(action: String = Settings.ACTION_APP_NOTIFICATION_SETTINGS) =
    Intent(action).putExtra(Settings.EXTRA_APP_PACKAGE, packageName)

private fun Context.appDetails() =
    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null))

private fun Context.openTurnSettings(intent: Intent, fallback: Intent = appDetails()) {
    try {
        startActivity(intent)
    } catch (_: ActivityNotFoundException) {
        try {
            startActivity(fallback)
        } catch (_: ActivityNotFoundException) {
            startActivity(Intent(Settings.ACTION_SETTINGS))
        }
    }
}

@Composable
internal fun BackgroundTurnsPrompt(prompt: TurnProtection.Prompt, onChoice: (Boolean) -> Unit) {
    AlertDialog(
        onDismissRequest = { onChoice(false) },
        title = { Text(stringResource(R.string.background_turns_prompt_title)) },
        text = {
            Text(
                stringResource(
                    if (prompt == TurnProtection.Prompt.Interrupted) R.string.background_turns_interrupted
                    else R.string.background_turns_description
                )
            )
        },
        dismissButton = {
            TextButton(onClick = { onChoice(false) }) { Text(stringResource(R.string.background_turns_later)) }
        },
        confirmButton = {
            Button(onClick = { onChoice(true) }) { Text(stringResource(R.string.background_turns_enable)) }
        },
    )
}

@Composable
private fun SettingItem(
    setting: PluginSetting,
    state: ChatState,
    model: ChatViewModel,
    open: (AppRoute.PluginPage) -> Unit,
    onLink: (PluginSetting) -> Unit,
) {
    val title = SettingTitle(setting)
    val leading: (@Composable () -> Unit)? =
        if (setting.icon.isBlank()) null else ({ SettingIcon(setting, state, model) })
    val value =
        when (setting.binding) {
            "thoughts" -> state.expandThoughts
            "fetch" -> state.fetchMode
            else -> setting.value
        }
    when (setting.type) {
        "markdown" -> {
            val inline = setting.localizedMarkdown.ifBlank { setting.markdown }
            val asset = setting.assetUrl.takeIf { setting.localizedMarkdown.isBlank() }.orEmpty()
            val content by
                produceState(inline, asset, inline) {
                    if (asset.isNotBlank()) {
                        try {
                            this.value = model.settingMarkdown(asset)
                        } catch (cancelled: CancellationException) {
                            throw cancelled
                        } catch (failure: Exception) {
                            model.pluginFailure(failure)
                        }
                    }
                }
            if (content.isNotEmpty()) Row(
                Modifier.padding(12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                leading?.invoke()
                Box(Modifier.weight(1f)) { MessageMarkdown(content) }
            }
        }
        "page",
        "link" ->
            ListItem(
                content = { Text(title) },
                leadingContent = leading,
                trailingContent = { Symbol("chevron_right") },
                modifier =
                    Modifier.clickable {
                        if (setting.type == "page")
                            open(AppRoute.PluginPage(setting.plugin, setting.key))
                        else onLink(setting)
                    },
            )
        "boolean" ->
            ListItem(
                content = { Text(title) },
                leadingContent = leading,
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
                    leadingContent = leading,
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
            val saved = value as? String ?: ""
            ListItem(
                content = { Text(title) },
                leadingContent = leading,
                supportingContent = {
                    if (saved.isNotBlank())
                        Text(saved, maxLines = 2, overflow = TextOverflow.Ellipsis)
                },
                trailingContent = { Symbol("chevron_right") },
                modifier = Modifier.clickable { editing = true },
            )
            if (editing) {
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
private fun SettingIcon(setting: PluginSetting, state: ChatState, model: ChatViewModel) {
    if (setting.icon.isBlank()) return
    if (setting.plugin == "base") {
        Symbol(setting.icon)
        return
    }
    if (!isDisplayAssetUrl(setting.icon)) return
    val generation = state.installedPlugins.firstOrNull { it.id == setting.plugin }?.generation
    val bitmap by produceState<Bitmap?>(null, setting.icon, generation, state.preset) {
        value = null
        try {
            val data = model.settingIcon(setting.icon)
            value = withContext(Dispatchers.Default) {
                val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeByteArray(data, 0, data.size, options)
                options.inJustDecodeBounds = false
                while (maxOf(options.outWidth, options.outHeight) / options.inSampleSize.coerceAtLeast(1) > 128) {
                    options.inSampleSize = options.inSampleSize.coerceAtLeast(1) * 2
                }
                BitmapFactory.decodeByteArray(data, 0, data.size, options)
            }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (_: Exception) {
            // A dynamic or missing image does not prevent the setting from being used.
        }
    }
    val image = bitmap
    if (image == null) Symbol("extension")
    else Image(image.asImageBitmap(), contentDescription = null, modifier = Modifier.size(24.dp))
}

@Composable
internal fun SettingTitle(setting: PluginSetting): String =
    setting.localizedTitle.ifBlank { localTitle(setting.label, setting.localized(LocalConfiguration.current.locales[0])) }

@Composable
private fun SettingTitle(option: SettingOption): String =
    option.localizedTitle.ifBlank { localTitle(option.label, option.title) }

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
            "providers" to { navigate(AppRoute.Providers) },
            "models" to { navigate(AppRoute.Models) },
            "research-services" to { navigate(AppRoute.Research) },
            "background-turns" to { navigate(AppRoute.BackgroundTurns) },
            "plugins" to { navigate(AppRoute.Plugins) },
            "language" to
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

    fun open(url: String): Boolean {
        val prefix =
            when {
                url.startsWith("minirp://actions/minirp.app/hooks/") ->
                    "minirp://actions/minirp.app/hooks/"
                url.startsWith("minichat://actions/minichat.app/hooks/") ->
                    "minichat://actions/minichat.app/hooks/"
                else -> return false
            }
        val handler = handlers[url.removePrefix(prefix)] ?: return false
        handler()
        return true
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
