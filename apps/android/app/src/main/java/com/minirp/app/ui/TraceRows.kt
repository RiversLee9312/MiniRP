package com.minirp.app.ui

import android.content.ClipData
import android.util.JsonToken
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import com.minirp.app.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val tracePayloadMaxHeight = 280.dp
private const val traceJsonPreviewLines = 12
internal val traceHeadingMinHeight = 40.dp

internal data class TraceHeader(
    val id: String,
    val parent: TraceHeader?,
    val running: Boolean,
    val tools: Int,
    val expanded: Boolean,
    val agent: AgentRole? = null,
    val agentName: String? = null,
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
            header.agentName != null -> header.agentName
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
        toggle = toggle,
    )
}

@Composable
internal fun TraceStep(
    step: TraceBlock,
    quoteDepth: Int,
    details: Boolean,
    toggleDetails: () -> Unit,
    openUiPages: OpenUiPageCache? = null,
    obscureHeading: Boolean = false,
) {
    Column(Modifier.fillMaxWidth().traceQuote(quoteDepth)) {
        if (step.tool.isBlank() && !step.compaction) {
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
            val headerModifier = if (obscureHeading) Modifier.alpha(0f).clearAndSetSemantics {}
                else Modifier
            TraceStepHeading(step, details, headerModifier, toggleDetails)
            if (details) {
                if (step.compaction) {
                    SelectionContainer {
                        Text(
                            listOf(step.reasoning, step.result).filter(String::isNotBlank).joinToString("\n\n"),
                            Modifier.padding(start = 12.dp, bottom = 6.dp)
                                .heightIn(max = tracePayloadMaxHeight)
                                .verticalScroll(rememberScrollState()),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                } else {
                    var argumentsExpanded by rememberSaveable { mutableStateOf(false) }
                    var resultExpanded by rememberSaveable { mutableStateOf(false) }
                    Column(Modifier.padding(start = 12.dp, bottom = 6.dp)) {
                        if (step.arguments.isNotBlank() || step.toolArgumentsUi != null) {
                            TraceToolPayload(
                                step = step,
                                presentation = step.toolArgumentsUi,
                                pages = openUiPages,
                                raw = step.arguments,
                                label = stringResource(R.string.tool_arguments),
                                tag = "arguments",
                                expanded = argumentsExpanded,
                                toggle = { argumentsExpanded = !argumentsExpanded },
                            )
                        }
                        if (step.result.isNotBlank() || step.toolResultUi != null) {
                            TraceToolPayload(
                                step = step,
                                presentation = step.toolResultUi,
                                pages = openUiPages,
                                raw = step.result,
                                label = stringResource(R.string.tool_result),
                                tag = "result",
                                expanded = resultExpanded,
                                toggle = { resultExpanded = !resultExpanded },
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
internal fun TraceStepHeading(step: TraceBlock, expanded: Boolean, modifier: Modifier, toggle: () -> Unit) {
    val label = if (step.compaction) stringResource(R.string.compaction_threshold)
        else step.toolTitle.ifBlank { step.tool.replace('_', ' ') }
    val skill = step.skillTitle.takeUnless { step.compaction || it == label || it.isBlank() }
    TraceDisclosure(label, expanded, modifier, detail = skill, toggle = toggle)
}

@Composable
private fun TraceToolPayload(
    step: TraceBlock,
    presentation: ToolTraceUi?,
    pages: OpenUiPageCache?,
    raw: String,
    label: String,
    tag: String,
    expanded: Boolean,
    toggle: () -> Unit,
) {
    val fallback: @Composable () -> Unit = {
        if (raw.isNotBlank()) TracePayload(raw, label, tag, expanded, toggle)
        else Text(stringResource(R.string.interactive_reply_unavailable))
    }
    if (presentation == null || pages == null) {
        fallback()
        return
    }
    OpenUiReply(
        block = step.copy(
            openUi = presentation.program,
            openUiTitle = presentation.title.ifBlank { label },
            openUiState = presentation.state,
            openUiRevision = presentation.hashCode(),
            openUiHostAction = "",
            streaming = false,
        ),
        interactions = null,
        pages = pages,
        viewKey = "${step.id}:trace-$tag",
        toolPresentation = true,
        toolHeader = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(label, Modifier.weight(1f), style = MaterialTheme.typography.labelMedium)
                if (raw.isNotBlank()) TraceCopyButton(raw, label, tag)
            }
        },
        fallback = fallback,
    )
}

@Composable
private fun TracePayload(raw: String, label: String, tag: String, expanded: Boolean, toggle: () -> Unit) {
    var payload by remember(raw) { mutableStateOf(TracePayloadText(raw)) }
    LaunchedEffect(raw) {
        payload = withContext(Dispatchers.Default) { formatTracePayload(raw) }
    }
    val collapsible = remember(payload) {
        val fields = payload.json
        if (fields == null) {
            payload.value.length > tracePreviewLength ||
                payload.value.lineSequence().take(tracePreviewLines + 1).count() > tracePreviewLines
        } else {
            // Fold whole payloads that are tall even with their long fields closed.
            val lineBreaks = payload.value.count { it == '\n' }
            val hiddenBreaks = fields.sumOf { field ->
                (field.valueStart until field.end).count { payload.value[it] == '\n' }
            }
            lineBreaks - hiddenBreaks >= traceJsonPreviewLines
        }
    }
    val collapsed = collapsible && !expanded
    val display = remember(payload.value, collapsed) {
        if (collapsed) tracePreview(payload.value) else payload.value
    }
    Column {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (collapsible) {
                TraceDisclosure(label, expanded, Modifier.weight(1f).testTag("trace-toggle:$tag"), toggle = toggle)
            } else {
                Text(label, Modifier.weight(1f), style = MaterialTheme.typography.labelMedium)
            }
            TraceCopyButton(raw, label, tag)
        }
        val json = payload.json
        val shape = MaterialTheme.shapes.medium
        Column(
            Modifier.fillMaxWidth()
                .clip(shape)
                .background(MaterialTheme.colorScheme.surfaceContainerLow)
                .padding(vertical = 6.dp, horizontal = 8.dp)
                .heightIn(max = tracePayloadMaxHeight)
                .verticalScroll(rememberScrollState())
                .testTag("trace-payload-body:$tag"),
        ) {
            if (json != null && !collapsed) {
                CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
                    Column(Modifier.testTag("trace-json:$tag")) {
                        TraceJsonBody(payload.value, 0, payload.value.length, json)
                    }
                }
            } else {
                SelectionContainer {
                    TraceCode(
                        display,
                        modifier = Modifier.testTag("trace-payload:$tag"),
                        maxLines = if (collapsed) tracePreviewLines else Int.MAX_VALUE,
                    )
                }
            }
        }
    }
}

@Composable
private fun TraceCopyButton(raw: String, label: String, tag: String) {
    val copy = stringResource(R.string.copy)
    val clipboard = LocalClipboard.current
    val coroutine = rememberCoroutineScope()
    IconButton(
        modifier = Modifier.testTag("trace-copy:$tag"),
        onClick = { coroutine.launch { clipboard.setClipEntry(ClipEntry(ClipData.newPlainText(label, raw))) } },
    ) {
        Symbol("content_copy", copy, size = 18)
    }
}

@Composable
private fun TraceJsonBody(source: String, start: Int, end: Int, fields: List<TraceJsonField>) {
    var cursor = start
    for (field in fields) {
        TraceJsonChunk(source, cursor, field.start)
        key(field.start) {
            TraceJsonDisclosure(source, field)
        }
        cursor = field.end
    }
    TraceJsonChunk(source, cursor, end)
}

@Composable
private fun TraceJsonChunk(source: String, start: Int, end: Int) {
    val text = remember(source, start, end) { source.substring(start, end).trim('\n') }
    if (text.isNotEmpty()) {
        SelectionContainer { TraceCode(text, Modifier.padding(start = 18.dp), json = true) }
    }
}

@Composable
private fun TraceJsonDisclosure(source: String, field: TraceJsonField) {
    var expanded by rememberSaveable { mutableStateOf(false) }
    val opensCollection = field.kind == JsonToken.BEGIN_OBJECT || field.kind == JsonToken.BEGIN_ARRAY
    val summary = remember(source, field, expanded) {
        val name = source.substring(field.start, field.valueStart).trimStart()
        val suffix = when {
            expanded && opensCollection -> source[field.valueStart].toString()
            expanded -> ""
            field.kind == JsonToken.BEGIN_OBJECT -> "{…}"
            field.kind == JsonToken.BEGIN_ARRAY -> "[…]"
            field.kind == JsonToken.STRING -> "\"…\""
            else -> "…"
        }
        (name + suffix + if (!expanded && field.hasNext) "," else "").ifBlank { "…" }
    }
    Row(
        Modifier.testTag("trace-json-field:${field.start}").fillMaxWidth()
            .clickable(
                onClickLabel = stringResource(if (expanded) R.string.collapse else R.string.expand),
                onClick = { expanded = !expanded },
            )
            .padding(start = (field.depth.coerceAtMost(8) * 10).dp)
            .heightIn(min = 36.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Symbol(if (expanded) "expand_more" else "chevron_right", size = 18)
        TraceCode(summary, Modifier.weight(1f), json = true, maxLines = 1)
    }
    if (expanded) {
        if (opensCollection) {
            TraceJsonBody(source, field.valueStart + 1, field.end, field.children)
        } else {
            SelectionContainer {
                TraceCode(
                    source.substring(field.valueStart, field.end),
                    Modifier.padding(start = (18 + field.depth.coerceAtMost(8) * 10).dp),
                    json = true,
                )
            }
        }
    }
}

@Composable
private fun TraceCode(text: String, modifier: Modifier = Modifier, json: Boolean = false, maxLines: Int = Int.MAX_VALUE) {
    Text(
        text,
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.onSurface,
        style = MaterialTheme.typography.bodySmall.copy(
            fontFamily = FontFamily.Monospace,
            textDirection = if (json) TextDirection.Ltr else TextDirection.Content,
            textAlign = if (json) TextAlign.Left else TextAlign.Start,
        ),
        maxLines = maxLines,
        overflow = if (maxLines == Int.MAX_VALUE) TextOverflow.Clip else TextOverflow.Ellipsis,
    )
}

private fun tracePreview(value: String): String {
    val limit = if (value.length > tracePreviewLength && value[tracePreviewLength - 1].isHighSurrogate()) {
        tracePreviewLength - 1
    } else tracePreviewLength
    return value.take(limit).lineSequence().take(tracePreviewLines).joinToString("\n").trimEnd() + "…"
}

@Composable
internal fun Modifier.traceQuote(depth: Int): Modifier {
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
    detail: String? = null,
    toggle: () -> Unit,
) {
    Row(
        modifier
            .fillMaxWidth()
            .clickable(
                onClickLabel = stringResource(if (expanded) R.string.collapse else R.string.expand),
                onClick = toggle,
            )
            .heightIn(min = traceHeadingMinHeight),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Symbol(if (expanded) "expand_more" else "chevron_right", size = 18)
        Column(Modifier.weight(1f)) {
            Text(
                label,
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (detail != null) Text(
                detail,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}
