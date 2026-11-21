package com.minirp.app.ui

import android.content.Context
import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.expressiveLightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import com.materialkolor.rememberDynamicColorScheme

internal val LocalMiniRPDarkTheme = compositionLocalOf { false }

@Composable
internal fun MiniRPTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    seedColor: Color? = null,
    content: @Composable () -> Unit,
) {
    val base = minirpColors(LocalContext.current, darkTheme)
    val accents = seedColor?.let { rememberDynamicColorScheme(seedColor = it, isDark = darkTheme) }
    val colors = if (accents == null) base else base.copy(
        primary = accents.primary, onPrimary = accents.onPrimary,
        primaryContainer = accents.primaryContainer, onPrimaryContainer = accents.onPrimaryContainer,
        inversePrimary = accents.inversePrimary,
        secondary = accents.secondary, onSecondary = accents.onSecondary,
        secondaryContainer = accents.secondaryContainer, onSecondaryContainer = accents.onSecondaryContainer,
        tertiary = accents.tertiary, onTertiary = accents.onTertiary,
        tertiaryContainer = accents.tertiaryContainer, onTertiaryContainer = accents.onTertiaryContainer,
    )
    CompositionLocalProvider(LocalMiniRPDarkTheme provides darkTheme) {
        MaterialExpressiveTheme(colorScheme = colors, content = content)
    }
}

internal fun minirpColors(context: Context, dark: Boolean) =
    when {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
            if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        dark -> darkColorScheme()
        else -> expressiveLightColorScheme()
    }
