package com.minirp.app

import com.minirp.app.core.PluginSigner
import java.io.File
import java.security.MessageDigest
import java.util.Base64
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import org.json.JSONObject

internal fun writeSignedPlugin(
    workspace: File,
    preset: String,
    id: String,
    files: Map<String, ByteArray>,
    archive: File = workspace.resolve("plugins/$id.plugin"),
): File {
    val entries = files.toSortedMap()
    val digest = MessageDigest.getInstance("SHA-256")
    val payload = buildString {
        append("MCPLUGIN/2\n")
        for ((path, bytes) in entries) {
            append(digest.digest(bytes).joinToString("") { "%02X".format(it.toInt() and 0xff) })
            append('\t').append(path).append('\n')
        }
    }
    val signer = workspace.resolve("preset_sessions/$preset/plugin_agents/$id").apply { mkdirs() }.resolve("signer.json")
    val request = JSONObject().put("path", signer.path)
        .put("payload", Base64.getEncoder().encodeToString(payload.toByteArray()))
    val signature = PluginSigner.sign(workspace, request).getString("signature")
    entries["META-INF/minichat.p7s"] = Base64.getDecoder().decode(signature)
    archive.parentFile?.mkdirs()
    ZipOutputStream(archive.outputStream()).use { zip ->
        for ((path, bytes) in entries) {
            zip.putNextEntry(ZipEntry(path))
            zip.write(bytes)
            zip.closeEntry()
        }
    }
    return archive
}
