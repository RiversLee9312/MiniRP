package com.minirp.app.ui

import org.json.JSONArray
import org.json.JSONObject

internal fun JSONArray.objects(): List<JSONObject> = (0 until length()).mapNotNull(::optJSONObject)

internal fun JSONObject?.presetAppearance(): PresetAppearance {
    val appearance = this?.optJSONObject("appearance") ?: return PresetAppearance()
    return PresetAppearance(
        backgroundUrl = appearance.optString("background_url"),
        blurBackground = appearance.optBoolean("background_blur", true),
        themeColor = appearance.optString("theme_color").takeIf { it.matches(Regex("#[0-9a-fA-F]{6}")) }
            ?.let(android.graphics.Color::parseColor),
    )
}

internal fun decodeProvider(value: JSONObject) =
    ProviderConfig(
        id = value.optString("id"),
        name = value.optString("name"),
        protocol = value.optString("protocol", "openai"),
        url = value.optString("url"),
        key = value.optString("key"),
        model = value.optString("model"),
        thinking = value.optBoolean("thinking"),
        contextWindow = value.optInt("context_window", 128000),
        maxOutput = value.optInt("max_output", 8192),
    )

internal fun encodeProvider(value: ProviderConfig) =
    JSONObject()
        .put("id", value.id)
        .put("name", value.name)
        .put("protocol", value.protocol)
        .put("url", value.url)
        .put("key", value.key)
        .put("model", value.model)
        .put("thinking", value.thinking)
        .put("context_window", value.contextWindow)
        .put("max_output", value.maxOutput)

internal fun decodeChoice(value: JSONObject) =
    Choice(
        value.getString("id"),
        value.optString("name", value.getString("id")),
        sequenceOf("context_length", "context_window", "inputTokenLimit", "max_input_tokens")
            .map { value.optInt(it) }
            .firstOrNull { it > 0 } ?: 0,
        value.optInt(
            "outputTokenLimit",
            value.optInt(
                "max_output_tokens",
                value.optJSONObject("top_provider")?.optInt("max_completion_tokens") ?: 0,
            ),
        ),
        ensemble = value.optBoolean("ensemble"),
        inputModalities =
            (value.optJSONArray("input_modalities") ?: value.optJSONObject("architecture")?.optJSONArray("input_modalities"))
                ?.let { (0 until it.length()).map(it::getString) },
        effortLevels = value.effortLevels(),
    )

internal fun JSONObject.effortLevels(): List<String> {
    val levels = optJSONArray("effort_levels")
        ?: optJSONObject("effort")?.optJSONArray("supported_levels")
        ?: optJSONObject("thinking")?.optJSONArray("values")
    if (levels != null) {
        return (0 until levels.length()).mapNotNull { levels.opt(it) as? String }
            .filter(String::isNotBlank)
            .distinct()
    }
    val effort = optJSONObject("capabilities")?.optJSONObject("effort") ?: return emptyList()
    if (!effort.optBoolean("supported")) return emptyList()
    return effort.keys().asSequence().filter { effort.optJSONObject(it)?.optBoolean("supported") == true }.toList()
}

internal fun List<String>.highestEffort(): String {
    // Capability maps may arrive in alphabetical order. Arrays with provider-specific levels keep their advertised order.
    val ranks = mapOf("none" to 0, "minimal" to 1, "low" to 2, "medium" to 3, "high" to 4,
        "xhigh" to 5, "max" to 6, "ultra" to 7)
    return if (all { it.lowercase() in ranks }) maxByOrNull { ranks.getValue(it.lowercase()) }.orEmpty()
    else lastOrNull().orEmpty()
}

internal fun decodeModel(value: JSONObject) =
    SavedModel(
        value.getString("provider"),
        decodeChoice(value),
        CompactionSettings(
            threshold = value.optInt("compact_at_percent", DEFAULT_COMPACTION_PERCENT).coerceIn(1, 100),
            background = value.optBoolean("background_compaction", true),
            backgroundThreshold = value.optInt("background_compact_at_percent", DEFAULT_BACKGROUND_COMPACTION_PERCENT).coerceIn(1, 99),
        ),
        reasoningEffort = value.optString("reasoning_effort"),
    )

internal fun encodeModel(value: SavedModel) =
    JSONObject()
        .put("provider", value.provider)
        .put("id", value.choice.id)
        .put("name", value.choice.name)
        .put("context_length", value.choice.contextWindow)
        .put("max_output_tokens", value.choice.maxOutput)
        .put("input_modalities", value.choice.inputModalities?.let(::JSONArray))
        .put("effort_levels", JSONArray(value.choice.effortLevels))
        .put("reasoning_effort", value.reasoningEffort)
        .putCompaction(value.compaction)

internal fun JSONObject.putCompaction(value: CompactionSettings): JSONObject =
    put("compact_at_percent", value.threshold)
        .put("background_compaction", value.background)
        .put("background_compact_at_percent", value.backgroundThreshold)

internal fun SavedModel.sameModel(other: SavedModel?) =
    provider == other?.provider && choice.id == other.choice.id

internal fun decodeToolTraceUi(value: JSONObject?): ToolTraceUi? {
    val program = value?.optString("program").orEmpty()
    if (value == null || program.isBlank()) return null
    return ToolTraceUi(program, value.optString("title"), value.optJSONObject("state")?.toString())
}

