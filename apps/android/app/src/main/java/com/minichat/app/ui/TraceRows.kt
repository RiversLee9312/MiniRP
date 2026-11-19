package com.minichat.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import com.minichat.app.R

internal data class TraceHeader(
    val id: String,
    val parent: TraceHeader?,
    val running: Boolean,
    val tools: Int,
    val expanded: Boolean,
    val agent: AgentRole? = null,
)

@Composable
internal fun TraceHeading(
    header: TraceHeader,
    pinned: Boolean = false,
    obscured: Boolean = false,
    toggle: () -> Unit,
) {
    val label =
        when {
            header.agent != null -> stringResource(header.agent.label)
            header.running -> stringResource(R.string.running)
            header.tools > 0 ->
                pluralStringResource(R.plurals.trace_actions, header.tools, header.tools)
            else -> stringResource(R.string.reasoning)
        }
    val modifier =
        when {
            pinned -> Modifier.testTag("pinned:${header.id}")
            obscured -> Modifier.alpha(0f).clearAndSetSemantics {}
            else -> Modifier
        }
    TraceDisclosure(
        label,
        header.expanded,
        modifier.traceQuote(if (header.parent == null) 0 else 1),
        toggle,
    )
}

@Composable
internal fun TraceStep(step: TraceBlock, quoteDepth: Int) {
    Column(Modifier.fillMaxWidth().traceQuote(quoteDepth)) {
        if (step.tool.isBlank()) {
            val text =
                if (quoteDepth == 1) step.reasoning
                else
                    listOf(step.reasoning, step.content)
                        .filter(String::isNotBlank)
                        .joinToString("\n\n")
            SelectionContainer {
                Text(
                    text,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(vertical = 4.dp),
                )
            }
        } else {
            var details by rememberSaveable { mutableStateOf(false) }
            TraceDisclosure(step.tool.replace('_', ' '), details) { details = !details }
            if (details) {
                SelectionContainer {
                    Text(
                        listOf(step.arguments, step.result)
                            .filter(String::isNotBlank)
                            .joinToString("\n\n"),
                        Modifier.padding(start = 12.dp, bottom = 6.dp),
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            }
        }
    }
}

@Composable
private fun Modifier.traceQuote(depth: Int): Modifier {
    if (depth == 0) return this
    val line = MaterialTheme.colorScheme.outlineVariant
    return drawBehind {
            repeat(depth) { level ->
                val inset = (9 + level * 20).dp.toPx()
                val x = if (layoutDirection == LayoutDirection.Ltr) inset else size.width - inset
                drawLine(line, Offset(x, 0f), Offset(x, size.height), strokeWidth = 1.dp.toPx())
            }
        }
        .padding(start = (depth * 20).dp)
}

@Composable
private fun TraceDisclosure(
    label: String,
    expanded: Boolean,
    modifier: Modifier = Modifier,
    toggle: () -> Unit,
) {
    Row(
        modifier
            .fillMaxWidth()
            .clickable(
                onClickLabel = stringResource(if (expanded) R.string.collapse else R.string.expand),
                onClick = toggle,
            )
            .heightIn(min = 40.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Symbol(if (expanded) "expand_more" else "chevron_right", size = 18)
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}
