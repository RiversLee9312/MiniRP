package com.minirp.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.contextmenu.builder.item
import androidx.compose.foundation.text.contextmenu.modifier.appendTextContextMenuComponents
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.text.selection.rememberSelectionState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.minirp.app.R

private object QuoteReplyMenuItem

@Composable
internal fun ReplySelection(onQuote: ((String) -> Unit)?, content: @Composable () -> Unit) {
    if (onQuote == null) {
        SelectionContainer(Modifier.fillMaxWidth(), content = content)
        return
    }
    val selection = rememberSelectionState()
    val label = stringResource(R.string.quote_reply)
    SelectionContainer(
        state = selection,
        modifier = Modifier.fillMaxWidth().appendTextContextMenuComponents {
            if (selection.selectedTexts.any { it.isNotBlank() }) {
                item(key = QuoteReplyMenuItem, label = label) {
                    val text = selection.selectedTexts.joinToString("\n") { it.text }
                    if (text.isNotBlank()) onQuote(text)
                    selection.clear()
                    close()
                }
            }
        },
        content = content,
    )
}

@Composable
internal fun QuoteAttachmentCard(quote: QuoteAttachment) {
    var expanded by rememberSaveable { mutableStateOf(false) }
    val label = stringResource(R.string.quote_reply)
    Surface(
        modifier = Modifier.padding(bottom = 4.dp).widthIn(max = 320.dp).testTag("sent-quote:${quote.id}"),
        shape = MaterialTheme.shapes.medium,
        color = MaterialTheme.colorScheme.surfaceContainerHighest,
    ) {
        Row(
            Modifier.clickable(
                onClickLabel = stringResource(R.string.expand),
                onClick = { expanded = true },
            ).padding(horizontal = 10.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.Top,
        ) {
            Symbol("reply", label, size = 18)
            Text(
                quote.text,
                Modifier.weight(1f, fill = false),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
            )
            Symbol("expand_more", size = 18)
        }
    }
    if (expanded) {
        FullSheet(label, onDismiss = { expanded = false }, heightLimit = 420.dp) {
            SelectionContainer(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState())) {
                Text(quote.text, Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
            }
        }
    }
}
