package com.minirp.app

import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.unit.sp
import com.minirp.app.ui.ChatMathFlavour
import com.minirp.app.ui.TraceBlock
import com.minirp.app.ui.TraceLog
import com.mikepenz.markdown.model.State
import com.mikepenz.markdown.model.parseMarkdown
import org.intellij.markdown.ast.ASTNode
import org.intellij.markdown.ast.getTextInNode
import org.intellij.markdown.flavours.gfm.GFMElementTypes
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class ChatMarkdownTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun mathDelimitersPreserveCodePricesAndIncompleteInput() {
        val tick = 96.toChar()
        val fence = "$tick$tick$tick"
        val source = """
            Prices are @5 and @10. Inline @x^2@ and \(\sqrt{2}\).

            @@
            \frac{1}{2}
            @@

            \[\sum_{i=1}^n i\]

            $tick@ignored@$tick and \@escaped.

            FENCEtext
            \(ignored\)
            FENCE

            Unclosed \(input
        """.trimIndent().replace('@', '$').replace("FENCE", fence)
        val document = parseMarkdown(source, flavour = ChatMathFlavour) as State.Success
        fun formulas(node: ASTNode): List<String> = when (node.type) {
            GFMElementTypes.INLINE_MATH, GFMElementTypes.BLOCK_MATH -> listOf(node.getTextInNode(source).toString())
            else -> node.children.flatMap(::formulas)
        }
        assertEquals(
            listOf("\$x^2\$", "\\(\\sqrt{2}\\)", "\$\$\n\\frac{1}{2}\n\$\$", "\\[\\sum_{i=1}^n i\\]"),
            formulas(document.node),
        )
    }

    @Test
    fun sharedTranscriptRendersCompactHeadingsAndNativeMath() {
        val unfinished = "# Overview\n\nInline formula \\(\\frac{a}"
        val source = unfinished + "{b}\\) stays in the paragraph.\n\n\\[\\sum_{i=1}^n i\\]"
        val document = parseMarkdown(source, flavour = ChatMathFlavour) as State.Success
        val reply = mutableStateOf(TraceBlock("reply", content = unfinished, streaming = true))
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(listOf(reply.value), expandThoughts = false)
            }
        }
        val paragraph = compose.onNodeWithText("Inline formula", substring = true)
        compose.waitUntil(10_000) { paragraph.isDisplayed() }
        val heading = mutableListOf<TextLayoutResult>()
        compose.onNodeWithText("Overview").assertIsDisplayed()
            .performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(heading) }
        assertEquals(24.sp, heading.single().layoutInput.style.fontSize)
        fun hasInlineFormula(): Boolean {
            val layouts = mutableListOf<TextLayoutResult>()
            paragraph.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
            return layouts.single().placeholderRects.any { it != null }
        }
        assertEquals(false, hasInlineFormula())
        compose.runOnIdle { reply.value = reply.value.copy(content = source) }
        compose.waitUntil(10_000, ::hasInlineFormula)
        compose.onNodeWithTag("chat-display-formula", useUnmergedTree = true).assertIsDisplayed()
        compose.runOnIdle { reply.value = reply.value.copy(streaming = false, markdown = document) }
        compose.waitUntil(10_000, ::hasInlineFormula)
        compose.onNodeWithTag("chat-display-formula", useUnmergedTree = true).assertIsDisplayed()
    }
}
