package com.minichat.app.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.util.LruCache
import android.view.MotionEvent
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.minichat.app.R
import java.util.Locale
import org.json.JSONObject

data class OpenUiInteractions(
    val reply: (String, String, JSONObject) -> Boolean,
    val restore: (String) -> String,
    val save: (String, String) -> Unit,
)

@Composable
internal fun OpenUiReply(
    block: TraceBlock,
    interactions: OpenUiInteractions?,
    pages: OpenUiPageCache,
) {
    var expanded by rememberSaveable { mutableStateOf(false) }
    var measuredHeight by rememberSaveable { mutableIntStateOf(120) }
    var loaded by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    val title = stringResource(R.string.interactive_reply)
    val context = LocalContext.current
    val supported = remember {
        WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)
    }
    if (!supported) {
        Text(stringResource(R.string.interactive_reply_unavailable))
        return
    }

    fun closeExpanded() {
        if (!expanded) return
        // Reopen the preview with any form changes made in the sheet.
        pages.discard(block.id)
        expanded = false
    }

    fun handleMessage(message: JSONObject) {
        when (message.optString("type")) {
            "height" -> measuredHeight = message.optInt("height", 120).coerceIn(72, 4096)
            "rendered" -> {
                loaded = true
                failed = false
            }
            "error" -> {
                failed = true
                loaded = false
            }
            "state" ->
                message.optJSONObject("value")?.let {
                    interactions?.save?.invoke(block.id, it.toString())
                }
            "reply" -> {
                val text = message.optString("text").trim()
                if (text.length !in 1..2000) return
                val data =
                    JSONObject()
                        .put("form", message.optJSONObject("form"))
                        .put("context", message.opt("context"))
                if (
                    data.toString().length <= 16384 &&
                        interactions?.reply?.invoke(block.id, text, data) == true
                ) {
                    closeExpanded()
                }
            }
            "link" -> {
                val uri = Uri.parse(message.optString("url"))
                if (uri.scheme == "https" && !uri.host.isNullOrBlank() && uri.userInfo == null)
                    runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, uri)) }
            }
        }
    }

    Surface(
        color = MaterialTheme.colorScheme.surfaceContainerLow,
        shape = MaterialTheme.shapes.large,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column {
            Box(
                Modifier.fillMaxWidth().height(measuredHeight.coerceAtMost(420).dp),
                contentAlignment = Alignment.Center,
            ) {
                if (!expanded && !failed)
                    OpenUiWebView(
                        block,
                        interactions,
                        Modifier.matchParentSize(),
                        false,
                        pages,
                        ::handleMessage,
                    )
                if (!loaded && !failed)
                    CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                if (failed && !loaded)
                    Text(
                        stringResource(R.string.interactive_reply_unavailable),
                        Modifier.padding(12.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
            }
            if (measuredHeight > 420 && !failed)
                TextButton(
                    onClick = { expanded = true },
                    modifier = Modifier.align(Alignment.End).padding(horizontal = 6.dp),
                ) {
                    Text(stringResource(R.string.expand))
                }
        }
    }
    if (expanded && !failed)
        FullSheet(title, onDismiss = ::closeExpanded) {
            OpenUiWebView(
                block,
                interactions,
                Modifier.weight(1f).fillMaxWidth(),
                true,
                null,
                ::handleMessage,
            )
        }
}

@Composable
private fun OpenUiWebView(
    block: TraceBlock,
    interactions: OpenUiInteractions?,
    modifier: Modifier,
    expanded: Boolean,
    pages: OpenUiPageCache?,
    onMessage: (JSONObject) -> Unit,
) {
    val dark = isSystemInDarkTheme()
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val locale = LocalConfiguration.current.locales[0].toLanguageTag()
    val colors = MaterialTheme.colorScheme
    val theme =
        remember(colors) {
            JSONObject()
                .put("foreground", colors.surface.rgb())
                .put("textNeutralPrimary", colors.onSurface.rgb())
                .put("textNeutralSecondary", colors.onSurfaceVariant.rgb())
                .put("intelligentSurface", colors.surfaceContainer.rgb())
                .put("intelligentSurfaceStrong", colors.surfaceContainerHigh.rgb())
                .put("intelligentBorder", colors.outlineVariant.rgb())
                .put("intelligentFocus", colors.primary.rgb())
                .put("intelligentRangeThumb", colors.primary.rgb())
                .put("intelligentChart1", colors.primary.rgb())
                .put("intelligentChart2", colors.secondary.rgb())
                .put("intelligentChart3", colors.tertiary.rgb())
        }
    val payload =
        remember(block.openUi, block.streaming, dark, rtl, locale, theme, expanded) {
            JSONObject()
                .put("program", block.openUi.orEmpty())
                .put("streaming", block.streaming)
                .put("dark", dark)
                .put("rtl", rtl)
                .put("locale", locale)
                .put("theme", theme)
                .put("expanded", expanded)
                .toString()
        }
    AndroidView(
        factory = ::OpenUiSlot,
        modifier = modifier,
        update = { slot ->
            slot.bind(block.id, expanded, pages, onMessage).render(payload) {
                interactions?.restore?.invoke(block.id)
            }
        },
        onReset = OpenUiSlot::unbind,
        onRelease = OpenUiSlot::unbind,
    )
}

private fun Color.rgb(): String = String.format(Locale.ROOT, "#%06x", toArgb() and 0xffffff)

internal class OpenUiPageCache {
    // The LazyColumn recycles the host. Keep a small number of detached documents by card ID.
    private val idle =
        object : LruCache<String, OpenUiPage>(4) {
            override fun entryRemoved(
                evicted: Boolean,
                key: String,
                oldValue: OpenUiPage,
                newValue: OpenUiPage?,
            ) {
                if (evicted || newValue != null) oldValue.destroy()
            }
        }
    private var closed = false

    fun take(id: String): OpenUiPage? {
        val page = idle.remove(id) ?: return null
        if (!page.failed) return page
        page.destroy()
        return null
    }

    fun release(id: String, page: OpenUiPage) {
        if (closed || page.failed) page.destroy() else idle.put(id, page)
    }

    fun discard(id: String) {
        idle.remove(id)?.destroy()
    }

    fun close() {
        closed = true
        idle.evictAll()
    }
}

private class OpenUiSlot(context: Context) : FrameLayout(context) {
    private var cardId: String? = null
    private var cache: OpenUiPageCache? = null

    fun bind(
        id: String,
        expanded: Boolean,
        pages: OpenUiPageCache?,
        onMessage: (JSONObject) -> Unit,
    ): OpenUiPage {
        if (cardId == id && cache === pages) {
            return (getChildAt(0) as OpenUiPage).apply { callback = onMessage }
        }
        unbind()
        val page = pages?.take(id) ?: OpenUiPage(context, expanded)
        page.tag = id
        cardId = id
        cache = pages
        addView(page, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        page.attach(onMessage)
        return page
    }

    fun unbind() {
        val id = cardId ?: return
        val page = getChildAt(0) as OpenUiPage
        page.detach()
        removeView(page)
        val pages = cache
        if (pages == null) page.destroy() else pages.release(id, page)
        cardId = null
        cache = null
    }
}

internal class OpenUiPage(
    context: Context,
    private val ownsVerticalScroll: Boolean,
) : WebView(context) {
    private var ready = false
    private var latest = ""
    private var initialState: JSONObject? = null
    private var height: JSONObject? = null
    private var status: JSONObject? = null
    private var pendingState: JSONObject? = null
    var callback: ((JSONObject) -> Unit)? = null
    val failed: Boolean
        get() = status?.optString("type") == "error"

    private val assetLoader =
        WebViewAssetLoader.Builder()
            .setDomain("minichat-ui.invalid")
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(context))
            .build()

    init {
        settings.javaScriptEnabled = true
        settings.allowFileAccess = false
        settings.allowContentAccess = false
        settings.javaScriptCanOpenWindowsAutomatically = false
        settings.setSupportMultipleWindows(false)
        setBackgroundColor(android.graphics.Color.TRANSPARENT)
        isVerticalScrollBarEnabled = false
        webViewClient =
            object : WebViewClient() {
                override fun shouldOverrideUrlLoading(
                    view: WebView,
                    request: WebResourceRequest,
                ): Boolean = true

                override fun shouldInterceptRequest(
                    view: WebView,
                    request: WebResourceRequest,
                ): WebResourceResponse? = assetLoader.shouldInterceptRequest(request.url)

                override fun onReceivedError(
                    view: WebView,
                    request: WebResourceRequest,
                    error: WebResourceError,
                ) {
                    if (request.isForMainFrame) receive(JSONObject().put("type", "error"))
                }

                override fun onRenderProcessGone(
                    view: WebView,
                    detail: RenderProcessGoneDetail,
                ): Boolean {
                    receive(JSONObject().put("type", "error"))
                    return true
                }
            }
        WebViewCompat.addWebMessageListener(this, "MinichatSurface", setOf(Origin)) {
            _,
            message,
            source,
            isMainFrame,
            _ ->
            if (!isMainFrame || source.toString().trimEnd('/') != Origin)
                return@addWebMessageListener
            val text = message.data ?: return@addWebMessageListener
            if (text.length > 40000) return@addWebMessageListener
            val event = runCatching { JSONObject(text) }.getOrNull() ?: return@addWebMessageListener
            if (event.optString("type") == "ready") {
                ready = true
                sendLatest()
            } else receive(event)
        }
        loadUrl("$Origin/assets/openui/index.html")
    }

    fun attach(onMessage: (JSONObject) -> Unit) {
        callback = onMessage
        onResume()
        height?.let(onMessage)
        status?.let(onMessage)
        pendingState?.let(onMessage)
        pendingState = null
    }

    fun detach() {
        callback = null
        onPause()
    }

    private fun receive(event: JSONObject) {
        when (event.optString("type")) {
            "height" -> height = event
            "rendered",
            "error" -> status = event
            "state" -> if (callback == null) pendingState = event
        }
        callback?.invoke(event)
    }

    fun render(payload: String, restore: () -> String?) {
        if (latest == payload) return
        if (latest.isEmpty()) {
            initialState = restore()?.let { runCatching { JSONObject(it) }.getOrNull() }
        }
        latest = payload
        sendLatest()
    }

    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        // Keep WebView gestures in the expanded body. The sheet's handle remains draggable.
        if (ownsVerticalScroll && event.actionMasked == MotionEvent.ACTION_DOWN) {
            parent?.requestDisallowInterceptTouchEvent(true)
        }
        val handled = super.dispatchTouchEvent(event)
        if (
            ownsVerticalScroll &&
                (!handled ||
                    event.actionMasked == MotionEvent.ACTION_UP ||
                    event.actionMasked == MotionEvent.ACTION_CANCEL)
        ) {
            parent?.requestDisallowInterceptTouchEvent(false)
        }
        return handled
    }

    private fun sendLatest() {
        if (!ready || latest.isEmpty()) return
        val payload = initialState?.let { JSONObject(latest).put("initialState", it).toString() }
        initialState = null
        val argument =
            JSONObject.quote(payload ?: latest)
                .replace("\u2028", "\\u2028")
                .replace("\u2029", "\\u2029")
        evaluateJavascript("window.renderOpenUi(JSON.parse($argument));", null)
    }

    private companion object {
        const val Origin = "https://minichat-ui.invalid"
    }
}
