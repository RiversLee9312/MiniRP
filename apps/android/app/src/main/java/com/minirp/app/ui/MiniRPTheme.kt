package com.minirp.app.ui

import android.content.Context
import android.os.Build
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.FiniteAnimationSpec
import androidx.compose.animation.core.tween
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MotionScheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.expressiveLightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalContext
import com.materialkolor.rememberDynamicColorScheme

private val LocalMiniRPDarkTheme = staticCompositionLocalOf<Boolean?> { null }

private object MiniRPMotionScheme : MotionScheme by MotionScheme.expressive() {
    // Material bottom sheets and drawers use this spec to move between their anchors.
    override fun <T> defaultSpatialSpec(): FiniteAnimationSpec<T> =
        tween(durationMillis = 390, easing = FastOutSlowInEasing)
}

@Composable
internal fun miniRPDarkTheme(): Boolean =
    LocalMiniRPDarkTheme.current
        ?: (MaterialTheme.colorScheme.surface.luminance() < MaterialTheme.colorScheme.onSurface.luminance())

@Composable
internal fun MiniRPTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    seedColor: Color? = null,
    content: @Composable () -> Unit,
) {
    val colors = if (seedColor == null) minirpColors(LocalContext.current, darkTheme)
        else rememberDynamicColorScheme(seedColor = seedColor, isDark = darkTheme)
    CompositionLocalProvider(LocalMiniRPDarkTheme provides darkTheme) {
        MaterialExpressiveTheme(colorScheme = colors, motionScheme = MiniRPMotionScheme, content = content)
    }
}

internal fun minirpColors(context: Context, dark: Boolean) =
    when {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
            if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        dark -> darkColorScheme()
        else -> expressiveLightColorScheme()
    }
