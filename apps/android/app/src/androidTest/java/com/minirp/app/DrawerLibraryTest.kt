package com.minirp.app

import android.content.Context
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ApplicationProvider
import com.minirp.app.ui.DrawerLibrary
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class DrawerLibraryTest {
    @get:Rule val compose = createComposeRule()

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
                    modifier = Modifier.fillMaxSize(),
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
        assertTrue(
            newChat.fetchSemanticsNode().boundsInRoot.top >
                chat.fetchSemanticsNode().boundsInRoot.bottom
        )
        newChat.performClick()
        compose.runOnIdle { picking.value = true }
        preset.assertIsDisplayed()
        chat.assertDoesNotExist()
        newChat.assertDoesNotExist()
        newPreset.performClick()
        compose.runOnIdle { picking.value = false }
        chat.assertIsDisplayed()
        newPreset.assertDoesNotExist()
        newChat.performClick()
        compose.runOnIdle { assertEquals(listOf("chat", "preset", "chat"), invocations) }
    }
}
