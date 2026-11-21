package com.minirp.app.ui

import android.content.Context
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.paging.compose.collectAsLazyPagingItems
import androidx.paging.compose.itemKey
import com.minirp.app.R
import com.minirp.app.data.AttachmentStore
import java.io.File
import java.util.UUID
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

data class SettingOption(val value: String, val title: String, val label: String)

data class PluginSetting(
    val plugin: String,
    val key: String,
    val title: String,
    val titles: JSONObject?,
    val value: Any?,
    val type: String,
    val markdown: String,
    val label: String,
    val binding: String,
    val multiline: Boolean,
    val options: List<SettingOption>,
    val children: List<PluginSetting>,
    val assetUrl: String = "",
    val url: String = "",
) {
    val listKey
        get() = "$plugin/$key"

    fun localized(locale: java.util.Locale): String =
        titles
            ?.let {
                it.optString(locale.toLanguageTag()).ifBlank {
                    val chinese =
                        if (locale.country in setOf("HK", "TW", "MO")) "zh-Hant" else "zh-Hans"
                    if (locale.language == "zh") it.optString(chinese)
                    else it.optString(locale.language)
                }
            }
            .orEmpty()
            .ifBlank { title }
}

data class PluginAttachmentType(
    val plugin: String,
    val id: String,
    val name: String,
    val mimeTypes: List<String>,
) {
    val key
        get() = "$plugin/$id"
}

data class PluginInfo(
    val id: String,
    val name: String,
    val builtIn: Boolean,
    val command: Boolean,
    val permissions: List<String>,
    val network: List<String>,
    val attachments: List<PluginAttachmentType>,
    val packageName: String = "",
    val signer: String = "",
    val scope: PluginScope = PluginScope(),
    val presetId: String = "",
    val generation: String = "",
    val enabled: Boolean = true,
) {
    fun availableIn(preset: String): Boolean = enabled && scope.allows(preset)

    val status: Int
        get() = when {
            builtIn -> R.string.built_in
            enabled -> R.string.plugin_enabled
            else -> R.string.plugin_disabled
        }
}

enum class PluginScopeMode(val id: String, val label: Int) {
    Global("global", R.string.plugin_scope_global),
    Include("include", R.string.plugin_scope_include),
    Exclude("exclude", R.string.plugin_scope_exclude),
}

data class PluginScope(
    val mode: PluginScopeMode = PluginScopeMode.Global,
    val presets: Set<String> = emptySet(),
) {
    fun allows(preset: String): Boolean =
        when (mode) {
            PluginScopeMode.Global -> true
            PluginScopeMode.Include -> preset in presets
            PluginScopeMode.Exclude -> preset !in presets
        }

    internal fun encode(): JSONObject =
        JSONObject().put("mode", mode.id).put("presets", JSONArray(presets.sorted()))
}

private fun JSONObject.strings(key: String) =
    optJSONArray(key)?.let { list -> List(list.length()) { list.getString(it) } }.orEmpty()

internal fun decodePluginSetting(value: JSONObject): PluginSetting =
    PluginSetting(
        value.getString("plugin"),
        value.getString("key"),
        value.optString("title"),
        value.optJSONObject("titles"),
        value.opt("value"),
        value.optString("type"),
        value.optString("markdown"),
        value.optString("label"),
        value.optString("binding"),
        value.optBoolean("multiline"),
        value
            .optJSONArray("options")
            ?.objects()
            ?.map {
                SettingOption(it.getString("value"), it.getString("title"), it.optString("label"))
            }
            .orEmpty(),
        value.optJSONArray("items")?.objects()?.map { decodePluginSetting(it) }.orEmpty(),
        assetUrl = value.optString("assetUrl"),
        url = value.optString("url"),
    )

internal fun decodePlugin(value: JSONObject): PluginInfo {
    val id = value.getString("id")
    val scope = value.optJSONObject("scope")
    return PluginInfo(
        id,
        value.getString("name"),
        value.optBoolean("builtIn"),
        value.optBoolean("command"),
        value.strings("permissions"),
        value.strings("network"),
        value
            .optJSONArray("attachments")
            ?.objects()
            ?.map {
                PluginAttachmentType(
                    id,
                    it.getString("id"),
                    it.getString("name"),
                    it.strings("mimeTypes"),
                )
            }
            .orEmpty(),
        packageName = value.optString("packageName"),
        signer = value.optString("signer"),
        presetId = value.optString("presetId"),
        generation = value.optString("generation"),
        enabled = value.optBoolean("enabled", true),
        scope =
            PluginScope(
                PluginScopeMode.entries.firstOrNull { it.id == scope?.optString("mode") }
                    ?: PluginScopeMode.Global,
                scope?.strings("presets").orEmpty().toSet(),
            ),
    )
}

