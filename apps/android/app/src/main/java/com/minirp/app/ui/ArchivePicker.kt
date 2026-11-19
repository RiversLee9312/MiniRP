package com.minirp.app.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import org.json.JSONObject

internal enum class ArchiveType(val id: String, val extension: String) {
    Plugin("plugin", "plugin"),
    Room("room", "minirp-room"),
    Preset("preset", "minirp-preset"),
    Save("save", "minirp-save"),
}

internal class ArchivePicker(
    val export: (ArchiveType, String, String, String) -> Unit,
    val importArchive: (ArchiveType) -> Unit,
)

@Composable
internal fun rememberArchivePicker(model: ChatViewModel): ArchivePicker {
    var destination by rememberSaveable { mutableStateOf<String?>(null) }
    var source by rememberSaveable { mutableStateOf<String?>(null) }
    val save = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val request = destination
        destination = null
        if (uri != null && request != null) model.exportArchive(uri, JSONObject(request))
    }
    val open = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        val type = source
        source = null
        if (uri != null && type != null) model.importArchive(uri, type)
    }
    return ArchivePicker(
        export = { type, name, preset, id ->
            destination = JSONObject().put("type", type.id).put("preset", preset)
                .put("room", if (type == ArchiveType.Room) id else "")
                .put("id", if (type == ArchiveType.Plugin) id else "").toString()
            val stem = name.filterNot { it.isISOControl() || it in "\\/:*?\"<>|" }
                .take(80).trim().trim('.').ifBlank { "MiniRP" }
            save.launch("$stem.${type.extension}")
        },
        importArchive = { type ->
            source = type.id
            open.launch(arrayOf("*/*"))
        },
    )
}
