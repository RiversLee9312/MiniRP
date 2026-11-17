package com.minichat.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.minichat.app.core.BrowserHost
import com.minichat.app.core.CoreClient
import java.io.File
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NativeCoreTest {
    @Test
    fun nativeCoreStreamsToolsAndPersistsUnicodeMemory() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val directory = File(app.cacheDir, "native-probe-${System.nanoTime()}").apply { mkdirs() }
        val context =
            object : ContextWrapper(app) {
                override fun getFilesDir(): File = directory
            }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val server = MockWebServer()
        server.start()
        val tool =
            JSONObject()
                .put("index", 0)
                .put("id", "memory-write")
                .put("type", "function")
                .put(
                    "function",
                    JSONObject()
                        .put("name", "db_write")
                        .put("arguments", """{"key":"probe/answer","content":"完整正文🙂"}"""),
                )
        fun event(delta: JSONObject) =
            "data: " +
                JSONObject()
                    .put("choices", org.json.JSONArray().put(JSONObject().put("delta", delta))) +
                "\n\ndata: [DONE]\n\n"
        server.enqueue(
            MockResponse.Builder()
                .setHeader("Content-Type", "text/event-stream")
                .body(
                    event(
                        JSONObject()
                            .put("reasoning_content", "记住任务")
                            .put("tool_calls", org.json.JSONArray().put(tool))
                    )
                )
                .build()
        )
        server.enqueue(
            MockResponse.Builder()
                .setHeader("Content-Type", "text/event-stream")
                .body(event(JSONObject().put("content", "已保存🙂")))
                .build()
        )
        val core = CoreClient(context, scope)
        try {
            core.send(
                JSONObject()
                    .put("op", "configure")
                    .put(
                        "provider",
                        JSONObject()
                            .put("protocol", "openai")
                            .put("url", server.url("/v1").toString())
                            .put("model", "fixture")
                            .put("key", "test"),
                    )
            )
            core.send(JSONObject().put("op", "select_chat").put("preset", "default"))
            core.send(JSONObject().put("op", "message").put("text", "记住任务"))
            var toolFinished = false
            var thoughts = false
            val completed =
                withTimeout(30_000) {
                    core.events.first { event ->
                        check(event.optString("kind") != "error") { event.toString() }
                        if (event.optString("kind") == "tool_end") toolFinished = true
                        if (event.optString("type") == "reasoning") thoughts = true
                        event.optString("kind") == "completed"
                    }
                }
            assertEquals("已保存🙂", completed.getString("text"))
            assertTrue(toolFinished)
            assertTrue(thoughts)
            core.send(
                JSONObject()
                    .put("op", "memory")
                    .put("tool", "db_read")
                    .put("arguments", JSONObject().put("key", "probe/answer"))
            )
            val memory =
                withTimeout(10_000) { core.events.first { it.optString("kind") == "memory" } }
            assertEquals("完整正文🙂", memory.getJSONObject("result").getString("content"))
        } finally {
            core.close()
            scope.cancel()
            server.close()
        }
    }

    @Test
    fun webViewCapturesCompleteMarkdown() = runBlocking {
        val app = ApplicationProvider.getApplicationContext<Context>()
        val server = MockWebServer()
        server.start()
        val html =
            "<html><head><title>Local capture</title></head><body><h1>Article</h1><p>" +
                "完整正文 retained text. ".repeat(10_000) +
                "</p><a href='/end'>FINALMARKER</a></body></html>"
        repeat(3) {
            server.enqueue(
                MockResponse.Builder()
                    .setHeader("Content-Type", "text/html; charset=utf-8")
                    .body(html)
                    .build()
            )
        }
        val browser = BrowserHost(app)
        fun request(tool: String, arguments: JSONObject = JSONObject()) =
            JSONObject().put("job", "probe").put("tool", tool).put("arguments", arguments)
        try {
            browser.execute(
                request("browser_navigate", JSONObject().put("url", server.url("/").toString()))
            )
            val page = browser.execute(request("capture_page"))
            assertTrue(page.getString("content").length > 150_000)
            assertTrue(page.getString("content").contains("FINALMARKER"))
            assertTrue(page.getString("content").contains(server.url("/end").toString()))
        } finally {
            withContext(Dispatchers.Main) { browser.close() }
            server.close()
        }
    }
}
