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
import androidx.compose.runtime.saveable.listSaver
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

internal data class ComposerSubmission(
    val text: String,
    val files: List<String>,
    val system: Boolean,
    val pluginAttachment: PluginAttachmentType?,
    val quotes: List<QuoteAttachment>,
)

private val draftQuoteSaver = listSaver<List<QuoteAttachment>, String>(
    save = { quotes -> quotes.flatMap { listOf(it.id, it.text) } },
    restore = { saved -> saved.chunked(2).map { (id, text) -> QuoteAttachment(id, text) } },
)

@Composable
internal fun rememberDraftQuotes(draft: BranchDraft?): MutableState<List<QuoteAttachment>> =
    rememberSaveable(stateSaver = draftQuoteSaver) { mutableStateOf(draft?.quotes.orEmpty()) }

@Composable
internal fun rememberDraftFiles(draft: BranchDraft?): MutableState<ArrayList<String>> =
    rememberSaveable { mutableStateOf(ArrayList(draft?.attachments.orEmpty().keys)) }

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
        onSubmit = { message ->
            onSend(message.text)
            true
        },
        allowAttachments = false,
    )

@Composable
internal fun Composer(
    busy: Boolean,
    preparing: Boolean,
    onCancel: () -> Unit,
    onSubmit: suspend (ComposerSubmission) -> Boolean,
    allowAttachments: Boolean = true,
    steering: Boolean = busy,
    pluginTypes: List<PluginAttachmentType> = emptyList(),
    draft: BranchDraft? = null,
    documentTypes: Array<String> = com.minirp.app.data.AttachmentStore.documentTypes,
    textState: MutableState<String> = rememberSaveable { mutableStateOf(draft?.text.orEmpty()) },
    systemMessageMode: Boolean? = null,
    filesState: MutableState<ArrayList<String>> = rememberDraftFiles(draft),
    quotesState: MutableState<List<QuoteAttachment>> = rememberDraftQuotes(draft),
) {
    var text by textState
    val retained = draft?.attachments.orEmpty()
    var files by filesState
    var quotes by quotesState
    var submitting by remember { mutableStateOf(false) }
    var mediaMenu by remember { mutableStateOf(false) }
    var sendMenu by remember { mutableStateOf(false) }
    var pendingType by rememberSaveable { mutableStateOf("") }
    var attachedType by rememberSaveable { mutableStateOf("") }
    val selectedType = pluginTypes.firstOrNull { it.key == attachedType }
    val submittedFiles = if (systemMessageMode == true) emptyList() else files
    val submittedQuotes = if (systemMessageMode == true) emptyList() else quotes
    val hasAttachments = submittedFiles.isNotEmpty() || submittedQuotes.isNotEmpty()
    val fileLimit = (maxMessageAttachments - quotes.size).coerceAtLeast(0)
    val attachmentsAvailable = submittedFiles.isEmpty() || attachedType.isEmpty() || selectedType != null
    val context = LocalContext.current
    val picker =
        rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            if (uris.isNotEmpty()) {
                val existing = if (attachedType == pendingType) files else emptyList()
                files = ArrayList((existing + uris.map { it.toString() }).distinct().take(fileLimit))
                attachedType = pendingType
            }
            pendingType = ""
        }
    val photos =
        rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(20)) {
            uris ->
            if (uris.isNotEmpty()) {
                val existing = if (attachedType.isEmpty()) files else emptyList()
                files = ArrayList((existing + uris.map { it.toString() }).distinct().take(fileLimit))
                attachedType = ""
            }
        }
    val drafts by
        produceState(emptyList<DraftAttachment>(), submittedFiles, retained) {
            value = AttachmentStore(context).describe(submittedFiles, retained)
        }
    val scope = rememberCoroutineScope()
    fun send(system: Boolean = systemMessageMode == true) {
        if (
            (text.isNotBlank() || hasAttachments) &&
                (!busy || steering) &&
                !submitting &&
                !preparing &&
                attachmentsAvailable &&
                (!(system || steering) || (!hasAttachments && text.isNotBlank()))
        ) {
            val message = ComposerSubmission(
                text, submittedFiles, system, selectedType.takeUnless { system }, submittedQuotes,
            )
            submitting = true
            scope.launch {
                try {
                    if (onSubmit(message)) {
                        if (text.startsWith(message.text))
                            text = text.removePrefix(message.text).trimStart('\n')
                        quotes = quotes - message.quotes.toSet()
                        files = arrayListOf()
                        attachedType = ""
                    }
                } finally {
                    submitting = false
                }
            }
        }
    }
    if (drafts.isNotEmpty() || submittedQuotes.isNotEmpty())
        LazyRow(
            contentPadding = PaddingValues(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(submittedQuotes, key = { it.id }) { quote ->
                val label = stringResource(R.string.quote_reply)
                InputChip(
                    selected = false,
                    onClick = { quotes = quotes - quote },
                    enabled = !submitting,
                    leadingIcon = { Symbol("reply", label, size = 18) },
                    label = {
                        Text(quote.text, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    },
                    trailingIcon = {
                        Symbol("close", stringResource(R.string.remove_attachment, label), size = 16)
                    },
                    modifier = Modifier.widthIn(max = 230.dp).testTag("draft-quote:${quote.id}"),
                )
            }
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
                    enabled = !busy && !preparing && !submitting && fileLimit > 0,
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
            placeholder = { Text(stringResource(if (systemMessageMode == true) R.string.system_message else R.string.input_hint)) },
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
                        (text.isNotBlank() && (!steering || !hasAttachments) ||
                            !steering && hasAttachments))
        val systemLabel = stringResource(R.string.send_as_system)
        val canChooseSystem = systemMessageMode == null && text.isNotBlank() && !hasAttachments
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
                            onLongClickLabel = systemLabel.takeIf { canChooseSystem },
                            onLongClick = if (canChooseSystem) ({ sendMenu = true }) else null,
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
                                    systemMessageMode == true -> R.string.send_as_system
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
