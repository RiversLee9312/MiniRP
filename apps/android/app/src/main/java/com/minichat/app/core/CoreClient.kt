package com.minichat.app.core

import android.content.Context
import java.io.ByteArrayOutputStream
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.receiveAsFlow
import okhttp3.Call
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject

/** The platform boundary performs only I/O. All agent decisions stay in the NativeAOT core. */
class CoreClient(
    context: Context,
    private val scope: CoroutineScope,
    seedDefaultPreset: Boolean = false,
) : AutoCloseable {
    private val owner = SupervisorJob(scope.coroutineContext[Job])
    private val clientScope = CoroutineScope(scope.coroutineContext + owner)
    private val commands = Channel<JSONObject>(Channel.UNLIMITED)
    @Volatile private var closed = false
    private val handle =
        NativeBridge.create(
            context.filesDir.resolve("workspace").apply { mkdirs() }.path.toByteArray(),
            seedDefaultPreset,
        )
    private val http =
        OkHttpClient.Builder()
            .connectTimeout(20, TimeUnit.SECONDS)
            .readTimeout(180, TimeUnit.SECONDS)
            .build()
    private val calls = ConcurrentHashMap<String, Call>()
    private val pluginJobs = ConcurrentHashMap<String, Job>()
    private val scripts = PluginScripts()
    private val pluginHttp =
        http.newBuilder().followRedirects(false).followSslRedirects(false).build()
    private val replies = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private val mutableEvents = Channel<JSONObject>(Channel.UNLIMITED)
    val events = mutableEvents.receiveAsFlow()
    var browser: (suspend (JSONObject) -> JSONObject)? = null
    var picker: (suspend (JSONObject) -> JSONObject)? = null

    init {
        check(handle != 0L) { "Could not initialize native core" }
        clientScope.launch(Dispatchers.IO) {
            for (command in commands) {
                if (closed) break
                val result =
                    JSONObject(
                        NativeBridge.dispatch(handle, command.toString().toByteArray())
                            .toString(Charsets.UTF_8)
                    )
                if (result.has("error")) {
                    val error =
                        JSONObject().put("kind", "error").put("message", result.optString("error"))
                    val reply = replies.remove(command.optString("request"))
                    if (reply != null) reply.complete(error) else mutableEvents.send(error)
                } else if (command.optString("op") == "steer") {
                    replies.remove(command.optString("request"))?.complete(result)
                }
            }
        }
        clientScope.launch(Dispatchers.IO) {
            while (isActive) {
                val events = JSONArray(NativeBridge.poll(handle).toString(Charsets.UTF_8))
                for (event in events.coalescedDeltas()) {
                    when (event.optString("kind")) {
                        "http_request" -> launch { request(event) }
                        "browser_request" ->
                            launch {
                                val reply =
                                    JSONObject()
                                        .put("op", "io_reply")
                                        .put("id", event.getString("id"))
                                try {
                                    reply.put(
                                        "result",
                                        browser?.invoke(event) ?: error("Browser unavailable"),
                                    )
                                } catch (error: Exception) {
                                    reply.put("error", error.message ?: "Browser failed")
                                }
                                send(reply)
                            }
                        "plugin_script",
                        "plugin_verify",
                        "plugin_http",
                        "plugin_picker" -> {
                            val id = event.getString("id")
                            val job =
                                launch(start = CoroutineStart.LAZY) {
                                    try {
                                        pluginRequest(event)
                                    } finally {
                                        pluginJobs.remove(id)
                                    }
                                }
                            pluginJobs[id] = job
                            job.start()
                        }
                        "plugin_script_close" ->
                            launch {
                                scripts.finish(event.getString("isolate"), event.getString("run"))
                            }
                        "plugin_isolate_close" ->
                            launch { scripts.close(event.getString("isolate")) }
                        "cancel_io" -> {
                            val id = event.optString("id")
                            calls.remove(id)?.cancel()
                            pluginJobs.remove(id)?.cancel()
                        }
                        else -> {
                            val reply = replies.remove(event.optString("request"))
                            if (reply != null) reply.complete(event) else mutableEvents.send(event)
                        }
                    }
                }
                delay(16)
            }
        }
    }

    private suspend fun pluginRequest(event: JSONObject) {
        val id = event.getString("id")
        val reply = JSONObject().put("op", "io_reply").put("id", id)
        try {
            val result =
                when (event.getString("kind")) {
                    "plugin_script" ->
                        scripts.step(event, ::takeBinary) { id, bytes ->
                            check(NativeBridge.writeBinary(handle, id.toByteArray(), bytes)) {
                                "Binary transfer was rejected."
                            }
                        }
                    "plugin_verify" -> PluginVerifier.verify(event)
                    "plugin_http" -> pluginHttpRequest(event)
                    else -> picker?.invoke(event) ?: error("No picker is available")
                }
            reply.put("result", result)
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            reply.put("error", failure.message ?: "Plugin request failed")
        }
        send(reply)
    }

    private fun pluginHttpRequest(event: JSONObject): JSONObject {
        val builder = Request.Builder().url(event.getString("url"))
        val headers = event.optJSONObject("headers") ?: JSONObject()
        headers.keys().forEach {
            if (!it.equals("Host", true)) builder.header(it, headers.getString(it))
        }
        val method = event.getString("method")
        builder.method(
            method,
            if (method == "GET" || method == "HEAD") null
            else
                event
                    .optString("body")
                    .toRequestBody(
                        (headers.optString("content-type").ifBlank { "text/plain" }).toMediaType()
                    ),
        )
        val call = pluginHttp.newCall(builder.build())
        call.timeout().timeout(event.optLong("timeout_ms", 15_000), TimeUnit.MILLISECONDS)
        val id = event.getString("id")
        calls[id] = call
        try {
            call.execute().use { response ->
                val source = response.body.source()
                check(!source.request(1_048_577)) { "Plugin HTTP response exceeded 1 MiB." }
                val output = JSONObject()
                response.headers.names().forEach {
                    output.put(it.lowercase(), response.headers.values(it).joinToString(", "))
                }
                return JSONObject()
                    .put("status", response.code)
                    .put("headers", output)
                    .put("body", source.readUtf8())
            }
        } finally {
            calls.remove(id)
        }
    }

    fun send(command: JSONObject) {
        if (!closed) commands.trySend(command).getOrThrow()
    }

    fun takeBinary(id: String): ByteArray =
        checkNotNull(NativeBridge.takeBinary(handle, id.toByteArray())) {
            "Binary chunk is unavailable."
        }

    suspend fun readAsset(
        url: String,
        limit: Int,
        agent: String = "",
        attachment: JSONObject? = null,
    ): ByteArray =
        withContext(Dispatchers.IO) {
            val info =
                query(
                        JSONObject()
                            .put("op", "asset_open")
                            .put("url", url)
                            .put("agent", agent)
                            .put("attachment", attachment)
                    )
                    .getJSONObject("result")
            val stream = info.getString("handle")
            try {
                val output = ByteArrayOutputStream()
                while (true) {
                    val part =
                        query(JSONObject().put("op", "asset_pull").put("handle", stream))
                            .getJSONObject("result")
                    if (part.optBoolean("done")) break
                    val bytes = takeBinary(part.getString("transfer"))
                    check(output.size() + bytes.size <= limit) {
                        "Asset exceeds the permitted size."
                    }
                    output.write(bytes)
                }
                output.toByteArray()
            } finally {
                send(JSONObject().put("op", "asset_close").put("handle", stream))
            }
        }

    suspend fun query(command: JSONObject, timeoutMillis: Long? = 20_000): JSONObject {
        val id = java.util.UUID.randomUUID().toString()
        val reply = CompletableDeferred<JSONObject>()
        replies[id] = reply
        try {
            send(command.put("request", id))
            val result =
                if (timeoutMillis == null) reply.await()
                else
                    withTimeoutOrNull(timeoutMillis) { reply.await() } ?: error("Request timed out")
            if (result.optString("kind") == "cancelled") return result
            check(result.optString("kind") != "error") { result.optString("message") }
            check(!result.has("error")) { result.optString("error") }
            return result
        } finally {
            replies.remove(id)
        }
    }

    private fun request(event: JSONObject) {
        val id = event.getString("id")
        val reply = { data: String, done: Boolean, status: Int ->
            send(
                JSONObject()
                    .put("op", "io_reply")
                    .put("id", id)
                    .put("data", data)
                    .put("done", done)
                    .put("status", status)
            )
        }
        try {
            val headers = event.optJSONObject("headers") ?: JSONObject()
            val builder = Request.Builder().url(event.getString("url"))
            headers.keys().forEach { builder.header(it, headers.getString(it)) }
            if (event.optString("method") == "POST")
                builder.post(
                    event.optString("body").toRequestBody("application/json".toMediaType())
                )
            val call = http.newCall(builder.build())
            calls[id] = call
            call.execute().use { response ->
                if (!response.isSuccessful || !event.optBoolean("stream")) {
                    reply(response.body.string(), true, response.code)
                } else {
                    response.body.byteStream().use { reader ->
                        val bytes = ByteArray(8192)
                        while (true) {
                            val count = reader.read(bytes)
                            if (count < 0) break
                            send(
                                JSONObject()
                                    .put("op", "io_reply")
                                    .put("id", id)
                                    .put(
                                        "data_base64",
                                        android.util.Base64.encodeToString(
                                            bytes,
                                            0,
                                            count,
                                            android.util.Base64.NO_WRAP,
                                        ),
                                    )
                                    .put("status", response.code)
                            )
                        }
                    }
                    reply("", true, response.code)
                }
            }
        } catch (error: Exception) {
            send(
                JSONObject()
                    .put("op", "io_reply")
                    .put("id", id)
                    .put("error", error.message ?: "Network failed")
            )
        } finally {
            calls.remove(id)
        }
    }

    override fun close() {
        closed = true
        commands.close()
        owner.cancel()
        calls.values.forEach(Call::cancel)
        clientScope.launch(NonCancellable + Dispatchers.IO) {
            owner.join()
            scripts.close()
            NativeBridge.destroy(handle)
        }
    }
}

private fun JSONArray.coalescedDeltas(): List<JSONObject> = buildList {
    var index = 0
    while (index < length()) {
        val event = getJSONObject(index++)
        val kind = event.optString("kind")
        if (kind == "delta" || kind == "open_ui_delta") {
            val block = event.optString("block")
            val type = event.optString("type")
            val nextText = StringBuilder(event.optString("text"))
            while (index < length()) {
                val next = getJSONObject(index)
                if (
                    next.optString("kind") != kind ||
                        next.optString("block") != block ||
                        next.optString("type") != type
                )
                    break
                nextText.append(next.optString("text"))
                index++
            }
            event.put("text", nextText.toString())
        }
        add(event)
    }
}
