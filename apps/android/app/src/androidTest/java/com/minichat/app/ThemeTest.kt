package com.minichat.app

import android.content.Context
import android.graphics.drawable.AdaptiveIconDrawable
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.test.core.app.ApplicationProvider
import androidx.test.filters.SdkSuppress
import com.minichat.app.ui.MinichatTheme
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Rule
import org.junit.Test

class ThemeTest {
    @get:Rule val compose = createComposeRule()

    @Test
    @SdkSuppress(minSdkVersion = 31)
    fun expressiveThemeUsesTheSystemPaletteInLightAndDarkMode() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val dark = mutableStateOf(false)
        var selected: ColorScheme? = null
        compose.setContent {
            MinichatTheme(darkTheme = dark.value) {
                val colors = MaterialTheme.colorScheme
                SideEffect { selected = colors }
            }
        }
        fun checkPalette(expected: ColorScheme) {
            compose.runOnIdle {
                val actual = checkNotNull(selected)
                assertEquals(expected.primary, actual.primary)
                assertEquals(expected.primaryContainer, actual.primaryContainer)
                assertEquals(expected.surfaceContainer, actual.surfaceContainer)
            }
        }
        checkPalette(dynamicLightColorScheme(context))
        compose.runOnIdle { dark.value = true }
        checkPalette(dynamicDarkColorScheme(context))
    }

    @Test
    @SdkSuppress(minSdkVersion = 33)
    fun launcherIconIncludesTheSystemThemingLayer() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val icon = context.getDrawable(R.mipmap.ic_launcher) as AdaptiveIconDrawable
        assertNotNull(icon.monochrome)
    }
}
