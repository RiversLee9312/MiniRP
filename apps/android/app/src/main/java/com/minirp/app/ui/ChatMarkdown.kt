package com.minirp.app.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.mikepenz.markdown.compose.LocalMarkdownDimens
import com.mikepenz.markdown.compose.components.MarkdownComponentModel
import com.mikepenz.markdown.compose.components.markdownComponents
import com.mikepenz.markdown.compose.elements.MarkdownTable
import com.mikepenz.markdown.compose.elements.MarkdownTableHeader
import com.mikepenz.markdown.compose.elements.MarkdownTableRow
import com.mikepenz.markdown.m3.elements.MarkdownCheckBox
import com.mikepenz.markdown.model.markdownDimens

internal val chatMarkdownComponents = markdownComponents(
    table = { ChatMarkdownTable(it) },
    checkbox = { MarkdownCheckBox(it.content, it.node, it.typography.text) },
)

@Composable
private fun ChatMarkdownTable(table: MarkdownComponentModel) {
    val minCellWidth = with(LocalDensity.current) { 104.sp.toDp() }.coerceAtLeast(104.dp)
    CompositionLocalProvider(
        LocalMarkdownDimens provides markdownDimens(tableCellWidth = minCellWidth, tableCellPadding = 10.dp),
    ) {
        MarkdownTable(
            table.content,
            table.node,
            table.typography.table,
            headerBlock = { source, row, width, style ->
                MarkdownTableHeader(
                    source, row, width, style,
                    verticalAlignment = Alignment.Top,
                    maxLines = Int.MAX_VALUE,
                    overflow = TextOverflow.Clip,
                )
            },
            rowBlock = { source, row, width, style ->
                MarkdownTableRow(
                    source, row, width, style,
                    verticalAlignment = Alignment.Top,
                    maxLines = Int.MAX_VALUE,
                    overflow = TextOverflow.Clip,
                )
            },
        )
    }
}