internal class PluginPicker {
    data class Request(val event: JSONObject, val answer: CompletableDeferred<JSONObject>)

    val pending = MutableStateFlow<Request?>(null)

    suspend fun request(event: JSONObject): JSONObject {
        val request = Request(event, CompletableDeferred())
        check(pending.compareAndSet(null, request)) { "A picker is already in use." }
        return try {
            request.answer.await()
        } finally {
            pending.compareAndSet(request, null)
        }
    }
}

@Composable
internal fun PluginPickerHost(picker: PluginPicker) {
    val request by picker.pending.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    fun complete(uris: List<android.net.Uri>) {
        val active = request ?: return
        scope.launch {
            try {
                val items = AttachmentStore(context).import(uris.map { it.toString() })
                active.answer.complete(JSONObject().put("files", items))
            } catch (failure: Exception) {
                active.answer.completeExceptionally(failure)
            }
        }
    }
    val file =
        rememberLauncherForActivityResult(
            ActivityResultContracts.OpenMultipleDocuments(),
            ::complete,
        )
    val media =
        rememberLauncherForActivityResult(
            ActivityResultContracts.PickMultipleVisualMedia(20),
            ::complete,
        )
    var approved by remember { mutableStateOf(false) }
    LaunchedEffect(request) { approved = false }
    val active = request ?: return
    if (!approved)
        AlertDialog(
            onDismissRequest = { active.answer.complete(JSONObject().put("files", JSONArray())) },
            title = {
                Text(
                    stringResource(R.string.plugin_resource_request, active.event.getString("name"))
                )
            },
            dismissButton = {
                TextButton(
                    onClick = { active.answer.complete(JSONObject().put("files", JSONArray())) }
                ) {
                    Text(stringResource(R.string.cancel))
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        approved = true
                        val args = active.event.optJSONObject("arguments") ?: JSONObject()
                        if (active.event.optString("picker") == "media") {
                            val type =
                                when (args.optString("type")) {
                                    "image" -> ActivityResultContracts.PickVisualMedia.ImageOnly
                                    "video" -> ActivityResultContracts.PickVisualMedia.VideoOnly
                                    else -> ActivityResultContracts.PickVisualMedia.ImageAndVideo
                                }
                            media.launch(PickVisualMediaRequest(type))
                        } else
                            file.launch(
                                args.strings("mimeTypes").take(16).toTypedArray().ifEmpty {
                                    arrayOf("*/*")
                                }
                            )
                    }
                ) {
                    Text(stringResource(R.string.choose_files))
                }
            },
        )
}

@Composable
internal fun PluginsScreen(
    state: ChatState,
    model: ChatViewModel,
    modifier: Modifier,
    onOpen: (PluginInfo) -> Unit,
) {
    val context = LocalContext.current
    var candidate by remember { mutableStateOf<Pair<File, PluginInfo>?>(null) }
    val scope = rememberCoroutineScope()
    val installer =
        rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
            if (uri != null)
                scope.launch {
                    var file: File? = null
                    try {
                        file = stagePlugin(context, uri)
                        candidate = file to model.inspectPlugin(file)
                    } catch (failure: Exception) {
                        file?.delete()
                        model.pluginFailure(failure)
                    }
                }
        }
    LazyColumn(modifier, contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        item(key = "install", contentType = "action") {
            FilledTonalButton(onClick = { installer.launch(arrayOf("*/*")) }) {
                Symbol("add", size = 18)
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.install_plugin))
            }
        }
        items(state.installedPlugins, key = { it.id }, contentType = { "plugin" }) { plugin ->
            Surface(shape = MaterialTheme.shapes.large, color = MaterialTheme.colorScheme.surfaceContainerLow) {
                ListItem(
                    content = { Text(plugin.name, maxLines = 2, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text(stringResource(plugin.status)) },
                    leadingContent = { Symbol("extension") },
                    trailingContent = { Symbol("chevron_right") },
                    modifier = Modifier.testTag("plugin:${plugin.id}").clickable(role = Role.Button) { onOpen(plugin) },
                    colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
                )
            }
        }
    }
    candidate?.let { (file, plugin) ->
        fun discard() {
            candidate = null
            file.delete()
        }
        AlertDialog(
            onDismissRequest = ::discard,
            title = { Text(plugin.name) },
            text = {
                Text(
                    (listOf(plugin.packageName, plugin.signer) +
                            plugin.permissions +
                            plugin.network)
                        .filter(String::isNotBlank)
                        .joinToString("\n")
                )
            },
            dismissButton = {
                TextButton(onClick = ::discard) { Text(stringResource(R.string.cancel)) }
            },
            confirmButton = {
                Button(
                    onClick = {
                        candidate = null
                        scope.launch {
                            try {
                                model.installPlugin(file)
                            } catch (failure: Exception) {
                                model.pluginFailure(failure)
                            } finally {
                                file.delete()
                            }
                        }
                    }
                ) {
                    Text(stringResource(R.string.install_plugin))
                }
            },
        )
    }
}

