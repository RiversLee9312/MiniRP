package com.minirp.app.core

import android.content.Context
import android.widget.FrameLayout
import com.minirp.app.ui.OpenUiPreview
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.OutputStream
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.receiveAsFlow
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.json.JSONArray
import org.json.JSONObject

/** The platform boundary performs only I/O. All agent decisions stay in the NativeAOT core. */
class CoreClient(
    private val context: Context,
    private val scope: CoroutineScope,
    seedDefaultPreset: Boolean = false,
) : AutoCloseable {
    private val owner = SupervisorJob(scope.coroutineContext[Job])
    private val clientScope = CoroutineScope(scope.coroutineContext + owner)
    private val commands = Channel<JSONObject>(Channel.UNLIMITED)
    @Volatile private var closed = false
    private val workspace = context.filesDir.resolve("workspace").apply { mkdirs() }
    private val handle =
        NativeBridge.create(
            workspace.path.toByteArray(),
            seedDefaultPreset,
        )
    private val diceRandom by lazy { SecureRandom() }
    private val http =
        OkHttpClient.Builder()
            .connectTimeout(20, TimeUnit.SECONDS)
            .readTimeout(180, TimeUnit.SECONDS)
            .build()
    private val streamingHttp = http.newBuilder().readTimeout(0, TimeUnit.SECONDS).build()
    private val calls = ConcurrentHashMap<String, Call>()
    private val platformJobs = ConcurrentHashMap<String, Job>()
    private val scripts = PluginScripts()
    private val container = ContainerHost(context, workspace)
    private val curl = CurlHost(context, workspace)
    private val openUiPreview = OpenUiPreview(context) { previewWindow?.invoke() }
    private val pluginHttp =
        http.newBuilder().followRedirects(false).followSslRedirects(false).build()
    private val replies = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private val mutableEvents = Channel<JSONObject>(Channel.UNLIMITED)
    val events = mutableEvents.receiveAsFlow()
    var browser: (suspend (JSONObject) -> JSONObject)? = null
    var picker: (suspend (JSONObject) -> JSONObject)? = null
    internal var previewWindow: (() -> FrameLayout?)? = null

    init {
        check(handle != 0L) { "Could not initialize native core" }
        if (container.available()) send(JSONObject().put("op", "_container_available"))
        if (curl.available) send(JSONObject().put("op", "_curl_available"))
        clientScope.launch(Dispatchers.IO) {
            val temporary = Regex("transfer-[0-9a-f-]{36}|preset-background-[0-9]+\\.media")
            val stale = System.currentTimeMillis() - TimeUnit.DAYS.toMillis(1)
            context.cacheDir.listFiles()?.forEach { file ->
                if (file.isFile && temporary.matches(file.name) && file.lastModified() < stale) file.delete()
            }
        }
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
                            startPlatformJob(event.getString("id")) {
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
                        "plugin_sign",
                        "roll_dice",
                        "agent_asset_sample",
                        "container_runtime",
                        "container_run",
                        "curl_download",
                        "open_ui_catalog",
                        "open_ui_preview",
                        "plugin_http",
                        "plugin_picker" -> {
                            val id = event.getString("id")
                            val script =
                                if (event.getString("kind") == "plugin_script") {
                                    scripts.prepare(event, ::takeBinary) { transfer, bytes ->
                                        check(
                                            NativeBridge.writeBinary(
                                                handle,
                                                transfer.toByteArray(),
                                                bytes,
                                            )
                                        ) {
                                            "Binary transfer was rejected."
                                        }
                                    }
                                } else null
                            startPlatformJob(id) { pluginRequest(event, script) }
                        }
                        "plugin_isolate_close" -> retireIsolate(event.getString("isolate"))
                        "cancel_io" -> {
                            val id = event.optString("id")
                            calls.remove(id)?.cancel()
                            platformJobs.remove(id)?.cancel()
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

    private fun startPlatformJob(id: String, work: suspend () -> Unit) {
        val job = clientScope.launch(Dispatchers.IO, start = CoroutineStart.LAZY) { work() }
        platformJobs[id] = job
        job.invokeOnCompletion { platformJobs.remove(id, job) }
        job.start()
    }

    @OptIn(DelicateCoroutinesApi::class, ExperimentalCoroutinesApi::class)
    private fun retireIsolate(isolate: String) {
        val close = scripts.detach(isolate) ?: return
        // A detached runtime still needs closing if the client stops before this job starts.
        clientScope.launch(Dispatchers.IO, start = CoroutineStart.ATOMIC) {
            withContext(NonCancellable) { close() }
        }
    }

    private suspend fun pluginRequest(event: JSONObject, script: (suspend () -> JSONObject)?) {
        val id = event.getString("id")
        val reply = JSONObject().put("op", "io_reply").put("id", id)
        try {
            val result =
                when (event.getString("kind")) {
                    "plugin_script" -> checkNotNull(script).invoke()
                    "plugin_verify" -> PluginVerifier.verify(event)
                    "plugin_sign" -> PluginSigner.sign(workspace, event)
                    "roll_dice" -> rollDice(event)
                    "open_ui_catalog" -> openUiPreview.catalog(event)
                    "open_ui_preview" -> openUiPreview.render(event)
                    "agent_asset_sample" ->
                        AgentMediaSampler.sample(
                            context.filesDir.resolve("workspace/attachments"),
                            event,
                            context.applicationContext,
                        )
                    "plugin_http" -> pluginHttpRequest(event)
                    "container_runtime", "container_run" -> container.handle(event)
                    "curl_download" -> curl.handle(event)
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

    private fun rollDice(event: JSONObject): JSONObject {
        val count = event.getInt("count")
        val sides = event.getInt("sides")
        require(count in 1..30 && sides in 2..1000) { "Invalid dice limits." }
        val rolls = JSONArray()
        repeat(count) { rolls.put(diceRandom.nextInt(sides) + 1) }
        return JSONObject().put("rolls", rolls)
    }

    private suspend fun pluginHttpRequest(event: JSONObject): JSONObject {
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
        return suspendCancellableCoroutine { continuation ->
            continuation.invokeOnCancellation { call.cancel() }
            call.enqueue(
                object : Callback {
                    override fun onFailure(call: Call, e: IOException) {
                        continuation.resumeWith(Result.failure(e))
                    }

                    override fun onResponse(call: Call, response: Response) {
                        continuation.resumeWith(
                            runCatching {
                                response.use {
                                    val source = it.body.source()
                                    check(!source.request(1_048_577)) {
                                        "Plugin HTTP response exceeded 1 MiB."
                                    }
                                    val output = JSONObject()
                                    it.headers.names().forEach { name ->
                                        output.put(
                                            name.lowercase(),
                                            it.headers.values(name).joinToString(", "),
                                        )
                                    }
                                    JSONObject()
                                        .put("status", it.code)
                                        .put("headers", output)
                                        .put("body", source.readUtf8())
                                }
                            }
                        )
                    }
                }
            )
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
            val request = JSONObject().put("op", "asset_open").put("url", url)
                .put("agent", agent).put("attachment", attachment)
            val output = ByteArrayOutputStream()
            copyAsset(request, limit.toLong(), output)
            output.toByteArray()
        }

    internal suspend fun savePresetBackground(preset: String, url: String, destination: File): String =
        withContext(Dispatchers.IO) {
            val request = JSONObject().put("op", "preset_background_open").put("preset", preset).put("url", url)
            destination.outputStream().buffered().use { copyAsset(request, 128L * 1024 * 1024, it) }
        }

    private suspend fun copyAsset(request: JSONObject, limit: Long, output: OutputStream): String {
        return copyOpenedAsset(query(request).getJSONObject("result"), limit, output)
    }

    internal suspend fun displayAssetInfo(request: JSONObject): JSONObject =
        query(request).getJSONObject("result").also { closeAsset(it.getString("handle")) }

    internal suspend fun saveDisplayAsset(request: JSONObject, output: OutputStream) {
        copyAsset(request, Long.MAX_VALUE, output)
    }

    internal suspend fun openDisplayMedia(request: JSONObject, accepts: (String) -> Boolean): Pair<String, java.io.InputStream>? {
        val info = query(request).getJSONObject("result")
        val handle = info.getString("handle")
        val mime = info.getString("mimeType").substringBefore(';').trim().lowercase(java.util.Locale.ROOT)
        if (!accepts(mime)) {
            closeAsset(handle)
            return null
        }
        return mime to object : java.io.InputStream() {
            private var chunk = ByteArray(0)
            private var position = 0
            private var received = 0
            @Volatile private var ended = false

            override fun read(): Int {
                val one = ByteArray(1)
                return if (read(one, 0, 1) < 0) -1 else one[0].toInt() and 255
            }

            override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
                if (length == 0) return 0
                if (ended) return -1
                try {
                    if (position == chunk.size) {
                        chunk = kotlinx.coroutines.runBlocking { pullAsset(handle) } ?: run {
                            close()
                            return -1
                        }
                        received += chunk.size
                        check(received <= 128 * 1024 * 1024) { "Asset exceeds the permitted size." }
                        position = 0
                    }
                    val count = minOf(length, chunk.size - position)
                    chunk.copyInto(buffer, offset, position, position + count)
                    position += count
                    return count
                } catch (error: Exception) {
                    close()
                    throw java.io.IOException("Could not read the Asset.", error)
                }
            }

            override fun close() {
                if (ended) return
                ended = true
                closeAsset(handle)
            }
        }
    }

    private suspend fun pullAsset(handle: String): ByteArray? {
        val part = query(JSONObject().put("op", "asset_pull").put("handle", handle)).getJSONObject("result")
        return if (part.optBoolean("done")) null else takeBinary(part.getString("transfer"))
    }

    private fun closeAsset(handle: String) = send(JSONObject().put("op", "asset_close").put("handle", handle))

    private suspend fun copyOpenedAsset(info: JSONObject, limit: Long, output: OutputStream): String {
        val stream = info.getString("handle")
        try {
            var total = 0L
            while (true) {
                val bytes = pullAsset(stream) ?: break
                total += bytes.size
                check(total <= limit) { "Asset exceeds the permitted size." }
                output.write(bytes)
            }
            return info.getString("mimeType")
        } finally {
            closeAsset(stream)
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
            val streaming = event.optBoolean("stream")
            val call = (if (streaming) streamingHttp else http).newCall(builder.build())
            calls[id] = call
            call.execute().use { response ->
                if (!response.isSuccessful || !streaming) {
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
            try {
                scripts.close()
            } finally {
                NativeBridge.destroy(handle)
            }
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
