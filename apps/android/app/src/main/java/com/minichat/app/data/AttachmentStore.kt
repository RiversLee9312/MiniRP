package com.minichat.app.data

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import com.minichat.app.R
import com.minichat.app.ui.PluginAttachmentType
import java.io.File
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

internal data class DraftAttachment(val uri: String, val name: String)

internal class AttachmentStore(private val context: Context) {
    companion object {
        val types = arrayOf("image/*", "audio/*", "application/pdf", "video/*")
    }

    private val resolver
        get() = context.contentResolver

    private val directory
        get() = context.filesDir.resolve("workspace/attachments").apply { mkdirs() }

    suspend fun describe(
        values: List<String>,
        retained: Map<String, JSONObject> = emptyMap(),
    ): List<DraftAttachment> =
        withContext(Dispatchers.IO) {
            values.map { value ->
                retained[value]?.let {
                    return@map DraftAttachment(value, it.optString("name"))
                }
                val uri = Uri.parse(value)
                val name = runCatching {
                    resolver
                        .query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
                        ?.use { cursor ->
                            if (cursor.moveToFirst()) cursor.getString(0) else null
                        }
                }
                    .getOrNull()
                DraftAttachment(
                    value,
                    name ?: uri.lastPathSegment?.substringAfterLast('/') ?: "Attachment",
                )
            }
        }

    suspend fun import(
        values: List<String>,
        protocol: String,
        pluginType: PluginAttachmentType? = null,
        retained: Map<String, JSONObject> = emptyMap(),
    ): JSONArray =
        withContext(Dispatchers.IO) {
            var remaining = if (protocol == "gemini") 14_000_000 else 16_000_000
            val created = mutableListOf<File>()
            check(values.size <= 20) { context.getString(R.string.attachment_too_large) }
            try {
                val files = JSONArray()
                for (draft in describe(values, retained)) {
                    ensureActive()
                    val saved = retained[draft.uri]
                    if (saved != null) {
                        files.put(saved)
                        continue
                    }
                    val uri = Uri.parse(draft.uri)
                    val mime = resolver.getType(uri)?.lowercase().orEmpty()
                    check(
                        protocol == "picker" ||
                            pluginType?.mimeTypes?.any {
                                it == mime ||
                                    it == "*/*" ||
                                    it.endsWith("/*") && mime.startsWith(it.dropLast(1))
                            } ?: supports(protocol, mime)
                    ) {
                        context.getString(R.string.attachment_type_unsupported)
                    }
                    val id = UUID.randomUUID().toString()
                    val file = directory.resolve(id)
                    created += file
                    val input =
                        resolver.openInputStream(uri)
                            ?: error(context.getString(R.string.attachment_read_failed))
                    input.use { source ->
                        file.outputStream().buffered().use { output ->
                            val buffer = ByteArray(32 * 1024)
                            while (true) {
                                ensureActive()
                                val read = source.read(buffer)
                                if (read < 0) break
                                remaining -= read
                                check(remaining >= 0) {
                                    context.getString(R.string.attachment_too_large)
                                }
                                output.write(buffer, 0, read)
                            }
                        }
                    }
                    check(file.length() > 0) { context.getString(R.string.attachment_read_failed) }
                    files.put(
                        JSONObject()
                            .put("id", id)
                            .put("name", draft.name.take(240))
                            .put("mime", mime)
                            .apply {
                                if (pluginType != null)
                                    put("plugin", pluginType.plugin).put("type", pluginType.id)
                            }
                    )
                }
                files
            } catch (error: Exception) {
                created.forEach(File::delete)
                throw error
            }
        }

    private fun supports(protocol: String, mime: String): Boolean {
        val image = mime in setOf("image/png", "image/jpeg", "image/webp", "image/gif")
        return when (protocol) {
            "openai" ->
                image ||
                    mime == "application/pdf" ||
                    mime in setOf("audio/wav", "audio/x-wav", "audio/mpeg", "audio/mp3")
            "anthropic",
            "responses" -> image || mime == "application/pdf"
            "ollama" -> image && mime != "image/gif"
            "gemini" ->
                (image && mime != "image/gif") ||
                    mime in
                        setOf(
                            "application/pdf",
                            "image/heic",
                            "image/heif",
                            "audio/wav",
                            "audio/x-wav",
                            "audio/mpeg",
                            "audio/mp3",
                            "audio/aac",
                            "audio/ogg",
                            "audio/flac",
                            "audio/mp4",
                            "video/mp4",
                            "video/webm",
                            "video/quicktime",
                            "video/mpeg",
                            "video/x-msvideo",
                            "video/x-flv",
                            "video/x-ms-wmv",
                            "video/3gpp",
                        )
            else -> false
        }
    }
}
