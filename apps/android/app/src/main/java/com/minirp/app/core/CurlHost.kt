package com.minirp.app.core

import android.content.Context
import android.os.SystemClock
import java.io.File
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.delay
import org.json.JSONObject

internal class CurlHost(context: Context, private val workspace: File) {
    private val executable = File(context.applicationInfo.nativeLibraryDir, "libminirp_curl.so")
    val available: Boolean get() = executable.isFile

    suspend fun handle(event: JSONObject): JSONObject {
        check(available) { "Install the MiniRP build with the curl runtime." }
        val run = event.getString("run")
        require(run.matches(Regex("[0-9a-f]{12}")))
        val directory = workspace.resolve("curl-runs").resolve(run)
        val files = event.getJSONArray("files")
        val arguments = event.getJSONArray("args")
        val cmd = listOf(executable.path, "--disable") + (0 until arguments.length()).map(arguments::getString)
        val errors = directory.resolve("stderr")
        val process = ProcessBuilder(cmd).directory(directory)
            .redirectOutput(directory.resolve("stdout")).redirectError(errors).apply {
                directory.resolve("stdin").takeIf(File::isFile)?.let(::redirectInput)
                val env = environment()
                for (key in listOf("CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE", "QLOGDIR")) env.remove(key)
                env["LDAPNOINIT"] = "1"
                repeat(files.length()) { index ->
                    val file = files.getJSONObject(index)
                    env["MINIRP_CURL_URL_$index"] = file.getString("url")
                    env["MINIRP_CURL_FILE_$index"] = file.getString("name")
                    env["MINIRP_CURL_WRITE_$index"] = if (file.getBoolean("write")) "1" else "0"
                }
            }.start()
        process.outputStream.close()
        val seconds = event.optInt("seconds")
        val started = SystemClock.elapsedRealtime()
        try {
            while (process.isAlive) {
                check(seconds <= 0 || SystemClock.elapsedRealtime() - started < TimeUnit.SECONDS.toMillis(seconds.toLong())) {
                    "curl timed out."
                }
                check(directory.listFiles().orEmpty().none { it.length() > 128L * 1024 * 1024 }) { "curl exceeded the Asset size limit." }
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
