package com.minirp.app.core

import android.content.Context
import android.os.SystemClock
import java.io.File
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.delay
import org.json.JSONObject

internal class AssetCommandHost(context: Context, private val workspace: File) {
    private val libraries = File(context.applicationInfo.nativeLibraryDir)
    private fun executable(tool: String) = libraries.resolve("libminirp_$tool.so")
    fun available(tool: String): Boolean = executable(tool).isFile

    suspend fun handle(event: JSONObject): JSONObject {
        val run = event.getString("run")
        val tool = event.optString("tool", "curl")
        require(run.matches(Regex("[0-9a-f]{12}")) && tool in listOf("curl", "ffmpeg", "ffprobe"))
        check(available(tool)) { "Install the MiniRP build with the $tool runtime." }
        val media = tool != "curl"
        val directory = workspace.resolve(if (media) "ffmpeg-runs" else "curl-runs").resolve(run)
        val files = event.getJSONArray("files")
        val arguments = event.getJSONArray("args")
        val cmd = listOf(executable(tool).path) + (if (media) emptyList() else listOf("--disable")) +
            (0 until arguments.length()).map(arguments::getString)
        val errors = directory.resolve("stderr")
        val process = ProcessBuilder(cmd).directory(directory)
            .redirectOutput(directory.resolve("stdout")).redirectError(errors).apply {
                directory.resolve("stdin").takeIf(File::isFile)?.let(::redirectInput)
                val env = environment()
                for (key in listOf("CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE", "QLOGDIR")) env.remove(key)
                env["LDAPNOINIT"] = "1"
                if (media) {
                    env.remove("FFREPORT")
                    env["LD_LIBRARY_PATH"] = libraries.path
                }
                val prefix = if (media) "MINIRP_MEDIA" else "MINIRP_CURL"
                repeat(files.length()) { index ->
                    val file = files.getJSONObject(index)
                    env["${prefix}_URL_$index"] = file.getString("url")
                    env["${prefix}_FILE_$index"] = file.getString("name")
                    env["${prefix}_WRITE_$index"] = if (file.getBoolean("write")) "1" else "0"
                }
                env.remove("${prefix}_URL_${files.length()}")
            }.start()
        process.outputStream.close()
        val seconds = event.optInt("seconds")
        val started = SystemClock.elapsedRealtime()
        try {
            while (process.isAlive) {
                check(seconds <= 0 || SystemClock.elapsedRealtime() - started < TimeUnit.SECONDS.toMillis(seconds.toLong())) {
                    "$tool timed out."
                }
                check(directory.listFiles().orEmpty().none { it.length() > 128L * 1024 * 1024 }) { "$tool exceeded the Asset size limit." }
                delay(40)
            }
            val result = JSONObject().put("exit_code", process.exitValue())
            val detail = errors.reader().use { reader ->
                val text = CharArray(2048)
                val length = reader.read(text)
                if (length > 0) String(text, 0, length).trim() else ""
            }
            if (detail.isNotEmpty()) result.put("stderr", detail)
            return result
        } finally {
            if (process.isAlive) {
                process.destroyForcibly()
                process.waitFor()
            }
        }
    }
}
