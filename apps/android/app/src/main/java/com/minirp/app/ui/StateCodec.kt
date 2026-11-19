package com.minirp.app.ui

import org.json.JSONArray
import org.json.JSONObject

internal fun JSONArray.objects(): List<JSONObject> = (0 until length()).mapNotNull(::optJSONObject)

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
        value.optInt("context_length", value.optInt("inputTokenLimit")),
        value.optInt(
            "outputTokenLimit",
            value.optInt(
                "max_output_tokens",
                value.optJSONObject("top_provider")?.optInt("max_completion_tokens") ?: 0,
            ),
        ),
        ensemble = value.optBoolean("ensemble"),
    )

internal fun decodeModel(value: JSONObject) =
    SavedModel(value.getString("provider"), decodeChoice(value))

internal fun encodeModel(value: SavedModel) =
    JSONObject()
        .put("provider", value.provider)
        .put("id", value.choice.id)
        .put("name", value.choice.name)
        .put("context_length", value.choice.contextWindow)
        .put("max_output_tokens", value.choice.maxOutput)

internal fun SavedModel.sameModel(other: SavedModel?) =
    provider == other?.provider && choice.id == other.choice.id

internal fun decodeBlock(value: JSONObject) =
    TraceBlock(
        value.getString("id"),
        value.optString("agent"),
        value.optString("kind") in setOf("user", "system"),
        value.optString("content"),
        value.optString("reasoning"),
        value.optString("tool"),
        value.optString("arguments"),
        value.optString("result"),
        version = value.optString("version"),
        attachmentNames = value.attachmentNames(),
        system = value.optString("kind") == "system",
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
    )

internal fun decodeEnsembleChat(value: JSONObject): EnsembleChat {
    val members = value.getJSONArray("members")
    return EnsembleChat(
        value.getString("id"),
        value.getString("name"),
        List(members.length()) { members.getString(it) },
    )
}

internal fun JSONObject.branchDraft(): BranchDraft {
    val files =
        optJSONArray("attachments")
            ?.objects()
            .orEmpty()
            .filter { it.optString("id").isNotBlank() }
            .associateBy { "saved-attachment:${it.optString("id")}" }
    return BranchDraft(optString("text"), files, optJSONObject("open_ui_action"))
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
    optJSONArray("attachments")?.objects()?.map { it.optString("name") }.orEmpty()

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
