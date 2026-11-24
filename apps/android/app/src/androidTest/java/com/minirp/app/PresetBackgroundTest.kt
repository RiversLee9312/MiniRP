package com.minirp.app

import android.content.ContextWrapper
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.drawable.AnimatedImageDrawable
import android.os.Build
import android.view.TextureView
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toPixelMap
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.captureToImage
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.text.TextLayoutResult
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.test.filters.SdkSuppress
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.data.Preferences
import com.minirp.app.ui.AgentConversation
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatState
import com.minirp.app.ui.ChatToolbar
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
import com.minirp.app.ui.MiniRPTheme
import com.minirp.app.ui.PresetAppearance
import com.minirp.app.ui.decodePresetBackground
import com.minirp.app.ui.minirpColors
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PresetBackgroundTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun homeTitleUsesAContrastHaloAndChatKeepsTheComposerOutsideTheBackdrop() {
        val file = File.createTempFile("background-color", ".png", compose.activity.cacheDir)
        try {
            val pixels = Bitmap.createBitmap(64, 64, Bitmap.Config.ARGB_8888)
            pixels.eraseColor(android.graphics.Color.BLUE)
            Canvas(pixels).drawRect(32f, 0f, 64f, 64f, Paint().apply { color = android.graphics.Color.WHITE })
            file.outputStream().use { pixels.compress(Bitmap.CompressFormat.PNG, 100, it) }
            pixels.recycle()
            val media = decodePresetBackground(file, "image/png")
            assertTrue(media.seedColor.blue > 0.9f && media.seedColor.red < 0.1f)
            val dark = mutableStateOf(false)
            val override = mutableStateOf<Color?>(null)
            val state = mutableStateOf(ChatState(preset = "image", presetName = "Backdrop title", ready = true))
            lateinit var theme: ColorScheme
            compose.setContent {
                MiniRPTheme(darkTheme = dark.value, seedColor = override.value ?: media.seedColor) {
                    val colors = MaterialTheme.colorScheme
                    SideEffect { theme = colors }
                    Scaffold(topBar = { ChatToolbar(state.value, {}, {}, {}, {}) }) { padding ->
                        AgentConversation(state.value, AgentRole.Chat, Modifier.fillMaxSize().padding(padding),
                            {}, { false }, {}, background = media)
                    }
                }
            }
            val backdrop = compose.onNodeWithTag("preset-background")
            backdrop.assertIsDisplayed()
            val title = compose.onNodeWithText("Backdrop title", useUnmergedTree = true)
            fun titleStyle() = mutableListOf<TextLayoutResult>().also { layouts ->
                title.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
            }.single().layoutInput.style
            fun backgroundPixel(atEdge: Boolean = false): Color {
                val bitmap = backdrop.captureToImage().toPixelMap()
                val x = if (atEdge) bitmap.width / 2 - maxOf(bitmap.width, bitmap.height) / 80 else bitmap.width / 10
                return bitmap[x, bitmap.height / 10]
            }
            assertEquals(Color.Black, titleStyle().color)
            assertEquals(Color.White.copy(alpha = 0.82f), titleStyle().shadow?.color)
            assertTrue(backgroundPixel().red < 0.04f)
            val fromImage = compose.runOnIdle { theme.primary }
            compose.runOnIdle {
                val baseline = minirpColors(compose.activity, false)
                assertEquals(baseline.background, theme.background)
                assertEquals(baseline.surface, theme.surface)
                override.value = Color.Red
            }
            compose.runOnIdle {
                assertNotEquals(fromImage, theme.primary)
                dark.value = true
            }
            assertEquals(Color.White, titleStyle().color)
            assertEquals(Color.Black.copy(alpha = 0.82f), titleStyle().shadow?.color)

            compose.runOnIdle {
                state.value = state.value.copy(chatId = "chat", appearance = PresetAppearance(blurBackground = false))
            }
            val composer = compose.onNodeWithTag("composer")
            assertTrue(backdrop.fetchSemanticsNode().boundsInRoot.bottom <= composer.fetchSemanticsNode().boundsInRoot.top)
            val darkPixel = backgroundPixel()
            assertTrue(darkPixel.blue < 0.85f)
            if (Build.VERSION.SDK_INT >= 31) {
                val sharp = backgroundPixel(atEdge = true)
                compose.runOnIdle { state.value = state.value.copy(appearance = PresetAppearance()) }
                assertTrue(backgroundPixel(atEdge = true).red > sharp.red + 0.1f)
            }
            // Some system dark themes recolor the light test host. Verify the light scrim in a light window.
            if ((compose.activity.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
                Configuration.UI_MODE_NIGHT_NO) {
                compose.runOnIdle { dark.value = false }
                val lightBlurred = backgroundPixel()
                compose.runOnIdle { state.value = state.value.copy(appearance = PresetAppearance(blurBackground = false)) }
                val lightSharp = backgroundPixel()
                assertTrue("Expected a light scrim. With blur: $lightBlurred, without blur: $lightSharp",
                    lightBlurred.red > 0.15f && lightSharp.red > 0.15f)
            }
        } finally { file.delete() }
    }

    @Test
    @SdkSuppress(minSdkVersion = 28)
    fun signedPresetAssetPlaysGifAndLoopsVideoFromTheRealHome() {
        val fixture = InstrumentationRegistry.getArguments().getString("backgroundFixture")
        assumeTrue("Pass a signed background fixture with -e backgroundFixture", fixture != null)
        val app = compose.activity.application
        val directory = temporary.newFolder()
        val workspace = directory.resolve("workspace").apply { mkdirs() }
        val preset = "23924493-4bbb-46fa-b373-322eb980ebc1"
        val config = workspace.resolve("presets").apply { mkdirs() }.resolve("$preset.json")
        fun setBackground(id: String) {
            config.writeText(JSONObject().put("name", "Media backdrop").put("appearance", JSONObject()
                .put("background_url", "minirp://assets/test.appearance/$id")).toString())
        }
        setBackground("gif")
        File(checkNotNull(fixture)).copyTo(workspace.resolve("plugins").apply { mkdirs() }.resolve("background-fixture.plugin"))
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        try {
            val preferences = Preferences(PreferenceDataStoreFactory.create(scope = scope) { directory.resolve("appearance.preferences_pb") })
            val model = compose.runOnIdle {
                ViewModelProvider.create(compose.activity, viewModelFactory {
                    initializer { ChatViewModel(app, context, preferences) }
                })[ChatViewModel::class.java]
            }
            compose.setContent { MiniRP(model) }
            val backdrop = compose.onNodeWithTag("preset-background")
            compose.waitUntil(15_000) { backdrop.isDisplayed() }
            compose.onNodeWithText(compose.activity.getString(R.string.welcome_to_preset)).assertIsDisplayed()
            compose.waitUntil(10_000) {
                compose.runOnIdle {
                    views(compose.activity.window.decorView).filterIsInstance<ImageView>()
                        .mapNotNull { it.drawable as? AnimatedImageDrawable }
                        .any { it.isRunning && it.repeatCount == AnimatedImageDrawable.REPEAT_INFINITE }
                }
            }
            setBackground("video")
            compose.runOnIdle { model.chooseChat(preset) }
            var sawBlue = false
            compose.waitUntil(15_000) {
                compose.runOnIdle {
                    val texture = views(compose.activity.window.decorView).filterIsInstance<TextureView>().firstOrNull()
                    val bitmap = texture?.getBitmap(1, 1) ?: return@runOnIdle false
                    val pixel = Color(bitmap.getPixel(0, 0))
                    bitmap.recycle()
                    if (pixel.blue > 0.65f && pixel.red < 0.3f) sawBlue = true
                    sawBlue && pixel.red > 0.65f && pixel.blue < 0.3f
                }
            }
            backdrop.assertIsDisplayed()
        } finally { scope.cancel() }
    }

    private fun views(view: View): Sequence<View> = sequence {
        yield(view)
        if (view is ViewGroup) for (index in 0 until view.childCount) yieldAll(views(view.getChildAt(index)))
    }
}