@Composable
internal fun PluginDetailsScreen(
    plugin: PluginInfo,
    state: ChatState,
    model: ChatViewModel,
    onExport: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var editingScope by rememberSaveable(plugin.id) { mutableStateOf(false) }
    var confirmingRemoval by rememberSaveable(plugin.id) { mutableStateOf(false) }
    var updating by remember(plugin.id) { mutableStateOf(false) }
    fun update(operation: () -> Job) {
        if (updating) return
        updating = true
        val pending = operation()
        scope.launch {
            try {
                pending.join()
            } finally {
                updating = false
            }
        }
    }
    val enabled = !updating
    val colors = MaterialTheme.colorScheme
    val canRun = plugin.command && plugin.availableIn(state.preset)
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        item(key = "identity") {
            Surface(shape = MaterialTheme.shapes.extraLarge, color = colors.surfaceContainer) {
                ListItem(
                    content = { Text(stringResource(plugin.status)) },
                    supportingContent = { Text(plugin.packageName.ifBlank { plugin.id }) },
                    leadingContent = { Symbol("extension", size = 28) },
                    trailingContent = if (plugin.builtIn) null else {
                        { Switch(checked = plugin.enabled, onCheckedChange = null, enabled = enabled) }
                    },
                    modifier = if (plugin.builtIn) Modifier else Modifier.testTag("plugin-enabled:${plugin.id}")
                        .toggleable(plugin.enabled, enabled = enabled, role = Role.Switch) { next ->
                            update { model.setPluginEnabled(plugin.id, next) }
                        },
                    colors = ListItemDefaults.colors(containerColor = colors.surfaceContainer),
                )
            }
        }
        if (canRun || !plugin.builtIn) {
            item(key = "actions") {
                Surface(shape = MaterialTheme.shapes.large, color = colors.surfaceContainerLow) {
                    Column {
                        if (canRun) {
                            PluginDetailAction(R.string.run_plugin, "play_arrow", enabled) { model.runPlugin(plugin.id) }
                        }
                        if (!plugin.builtIn) {
                            if (canRun) HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                            PluginDetailAction(R.string.plugin_scope, "tune", enabled, support = plugin.scope.mode.label) {
                                editingScope = true
                            }
                            HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                            PluginDetailAction(
                                R.string.export_archive, "download", enabled && !state.archiveBusy,
                                Modifier.testTag("plugin-export:${plugin.id}"),
                                onClick = onExport,
                            )
                        }
                    }
                }
            }
        }
        if (!plugin.builtIn) {
            item(key = "uninstall") {
                OutlinedButton(
                    onClick = { confirmingRemoval = true }, enabled = enabled && !state.archiveBusy,
                    modifier = Modifier.fillMaxWidth().testTag("plugin-uninstall:${plugin.id}"),
                    colors = ButtonDefaults.outlinedButtonColors(contentColor = colors.error),
                ) {
                    Symbol("delete", size = 18)
                    Spacer(Modifier.width(8.dp))
                    Text(stringResource(R.string.remove_plugin))
                }
            }
        }
    }
    if (editingScope) PluginScopeSheet(plugin, model, onDismiss = { editingScope = false })
    if (confirmingRemoval) {
        val dismiss = { confirmingRemoval = false }
        AlertDialog(
            onDismissRequest = dismiss,
            title = { Text(stringResource(R.string.uninstall_plugin_title, plugin.name)) },
            dismissButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.cancel)) } },
            confirmButton = {
                Button(
                    onClick = {
                        dismiss()
                        update { model.removePlugin(plugin.id) }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = colors.error, contentColor = colors.onError),
                ) { Text(stringResource(R.string.remove_plugin)) }
            },
        )
    }
}