internal fun TraceBlock.withToolTraceMetadata(value: JSONObject): TraceBlock {
    fun title(key: String, previous: String): String = when {
        !value.has(key) -> previous
        value.isNull(key) -> ""
        else -> value.optString(key)
    }
    val tool = title("tool_title", toolTitle)
    val skill = title("skill_title", skillTitle)
    val argumentsUi = if (value.has("tool_arguments_ui")) decodeToolTraceUi(value.optJSONObject("tool_arguments_ui")) else toolArgumentsUi
    val resultUi = if (value.has("tool_result_ui")) decodeToolTraceUi(value.optJSONObject("tool_result_ui")) else toolResultUi
    return if (tool == toolTitle && skill == skillTitle && argumentsUi == toolArgumentsUi && resultUi == toolResultUi) this
    else copy(toolTitle = tool, skillTitle = skill, toolArgumentsUi = argumentsUi, toolResultUi = resultUi)
}

internal fun decodeBlock(value: JSONObject) =
    TraceBlock(
        value.getString("id"),
        value.optString("agent"),
        value.optString("kind", value.optString("role")) in setOf("user", "system"),
        value.optString("content"),
        value.optString("reasoning"),
        value.optString("tool").takeUnless { it == "compact_context" }.orEmpty(),
        value.optString("arguments"),
        value.optString("result"),
        version = value.optString("version"),
        attachmentNames = value.attachmentNames(),
        system = value.optString("kind", value.optString("role")) == "system",
        thumbnails = value.thumbnails(),
        openUi = value.optString("open_ui").takeIf(String::isNotBlank),
        replyEnd =
            if (value.optString("kind") == "reply_end")
                ReplyEnd(value.optString("reply"), value.optString("first_block"))
            else null,
        speaker = value.optString("speaker").takeUnless { value.isNull("speaker") }.orEmpty(),
        openUiTitle = value.optString("open_ui_title"),
        openUiState = value.optJSONObject("open_ui_state")?.toString(),
        openUiRevision = value.optInt("open_ui_revision"),
        openUiHostAction = value.optString("open_ui_host_action"),
        compaction = value.optBoolean("compaction") || value.optString("tool") == "compact_context",
        pluginAgentId = value.optString("plugin_agent"),
        pluginRun = value.optString("plugin_run", value.optString("plugin_agent")),
        pluginAgentName = value.optString("plugin_agent_name"),
        pluginStatus = value.optString("plugin_status"),
        pluginSequence = value.optInt("plugin_sequence"),
        running = value.optString("plugin_status") == "running",
        quotes = value.quoteAttachments(),
    ).withToolTraceMetadata(value)

internal fun decodeEnsembleChat(value: JSONObject): EnsembleChat {
    val members = value.getJSONArray("members")
    return EnsembleChat(
        value.getString("id"),
        value.getString("name"),
        List(members.length()) { members.getString(it) },
    )
}

internal fun decodeEnsembleActivity(value: JSONObject): EnsembleActivity? {
    val conversation = value.optString("conversation")
    val participant = value.optString("participant")
    return if (conversation.isBlank() || participant.isBlank()) null
        else EnsembleActivity(conversation, participant, value.optString("speaker"))
}

internal fun JSONObject.isEnsembleMessage(): Boolean =
    optString("role", optString("kind")) in setOf("user", "system", "assistant")

internal fun JSONObject.branchDraft(): BranchDraft {
    val files = optJSONArray("attachments")?.objects().orEmpty()
        .filter { it.optString("kind") != "quote" && it.optString("id").isNotBlank() }
        .associateBy { "saved-attachment:${it.optString("id")}" }
    return BranchDraft(optString("text"), files, optJSONObject("open_ui_action"), quoteAttachments())
}

internal fun List<TraceBlock>.withLegacyReplyActions(): List<TraceBlock> {
    val savedStarts = mapNotNull { it.replyEnd?.firstBlock }.toHashSet()
    return buildList {
        val reply = mutableListOf<String>()
        var last = ""
        var hasSavedEnd = false
        fun appendLegacyEnd() {
            if (reply.isNotEmpty()) {
                add(TraceBlock("copy:$last", replyEnd = ReplyEnd(reply.joinToString("\n\n"), "")))
                reply.clear()
            }
        }
        for (block in this@withLegacyReplyActions) {
            if (block.role == AgentRole.Chat) {
                if (block.user) appendLegacyEnd()
                if (block.id in savedStarts) hasSavedEnd = true
                if (block.replyEnd != null) {
                    hasSavedEnd = false
                    reply.clear()
                } else if (!block.user && !hasSavedEnd) {
                    val text =
                        block.content.trim('\r', '\n').ifBlank {
                            block.openUi?.let { "```openui-lang\n$it\n```" }.orEmpty()
                        }
                    if (text.isNotBlank()) {
                        reply.add(text)
                        last = block.id
                    }
                }
            }
            add(block)
        }
        appendLegacyEnd()
    }
}

internal fun JSONObject.attachmentNames(): List<String> =
    optJSONArray("attachments")?.objects().orEmpty()
        .filter { it.optString("kind") != "quote" }.map { it.optString("name") }

internal fun JSONObject.quoteAttachments(): List<QuoteAttachment> =
    optJSONArray("attachments")?.objects().orEmpty().mapNotNull { item ->
        val text = item.optString("text")
        val id = item.optString("id")
        if (item.optString("kind") == "quote" && id.isNotBlank() && text.isNotBlank())
            QuoteAttachment(id, text)
        else null
    }

internal fun QuoteAttachment.toJson(): JSONObject =
    JSONObject().put("id", id).put("kind", "quote").put("text", text)

internal fun JSONObject.thumbnails(): List<AttachmentThumbnail> =
    optJSONArray("attachments")
        ?.objects()
        ?.mapNotNull { attachment ->
            val url =
                attachment.optString("thumbnail_url").ifBlank {
                    if (attachment.has("thumbnail"))
                        "minirp://assets/minirp.legacy/attachment-thumbnail?attachment=${attachment.optString("id")}"
                    else ""
                }
            url.takeIf(String::isNotBlank)?.let { AttachmentThumbnail(it, attachment) }
        }
        .orEmpty()
