package com.minirp.app.ui

import android.util.JsonReader
import android.util.JsonToken
import org.json.JSONObject
import java.io.IOException
import java.io.StringReader

internal const val tracePreviewLength = 180
internal const val tracePreviewLines = 4

internal data class TracePayloadText(val value: String, val json: List<TraceJsonField>? = null)

internal data class TraceJsonField(
    val start: Int,
    val valueStart: Int,
    val end: Int,
    val depth: Int,
    val hasNext: Boolean,
    val kind: JsonToken,
    val children: List<TraceJsonField>,
)

internal fun formatTracePayload(value: String): TracePayloadText {
    // The whole original value remains available for copying, including large payloads.
    if (value.length > 128 * 1024) return TracePayloadText(value)
    val first = value.firstOrNull { !it.isWhitespace() }
    if (first != '{' && first != '[') return TracePayloadText(value)
    return try {
        JsonReader(StringReader(value)).use { reader ->
            val output = StringBuilder(value.length)
            val fields = reader.appendValue(output, depth = 0)
            if (reader.peek() == JsonToken.END_DOCUMENT) TracePayloadText(output.toString(), json = fields)
            else TracePayloadText(value)
        }
    } catch (_: IOException) {
        TracePayloadText(value)
    } catch (_: IllegalStateException) {
        TracePayloadText(value)
    }
}

private fun JsonReader.appendValue(output: StringBuilder, depth: Int): List<TraceJsonField> {
    if (depth > 64) throw IOException("Trace JSON nesting exceeds the display limit.")
    when (peek()) {
        JsonToken.BEGIN_OBJECT -> return appendCollection(output, depth, objectValue = true)
        JsonToken.BEGIN_ARRAY -> return appendCollection(output, depth, objectValue = false)
        JsonToken.STRING -> output.append(JSONObject.quote(nextString()))
        JsonToken.NUMBER -> output.append(nextString())
        JsonToken.BOOLEAN -> output.append(nextBoolean())
        JsonToken.NULL -> {
            nextNull()
            output.append("null")
        }
        else -> throw IOException("Invalid trace JSON.")
    }
    return emptyList()
}

private fun JsonReader.appendCollection(output: StringBuilder, depth: Int, objectValue: Boolean): List<TraceJsonField> {
    if (objectValue) beginObject() else beginArray()
    output.append(if (objectValue) '{' else '[')
    val fields = mutableListOf<TraceJsonField>()
    var next = hasNext()
    val nonEmpty = next
    while (next) {
        output.append('\n')
        val start = output.length
        repeat(depth + 1) { output.append("  ") }
        if (objectValue) output.append(JSONObject.quote(nextName())).append(": ")
        val valueStart = output.length
        val kind = peek()
        val nested = appendValue(output, depth + 1)
        next = hasNext()
        if (next) output.append(',')
        val end = output.length
        if (valueExceedsPreview(output, valueStart, end)) {
            fields.add(TraceJsonField(start, valueStart, end, depth + 1, next, kind, nested))
        } else {
            fields.addAll(nested)
        }
    }
    if (nonEmpty) output.indent(depth)
    if (objectValue) endObject() else endArray()
    output.append(if (objectValue) '}' else ']')
    return fields
}

private fun valueExceedsPreview(text: CharSequence, start: Int, end: Int): Boolean {
    if (end - start > tracePreviewLength) return true
    var lines = 1
    for (index in start until end) {
        if (text[index] == '\n' && ++lines > tracePreviewLines) return true
    }
    return false
}

private fun StringBuilder.indent(depth: Int) {
    append('\n')
    repeat(depth) { append("  ") }
}
