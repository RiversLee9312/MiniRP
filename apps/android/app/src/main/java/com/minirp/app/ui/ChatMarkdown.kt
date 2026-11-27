package com.minirp.app.ui

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.InlineTextContent
import androidx.compose.foundation.text.appendInlineContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.hrm.latex.renderer.Latex
import com.hrm.latex.renderer.measure.rememberLatexMeasurer
import com.hrm.latex.renderer.model.LatexConfig
import com.hrm.latex.renderer.model.LatexTheme
import com.mikepenz.markdown.compose.LocalMarkdownDimens
import com.mikepenz.markdown.compose.components.MarkdownComponentModel
import com.mikepenz.markdown.compose.components.markdownComponents
import com.mikepenz.markdown.compose.elements.MarkdownCodeFence
import com.mikepenz.markdown.compose.elements.MarkdownParagraph
import com.mikepenz.markdown.compose.elements.MarkdownTable
import com.mikepenz.markdown.compose.elements.MarkdownTableHeader
import com.mikepenz.markdown.compose.elements.MarkdownTableRow
import com.mikepenz.markdown.m3.markdownTypography
import com.mikepenz.markdown.m3.elements.MarkdownCheckBox
import com.mikepenz.markdown.model.markdownAnnotator
import com.mikepenz.markdown.model.markdownDimens
import com.mikepenz.markdown.model.markdownInlineContent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.intellij.markdown.MarkdownTokenTypes
import org.intellij.markdown.ast.ASTNode
import org.intellij.markdown.ast.findChildOfType
import org.intellij.markdown.ast.getTextInNode
import org.intellij.markdown.flavours.gfm.GFMElementTypes

internal val chatMarkdownComponents = markdownComponents(
    table = { ChatMarkdownTable(it) },
    checkbox = { MarkdownCheckBox(it.content, it.node, it.typography.text) },
)

internal val replyMarkdownComponents = markdownComponents(
    paragraph = { ChatMarkdownParagraph(it) },
    codeFence = { ChatMarkdownFence(it) },
    table = chatMarkdownComponents.table,
    checkbox = chatMarkdownComponents.checkbox,
)

@Composable
internal fun chatMarkdownTypography() = MaterialTheme.typography.run {
    markdownTypography(
        h1 = headlineSmall.copy(fontWeight = FontWeight.SemiBold),
        h2 = titleLarge.copy(fontWeight = FontWeight.SemiBold),
        h3 = titleMedium.copy(fontWeight = FontWeight.Bold),
        h4 = titleMedium,
        h5 = titleSmall.copy(fontWeight = FontWeight.Bold),
        h6 = titleSmall,
    )
}

private val ASTNode.isChatFormula
    get() = type == GFMElementTypes.INLINE_MATH || type == GFMElementTypes.BLOCK_MATH

private fun ASTNode.formulaText(source: String): String {
    val delimiterWidth = if (type == GFMElementTypes.INLINE_MATH && source[startOffset] == '$') 1 else 2
    return source.substring(startOffset + delimiterWidth, endOffset - delimiterWidth).trim()
}

private val ASTNode.formulaKey get() = "formula:$startOffset"

internal val chatMathAnnotator = markdownAnnotator { source, child ->
    child.isChatFormula.also { isFormula ->
        if (isFormula) appendInlineContent(child.formulaKey, child.getTextInNode(source).toString())
    }
}

@Composable
internal fun chatMathInlineContent(source: String, node: ASTNode) = markdownInlineContent(
    rememberInlineFormulas(source, node),
)

@Composable
private fun rememberInlineFormulas(source: String, node: ASTNode): Map<String, InlineTextContent> {
    val formulas = remember(source, node) {
        buildMap {
            fun collect(child: ASTNode) {
                if (child.isChatFormula) put(child.formulaKey, child.formulaText(source))
                else child.children.forEach(::collect)
            }
            collect(node)
        }
    }
    if (formulas.isEmpty()) return emptyMap()
    val dark = miniRPDarkTheme()
    val config = latexConfig()
    val measurer = rememberLatexMeasurer(config)
    val lock = remember(measurer) { Mutex() }
    val inline by produceState(emptyMap<String, InlineTextContent>(), formulas, measurer, dark) {
        value = withContext(Dispatchers.Default) {
            lock.withLock {
                buildMap {
                    for ((id, formula) in formulas) {
                        ensureActive()
                        measurer.inlineContent(formula, config, dark)?.let { put(id, it) }
                    }
                }
            }
        }
    }
    return inline
}

@Composable
private fun latexConfig() = LatexConfig(
    fontSize = MaterialTheme.typography.bodyLarge.fontSize,
    theme = LatexTheme.light(color = MaterialTheme.colorScheme.onBackground),
    accessibilityEnabled = true,
)

@Composable
private fun ChatMarkdownParagraph(paragraph: MarkdownComponentModel) {
    val display = paragraph.node.children.singleOrNull { it.type == GFMElementTypes.BLOCK_MATH }
        ?.takeIf { it.getTextInNode(paragraph.content) == paragraph.node.getTextInNode(paragraph.content).trim() }
    if (display == null) {
        MarkdownParagraph(paragraph.content, paragraph.node, modifier = Modifier.fillMaxWidth(), style = paragraph.typography.paragraph)
    } else {
        ChatDisplayFormula(display.formulaText(paragraph.content))
    }
}

@Composable
private fun ChatMarkdownFence(fence: MarkdownComponentModel) {
    val (source, node, typography) = fence
    val language = node.findChildOfType(MarkdownTokenTypes.FENCE_LANG)?.getTextInNode(source)?.toString()?.trim()
    val lines = node.children.filter { it.type == MarkdownTokenTypes.CODE_FENCE_CONTENT }
    val formula = if (language == "math" && node.findChildOfType(MarkdownTokenTypes.CODE_FENCE_END) != null && lines.isNotEmpty())
        source.substring(lines.first().startOffset, lines.last().endOffset).trimIndent()
    else ""
    if (formula.isNotBlank() && formula.length <= maxChatFormulaLength) ChatDisplayFormula(formula)
    else MarkdownCodeFence(source, node, typography.code)
}

@Composable
private fun ChatDisplayFormula(formula: String) {
    Box(Modifier.fillMaxWidth().padding(vertical = 8.dp), contentAlignment = Alignment.Center) {
        Latex(
            latex = formula,
            modifier = Modifier.horizontalScroll(rememberScrollState()).testTag("chat-display-formula"),
            config = latexConfig(),
            isDarkTheme = miniRPDarkTheme(),
        )
    }
}

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
