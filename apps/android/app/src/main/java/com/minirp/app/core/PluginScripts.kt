package com.minirp.app.core

import android.os.Build
import android.util.Log
import com.caoccao.javet.enums.V8ValueType
import com.caoccao.javet.interop.V8Host
import com.caoccao.javet.interop.V8Runtime
import com.caoccao.javet.values.reference.V8ValueObject
import com.caoccao.javet.values.reference.V8ValueTypedArray
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject

internal class PluginScripts(
    private val createRuntime: () -> V8Runtime = { V8Host.getV8Instance().createV8Runtime() }
) {
    private val isolates = ConcurrentHashMap<String, Runner>()

    fun prepare(
        request: JSONObject,
        read: (String) -> ByteArray,
        write: (String, ByteArray) -> Unit,
    ): suspend () -> JSONObject {
        val id = request.getString("isolate")
        val runner =
            if (request.getString("action") == "load")
                isolates.computeIfAbsent(id) { Runner(createRuntime) }
            else isolates[id]
        return {
            check(Build.VERSION.SDK_INT >= 29) { "V8 plugins require Android 10 or later." }
            checkNotNull(runner) { "Plugin isolate is closed" }
            try {
                runner.step(request, read, write)
            } finally {
                if (runner.retired) isolates.remove(id, runner)
            }
        }
    }

    fun detach(id: String): (suspend () -> Unit)? {
        val runner = isolates.remove(id) ?: return null
        runner.retire()
        return {
            try {
                runner.close()
            } catch (failure: Exception) {
                Log.w("MiniRP", "V8 isolate cleanup failed", failure)
            }
        }
    }

    suspend fun close() {
        for (id in isolates.keys.toList()) detach(id)?.invoke()
    }

    private class Runner(private val createRuntime: () -> V8Runtime) {
        private val mutex = Mutex()
        private val interruption = Any()
        private var execution: CancellableContinuation<JSONObject>? = null
        private var runtime: V8Runtime? = null
        @Volatile
        var retired = false
            private set

        suspend fun step(
            request: JSONObject,
            read: (String) -> ByteArray,
            write: (String, ByteArray) -> Unit,
        ): JSONObject = mutex.withLock {
            check(!retired) { "Plugin isolate is closed" }
            try {
                val loaded = runtime != null
                val current = runtime ?: createRuntime().also { runtime = it }
                current
                    .getGuard(2_000)
                    .apply { setDebugModeEnabled(true) }
                    .use { guard ->
                        suspendCancellableCoroutine { continuation ->
                            continuation.invokeOnCancellation {
                                synchronized(interruption) {
                                    if (execution === continuation) {
                                        // Termination can arrive just before V8 starts executing.
                                        guard.setTimeoutMillis(1)
                                        current.terminateExecution()
                                    }
                                }
                            }
                            synchronized(interruption) {
                                execution = continuation
                                if (retired) continuation.cancel()
                            }
                            val ensureActive = {
                                if (!continuation.isActive)
                                    throw CancellationException("Plugin execution was cancelled")
                            }
                            val result = runCatching {
                                ensureActive()
                                execute(current, loaded, request, read, write, ensureActive)
                            }
                            synchronized(interruption) { execution = null }
                            continuation.resumeWith(result)
                        }
                    }
            } catch (failure: Throwable) {
                runCatching { closeLocked() }.exceptionOrNull()?.let(failure::addSuppressed)
                throw failure
            }
        }

        private fun execute(
            runtime: V8Runtime,
            loaded: Boolean,
            request: JSONObject,
            read: (String) -> ByteArray,
            write: (String, ByteArray) -> Unit,
            ensureActive: () -> Unit,
        ): JSONObject {
            val action = request.getString("action")
            if (action == "load") {
                if (!loaded) {
                    runtime.allowEval(false)
                    runtime.getExecutor(request.getString("bootstrap")).executeVoid()
                    runtime.getExecutor(request.getString("source")).executeVoid()
                }
                return JSONObject()
            }
            check(loaded) { "Plugin isolate has not loaded" }
            val run = quote(request.getString("run"))
            if (action == "finish") {
                runtime
                    .getExecutor("__minirp.finish($run,${request.optBoolean("abort")})")
                    .executeVoid()
                return JSONObject()
            }
            if (action == "start") {
                runtime
                    .getExecutor(
                        "__minirp.start(" + run + "," + quote(request.getString("payload")) + ")"
                    )
                    .executeVoid()
            } else if (action == "pull") {
                runtime.getExecutor("__minirp.pull($run)").executeVoid()
            } else {
                val replies = request.getJSONArray("replies")
                for (index in 0 until replies.length()) {
                    ensureActive()
                    val reply = replies.getJSONObject(index)
                    val transfer = reply.optString("transfer")
                    if (transfer.isEmpty()) {
                        runtime
                            .getExecutor(
                                "__minirp.reply(" + run + "," + quote(reply.toString()) + ")"
                            )
                            .executeVoid()
                    } else {
                        val bytes = read(transfer)
                        ensureActive()
                        runtime.createV8ValueTypedArray(V8ValueType.Uint8Array, bytes.size).use {
                            array ->
                            check(array.fromBytes(bytes))
                            runtime.globalObject.get<V8ValueObject>("__minirp").use { bridge ->
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
            val result = JSONObject(runtime.getExecutor("__minirp.poll($run)").executeString())
            if (result.optBoolean("binary")) {
                runtime
                    .getExecutor("__minirp.takeBytes($run)")
                    .execute<V8ValueTypedArray>()
                    .use { data ->
                        check(data.length in 1..65_536) { "Invalid outgoing asset chunk." }
                        write(request.getString("upload"), data.toBytes())
                    }
            }
            return result
        }

        fun retire() {
            synchronized(interruption) {
                retired = true
                execution?.cancel()
            }
        }

        suspend fun close() = mutex.withLock { closeLocked() }

        private fun closeLocked() {
            retired = true
            val former = runtime
            runtime = null
            former?.close()
        }

        private fun quote(text: String) =
            JSONObject.quote(text).replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
    }
}
