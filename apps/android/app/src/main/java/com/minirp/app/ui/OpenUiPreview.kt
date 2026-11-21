package com.minirp.app.ui

import android.content.Context
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.Canvas
import android.util.Base64
import android.view.Gravity
import android.view.View
import android.view.View.MeasureSpec
import android.webkit.WebView
import android.widget.FrameLayout
import androidx.compose.ui.graphics.toArgb
import com.minirp.app.core.ImageViewport
import java.io.ByteArrayOutputStream
import kotlin.coroutines.resume
import kotlin.math.roundToInt
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONArray
import org.json.JSONObject

internal class OpenUiPreview(private val context: Context, private val window: () -> FrameLayout?) {
    private val slots = Semaphore(2)
    private val catalog by lazy {
        context.assets.open("openui/card-prompt.txt").bufferedReader().use { it.readText() }
    }

    fun catalog(request: JSONObject): JSONObject {
        val text = catalog
        var start = request.optInt("offset").coerceIn(0, text.length)
        val query = request.optString("query").trim()
        if (query.isNotEmpty()) {
            val match = text.indexOf(query, start, ignoreCase = true)
            if (match < 0) return JSONObject().put("available", true).put("found", false)
            start = (match - 80).coerceAtLeast(start)
        }
        if (start < text.length && Character.isLowSurrogate(text[start])) start--
        var end = (start + request.optInt("length", 4000).coerceIn(1, 8000)).coerceAtMost(text.length)
        if (end < text.length && Character.isHighSurrogate(text[end - 1])) end++
        return JSONObject().put("available", true).put("text", text.substring(start, end))
            .put("offset", start).put("next_offset", if (end == text.length) JSONObject.NULL else end)
    }

    suspend fun render(request: JSONObject): JSONObject = withContext(Dispatchers.Main) {
        if (!OpenUiPage.isSupported()) {
            return@withContext JSONObject().put("available", false)
                .put("reason", "This Android WebView does not support OpenUI.")
        }
        withTimeoutOrNull(20_000) { slots.withPermit { renderPage(request) } }
            ?: failure("preview-timeout", "The Android renderer did not finish in time.")
    }

    private suspend fun renderPage(request: JSONObject): JSONObject {
        val configuration = context.resources.configuration
        val defaultWidth = (configuration.screenWidthDp - 32).coerceIn(240, 768)
        val cssWidth = request.optInt("width").takeIf { it > 0 }?.coerceIn(240, 768) ?: defaultWidth
        val density = context.resources.displayMetrics.density
        val dark = request.optBoolean(
            "dark", configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES,
        )
        val colors = minirpColors(context, dark)
        val payload = openUiPayload(
            request.getString("open_ui"), request.optJSONObject("open_ui_state"),
            request.optInt("open_ui_revision"), dark,
            configuration.layoutDirection == View.LAYOUT_DIRECTION_RTL,
            configuration.locales[0].toLanguageTag(), colors.openUiTheme(),
        ).put("previewSteps", request.optJSONArray("steps") ?: JSONArray())
        val output = CompletableDeferred<JSONObject>()
        val page = OpenUiPage(context, ownsVerticalScroll = false, offline = true)
        val wantsImage = request.optBoolean("capture_image")
        val parent = if (wantsImage) window()?.takeIf { it.isAttachedToWindow } else null
        try {
            val width = (cssWidth * density).roundToInt()
            val height = (800 * density).roundToInt()
            if (parent != null) {
                page.settings.offscreenPreRaster = true
                page.importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS
                page.isFocusable = false
                page.translationX = -width.toFloat()
                parent.addView(page, FrameLayout.LayoutParams(width, height, Gravity.TOP or Gravity.LEFT))
            }
            page.measure(
                MeasureSpec.makeMeasureSpec(width, MeasureSpec.EXACTLY),
                MeasureSpec.makeMeasureSpec(height, MeasureSpec.EXACTLY),
            )
            page.layout(0, 0, width, height)
            page.attach { event ->
                when (event.optString("type")) {
                    "preview_result" -> output.complete(event.getJSONObject("result"))
                    "page_error" -> output.complete(failure("webview-error", "The OpenUI page failed to load."))
                }
            }
            page.render(payload.toString()) { null }
            val result = output.await()
                .put("available", true)
                .put("offline", true)
                .put("title", request.optString("open_ui_title"))
            if (wantsImage) {
                when {
                    !result.optBoolean("rendered") -> noImage(result, "The card did not render.")
                    !page.isAttachedToWindow -> noImage(result, "Open the Android app to capture a card image.")
                    else -> capture(page, result, colors.surface.toArgb(), density, ImageViewport.read(request))
                }
            }
            return result
        } finally {
            page.detach()
            parent?.removeView(page)
            page.destroy()
        }
    }

    private suspend fun capture(page: OpenUiPage, result: JSONObject, background: Int, density: Float, viewport: ImageViewport?) {
        val ready = withTimeoutOrNull(4000) {
            suspendCancellableCoroutine { continuation ->
                page.postVisualStateCallback(0, object : WebView.VisualStateCallback() {
                    override fun onComplete(requestId: Long) {
                        if (continuation.isActive) continuation.resume(true)
                    }
                })
            }
        }
        if (ready != true || !page.isAttachedToWindow) {
            noImage(result, "The visual frame did not finish in time.")
            return
        }
        val contentHeight = (result.getJSONObject("view").getInt("height") * density).roundToInt()
        val height = contentHeight.coerceIn(1, page.height)
        val region = viewport?.requireInside(page.width, height) ?: ImageViewport(0, 0, page.width, height)
        val scale = if (viewport == null) minOf(1f, 1024f / region.width, 1500f / region.height)
            else minOf(1f, ImageViewport.MaxSide.toFloat() / maxOf(region.width, region.height))
        val image = Bitmap.createBitmap(
            (region.width * scale).roundToInt().coerceAtLeast(1),
            (region.height * scale).roundToInt().coerceAtLeast(1),
            Bitmap.Config.ARGB_8888,
        )
        try {
            val canvas = Canvas(image)
            canvas.drawColor(background)
            canvas.scale(scale, scale)
            canvas.translate(-region.x.toFloat(), -region.y.toFloat())
            page.draw(canvas)
            val encoded = withContext(Dispatchers.Default) {
                val bytes = ByteArrayOutputStream()
                if (image.compress(Bitmap.CompressFormat.PNG, 100, bytes) && bytes.size() <= 2_000_000)
                    Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP) else null
            }
            if (encoded == null) noImage(result, "The screenshot exceeded the image limit.")
            else result.put("image_base64", encoded).put("screenshot", JSONObject()
                .put("captured", true).put("width", image.width).put("height", image.height)
                .put("source_width", page.width).put("source_height", height)
                .put("cropped", viewport != null || contentHeight > height))
        } finally { image.recycle() }
    }

    private fun noImage(result: JSONObject, reason: String) {
        result.put("screenshot", JSONObject().put("captured", false).put("reason", reason))
    }

    private fun failure(code: String, message: String) = JSONObject()
        .put("available", true)
        .put("ok", false)
        .put("rendered", false)
        .put("diagnostics", JSONArray().put(JSONObject()
            .put("source", "platform").put("code", code).put("message", message)))
}
