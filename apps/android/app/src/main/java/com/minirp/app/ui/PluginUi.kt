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
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
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
)

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
                val items = AttachmentStore(context).import(uris.map { it.toString() }, "picker")
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
internal fun PluginsScreen(state: ChatState, model: ChatViewModel, modifier: Modifier) {
    val context = LocalContext.current
    var candidate by remember { mutableStateOf<Pair<File, PluginInfo>?>(null) }
    var removing by remember { mutableStateOf<PluginInfo?>(null) }
    var editingScope by rememberSaveable { mutableStateOf<String?>(null) }
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
    LazyColumn(modifier, contentPadding = PaddingValues(16.dp)) {
        item {
            TextButton(onClick = { installer.launch(arrayOf("*/*")) }) {
                Text(stringResource(R.string.install_plugin))
            }
        }
        items(state.installedPlugins, key = { it.id }) { plugin ->
            var menu by remember { mutableStateOf(false) }
            val canRun = plugin.command && plugin.scope.allows(state.preset)
            ListItem(
                content = { Text(plugin.name) },
                supportingContent = {
                    Text(
                        stringResource(
                            if (plugin.builtIn) R.string.built_in else plugin.scope.mode.label
                        )
                    )
                },
                modifier =
                    if (plugin.builtIn) Modifier
                    else Modifier.clickable { editingScope = plugin.id },
                trailingContent = {
                    if (canRun || !plugin.builtIn)
                        Box {
                            IconButton(onClick = { menu = true }) {
                                Symbol("more_horiz", stringResource(R.string.more_options))
                            }
                            DropdownMenu(menu, { menu = false }) {
                                if (canRun)
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.run_plugin)) },
                                        onClick = {
                                            menu = false
                                            model.runPlugin(plugin.id)
                                        },
                                    )
                                if (!plugin.builtIn) {
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.plugin_scope)) },
                                        onClick = {
                                            menu = false
                                            editingScope = plugin.id
                                        },
                                    )
                                    DropdownMenuItem(
                                        text = { Text(stringResource(R.string.remove_plugin)) },
                                        onClick = {
                                            menu = false
                                            removing = plugin
                                        },
                                    )
                                }
                            }
                        }
                },
            )
        }
    }
    state.installedPlugins
        .firstOrNull { it.id == editingScope && !it.builtIn }
        ?.let { plugin ->
            PluginScopeSheet(plugin, model, onDismiss = { editingScope = null })
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
    removing?.let { plugin ->
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text(plugin.name) },
            dismissButton = {
                TextButton(onClick = { removing = null }) { Text(stringResource(R.string.cancel)) }
            },
            confirmButton = {
                Button(
                    onClick = {
                        model.removePlugin(plugin.id)
                        removing = null
                    }
                ) {
                    Text(stringResource(R.string.remove_plugin))
                }
            },
        )
    }
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
