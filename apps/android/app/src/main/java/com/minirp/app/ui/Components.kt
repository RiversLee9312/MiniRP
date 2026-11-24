package com.minirp.app.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.minirp.app.R
import com.minirp.app.data.AttachmentStore
import com.minirp.app.data.DraftAttachment
import kotlinx.coroutines.launch

private val symbols = FontFamily(Font(R.font.material_symbols_rounded))

@Composable
fun Symbol(name: String, description: String? = null, size: Int = 24) {
    val mirrored =
        LocalLayoutDirection.current == LayoutDirection.Rtl &&
            name in setOf("arrow_back", "arrow_forward", "chevron_left", "chevron_right", "reply")
    Text(
        name,
        fontFamily = symbols,
        fontSize = size.sp,
        modifier =
            Modifier.graphicsLayer { scaleX = if (mirrored) -1f else 1f }
                .clearAndSetSemantics { if (description != null) contentDescription = description },
    )
}

@Composable
fun Composer(busy: Boolean, onCancel: () -> Unit, onSend: (String) -> Unit) =
    Composer(
        busy,
        false,
        onCancel,
        onSubmit = { text, _, _, _ ->
            onSend(text)
            true
        },
        allowAttachments = false,
    )

@Composable
internal fun Composer(
    busy: Boolean,
    preparing: Boolean,
    onCancel: () -> Unit,
    onSubmit: suspend (String, List<String>, Boolean, PluginAttachmentType?) -> Boolean,
    allowAttachments: Boolean = true,
    steering: Boolean = busy,
    pluginTypes: List<PluginAttachmentType> = emptyList(),
    draft: BranchDraft? = null,
    documentTypes: Array<String> = com.minirp.app.data.AttachmentStore.documentTypes,
    textState: MutableState<String> = rememberSaveable { mutableStateOf(draft?.text.orEmpty()) },
) {
    var text by textState
    val retained = draft?.attachments.orEmpty()
    var files by rememberSaveable { mutableStateOf(ArrayList(retained.keys)) }
    var submitting by remember { mutableStateOf(false) }
    var mediaMenu by remember { mutableStateOf(false) }
    var sendMenu by remember { mutableStateOf(false) }
    var pendingType by rememberSaveable { mutableStateOf("") }
    var attachedType by rememberSaveable { mutableStateOf("") }
    val selectedType = pluginTypes.firstOrNull { it.key == attachedType }
    val attachmentsAvailable = files.isEmpty() || attachedType.isEmpty() || selectedType != null
    val context = LocalContext.current
    val picker =
        rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            if (uris.isNotEmpty()) {
                val existing = if (attachedType == pendingType) files else emptyList()
                files = ArrayList((existing + uris.map { it.toString() }).distinct().take(20))
                attachedType = pendingType
            }
            pendingType = ""
        }
    val photos =
        rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(20)) {
            uris ->
            if (uris.isNotEmpty()) {
                val existing = if (attachedType.isEmpty()) files else emptyList()
                files = ArrayList((existing + uris.map { it.toString() }).distinct().take(20))
                attachedType = ""
            }
        }
    val drafts by
        produceState(emptyList<DraftAttachment>(), files, retained) {
            value = AttachmentStore(context).describe(files, retained)
        }
    val scope = rememberCoroutineScope()
    fun send(system: Boolean = false) {
        if (
            (text.isNotBlank() || files.isNotEmpty()) &&
                (!busy || steering) &&
                !submitting &&
                !preparing &&
                attachmentsAvailable &&
                (!(system || steering) || (files.isEmpty() && text.isNotBlank()))
        ) {
            val submittedText = text
            submitting = true
            scope.launch {
                try {
                    if (onSubmit(submittedText, files, system, selectedType)) {
                        // A quote inserted while an upload was in flight belongs to the next draft.
                        if (text.startsWith(submittedText))
                            text = text.removePrefix(submittedText).trimStart('\n')
                        files = arrayListOf()
                        attachedType = ""
                    }
                } finally {
                    submitting = false
                }
            }
        }
    }
    if (drafts.isNotEmpty())
        LazyRow(
            contentPadding = PaddingValues(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(drafts, key = { it.uri }) { draft ->
                InputChip(
                    selected = false,
                    onClick = { files = ArrayList(files.filterNot { it == draft.uri }) },
                    enabled = !submitting,
                    label = {
                        Text(
                            draft.name,
                            maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                        )
                    },
                    trailingIcon = {
                        Symbol(
                            "close",
                            stringResource(R.string.remove_attachment, draft.name),
                            size = 16,
                        )
                    },
                    modifier = Modifier.widthIn(max = 190.dp),
                )
            }
        }
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = androidx.compose.ui.Alignment.Bottom,
    ) {
        if (allowAttachments)
            Box {
                IconButton(
                    onClick = { mediaMenu = true },
                    enabled = !busy && !preparing && !submitting,
                ) {
                    Symbol("add", stringResource(R.string.add_attachment))
                }
                DropdownMenu(mediaMenu, { mediaMenu = false }) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.photos_videos)) },
                        onClick = {
                            mediaMenu = false
                            photos.launch(
                                PickVisualMediaRequest(
                                    ActivityResultContracts.PickVisualMedia.ImageAndVideo
                                )
                            )
                        },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.choose_files)) },
                        onClick = {
                            mediaMenu = false
                            pendingType = ""
                            picker.launch(documentTypes)
                        },
                    )
                    pluginTypes.forEach { type ->
                        DropdownMenuItem(
                            text = { Text(type.name) },
                            onClick = {
                                mediaMenu = false
                                pendingType = type.key
                                picker.launch(type.mimeTypes.toTypedArray())
                            },
                        )
                    }
                }
            }
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text(stringResource(R.string.input_hint)) },
            modifier = Modifier.weight(1f).testTag("composer"),
            shape = MaterialTheme.shapes.extraLarge,
            minLines = 1,
            maxLines = 8,
            readOnly = submitting,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(onSend = { send() }),
        )
        Spacer(Modifier.width(8.dp))
        val stopping = steering && text.isBlank()
        val canSend =
            !preparing &&
                !submitting &&
                (!busy || steering) &&
                (stopping ||
                    attachmentsAvailable &&
                        (text.isNotBlank() && (!steering || files.isEmpty()) ||
                            !steering && files.isNotEmpty()))
        val systemLabel = stringResource(R.string.send_as_system)
        Box {
            Surface(
                shape = MaterialTheme.shapes.large,
                color =
                    if (canSend) MaterialTheme.colorScheme.primary
                    else MaterialTheme.colorScheme.surfaceContainerHighest,
                contentColor =
                    if (canSend) MaterialTheme.colorScheme.onPrimary
                    else MaterialTheme.colorScheme.onSurfaceVariant,
            ) {
                Box(
                    Modifier.size(56.dp)
                        .testTag("send")
                        .combinedClickable(
                            enabled = canSend,
                            role = androidx.compose.ui.semantics.Role.Button,
                            onClick = { if (stopping) onCancel() else send() },
                            onLongClickLabel =
                                if (text.isNotBlank() && files.isEmpty()) systemLabel else null,
                            onLongClick =
                                if (text.isNotBlank() && files.isEmpty()) ({ sendMenu = true })
                                else null,
                        ),
                    contentAlignment = Alignment.Center,
                ) {
                    if (preparing)
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    else
                        Symbol(
                            when {
                                stopping -> "stop"
                                steering -> "reply"
                                else -> "arrow_upward"
                            },
                            stringResource(
                                when {
                                    stopping -> R.string.stop
                                    steering -> R.string.steer
                                    else -> R.string.send
                                }
                            ),
                        )
                }
            }
            DropdownMenu(sendMenu, { sendMenu = false }) {
                DropdownMenuItem(
                    text = { Text(systemLabel) },
                    onClick = {
                        sendMenu = false
                        send(system = true)
                    },
                )
                if (steering)
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.stop)) },
                        onClick = {
                            sendMenu = false
                            onCancel()
                        },
                    )
            }
        }
    }
}
