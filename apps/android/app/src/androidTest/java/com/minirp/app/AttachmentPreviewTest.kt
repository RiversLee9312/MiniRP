package com.minirp.app

import android.graphics.Bitmap
import android.content.res.Configuration
import android.os.LocaleList
import android.view.View
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.ui.Modifier
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.semantics.SemanticsActions
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.ui.AgentConversation
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.BranchDraft
import com.minirp.app.ui.ChatState
import com.minirp.app.ui.TraceLog
import com.minirp.app.ui.appLanguageTags
import com.minirp.app.ui.decodeBlock
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Locale
import java.util.UUID
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Rule
import org.junit.Test
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals

class AttachmentPreviewTest {
    @get:Rule val compose = createComposeRule()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    fun savedConversationShowsPreviewForLegacyTextAttachment() {
        val id = UUID.randomUUID().toString().replace("-", "").take(12)
        val file = file(id).apply { writeText("Text from the saved attachment.") }
        try {
            val row = decodeBlock(JSONObject().put("id", "attachment-fixture").put("kind", "user")
                .put("attachments", JSONArray().put(JSONObject().put("id", id).put("name", "Field notes.txt"))))
            compose.setContent {
                MaterialExpressiveTheme { TraceLog(listOf(row), false, Modifier.fillMaxSize()) }
            }
            compose.onNodeWithText("Field notes.txt").performClick()
            compose.waitUntil(8_000) {
                compose.onNodeWithTag("attachment-text").isDisplayed()
            }
            compose.onNodeWithText("Text from the saved attachment.").assertIsDisplayed()
            compose.onNodeWithContentDescription(context.getString(R.string.close)).performClick()
            compose.onNodeWithTag("attachment-preview").assertDoesNotExist()
        } finally { file.delete() }
    }

    @Test
    fun draftImageOpensBeforeSendAndCanStillBeRemovedSeparately() {
        val id = UUID.randomUUID().toString()
        val file = file(id)
        val image = Bitmap.createBitmap(64, 96, Bitmap.Config.ARGB_8888)
        image.eraseColor(android.graphics.Color.rgb(55, 97, 144))
        file.outputStream().use { image.compress(Bitmap.CompressFormat.PNG, 100, it) }
        image.recycle()
        try {
            val metadata = JSONObject().put("id", id).put("name", "Reference.png").put("mime", "image/png")
            val draft = BranchDraft("", mapOf("saved-attachment:" + id to metadata), null)
            compose.setContent {
                MaterialExpressiveTheme {
                    AgentConversation(ChatState(ready = true, chatId = "fixture", branchDraft = draft),
                        AgentRole.Chat, onCancel = {}, onSend = { error("Preview should not send the draft") }, onModels = {})
                }
            }
            compose.waitUntil(8_000) { compose.onNodeWithText("Reference.png").isDisplayed() }
            compose.onNodeWithText("Reference.png").performClick()
            compose.waitUntil(8_000) { compose.onNodeWithContentDescription("Reference.png").isDisplayed() }
            compose.onNodeWithTag("attachment-preview").assertIsDisplayed()
            compose.onNodeWithContentDescription(context.getString(R.string.close)).performClick()
            compose.onNodeWithContentDescription(context.getString(R.string.remove_attachment, "Reference.png")).performClick()
            compose.waitUntil(8_000) { !compose.onNodeWithText("Reference.png").isDisplayed() }
        } finally { file.delete() }
    }

    @Test
    fun audioPreviewLetsTheReaderSeekPlayAndPause() {
        val id = UUID.randomUUID().toString()
        val bytes = ByteBuffer.allocate(64_044).order(ByteOrder.LITTLE_ENDIAN)
        bytes.put("RIFF".toByteArray()).putInt(64_036)
        bytes.put("WAVEfmt ".toByteArray()).putInt(16).putShort(1).putShort(1)
        bytes.putInt(8_000).putInt(8_000).putShort(1).putShort(8)
        bytes.put("data".toByteArray()).putInt(64_000)
        bytes.put(ByteArray(64_000) { 0x80.toByte() })
        val source = file(id).apply { writeBytes(bytes.array()) }
        try {
            val attachment = JSONObject().put("id", id).put("name", "Sample.wav").put("mime", "audio/wav")
            val message = decodeBlock(JSONObject().put("id", "sound-fixture").put("kind", "user")
                .put("attachments", JSONArray().put(attachment)))
            compose.setContent { MaterialExpressiveTheme { TraceLog(listOf(message), false, Modifier.fillMaxSize()) } }
            compose.onNodeWithText("Sample.wav").performClick()
            compose.waitUntil(8_000) { compose.onNodeWithText("0:08").isDisplayed() }
            compose.onNodeWithContentDescription(context.getString(R.string.audio_position))
                .performSemanticsAction(SemanticsActions.SetProgress) { it(0.5f) }
            compose.waitUntil(8_000) { compose.onNodeWithText("0:04").isDisplayed() }
            compose.onNodeWithContentDescription(context.getString(R.string.audio_play)).performClick()
            compose.waitUntil(8_000) { compose.onNodeWithContentDescription(context.getString(R.string.audio_pause)).isDisplayed() }
            compose.onNodeWithContentDescription(context.getString(R.string.audio_pause)).performClick()
            compose.onNodeWithContentDescription(context.getString(R.string.close)).performClick()
            compose.onNodeWithTag("attachment-preview").assertDoesNotExist()
        } finally { source.delete() }
    }

    @Test
    fun generatedLanguageListIncludesFiftyLanguagesAndLocalizedUrduLayout() {
        val tags = appLanguageTags(context.resources)
        assertEquals(50, tags.distinct().size)
        val urdu = Locale.forLanguageTag("ur")
        val localized = context.createConfigurationContext(Configuration(context.resources.configuration).apply {
            setLocales(LocaleList(urdu))
        })
        assertEquals(View.LAYOUT_DIRECTION_RTL, localized.resources.configuration.layoutDirection)
        val english = context.createConfigurationContext(Configuration(context.resources.configuration).apply {
            setLocales(LocaleList(Locale.ENGLISH))
        })
        assertNotEquals(english.getString(R.string.settings), localized.getString(R.string.settings))
    }

    private fun file(id: String): File = context.filesDir.resolve("workspace/attachments").apply { mkdirs() }.resolve(id)
}
