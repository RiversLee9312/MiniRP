package com.minirp.app.core

import android.annotation.SuppressLint
import android.content.Context
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebView
import android.webkit.WebViewClient
import com.caoccao.javet.interop.V8Runtime
import com.caoccao.javet.interop.callback.IJavetDirectCallable
import com.caoccao.javet.interop.callback.JavetCallbackContext
import com.caoccao.javet.interop.callback.JavetCallbackType
import com.caoccao.javet.values.primitive.V8ValueInteger
import com.caoccao.javet.values.reference.V8ValueFunction
import com.caoccao.javet.values.reference.V8ValueObject
import java.io.IOException
import java.security.SecureRandom
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import org.json.JSONObject
import org.json.JSONTokener

/** Each plugin has its own keys in a local WebView that evaluates only the bundled provider. */
internal class PluginWebCrypto(private val context: Context, private val provider: String) {
    private var page: WebView? = null
    private var rendererGone = false

    suspend fun run(request: String, ensureActive: () -> Unit): String = withContext(Dispatchers.Main) {
        val view = page ?: open()
        val payload = JSONObject.quote(request).replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
        evaluate(view, """
            globalThis.minirpCryptoResult = null;
            globalThis.minirpCryptoExecute($payload).then(
                result => { globalThis.minirpCryptoResult = JSON.stringify({ result }); },
                () => { globalThis.minirpCryptoResult = JSON.stringify({ failed: true }); }
            );
        """.trimIndent())
        var reply: String
        do {
            ensureActive()
            reply = evaluate(view, "globalThis.minirpCryptoResult")
            if (reply == "null") delay(8)
        } while (reply == "null")
        val result = JSONObject(JSONTokener(reply).nextValue() as String)
        if (result.optBoolean("failed")) throw IOException("The Web Crypto provider failed.")
        result.getString("result")
    }

    @SuppressLint("SetJavaScriptEnabled")
    private suspend fun open(): WebView {
        val view = WebView(context).also { page = it }
        try {
            view.settings.apply {
                javaScriptEnabled = true
                blockNetworkLoads = true
                allowFileAccess = false
                allowContentAccess = false
            }
            suspendCancellableCoroutine { ready ->
                view.webViewClient = object : WebViewClient() {
                    override fun onPageFinished(view: WebView, url: String) {
                        if (ready.isActive) ready.resume(Unit)
                    }

                    override fun onRenderProcessGone(view: WebView, detail: RenderProcessGoneDetail): Boolean {
                        rendererGone = true
                        if (ready.isActive) ready.resumeWithException(IOException("The Web Crypto renderer exited."))
                        return true
                    }
                }
                view.loadDataWithBaseURL("https://minirp-crypto.invalid/", "<meta charset=utf-8>", "text/html", "UTF-8", null)
            }
            val available = evaluate(view, """
                (() => {
                    if (!globalThis.crypto?.subtle) return false;
                    globalThis.minirpCryptoExecute = ($provider)(globalThis.crypto);
                    return true;
                })()
            """.trimIndent())
            check(available == "true") { "Web Crypto is unavailable in Android WebView." }
            return view
        } catch (error: Throwable) {
            view.destroy()
            page = null
            rendererGone = false
            throw error
        }
    }

    private suspend fun evaluate(view: WebView, source: String): String = suspendCancellableCoroutine { reply ->
        if (rendererGone) {
            reply.resumeWithException(IOException("The Web Crypto renderer exited."))
        } else {
            view.evaluateJavascript(source) { result ->
                if (reply.isActive) reply.resume(result ?: "null")
            }
        }
    }

    suspend fun close() = withContext(NonCancellable + Dispatchers.Main.immediate) {
        page?.destroy()
        page = null
    }

    companion object {
        private val entropy by lazy { SecureRandom() }
        private const val Hex = "0123456789abcdef"

        fun install(runtime: V8Runtime, source: String): V8ValueObject {
            val callback = IJavetDirectCallable.NoThisAndResult<Exception> { args ->
                val length = (args.single() as V8ValueInteger).value
                require(length in 0..65_536)
                val bytes = ByteArray(length).also(entropy::nextBytes)
                runtime.createV8ValueString(buildString(length * 2) {
                    for (value in bytes) {
                        val unsigned = value.toInt() and 255
                        append(Hex[unsigned shr 4])
                        append(Hex[unsigned and 15])
                    }
                })
            }
            val context = JavetCallbackContext("entropy", JavetCallbackType.DirectCallNoThisAndResult, callback)
            return runtime.createV8ValueFunction(context).use { random ->
                runtime.getExecutor(source).execute<V8ValueFunction>().use { install ->
                    install.call<V8ValueObject>(null, random)
                }
            }
        }
    }
}
