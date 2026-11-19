package com.minichat.app.core

import android.os.Build
import com.caoccao.javet.enums.V8ValueType
import com.caoccao.javet.interop.V8Host
import com.caoccao.javet.interop.V8Runtime
import com.caoccao.javet.values.reference.V8ValueObject
import com.caoccao.javet.values.reference.V8ValueTypedArray
import java.util.concurrent.ConcurrentHashMap
import org.json.JSONObject

internal class PluginScripts : AutoCloseable {
    private val isolates = ConcurrentHashMap<String, Runner>()

    fun step(
        request: JSONObject,
        read: (String) -> ByteArray,
        write: (String, ByteArray) -> Unit,
    ): JSONObject {
        check(Build.VERSION.SDK_INT >= 29) { "V8 plugins require Android 10 or later." }
        val id = request.getString("isolate")
        if (request.getString("action") == "load") {
            isolates.computeIfAbsent(id) { Runner() }
        }
        return try {
            checkNotNull(isolates[id]) { "Plugin isolate is closed" }.step(request, read, write)
        } catch (exception: Exception) {
            close(id)
            throw exception
        }
    }

    fun close(id: String) {
        isolates.remove(id)?.close()
    }

    fun finish(isolate: String, run: String) {
        isolates[isolate]?.finish(run)
    }

    override fun close() {
        isolates.keys.toList().forEach(::close)
    }

    private class Runner : AutoCloseable {
        private val runtime: V8Runtime = V8Host.getV8Instance().createV8Runtime()
        private var closed = false
        private var initialized = false

        @Synchronized
        fun step(
            request: JSONObject,
            read: (String) -> ByteArray,
            write: (String, ByteArray) -> Unit,
        ): JSONObject {
            check(!closed)
            runtime
                .getGuard(2_000)
                .apply { setDebugModeEnabled(true) }
                .use {
                    if (request.getString("action") == "load") {
                        if (!initialized) {
                            runtime.allowEval(false)
                            runtime.getExecutor(request.getString("bootstrap")).executeVoid()
                            runtime.getExecutor(request.getString("source")).executeVoid()
                            initialized = true
                        }
                        return JSONObject()
                    }
                    val run = quote(request.getString("run"))
                    if (request.getString("action") == "start") {
                        runtime
                            .getExecutor(
                                "__minichat.start(" +
                                    run +
                                    "," +
                                    quote(request.getString("payload")) +
                                    ")"
                            )
                            .executeVoid()
                    } else if (request.getString("action") == "pull") {
                        runtime.getExecutor("__minichat.pull($run)").executeVoid()
                    } else if (request.getString("action") == "cancel") {
                        runtime.getExecutor("__minichat.cancel($run)").executeVoid()
                    } else {
                        val replies = request.getJSONArray("replies")
                        for (index in 0 until replies.length()) {
                            val reply = replies.getJSONObject(index)
                            val transfer = reply.optString("transfer")
                            if (transfer.isEmpty()) {
                                runtime
                                    .getExecutor(
                                        "__minichat.reply(" +
                                            run +
                                            "," +
                                            quote(reply.toString()) +
                                            ")"
                                    )
                                    .executeVoid()
                            } else {
                                val bytes = read(transfer)
                                runtime
                                    .createV8ValueTypedArray(V8ValueType.Uint8Array, bytes.size)
                                    .use { array ->
                                        check(array.fromBytes(bytes))
                                        runtime.globalObject.get<V8ValueObject>("__minichat").use {
                                            bridge ->
                                            bridge.invokeVoid(
                                                "reply",
                                                request.getString("run"),
                                                reply.toString(),
                                                array,
                                            )
                                        }
                                    }
                            }
                        }
                    }
                    val result =
                        JSONObject(runtime.getExecutor("__minichat.poll($run)").executeString())
                    if (result.optBoolean("binary")) {
                        runtime
                            .getExecutor("__minichat.takeBytes($run)")
                            .execute<V8ValueTypedArray>()
                            .use { data ->
                                check(data.length in 1..65_536) { "Invalid outgoing asset chunk." }
                                write(request.getString("upload"), data.toBytes())
                            }
                    }
                    return result
                }
        }

        @Synchronized
        fun finish(run: String) {
            if (!closed && initialized)
                runtime.getExecutor("__minichat.finish(" + quote(run) + ")").executeVoid()
        }

        @Synchronized
        override fun close() {
            if (closed) return
            closed = true
            runtime.close()
        }

        private fun quote(text: String) =
            JSONObject.quote(text).replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
    }
}
