package com.minirp.app.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.IconButton
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.res.stringResource
import com.minirp.app.R
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
    val exportTranscript: (String, TranscriptTarget) -> Unit,
)

internal data class TranscriptTarget(
    val preset: String,
    val agent: AgentRole,
    val chat: String = "",
    val conversation: String = "",
    val run: String = "",
)

@Composable
internal fun rememberArchivePicker(model: ChatViewModel): ArchivePicker {
    var destination by rememberSaveable { mutableStateOf<String?>(null) }
    var source by rememberSaveable { mutableStateOf<String?>(null) }
    val onSave: (android.net.Uri?) -> Unit = { uri ->
        val request = destination
        destination = null
        if (uri != null && request != null) model.exportArchive(uri, JSONObject(request))
    }
    val save = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream"), onResult = onSave)
    val saveTranscript = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/x-ndjson"), onResult = onSave)
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
            save.launch(exportName(name, type.extension))
        },
        importArchive = { type ->
            source = type.id
            open.launch(arrayOf("*/*"))
        },
        exportTranscript = { name, target ->
            destination = JSONObject().put("type", "transcript").put("preset", target.preset)
                .put("agent", target.agent.id).put("chat", target.chat)
                .put("conversation", target.conversation).put("run", target.run).toString()
            saveTranscript.launch(exportName(name, "jsonl"))
        },
    )
}

private fun exportName(name: String, extension: String): String {
    val stem = name.filterNot { it.isISOControl() || it in "\\/:*?\"<>|" }
        .take(80).trim().trim('.').ifBlank { "MiniRP" }
    return "$stem.$extension"
}

@Composable
internal fun ExportTranscriptButton(onClick: () -> Unit, enabled: Boolean) {
    IconButton(onClick = onClick, enabled = enabled) {
        Symbol("download", stringResource(R.string.export_transcript))
    }
}
