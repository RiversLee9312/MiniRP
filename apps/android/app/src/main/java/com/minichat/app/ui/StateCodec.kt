package com.minichat.app.ui

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
        value.optString("kind") == "user",
        value.optString("content"),
        value.optString("reasoning"),
        value.optString("tool"),
        value.optString("arguments"),
        value.optString("result"),
        version = value.optString("version"),
        attachmentNames = value.attachmentNames(),
    )

internal fun JSONObject.attachmentNames(): List<String> =
    optJSONArray("attachments")?.objects()?.map { it.optString("name") }.orEmpty()
