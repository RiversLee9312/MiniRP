package com.minirp.app.core

import android.graphics.Rect
import kotlin.math.roundToInt
import org.json.JSONObject

internal data class ImageViewport(val x: Int, val y: Int, val width: Int, val height: Int) {
    fun requireInside(sourceWidth: Int, sourceHeight: Int): ImageViewport {
        require(x.toLong() + width <= sourceWidth && y.toLong() + height <= sourceHeight) {
            "The viewport is outside the $sourceWidth × $sourceHeight source."
        }
        return this
    }

    fun rect() = Rect(x, y, x + width, y + height)

    companion object {
        const val MaxSide = 4096

        fun read(request: JSONObject): ImageViewport? {
            if (request.isNull("viewport")) return null
            val rect = request.getJSONObject("viewport")
            val area = ImageViewport(rect.getInt("x"), rect.getInt("y"), rect.getInt("width"), rect.getInt("height"))
            require(area.x >= 0 && area.y >= 0 && area.width > 0 && area.height > 0)
            return area
        }

        fun fit(width: Int, height: Int, limit: Int = MaxSide): Pair<Int, Int> {
            val scale = minOf(1.0, limit.toDouble() / maxOf(width, height))
            return (width * scale).roundToInt().coerceAtLeast(1) to (height * scale).roundToInt().coerceAtLeast(1)
        }
    }
}
