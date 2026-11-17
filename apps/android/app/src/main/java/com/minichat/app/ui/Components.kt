package com.minichat.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
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
import com.mikepenz.markdown.m3.Markdown
import com.minichat.app.R

private val symbols = FontFamily(Font(R.font.material_symbols_rounded))

@Composable
fun Symbol(name: String, description: String? = null) {
    val mirrored =
        LocalLayoutDirection.current == LayoutDirection.Rtl &&
            name in setOf("arrow_back", "arrow_forward", "chevron_left", "chevron_right")
    Text(
        name,
        fontFamily = symbols,
        fontSize = 24.sp,
        modifier =
            Modifier.graphicsLayer { scaleX = if (mirrored) -1f else 1f }
                .clearAndSetSemantics { if (description != null) contentDescription = description },
    )
}

@Composable
fun Composer(busy: Boolean, onCancel: () -> Unit, onSend: (String) -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    fun send() {
        if (text.isNotBlank() && !busy) {
            onSend(text)
            text = ""
        }
    }
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = androidx.compose.ui.Alignment.Bottom,
    ) {
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text(stringResource(R.string.input_hint)) },
            modifier = Modifier.weight(1f).testTag("composer"),
            shape = MaterialTheme.shapes.extraLarge,
            minLines = 1,
            maxLines = 8,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(onSend = { send() }),
        )
        Spacer(Modifier.width(8.dp))
        FilledIconButton(
            onClick = { if (busy) onCancel() else send() },
            enabled = busy || text.isNotBlank(),
            modifier = Modifier.size(56.dp),
        ) {
            Symbol(
                if (busy) "stop" else "arrow_upward",
                stringResource(if (busy) R.string.stop else R.string.send),
            )
        }
    }
}

@Composable
fun TraceLog(
    blocks: List<TraceBlock>,
    expandThoughts: Boolean,
    modifier: Modifier = Modifier,
    streaming: Boolean = false,
) {
    val scroll = rememberLazyListState()
    val following by remember {
        derivedStateOf {
            !scroll.canScrollForward ||
                scroll.layoutInfo.visibleItemsInfo.lastOrNull()?.index == blocks.lastIndex
        }
    }
    LaunchedEffect(blocks.lastOrNull()) {
        if (following && blocks.isNotEmpty()) scroll.animateScrollToItem(blocks.lastIndex)
    }
    LazyColumn(
        modifier,
        state = scroll,
        contentPadding = PaddingValues(20.dp),
        verticalArrangement = Arrangement.spacedBy(18.dp),
    ) {
        items(blocks, key = { it.id }) { block ->
            if (block.user) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    Surface(
                        color = MaterialTheme.colorScheme.surfaceContainerHigh,
                        shape = MaterialTheme.shapes.large,
                        modifier = Modifier.widthIn(max = 340.dp),
                    ) {
                        SelectionContainer {
                            Text(
                                block.content,
                                Modifier.padding(horizontal = 16.dp, vertical = 12.dp),
                            )
                        }
                    }
                }
            } else {
                TraceMessage(block, expandThoughts, streaming)
            }
        }
    }
}

@Composable
private fun TraceMessage(block: TraceBlock, defaultExpanded: Boolean, streaming: Boolean) {
    Column(Modifier.fillMaxWidth()) {
        if (block.reasoning.isNotBlank()) {
            var expanded by rememberSaveable(block.id) { mutableStateOf(defaultExpanded) }
            FoldHeader(stringResource(R.string.reasoning), block.agent, expanded) {
                expanded = !expanded
            }
            if (expanded) {
                SelectionContainer {
                    Text(
                        block.reasoning,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(vertical = 8.dp),
                    )
                }
            }
        }
        if (block.tool.isNotBlank()) {
            var expanded by rememberSaveable(block.id) { mutableStateOf(false) }
            FoldHeader(block.tool, block.agent, expanded) { expanded = !expanded }
            if (block.running) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 4.dp))
            if (expanded) {
                SelectionContainer {
                    Text(
                        block.arguments + "\n" + block.result,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(vertical = 8.dp),
                    )
                }
            }
        }
        if (block.content.isNotBlank()) {
            SelectionContainer {
                if (streaming) Text(block.content, modifier = Modifier.fillMaxWidth())
                else Markdown(block.content, modifier = Modifier.fillMaxWidth())
            }
        }
    }
}

@Composable
private fun FoldHeader(label: String, agent: String, expanded: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth()
            .clickable(
                onClickLabel = stringResource(if (expanded) R.string.collapse else R.string.expand),
                onClick = onClick,
            )
            .padding(vertical = 6.dp),
        verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
    ) {
        Symbol(if (expanded) "expand_more" else "chevron_right")
        Text(label, style = MaterialTheme.typography.labelLarge)
        Spacer(Modifier.width(8.dp))
        Text(
            agent,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
