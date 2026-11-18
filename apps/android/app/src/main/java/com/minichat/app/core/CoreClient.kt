package com.minichat.app.core

import android.content.Context
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
class CoreClient(context: Context, private val scope: CoroutineScope) : AutoCloseable {
    private val owner = SupervisorJob(scope.coroutineContext[Job])
    private val clientScope = CoroutineScope(scope.coroutineContext + owner)
    private val commands = Channel<JSONObject>(Channel.UNLIMITED)
    @Volatile private var closed = false
    private val handle =
        NativeBridge.create(
            context.filesDir.resolve("workspace").apply { mkdirs() }.path.toByteArray()
        )
    private val http =
        OkHttpClient.Builder()
            .connectTimeout(20, TimeUnit.SECONDS)
            .readTimeout(180, TimeUnit.SECONDS)
            .build()
    private val calls = ConcurrentHashMap<String, Call>()
    private val replies = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private val mutableEvents = Channel<JSONObject>(Channel.UNLIMITED)
    val events = mutableEvents.receiveAsFlow()
    var browser: (suspend (JSONObject) -> JSONObject)? = null

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
                if (result.has("error"))
                    mutableEvents.send(
                        JSONObject().put("kind", "error").put("message", result.optString("error"))
                    )
            }
        }
        clientScope.launch(Dispatchers.IO) {
            while (isActive) {
                val events = JSONArray(NativeBridge.poll(handle).toString(Charsets.UTF_8))
                for (index in 0 until events.length()) {
                    val event = events.getJSONObject(index)
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
                        "cancel_io" -> calls.remove(event.optString("id"))?.cancel()
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

    fun send(command: JSONObject) {
        if (!closed) commands.trySend(command).getOrThrow()
    }

    suspend fun query(command: JSONObject): JSONObject {
        val id = java.util.UUID.randomUUID().toString()
        val reply = CompletableDeferred<JSONObject>()
        replies[id] = reply
        try {
            send(command.put("request", id))
            val result = withTimeoutOrNull(20_000) { reply.await() } ?: error("Request timed out")
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
        clientScope.launch(NonCancellable + Dispatchers.IO) { NativeBridge.destroy(handle) }
    }
}
