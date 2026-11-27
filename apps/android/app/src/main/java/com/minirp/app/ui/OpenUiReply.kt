package com.minirp.app.ui

import android.content.Context
import android.content.Intent
import android.graphics.RectF
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.util.LruCache
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.minirp.app.R
import java.io.ByteArrayInputStream
import java.io.InputStream
import java.util.Locale
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.json.JSONObject

data class OpenUiInteractions(
    val reply: (String, String, JSONObject) -> Boolean,
    val restore: (String) -> String,
    val save: (String, String) -> Unit,
    val hostAction: ((String, String) -> Boolean)? = null,
)

internal const val PresetHomeAction = "preset-home"
internal enum class PresetHomeResult { Returned, SetupPending, Working, Unavailable }

private const val openUiInlineLimit = 420
private const val openUiPreviewHeight = 144
private const val openUiSheetHeightLimit = 560

@Composable
internal fun OpenUiReply(
    block: TraceBlock,
    interactions: OpenUiInteractions?,
    pages: OpenUiPageCache,
    displayAssets: DisplayAssets? = null,
    viewKey: String = block.id,
    toolPresentation: Boolean = false,
    toolHeader: (@Composable () -> Unit)? = null,
    fallback: (@Composable () -> Unit)? = null,
) {
    var expanded by rememberSaveable { mutableStateOf(false) }
    var measuredHeight by rememberSaveable { mutableIntStateOf(120) }
    val hasPreview = measuredHeight > openUiInlineLimit
    val toolRevision = block.openUiRevision.takeIf { toolPresentation }
    var loaded by remember(viewKey, toolRevision) { mutableStateOf(false) }
    var failed by remember(viewKey, toolRevision) { mutableStateOf(false) }
    val presetHome = block.openUiHostAction == PresetHomeAction
    val title = if (presetHome) stringResource(R.string.preset_return_home)
        else block.openUiTitle.ifBlank { stringResource(R.string.interactive_reply) }
    val renderedBlock = if (presetHome) {
        val detail = JSONObject.quote(stringResource(R.string.preset_return_home_detail))
        val label = JSONObject.quote(title)
        block.copy(openUi = """root = Col([Text($detail), Button($label, @ToAssistant("$PresetHomeAction"))])""")
    } else block
    val context = LocalContext.current
    val supported = remember { OpenUiPage.isSupported() }
    if (!supported) {
        if (fallback != null) fallback() else Text(stringResource(R.string.interactive_reply_unavailable))
        return
    }
    if (failed && fallback != null) {
        fallback()
        return
    }
    LaunchedEffect(viewKey, toolRevision, loaded, failed) {
        if (toolPresentation && !loaded && !failed) {
            delay(10_000)
            failed = true
        }
    }

    fun closeExpanded() {
        if (!expanded) return
        // Reopen the preview with any form changes made in the sheet.
        pages.discard(openUiCacheKey(viewKey, toolPresentation))
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
                if (presetHome) {
                    if (text == PresetHomeAction && interactions?.hostAction?.invoke(block.id, text) == true)
                        closeExpanded()
                    return
                }
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
                val url = message.optString("url")
                if (isDisplayAssetUrl(url)) {
                    displayAssets?.download(block.id, url)
                    return
                }
                val uri = Uri.parse(url)
                if (uri.scheme == "https" && !uri.host.isNullOrBlank() && uri.userInfo == null)
                    runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, uri)) }
            }
        }
    }

    toolHeader?.invoke()
    Surface(
        color = MaterialTheme.colorScheme.surfaceContainerLow,
        shape = MaterialTheme.shapes.large,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column {
            Box(
                Modifier.fillMaxWidth()
                    .height((if (hasPreview) openUiPreviewHeight else measuredHeight).dp)
                    .testTag("open-ui-preview:$viewKey"),
                contentAlignment = Alignment.Center,
            ) {
                if (!expanded && !failed) {
                    OpenUiWebView(
                        renderedBlock,
                        interactions,
                        Modifier.matchParentSize(),
                        false,
                        pages,
                        displayAssets,
                        viewKey,
                        toolPresentation,
                        previewOnly = hasPreview,
                        onMessage = ::handleMessage,
                    )
                    if (hasPreview && loaded) {
                        Box(
                            Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(24.dp)
                                .background(
                                    Brush.verticalGradient(
                                        listOf(Color.Transparent, MaterialTheme.colorScheme.surfaceContainerLow),
                                    )
                                ),
                        )
                    }
                }
                if (!loaded && !failed)
                    CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                if (failed && !loaded)
                    Text(
                        stringResource(R.string.interactive_reply_unavailable),
                        Modifier.padding(12.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
            }
            if (hasPreview && !failed)
                TextButton(
                    onClick = { expanded = true },
                    modifier = Modifier.align(Alignment.End).padding(horizontal = 6.dp),
                ) {
                    Text(stringResource(R.string.expand))
                }
        }
    }
    if (expanded && !failed)
        FullSheet(title, onDismiss = ::closeExpanded, heightLimit = openUiSheetHeightLimit.dp) {
            OpenUiWebView(
                renderedBlock,
                interactions,
                Modifier.weight(1f).fillMaxWidth().testTag("open-ui-expanded:$viewKey"),
                true,
                null,
                displayAssets,
                viewKey,
                toolPresentation,
                onMessage = ::handleMessage,
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
    displayAssets: DisplayAssets?,
    viewKey: String,
    toolPresentation: Boolean,
    previewOnly: Boolean = false,
    onMessage: (JSONObject) -> Unit,
) {
    val dark = miniRPDarkTheme()
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val locale = LocalConfiguration.current.locales[0].toLanguageTag()
    val theme = MaterialTheme.colorScheme.openUiTheme().toString()
    val payload =
        remember(
            block.openUi,
            block.streaming,
            block.openUiState,
            block.openUiRevision,
            dark,
            rtl,
            locale,
            theme,
            expanded,
        ) {
            openUiPayload(
                block.openUi.orEmpty(), block.openUiState?.let(::JSONObject), block.openUiRevision,
                dark, rtl, locale, JSONObject(theme),
            )
                .put("streaming", block.streaming)
                .put("expanded", expanded)
                .toString()
        }
    AndroidView(
        factory = ::OpenUiSlot,
        modifier = modifier,
        update = { slot ->
            slot.setPreviewOnly(previewOnly)
            val page = slot.bind(viewKey, expanded, pages, toolPresentation, onMessage)
            page.openMedia = if (toolPresentation) null
                else displayAssets?.let { assets -> { url -> assets.openMedia(block.id, url) } }
            page.render(payload) {
                if (toolPresentation) null else interactions?.restore?.invoke(block.id)
            }
        },
        // The detached document cache already reuses the expensive WebView. Keep a
        // fresh Compose interop holder when a signed tool disclosure removes its body.
        onReset = if (toolPresentation) null else OpenUiSlot::unbind,
        onRelease = OpenUiSlot::unbind,
    )
}

private fun Color.rgb(): String = String.format(Locale.ROOT, "#%06x", toArgb() and 0xffffff)

internal fun ColorScheme.openUiTheme() =
    JSONObject()
        .put("foreground", surface.rgb())
        .put("textNeutralPrimary", onSurface.rgb())
        .put("textNeutralSecondary", onSurfaceVariant.rgb())
        .put("intelligentSurface", surfaceContainer.rgb())
        .put("intelligentSurfaceStrong", surfaceContainerHigh.rgb())
        .put("intelligentBorder", outlineVariant.rgb())
        .put("intelligentFocus", primary.rgb())
        .put("intelligentRangeThumb", primary.rgb())
        .put("intelligentChart1", primary.rgb())
        .put("intelligentChart2", secondary.rgb())
        .put("intelligentChart3", tertiary.rgb())

internal fun openUiPayload(
    program: String,
    state: JSONObject?,
    revision: Int,
    dark: Boolean,
    rtl: Boolean,
    locale: String,
    theme: JSONObject,
) = JSONObject()
    .put("program", program)
    .put("managedState", state)
    .put("managedRevision", revision)
    .put("dark", dark)
    .put("rtl", rtl)
    .put("locale", locale)
    .put("theme", theme)

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

private fun openUiCacheKey(id: String, toolPresentation: Boolean): String =
    if (toolPresentation) "tool:$id" else "reply:$id"

private class OpenUiSlot(context: Context) : FrameLayout(context) {
    private class Binding(
        val key: String,
        val cache: OpenUiPageCache?,
        val page: OpenUiPage,
        var onMessage: (JSONObject) -> Unit,
    )

    private val main = Handler(Looper.getMainLooper())
    private var previewOnly = false
    private val normalFocusability = descendantFocusability
    private var bound: Binding? = null
    private val messages = mutableListOf<JSONObject>()
    private var pendingHeight: JSONObject? = null
    private var scheduled = false
    private val publish = Runnable {
        scheduled = false
        val binding = bound ?: return@Runnable
        val height = pendingHeight
        val batch = messages.toList()
        pendingHeight = null
        messages.clear()
        // AndroidView.update can run during lazy-list measurement. Replay and
        // resize after layout returns, and drop callbacks from released holders.
        if (height != null) binding.onMessage(height)
        for (message in batch) {
            if (bound !== binding) break
            binding.onMessage(message)
        }
    }

    fun setPreviewOnly(value: Boolean) {
        if (previewOnly == value) return
        previewOnly = value
        if (value) findFocus()?.clearFocus()
        descendantFocusability = if (value) ViewGroup.FOCUS_BLOCK_DESCENDANTS else normalFocusability
        importantForAccessibility = if (value) View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS
            else View.IMPORTANT_FOR_ACCESSIBILITY_AUTO
    }

    override fun dispatchTouchEvent(event: MotionEvent): Boolean =
        !previewOnly && super.dispatchTouchEvent(event)

    fun bind(
        id: String,
        expanded: Boolean,
        pages: OpenUiPageCache?,
        toolPresentation: Boolean,
        onMessage: (JSONObject) -> Unit,
    ): OpenUiPage {
        val cacheKey = openUiCacheKey(id, toolPresentation)
        val existing = bound
        if (existing != null && existing.key == cacheKey && existing.cache === pages) {
            existing.onMessage = onMessage
            return existing.page
        }
        unbind()
        val page = pages?.take(cacheKey) ?: OpenUiPage(context, expanded, toolPresentation = toolPresentation)
        page.tag = id
        val binding = Binding(cacheKey, pages, page, onMessage)
        bound = binding
        addView(page, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        var replaying = true
        page.attach { message ->
            if (bound !== binding) return@attach
            when (message.optString("type")) {
                "height", "rendered", "error" -> {
                    if (message.optString("type") == "height") pendingHeight = message else messages.add(message)
                    if (!scheduled) {
                        scheduled = true
                        main.post(publish)
                    }
                }
                "state" -> {
                    // Live form changes must be saved before a following action
                    // closes the view. Cached state can replay during attachment.
                    if (replaying) main.post { binding.onMessage(message) } else binding.onMessage(message)
                }
                else -> binding.onMessage(message)
            }
        }
        replaying = false
        return page
    }

    fun unbind() {
        val binding = bound ?: return
        bound = null
        main.removeCallbacks(publish)
        pendingHeight = null
        messages.clear()
        scheduled = false
        val page = binding.page
        removeView(page)
        page.detach()
        if (binding.cache == null) page.destroy() else binding.cache.release(binding.key, page)
    }
}

internal class OpenUiPage(
    context: Context,
    private val ownsVerticalScroll: Boolean,
    private val offline: Boolean = false,
    private val toolPresentation: Boolean = false,
) : WebView(context) {
    private var ready = false
    private var latest = ""
    private var initialState: JSONObject? = null
    private var height: JSONObject? = null
    private var status: JSONObject? = null
    private var pendingState: JSONObject? = null
    private var toolScrollRegions: List<RectF> = emptyList()
    private var handlingVerticalScroll = false
    var callback: ((JSONObject) -> Unit)? = null
    @Volatile var openMedia: (suspend (String) -> Pair<String, InputStream>?)? = null
    val failed: Boolean
        get() = status?.optString("type") == "error"

    private val assetLoader =
        WebViewAssetLoader.Builder()
            .setDomain("minirp-ui.invalid")
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(context))
            .build()

    init {
        settings.javaScriptEnabled = true
        settings.allowFileAccess = false
        settings.allowContentAccess = false
        settings.javaScriptCanOpenWindowsAutomatically = false
        settings.setSupportMultipleWindows(false)
        setBackgroundColor(android.graphics.Color.TRANSPARENT)
        isVerticalScrollBarEnabled = ownsVerticalScroll
        webViewClient =
            object : WebViewClient() {
                override fun shouldOverrideUrlLoading(
                    view: WebView,
                    request: WebResourceRequest,
                ): Boolean = true

                override fun shouldInterceptRequest(
                    view: WebView,
                    request: WebResourceRequest,
                ): WebResourceResponse? {
                    val uri = request.url
                    if (toolPresentation) {
                        val local = uri.scheme == "https" && uri.authority == "minirp-ui.invalid" &&
                            request.method == "GET" && ToolResources.matches(uri.encodedPath.orEmpty())
                        return if (local) assetLoader.shouldInterceptRequest(uri) ?: unavailable() else unavailable()
                    }
                    if (uri.scheme == "https" && uri.authority == "minirp-ui.invalid" &&
                        uri.encodedPath.orEmpty().startsWith(AssetResourcePath)) {
                        val url = Uri.decode(uri.encodedPath.orEmpty().removePrefix(AssetResourcePath))
                        val reader = openMedia
                        if (request.isForMainFrame || request.method != "GET" || !isDisplayAssetUrl(url) || reader == null)
                            return unavailable()
                        val media = runCatching { runBlocking { reader(url) } }.getOrNull() ?: return unavailable()
                        return WebResourceResponse(media.first, null, 200, "OK",
                            mapOf("Cache-Control" to "no-store", "X-Content-Type-Options" to "nosniff"), media.second)
                    }
                    return assetLoader.shouldInterceptRequest(uri) ?: if (offline) unavailable() else null
                }

                override fun onReceivedError(
                    view: WebView,
                    request: WebResourceRequest,
                    error: WebResourceError,
                ) {
                    if (request.isForMainFrame) pageError()
                }

                override fun onRenderProcessGone(
                    view: WebView,
                    detail: RenderProcessGoneDetail,
                ): Boolean {
                    pageError()
                    return true
                }
            }
        WebViewCompat.addWebMessageListener(this, "MiniRPSurface", setOf(Origin)) {
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
            when (event.optString("type")) {
                "ready" -> {
                    ready = true
                    sendLatest()
                }
                "height", "rendered", "error" -> receive(event)
                else -> if (!toolPresentation) receive(event)
            }
        }
        val entry = if (toolPresentation) "tool.html" else "index.html"
        loadUrl("$Origin/assets/openui/$entry")
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
        if (handlingVerticalScroll) parent?.requestDisallowInterceptTouchEvent(false)
        handlingVerticalScroll = false
        callback = null
        openMedia = null
        onPause()
    }

    private fun unavailable() = WebResourceResponse(
        "text/plain", "utf-8", 403, "Asset unavailable", emptyMap(),
        ByteArrayInputStream(ByteArray(0)),
    )

    private fun pageError() {
        receive(JSONObject().put("type", if (offline) "page_error" else "error"))
    }

    private fun receive(event: JSONObject) {
        when (event.optString("type")) {
            "height" -> {
                height = event
                if (toolPresentation && !ownsVerticalScroll)
                    toolScrollRegions = event.toolScrollBounds()
            }
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
        if (event.actionMasked == MotionEvent.ACTION_DOWN) {
            // The sheet body owns its scroll. Inline cards claim only gestures
            // starting within a scrollable read-only field.
            handlingVerticalScroll = ownsVerticalScroll || toolScrollRegions.any { it.contains(event.x, event.y) }
            if (handlingVerticalScroll) parent?.requestDisallowInterceptTouchEvent(true)
        }
        val handled = super.dispatchTouchEvent(event)
        if (
            handlingVerticalScroll &&
                (!handled ||
                    event.actionMasked == MotionEvent.ACTION_UP ||
                    event.actionMasked == MotionEvent.ACTION_CANCEL)
        ) {
            parent?.requestDisallowInterceptTouchEvent(false)
            handlingVerticalScroll = false
        }
        return handled
    }

    private fun JSONObject.toolScrollBounds(): List<RectF> {
        val regions = optJSONArray("scrollRegions") ?: return emptyList()
        return (0 until regions.length().coerceAtMost(64)).mapNotNull { index ->
            val bounds = regions.optJSONArray(index) ?: return@mapNotNull null
            val left = bounds.optDouble(0).toFloat()
            val top = bounds.optDouble(1).toFloat()
            val right = bounds.optDouble(2).toFloat()
            val bottom = bounds.optDouble(3).toFloat()
            if (listOf(left, top, right, bottom).any { !it.isFinite() } || right <= left || bottom <= top) null
            else RectF(left, top, right, bottom)
        }
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

    companion object {
        private const val Origin = "https://minirp-ui.invalid"
        private const val AssetResourcePath = "/asset-resource/"
        private val ToolResources = Regex("/assets/openui/(?:tool\\.html|surface\\.(?:css|js)|[a-zA-Z0-9_-]+\\.(?:woff2?|ttf))")

        fun isSupported() = WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)
    }
}
