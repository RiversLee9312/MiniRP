package com.minirp.app

import android.content.Context
import android.content.res.Configuration
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollToIndex
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.test.core.app.ApplicationProvider
import com.minirp.app.ui.ArchiveType
import com.minirp.app.ui.DrawerLibrary
import com.minirp.app.ui.Choice
import com.minirp.app.ui.LibraryChoiceRow
import java.util.Locale
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class DrawerLibraryTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun longPressOpensTheExportActionWithoutOpeningTheRoom() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val invocations = mutableListOf<String>()
        compose.setContent {
            MaterialExpressiveTheme {
                LibraryChoiceRow(
                    Choice("room", "Room fixture"), selected = false, enabled = true,
                    onOpen = { invocations += "open" }, onDelete = { invocations += "delete" },
                    actions = listOf(R.string.export_archive to { invocations += "export" }),
                )
            }
        }
        compose.onNodeWithText("Room fixture").performTouchInput { longClick() }
        compose.onNodeWithText(context.getString(R.string.export_archive)).assertIsDisplayed().performClick()
        compose.runOnIdle { assertEquals(listOf("export"), invocations) }
    }

    @Test
    fun bottomActionTracksTheVisibleListInBothDirections() {
        val picking = mutableStateOf(false)
        val invocations = mutableListOf<String>()
        compose.setContent {
            MaterialExpressiveTheme {
                DrawerLibrary(
                    picking = picking.value,
                    enabled = true,
                    onNewChat = { invocations += "chat" },
                    onCreatePreset = { invocations += "preset" },
                    modifier = Modifier.fillMaxSize().testTag("drawer-library"),
                ) { presets, padding ->
                    Box(Modifier.fillMaxSize().padding(padding)) {
                        Text(if (presets) "Preset row" else "Chat row")
                    }
                }
            }
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val newChat = compose.onNodeWithContentDescription(context.getString(R.string.new_chat))
        val newPreset =
            compose.onNodeWithContentDescription(context.getString(R.string.create_preset))
        val chat = compose.onNodeWithText("Chat row")
        val preset = compose.onNodeWithText("Preset row")
        val initial = newChat.fetchSemanticsNode().boundsInRoot
        val drawer = compose.onNodeWithTag("drawer-library").fetchSemanticsNode().boundsInRoot
        val inset = with(compose.density) { 16.dp.roundToPx().toFloat() }
        assertEquals(drawer.right - inset, initial.right, 1f)
        assertEquals(drawer.bottom - inset, initial.bottom, 1f)
        assertTrue(
            initial.top > chat.fetchSemanticsNode().boundsInRoot.bottom
        )
        newChat.performClick()
        compose.runOnIdle { picking.value = true }
        preset.assertIsDisplayed()
        chat.assertDoesNotExist()
        newChat.assertDoesNotExist()
        val create = newPreset.fetchSemanticsNode().boundsInRoot
        assertEquals(initial.right, create.right, 1f)
        assertEquals(initial.bottom, create.bottom, 1f)
        newPreset.performClick()
        compose.runOnIdle { picking.value = false }
        chat.assertIsDisplayed()
        newPreset.assertDoesNotExist()
        newChat.performClick()
        compose.runOnIdle { assertEquals(listOf("chat", "preset", "chat"), invocations) }
    }

    @Test
    fun importAnimatesBothWaysWhileTheRoomActionStaysAnchoredAndTheMenuClosesOnExit() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val picking = mutableStateOf(false)
        val imports = mutableListOf<ArchiveType>()
        compose.mainClock.autoAdvance = false
        compose.setContent {
            MaterialExpressiveTheme {
                DrawerLibrary(
                    picking = picking.value,
                    enabled = true,
                    onNewChat = {},
                    onCreatePreset = {},
                    modifier = Modifier.fillMaxSize(),
                    newConversationLabel = R.string.new_room,
                    onArchiveImport = { imports += it },
                ) { _, _ -> }
            }
        }
        compose.mainClock.advanceTimeByFrame()
        val newRoom = compose.onNodeWithContentDescription(context.getString(R.string.new_room))
        val create = compose.onNodeWithContentDescription(context.getString(R.string.create_preset))
        val anchor = compose.onNodeWithTag("drawer-import-fab", useUnmergedTree = true)
        val menu = compose.onNodeWithContentDescription(context.getString(R.string.import_menu))
        val importPreset = compose.onNodeWithText(context.getString(R.string.import_preset))
        val stable = newRoom.fetchSemanticsNode().boundsInRoot
        anchor.assertDoesNotExist()

        compose.runOnIdle { picking.value = true }
        compose.mainClock.advanceTimeBy(48)
        val enteringTop = anchor.fetchSemanticsNode().boundsInRoot.top
        val createBounds = create.fetchSemanticsNode().boundsInRoot
        assertEquals(stable.right, createBounds.right, 1f)
        assertEquals(stable.bottom, createBounds.bottom, 1f)
        compose.mainClock.advanceTimeBy(1_000)
        val settled = menu.assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        assertTrue(enteringTop > settled.top)
        menu.performClick()
        compose.mainClock.advanceTimeBy(1_000)
        importPreset.assertIsDisplayed()

        compose.runOnIdle { picking.value = false }
        compose.mainClock.advanceTimeBy(48)
        assertTrue(anchor.fetchSemanticsNode().boundsInRoot.top > settled.top)
        importPreset.assertDoesNotExist()
        val returning = newRoom.fetchSemanticsNode().boundsInRoot
        assertEquals(stable.right, returning.right, 1f)
        assertEquals(stable.bottom, returning.bottom, 1f)
        compose.mainClock.advanceTimeBy(1_000)
        anchor.assertDoesNotExist()

        compose.runOnIdle { picking.value = true }
        compose.mainClock.advanceTimeBy(1_000)
        menu.assertIsDisplayed()
        importPreset.assertDoesNotExist()
        compose.runOnIdle { assertTrue(imports.isEmpty()) }
    }

    @Test
    fun archiveImportMenuContainsBothFormatsAndExplainsWhatTheyInclude() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val picking = mutableStateOf(true)
        val imports = mutableListOf<ArchiveType>()
        compose.setContent {
            MaterialExpressiveTheme {
                DrawerLibrary(
                    picking = picking.value,
                    enabled = true,
                    onNewChat = {},
                    onCreatePreset = {},
                    modifier = Modifier.fillMaxSize(),
                    onArchiveImport = { imports += it },
                ) { _, _ -> }
            }
        }
        val menu = compose.onNodeWithContentDescription(context.getString(R.string.import_menu))
        val create = compose.onNodeWithContentDescription(context.getString(R.string.create_preset))
        val importBounds = menu.fetchSemanticsNode().boundsInRoot
        val createBounds = create.fetchSemanticsNode().boundsInRoot
        assertTrue(importBounds.bottom < createBounds.top)
        val preset = compose.onNodeWithText(context.getString(R.string.import_preset))
        val save = compose.onNodeWithText(context.getString(R.string.import_save))
        preset.assertDoesNotExist()
        save.assertDoesNotExist()
        menu.performClick()
        compose.onNodeWithText(context.getString(R.string.import_help)).assertIsDisplayed().performClick()
        compose.onNodeWithText(context.getString(R.string.import_preset_detail)).assertIsDisplayed()
        compose.onNodeWithText(context.getString(R.string.import_save_detail)).assertIsDisplayed()
        compose.onNode(hasText(context.getString(R.string.close)) and hasAnyAncestor(isDialog())).performClick()
        menu.performClick()
        preset.assertIsDisplayed().performClick()
        menu.performClick()
        save.assertIsDisplayed().performClick()
        compose.runOnIdle {
            assertEquals(listOf(ArchiveType.Preset, ArchiveType.Save), imports)
            picking.value = false
        }
        menu.assertDoesNotExist()
    }

    @Test
    fun presetActionsAlignWithLargeGermanLabelsInANarrowDrawer() =
        assertPresetActionsAlign(Locale.GERMAN, LayoutDirection.Ltr, 280.dp)

    @Test
    fun presetActionsAlignAndMirrorInAnArabicDrawer() =
        assertPresetActionsAlign(Locale.forLanguageTag("ar"), LayoutDirection.Rtl, 360.dp)

    private fun assertPresetActionsAlign(locale: Locale, direction: LayoutDirection, width: Dp) {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val localized = context.createConfigurationContext(Configuration(context.resources.configuration).apply {
            setLocale(locale)
            fontScale = 1.5f
        })
        compose.setContent {
            CompositionLocalProvider(
                LocalContext provides localized,
                LocalConfiguration provides localized.resources.configuration,
                LocalDensity provides Density(LocalDensity.current.density, 1.5f),
                LocalLayoutDirection provides direction,
            ) {
                MaterialExpressiveTheme {
                    DrawerLibrary(
                        picking = true,
                        enabled = true,
                        onNewChat = {},
                        onCreatePreset = {},
                        modifier = Modifier.width(width).fillMaxHeight().testTag("drawer-library"),
                        onArchiveImport = {},
                    ) { _, padding ->
                        LazyColumn(Modifier.fillMaxSize().testTag("preset-list"), contentPadding = padding) {
                            items(32) { index -> Text("Preset $index", Modifier.padding(16.dp)) }
                        }
                    }
                }
            }
        }
        val drawer = compose.onNodeWithTag("drawer-library").fetchSemanticsNode().boundsInRoot
        val create = compose.onNodeWithContentDescription(localized.getString(R.string.create_preset))
        val menu = compose.onNodeWithContentDescription(localized.getString(R.string.import_menu))
        val close = compose.onNodeWithContentDescription(localized.getString(R.string.close))
        val inset = with(compose.density) { 16.dp.roundToPx().toFloat() }
        val gap = with(compose.density) { 8.dp.roundToPx().toFloat() }
        val minimumTouchHeight = with(compose.density) { 48.dp.toPx() }
        fun assertAligned(importLabel: String) {
            val createBounds = create.assertIsDisplayed().fetchSemanticsNode().boundsInRoot
            val importBounds = compose.onNodeWithContentDescription(importLabel)
                .assertIsDisplayed().fetchSemanticsNode().boundsInRoot
            assertEquals(drawer.bottom - inset, createBounds.bottom, 1f)
            assertEquals(createBounds.top - gap, importBounds.bottom, 1f)
            assertEquals(createBounds.height, importBounds.height, 1f)
            assertTrue(importBounds.height >= minimumTouchHeight)
            if (direction == LayoutDirection.Ltr) {
                assertEquals(drawer.right - inset, createBounds.right, 1f)
                assertEquals(createBounds.right, importBounds.right, 1f)
            } else {
                assertEquals(drawer.left + inset, createBounds.left, 1f)
                assertEquals(createBounds.left, importBounds.left, 1f)
            }
        }
        assertAligned(localized.getString(R.string.import_menu))
        menu.performClick()
        compose.onNodeWithText(localized.getString(R.string.import_preset)).assertIsDisplayed()
        assertAligned(localized.getString(R.string.close))
        close.performClick()
        assertAligned(localized.getString(R.string.import_menu))
        compose.onNodeWithTag("preset-list").performScrollToIndex(31)
        val lastPreset = compose.onNodeWithText("Preset 31").assertIsDisplayed().fetchSemanticsNode().boundsInRoot
        assertTrue(lastPreset.bottom < menu.fetchSemanticsNode().boundsInRoot.top)
    }
}
