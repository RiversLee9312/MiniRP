package com.minichat.app.core

import android.annotation.SuppressLint
import android.content.Context
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import kotlin.coroutines.resume
import kotlinx.coroutines.*
import org.json.JSONObject
import org.json.JSONTokener

/**
 * Isolated, disposable WebViews expose semantic operations, never arbitrary model-supplied code.
 */
class BrowserHost(private val context: Context) : AutoCloseable {
    private val pages = mutableMapOf<String, WebView>()
    private val script by lazy {
        context.assets.open("browser.js").bufferedReader().use { it.readText() }
    }
    private val turndown by lazy {
        context.assets.open("turndown.js").bufferedReader().use { it.readText() }
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun page(job: String): WebView =
        pages.getOrPut(job) {
            WebView(context).apply {
                settings.javaScriptEnabled = true
                settings.domStorageEnabled = true
                settings.allowFileAccess = false
                settings.allowContentAccess = false
                settings.setSupportMultipleWindows(false)
                settings.setGeolocationEnabled(false)
                settings.userAgentString = settings.userAgentString.replace("; wv", "")
                layout(0, 0, 1280, 900)
                webViewClient =
                    object : WebViewClient() {
                        override fun shouldOverrideUrlLoading(
                            view: WebView,
                            request: WebResourceRequest,
                        ): Boolean = request.url.scheme !in setOf("http", "https")
                    }
            }
        }

    private suspend fun evaluate(view: WebView, source: String): String =
        suspendCancellableCoroutine { continuation ->
            view.evaluateJavascript(source) { result ->
                if (continuation.isActive)
                    continuation.resume((JSONTokener(result).nextValue() as? String) ?: result)
            }
        }

    suspend fun execute(event: JSONObject): JSONObject =
        withContext(Dispatchers.Main) {
            withTimeout(120_000) {
                val job = event.getString("job")
                val tool = event.getString("tool")
                if (tool == "browser_close") {
                    pages.remove(job)?.destroy()
                    return@withTimeout JSONObject().put("closed", true)
                }
                val view = page(job)
                val args = event.optJSONObject("arguments") ?: JSONObject()
                when (tool) {
                    "browser_navigate" -> {
                        val url = args.getString("url")
                        require(android.net.Uri.parse(url).scheme in setOf("http", "https"))
                        view.loadUrl(url)
                        delay(500)
                        repeat(80) {
                            if (evaluate(view, "document.readyState") == "complete") return@repeat
                            delay(100)
                        }
                    }
                    "browser_wait" -> delay(args.optInt("seconds", 1).coerceIn(1, 10) * 1000L)
                }
                evaluate(view, script)
                if (tool == "capture_page") {
                    evaluate(view, turndown)
                    val metadata =
                        JSONObject(evaluate(view, "JSON.stringify(window.minichat.capture())"))
                    val body = StringBuilder()
                    var offset = 0
                    val length = metadata.getInt("length")
                    while (offset < length) {
                        body.append(
                            evaluate(
                                view,
                                "window.minichat.content.slice($offset,${offset + 16000})",
                            )
                        )
                        offset += 16000
                    }
                    metadata.put("content", body.toString())
                } else {
                    val json = JSONObject.quote(args.toString())
                    JSONObject(
                        evaluate(
                            view,
                            "JSON.stringify(window.minichat.perform(${JSONObject.quote(tool)},JSON.parse($json)))",
                        )
                    )
                }
            }
        }

    override fun close() {
        pages.values.forEach(WebView::destroy)
        pages.clear()
    }
}
