package com.minirp.app

import android.content.Context
import android.graphics.drawable.AdaptiveIconDrawable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.DrawerState
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.SheetState
import androidx.compose.material3.SheetValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.unit.dp
import androidx.test.core.app.ApplicationProvider
import androidx.test.filters.SdkSuppress
import com.minirp.app.ui.MiniRPTheme
import kotlinx.coroutines.launch
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
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
            MiniRPTheme(darkTheme = dark.value) {
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
    @OptIn(ExperimentalMaterial3Api::class)
    fun drawersAndSheetsOpenWithoutCrossingTheirAnchors() {
        val showSheet = mutableStateOf(false)
        lateinit var drawer: DrawerState
        lateinit var sheet: SheetState
        compose.setContent {
            MiniRPTheme {
                val drawerState = rememberDrawerState(DrawerValue.Closed)
                val sheetState = rememberBottomSheetState(
                    initialValue = SheetValue.Hidden,
                    enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded),
                )
                val scope = rememberCoroutineScope()
                SideEffect {
                    drawer = drawerState
                    sheet = sheetState
                }
                ModalNavigationDrawer(
                    drawerState = drawerState,
                    drawerContent = { ModalDrawerSheet(drawerState = drawerState) { Text("Drawer") } },
                ) {
                    TextButton(onClick = { scope.launch { drawerState.open() } }) { Text("Open drawer") }
                }
                if (showSheet.value) {
                    ModalBottomSheet(onDismissRequest = {}, sheetState = sheetState) {
                        Box(Modifier.height(320.dp))
                    }
                }
            }
        }
        compose.mainClock.autoAdvance = false
        fun sample(readOffset: () -> Float): List<Float> = List(38) {
            compose.mainClock.advanceTimeByFrame()
            compose.runOnIdle { readOffset() }
        }.filter { it.isFinite() }

        compose.onNodeWithText("Open drawer").performClick()
        val drawerOffsets = sample { drawer.currentOffset }
        assertTrue(drawerOffsets.toString(), drawerOffsets.first() < drawerOffsets.last())
        assertTrue(drawerOffsets.toString(), drawerOffsets.zipWithNext().all { (from, to) -> to >= from - 0.1f })
        assertTrue(compose.runOnIdle { drawer.isOpen })

        compose.runOnIdle { showSheet.value = true }
        val sheetOffsets = sample { runCatching { sheet.requireOffset() }.getOrDefault(Float.NaN) }
        assertTrue(sheetOffsets.toString(), sheetOffsets.first() > sheetOffsets.last())
        assertTrue(sheetOffsets.toString(), sheetOffsets.zipWithNext().all { (from, to) -> to <= from + 0.1f })
        assertEquals(SheetValue.Expanded, compose.runOnIdle { sheet.currentValue })
    }

    @Test
    @SdkSuppress(minSdkVersion = 33)
    fun launcherIconIncludesTheSystemThemingLayer() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val icon = context.getDrawable(R.mipmap.ic_launcher) as AdaptiveIconDrawable
        assertNotNull(icon.monochrome)
    }
}
