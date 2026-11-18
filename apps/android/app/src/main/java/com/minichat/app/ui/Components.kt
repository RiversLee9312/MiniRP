package com.minichat.app.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
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
import com.minichat.app.R
import com.minichat.app.data.AttachmentStore
import com.minichat.app.data.DraftAttachment
import kotlinx.coroutines.launch

private val symbols = FontFamily(Font(R.font.material_symbols_rounded))

@Composable
fun Symbol(name: String, description: String? = null, size: Int = 24) {
    val mirrored =
        LocalLayoutDirection.current == LayoutDirection.Rtl &&
            name in setOf("arrow_back", "arrow_forward", "chevron_left", "chevron_right")
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
        onSubmit = { text, _ ->
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
    onSubmit: suspend (String, List<String>) -> Boolean,
    allowAttachments: Boolean = true,
) {
    var text by rememberSaveable { mutableStateOf("") }
    var files by rememberSaveable { mutableStateOf(arrayListOf<String>()) }
    var submitting by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val picker =
        rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            files = ArrayList((files + uris.map { it.toString() }).distinct().take(20))
        }
    val drafts by
        produceState(emptyList<DraftAttachment>(), files) {
            value = AttachmentStore(context).describe(files)
        }
    val scope = rememberCoroutineScope()
    fun send() {
        if ((text.isNotBlank() || files.isNotEmpty()) && !busy && !submitting && !preparing) {
            submitting = true
            scope.launch {
                try {
                    if (onSubmit(text, files)) {
                        text = ""
                        files = arrayListOf()
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
            IconButton(
                onClick = { picker.launch(AttachmentStore.types) },
                enabled = !busy && !preparing && !submitting,
            ) {
                Symbol("add", stringResource(R.string.add_attachment))
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
        FilledIconButton(
            onClick = { if (busy) onCancel() else send() },
            enabled =
                busy || !preparing && !submitting && (text.isNotBlank() || files.isNotEmpty()),
            modifier = Modifier.size(56.dp),
        ) {
            if (preparing) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
            else
                Symbol(
                    if (busy) "stop" else "arrow_upward",
                    stringResource(if (busy) R.string.stop else R.string.send),
                )
        }
    }
}