@Composable
private fun PluginDetailAction(
    label: Int, icon: String, enabled: Boolean, modifier: Modifier = Modifier, support: Int? = null,
    onClick: () -> Unit,
) {
    val colors = MaterialTheme.colorScheme
    ListItem(
        content = { Text(stringResource(label)) },
        leadingContent = { Symbol(icon) },
        supportingContent = support?.let { { Text(stringResource(it)) } },
        trailingContent = if (support == null) null else { { Symbol("chevron_right") } },
        modifier = modifier.clickable(enabled = enabled, role = Role.Button, onClick = onClick),
        colors = ListItemDefaults.colors(containerColor = colors.surfaceContainerLow),
        enabled = enabled,
    )
}

@Composable
private fun PluginScopeSheet(plugin: PluginInfo, model: ChatViewModel, onDismiss: () -> Unit) {
    val presets = model.presets.collectAsLazyPagingItems()
    var mode by rememberSaveable(plugin.id) { mutableStateOf(plugin.scope.mode) }
    var checked by rememberSaveable(plugin.id) { mutableStateOf(ArrayList(plugin.scope.presets)) }
    var saving by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    FullSheet(stringResource(R.string.plugin_scope), onDismiss) {
        LazyColumn(Modifier.weight(1f).fillMaxWidth()) {
            item(key = "mode", contentType = "mode") {
                Column(Modifier.selectableGroup()) {
                    PluginScopeMode.entries.forEach { option ->
                        ListItem(
                            content = { Text(stringResource(option.label)) },
                            leadingContent = {
                                RadioButton(selected = mode == option, onClick = null)
                            },
                            modifier =
                                Modifier.selectable(
                                    selected = mode == option,
                                    role = Role.RadioButton,
                                    enabled = !saving,
                                    onClick = { mode = option },
                                ),
                        )
                    }
                }
            }
            if (mode != PluginScopeMode.Global) {
                items(
                    presets.itemCount,
                    key = presets.itemKey { "preset:${it.id}" },
                    contentType = { "preset" },
                ) { index ->
                    val preset = presets[index] ?: return@items
                    val selected = preset.id in checked
                    ListItem(
                        content = { Text(preset.name) },
                        leadingContent = { Checkbox(checked = selected, onCheckedChange = null) },
                        modifier =
                            Modifier.toggleable(
                                value = selected,
                                role = Role.Checkbox,
                                enabled = !saving,
                            ) { value ->
                                checked =
                                    ArrayList(
                                        if (value) checked + preset.id else checked - preset.id
                                    )
                            },
                    )
                }
                pagingStatus(presets)
            }
        }
        Button(
            onClick = {
                saving = true
                scope.launch {
                    try {
                        model.setPluginScope(plugin.id, PluginScope(mode, checked.toSet()))
                        onDismiss()
                    } catch (cancelled: kotlinx.coroutines.CancellationException) {
                        throw cancelled
                    } catch (failure: Exception) {
                        model.pluginFailure(failure)
                    } finally {
                        saving = false
                    }
                }
            },
            enabled = !saving && (mode != PluginScopeMode.Include || checked.isNotEmpty()),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        ) {
            Text(stringResource(R.string.save))
        }
    }
}

private suspend fun stagePlugin(context: Context, uri: android.net.Uri): File =
    withContext(Dispatchers.IO) {
        val file = context.cacheDir.resolve(UUID.randomUUID().toString() + ".plugin")
        try {
            val input = context.contentResolver.openInputStream(uri) ?: error("Cannot open plugin")
            input.use { source ->
                file.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var size = 0L
                    while (true) {
                        kotlinx.coroutines.currentCoroutineContext().ensureActive()
                        val read = source.read(buffer)
                        if (read < 0) break
                        size += read
                        check(size <= 128L * 1024 * 1024) { "Plugin package is too large." }
                        output.write(buffer, 0, read)
                    }
                }
            }
            file
        } catch (failure: Exception) {
            file.delete()
            throw failure
        }
    }
