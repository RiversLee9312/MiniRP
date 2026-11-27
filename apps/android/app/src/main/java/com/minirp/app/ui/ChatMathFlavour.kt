package com.minirp.app.ui

import org.intellij.markdown.flavours.gfm.GFMElementTypes
import org.intellij.markdown.flavours.gfm.GFMFlavourDescriptor
import org.intellij.markdown.flavours.gfm.GFMTokenTypes
import org.intellij.markdown.flavours.gfm.lexer._GFMLexer
import org.intellij.markdown.lexer.GeneratedLexer
import org.intellij.markdown.lexer.MarkdownLexer
import org.intellij.markdown.parser.sequentialparsers.RangesListBuilder
import org.intellij.markdown.parser.sequentialparsers.SequentialParser
import org.intellij.markdown.parser.sequentialparsers.SequentialParserManager
import org.intellij.markdown.parser.sequentialparsers.TokensCache
import org.intellij.markdown.parser.sequentialparsers.impl.MathParser

internal const val maxChatFormulaLength = 4096

internal object ChatMathFlavour : GFMFlavourDescriptor() {
    private val parsers = super.sequentialParserManager.getParserSequence().map {
        if (it is MathParser) ChatMathParser else it
    }
    override val sequentialParserManager = object : SequentialParserManager() {
        override fun getParserSequence() = parsers
    }

    override fun createInlinesLexer() = MarkdownLexer(MathDelimiterLexer())
}

private class MathDelimiterLexer(private val delegate: GeneratedLexer = _GFMLexer()) : GeneratedLexer by delegate {
    override fun reset(buffer: CharSequence, start: Int, end: Int, initialState: Int) {
        // The lexer recognizes dollars. Give it equal-length tokens for TeX delimiters
        // while leaving the underlying source and every Markdown AST offset intact.
        var masked: StringBuilder? = null
        var index = start
        while (index + 1 < end) {
            if (buffer[index] != '\\') {
                index++
                continue
            }
            if (buffer[index + 1] in "()[]") {
                val target = masked ?: StringBuilder(buffer).also { masked = it }
                target.setCharAt(index, '$')
                target.setCharAt(index + 1, '$')
            }
            index += 2
        }
        delegate.reset(masked ?: buffer, start, end, initialState)
    }
}

private object ChatMathParser : SequentialParser {
    private fun TokensCache.Iterator.delimiter(tokens: TokensCache): String? =
        if (type == GFMTokenTypes.DOLLAR && length <= 2) tokens.originalText.subSequence(start, end).toString()
        else null

    override fun parse(tokens: TokensCache, rangesToGlue: List<IntRange>): SequentialParser.ParsingResult {
        val result = SequentialParser.ParsingResultBuilder()
        val remaining = RangesListBuilder()
        var token = tokens.RangesListIterator(rangesToGlue)
        while (token.type != null) {
            val opening = token.delimiter(tokens)
            val closing = when (opening) {
                "$", "$$" -> opening
                "\\(" -> "\\)"
                "\\[" -> "\\]"
                else -> null
            }
            val inline = opening == "$" || opening == "\\("
            if (closing != null && (opening != "$" || !token.charLookup(1).isWhitespace())) {
                val end = findEnd(tokens, token, closing, inline)
                if (end != null) {
                    val type = if (inline) GFMElementTypes.INLINE_MATH else GFMElementTypes.BLOCK_MATH
                    result.withNode(SequentialParser.Node(token.index..end.index + 1, type))
                    token = end.advance()
                    continue
                }
            }
            remaining.put(token.index)
            token = token.advance()
        }
        return result.withFurtherProcessing(remaining.get())
    }

    private fun findEnd(
        tokens: TokensCache, opening: TokensCache.RangesListIterator, closing: String, inline: Boolean,
    ): TokensCache.RangesListIterator? {
        var previousIndex = opening.index
        var token = opening.advance()
        while (token.type != null && token.index == previousIndex + 1 && token.start - opening.end <= maxChatFormulaLength) {
            if (inline && token.charLookup(0) == '\n') return null
            if (token.delimiter(tokens) == closing) {
                val source = tokens.originalText.subSequence(opening.end, token.start)
                if (source.isBlank()) return null
                if (closing == "$" && (source.last().isWhitespace() || token.charLookup(1).isDigit())) return null
                return token
            }
            previousIndex = token.index
            token = token.advance()
        }
        return null
    }
}
