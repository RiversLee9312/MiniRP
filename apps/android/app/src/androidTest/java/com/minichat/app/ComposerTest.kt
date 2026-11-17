package com.minichat.app

import android.content.Context
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ApplicationProvider
import com.minichat.app.ui.Composer
import com.minichat.app.ui.TraceBlock
import com.minichat.app.ui.TraceLog
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class ComposerTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun thoughtsExpandAndCollapseOnSuccessiveTaps() {
        val label =
            ApplicationProvider.getApplicationContext<Context>().getString(R.string.reasoning)
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(listOf(TraceBlock("fold", "EnvAgent", reasoning = "Trace details")), false)
            }
        }
        compose.onNodeWithText("Trace details").assertDoesNotExist()
        compose.onNodeWithText(label).performClick()
        compose.onNodeWithText("Trace details").assertIsDisplayed()
        compose.onNodeWithText(label).performClick()
        compose.onNodeWithText("Trace details").assertDoesNotExist()
    }

    @Test
    fun inputGrowsThenKeepsItsHeightWhileTextScrolls() {
        compose.setContent { MaterialExpressiveTheme { Composer(false, {}, {}) } }
        compose.waitForIdle()
        val input = compose.onNodeWithTag("composer")
        val original = input.fetchSemanticsNode().boundsInRoot.height
        input.performTextInput("Line\n".repeat(4))
        compose.waitForIdle()
        val expanded = input.fetchSemanticsNode().boundsInRoot.height
        input.performTextInput("Line\n".repeat(20))
        compose.waitForIdle()
        val maximum = input.fetchSemanticsNode().boundsInRoot.height
        input.performTextInput("Line\n".repeat(30))
        compose.waitForIdle()
        val afterMoreInput = input.fetchSemanticsNode().boundsInRoot.height
        assertTrue(expanded > original)
        assertTrue(maximum > expanded)
        assertTrue(kotlin.math.abs(afterMoreInput - maximum) < 1f)
    }
}
