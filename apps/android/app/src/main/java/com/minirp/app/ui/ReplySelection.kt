package com.minirp.app.ui

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.text.contextmenu.builder.item
import androidx.compose.foundation.text.contextmenu.modifier.appendTextContextMenuComponents
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.text.selection.rememberSelectionState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
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

internal fun appendReplyQuote(draft: String, selection: String): String {
    if (selection.isBlank()) return draft
    val quote = selection.lineSequence().joinToString("\n") { line ->
        if (line.isEmpty()) ">" else "> $line"
    }
    val separator = when {
        draft.isEmpty() || draft.endsWith("\n\n") -> ""
        draft.endsWith('\n') -> "\n"
        else -> "\n\n"
    }
    return "$draft$separator$quote\n\n"
}
