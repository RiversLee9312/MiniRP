package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.core.CoreClient
import java.io.File
import java.net.InetAddress
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.ZipFile
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ContainerPortabilityTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun restoresWindowsGuestFilesAndBranchesIndependently() = runBlocking {
        val argument = InstrumentationRegistry.getArguments().getString("containerArchive")
        assumeTrue("Pass a Windows container archive to test cross-device restore.", !argument.isNullOrBlank())
        val archive = File(checkNotNull(argument))
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = temporary.newFolder()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val round = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        var jobKey = ""
        var jobUrl = ""
        var checkedOutput = false
        val (preset, chat) = ZipFile(archive).use { zip ->
            val header = zip.getInputStream(zip.getEntry("archive.json")).bufferedReader().use { JSONObject(it.readText()) }
            val preset = header.getString("preset")
            preset to zip.entries().asSequence().single {
                it.name.matches(Regex("conversations/$preset/[0-9a-f-]+\\.json"))
            }.name.substringAfterLast('/').removeSuffix(".json")
        }
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (request.url.encodedPath == "/container-check")
                        return MockResponse.Builder().body("network-ok").build()
                    return dispatchModel(request)
                }

                private fun dispatchModel(request: RecordedRequest): MockResponse = try {
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    val tools = body.optJSONArray("tools")
                    fun call(name: String, args: JSONObject = JSONObject()): JSONObject = JSONObject().put("tool_calls", JSONArray().put(
                        JSONObject().put("index", 0).put("id", "vm-${round.get()}").put("type", "function")
                            .put("function", JSONObject().put("name", name).put("arguments", args.toString()))))
                    fun result(): JSONObject {
                        val messages = body.getJSONArray("messages")
                        val tool = (0 until messages.length()).map(messages::getJSONObject).last { it.optString("role") == "tool" }
                        return JSONObject(tool.getString("content"))
                    }
                    val delta = if (tools == null || tools.length() == 0) {
                        JSONObject().put("content", "Container")
                    } else when (val step = round.getAndIncrement()) {
                        0, 2, 4 -> {
                            assertTrue((0 until tools.length()).any { tools.getJSONObject(it).getJSONObject("function").getString("name") == "shell" })
                            val command = when (step) {
                                0 -> "set -e; apk --version | grep -q 'apk-tools'; apk info --exists apk-tools >/dev/null; " +
                                    "test \"\$(printf '%s' '{\"answer\":42}' | jq -er .answer)\" = 42; " +
                                    "printf '%s\\n' agent-42 | rg -qx 'agent-[0-9]+'; " +
                                    "test \"\$(sqlite3 :memory: 'select 6*7')\" = 42; " +
                                    "pip --version >/dev/null; npm --version >/dev/null; git --version >/dev/null; " +
                                    "pdftotext -v >/dev/null 2>&1; unzip -v >/dev/null; zstd --version >/dev/null; diff --version >/dev/null; " +
                                    "uname -m; cat checkpoint; echo; " +
                                    "curl -fsS --max-time 15 http://10.0.2.2:${server.port}/container-check; printf changed > fork-file"
                                2 -> "set -e; cat checkpoint; test ! -e fork-file; printf child > fork-file"
                                else -> "set -e; cat fork-file"
                            }
                            JSONObject().put("tool_calls", JSONArray().put(JSONObject()
                                .put("index", 0).put("id", "vm-$step").put("type", "function")
                                .put("function", JSONObject().put("name", "shell").put("arguments",
                                    JSONObject().put("command", command).put("seconds", if (step == 0) 120 else 60).toString()))))
                        }
                        1, 3, 5 -> {
                            val reply = result()
                            assertEquals(reply.toString(), 0, reply.optInt("exit_code", -1))
                            assertEquals(listOf("x86_64\noriginal-branch\nnetwork-ok", "original-branch", "changed")[(step - 1) / 2],
                                reply.getString("output"))
                            JSONObject().put("content", "VM $step")
                        }
                        6 -> call("shell", JSONObject().put("command", "(sleep 1; printf resident > background; printf late) & printf started"))
                        7 -> {
                            assertEquals("started", result().getString("output"))
                            call("shell", JSONObject().put("command", "for n in \$(seq 1 20); do test -f background && break; sleep 0.1; done; " +
                                "test \"\$(cat background)\" = resident || exit 8; python3 -c 'print(\"x\"*8192); print(\"timeout marker\", flush=True)'; sleep 20")
                                .put("async", true).put("seconds", 3))
                        }
                        8 -> {
                            val begun = result()
                            assertEquals("running", begun.getString("status"))
                            jobKey = begun.getString("db_key")
                            jobUrl = begun.getString("asset_url")
                            call("db_watch", JSONObject().put("key", jobKey))
                        }
                        9 -> {
                            assertTrue(result().getBoolean("watching"))
                            call("db_read", JSONObject().put("key", jobKey))
                        }
                        else -> {
                            val latest = result()
                            when {
                                latest.has("lines") -> {
                                    assertEquals("timeout marker", latest.getJSONArray("lines").getJSONObject(0).getString("text"))
                                    checkedOutput = true
                                    JSONObject().put("content", "Output recovered")
                                }
                                latest.has("tools") -> call("plugin_base_grep", JSONObject().put("url", jobUrl).put("pattern", "timeout marker"))
                                else -> {
                                    check(latest.has("content")) { latest.toString() }
                                    val status = JSONObject(latest.getString("content"))
                                    if (status.getString("status") == "running") {
                                        Thread.sleep(350)
                                        call("db_read", JSONObject().put("key", jobKey))
                                    } else {
                                        assertEquals("failed", status.getString("status"))
                                        assertEquals(jobUrl, status.getString("asset_url"))
                                        assertTrue(status.getString("error").contains("timed out"))
                                        call("activate_skill", JSONObject().put("name", "view_asset"))
                                    }
                                }
                            }
                        }
                    }
                    val event = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body("data: $event\n\ndata: [DONE]\n\n").build()
                } catch (error: Throwable) {
                    failure.compareAndSet(null, error)
                    MockResponse.Builder().code(500).body("Unexpected model request").build()
                }
            }
            server.start(InetAddress.getByName("127.0.0.1"), 0)
            val core = CoreClient(object : ContextWrapper(app) { override fun getFilesDir() = directory }, scope)
            suspend fun load(command: JSONObject) : JSONObject {
                core.send(command)
                return withTimeout(20_000) {
                    core.events.first {
                        check(it.optString("kind") != "error") { it.toString() }
                        it.optString("kind") == "loaded"
                    }
                }
            }
            suspend fun select(id: String) = load(JSONObject().put("op", "select_chat").put("preset", preset).put("id", id))
            suspend fun turn(input: String, result: String) {
                core.send(JSONObject().put("op", "message").put("text", input))
                val event = withTimeout(180_000) {
                    core.events.first {
                        failure.get()?.let { throw AssertionError("Container fixture failed", it) }
                        check(it.optString("kind") != "error") { it.toString() }
                        it.optString("kind") == "completed"
                    }
                }
                assertEquals(result, event.getString("text"))
            }
            try {
                core.query(JSONObject().put("op", "archive_import").put("type", "save").put("path", archive.path))
                core.send(JSONObject().put("op", "configure").put("provider",
                    JSONObject().put("url", server.url("/v1").toString()).put("model", "fixture")))
                val original = select(chat).getJSONObject("chat")
                val blocks = original.getJSONArray("blocks")
                val checkpoint = (0 until blocks.length()).map(blocks::getJSONObject).last {
                    it.optString("kind") == "reply_end"
                }.getString("id")
                turn("Write a new file after the snapshot.", "VM 1")
                load(JSONObject().put("op", "branch").put("chat", chat).put("mode", "reply").put("block", checkpoint))
                turn("Read the earlier snapshot and write in the branch.", "VM 3")
                select(chat)
                turn("Check the original branch.", "VM 5")
                turn("Verify background work and keep timeout output.", "Output recovered")
                assertTrue(checkedOutput)
                assertNull(failure.get())
            } finally {
                core.close()
                scope.cancel()
            }
        }
    }
}
