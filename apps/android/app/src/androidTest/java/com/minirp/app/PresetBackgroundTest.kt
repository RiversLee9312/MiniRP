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
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.graphics.compositeOver
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.toPixelMap
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.captureToImage
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onRoot
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
import com.minirp.app.ui.backgroundScrim
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
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PresetBackgroundTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Before
    fun showScreenshotActivity() {
        if (InstrumentationRegistry.getArguments().getString("capturePresetHome") == "true") {
            compose.activityRule.scenario.onActivity { activity ->
                activity.setShowWhenLocked(true)
                activity.setTurnScreenOn(true)
            }
        }
    }

    @Test
    fun homeTitleUsesTheConversationGlassOnlyInItsBanner() {
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
            val banner = compose.onNodeWithTag("preset-home-banner")
            banner.assertIsDisplayed()
            assertEquals(backdrop.fetchSemanticsNode().boundsInRoot.width, banner.fetchSemanticsNode().boundsInRoot.width, 0.5f)
            val title = compose.onNodeWithText("Backdrop title", useUnmergedTree = true)
            fun titleStyle() = mutableListOf<TextLayoutResult>().also { layouts ->
                title.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
            }.single().layoutInput.style
            fun backgroundPixel(atEdge: Boolean = false): Color {
                val bitmap = backdrop.captureToImage().toPixelMap()
                val x = if (atEdge) bitmap.width / 2 - maxOf(bitmap.width, bitmap.height) / 80 else bitmap.width / 10
                return bitmap[x, bitmap.height / 10]
            }
            fun bannerPixel(): Color {
                val row = banner.fetchSemanticsNode().boundsInRoot.center.y - backdrop.fetchSemanticsNode().boundsInRoot.top
                val pixels = backdrop.captureToImage().toPixelMap()
                return pixels[pixels.width / 4, row.toInt()]
            }
            fun assertScrimContrast(darkTheme: Boolean) {
                val colors = compose.runOnIdle { theme }
                val scrim = backgroundScrim(colors, darkTheme)
                for (image in listOf(Color.Black, Color.White)) {
                    val background = scrim.compositeOver(image).luminance()
                    for (color in listOf(colors.onSurface, colors.onSurfaceVariant, colors.primary, colors.error)) {
                        val text = color.luminance()
                        val ratio = (maxOf(text, background) + 0.05f) / (minOf(text, background) + 0.05f)
                        assertTrue("Expected 4.5:1, got ${ratio}:1 with $color over $image", ratio >= 4.5f)
                    }
                }
            }
            assertNull(titleStyle().shadow)
            assertTrue(backgroundPixel().red < 0.04f)
            assertScrimContrast(false)
            val lightGlass = bannerPixel()
            if ((compose.activity.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
                Configuration.UI_MODE_NIGHT_NO) {
                assertTrue("Expected light glass over the blue backdrop: $lightGlass", lightGlass.red > 0.15f)
            }
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
            assertNull(titleStyle().shadow)
            assertScrimContrast(true)
            val darkGlass = bannerPixel()
            assertTrue("Expected dark glass over the blue backdrop: $darkGlass", darkGlass.blue < 0.85f)

            compose.runOnIdle {
                state.value = state.value.copy(chatId = "chat", appearance = PresetAppearance(blurBackground = false))
            }
            banner.assertDoesNotExist()
            val composer = compose.onNodeWithTag("composer")
            assertTrue(backdrop.fetchSemanticsNode().boundsInRoot.bottom <= composer.fetchSemanticsNode().boundsInRoot.top)
            val darkPixel = backgroundPixel()
            assertTrue(darkPixel.blue < 0.85f)
            if (Build.VERSION.SDK_INT >= 31) {
                val sharp = backgroundPixel(atEdge = true)
                compose.runOnIdle { state.value = state.value.copy(appearance = PresetAppearance()) }
                assertTrue(backgroundPixel(atEdge = true).red > sharp.red + 0.01f)
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
        val arguments = InstrumentationRegistry.getArguments()
        val fixture = arguments.getString("backgroundFixture")
        assumeTrue("Pass a signed background fixture with -e backgroundFixture", fixture != null)
        val app = compose.activity.application
        val directory = temporary.newFolder()
        val workspace = directory.resolve("workspace").apply { mkdirs() }
        val preset = "23924493-4bbb-46fa-b373-322eb980ebc1"
        val config = workspace.resolve("presets").apply { mkdirs() }.resolve("$preset.json")
        fun setBackground(id: String, name: String = "Harbor at Dusk", blur: Boolean = true, greeting: Boolean = false) {
            config.writeText(JSONObject().put("name", name).put("appearance", JSONObject()
                .put("background_url", if (id.isEmpty()) "" else "minirp://assets/test.appearance/$id")
                .put("background_blur", blur)).put("chat_agent", JSONObject()
                .put("greeting_system_message", if (greeting) "Describe the harbor at dusk." else "")).toString())
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
            val dark = mutableStateOf(false)
            compose.setContent { MiniRP(model, darkTheme = dark.value) }
            val backdrop = compose.onNodeWithTag("preset-background")
            val banner = compose.onNodeWithTag("preset-home-banner")
            val capturesEnabled = arguments.getString("capturePresetHome") == "true"
            fun capture(name: String) {
                if (!capturesEnabled) return
                app.cacheDir.resolve("preset-home-$name.png").outputStream().use {
                    compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it)
                }
            }
            compose.waitUntil(15_000) { backdrop.isDisplayed() }
            if (capturesEnabled) compose.runOnIdle {
                model.markPresetHintSeen(inChat = false)
                model.markPresetHintSeen(inChat = true)
            }
            banner.assertIsDisplayed()
            compose.onNodeWithText(compose.activity.getString(R.string.welcome_to_preset)).assertIsDisplayed()
            compose.waitUntil(10_000) {
                compose.runOnIdle {
                    views(compose.activity.window.decorView).filterIsInstance<ImageView>()
                        .mapNotNull { it.drawable as? AnimatedImageDrawable }
                        .any { it.isRunning && it.repeatCount == AnimatedImageDrawable.REPEAT_INFINITE }
                }
            }
            capture("gif-light")
            setBackground("video")
            compose.runOnIdle { model.chooseChat(preset) }
            var firstFrame: IntArray? = null
            compose.waitUntil(15_000) {
                compose.runOnIdle {
                    val texture = views(compose.activity.window.decorView).filterIsInstance<TextureView>().firstOrNull()
                    val bitmap = texture?.getBitmap(8, 8) ?: return@runOnIdle false
                    val frame = IntArray(64)
                    bitmap.getPixels(frame, 0, 8, 0, 0, 8, 8)
                    bitmap.recycle()
                    if (frame.all { (it and 0x00ffffff) == 0 }) return@runOnIdle false
                    val first = firstFrame
                    if (first == null) firstFrame = frame
                    first != null && frame.indices.count { frame[it] != first[it] } >= 8
                }
            }
            backdrop.assertIsDisplayed()
            banner.assertIsDisplayed()
            capture("video-light")
            if (capturesEnabled) {
                setBackground("still")
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.appearance.backgroundUrl.endsWith("/still") && banner.isDisplayed() }
                capture("image-light")
                setBackground("still", greeting = true)
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatGreeting && banner.isDisplayed() }
                compose.runOnIdle { model.createChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatId.isNotEmpty() && backdrop.isDisplayed() && !banner.isDisplayed() }
                capture("conversation-light")
                setBackground("still")
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatId.isEmpty() && banner.isDisplayed() }
                compose.runOnIdle { dark.value = true }
                capture("image-dark")
                setBackground("still", "Harbor at Dusk — the Last Ferry Across the Bay", blur = false, greeting = true)
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { !model.state.value.appearance.blurBackground && banner.isDisplayed() }
                capture("image-blur-off-dark")
                compose.runOnIdle { model.createChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatId.isNotEmpty() && backdrop.isDisplayed() && !banner.isDisplayed() }
                capture("conversation-blur-off-dark")
                setBackground("still", greeting = true)
                compose.runOnIdle { dark.value = false; model.chooseChat(preset) }
                val composer = compose.onNodeWithTag("composer")
                compose.waitUntil(15_000) { model.state.value.chatGreeting && banner.isDisplayed() && !composer.isDisplayed() }
                assertEquals(backdrop.fetchSemanticsNode().boundsInRoot.width, banner.fetchSemanticsNode().boundsInRoot.width, 0.5f)
                capture("no-composer-light")
                setBackground("")
                compose.runOnIdle { dark.value = false; model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.appearance.backgroundUrl.isEmpty() && !banner.isDisplayed() }
                capture("no-background-light")
            }
        } finally { scope.cancel() }
    }

    private fun views(view: View): Sequence<View> = sequence {
        yield(view)
        if (view is ViewGroup) for (index in 0 until view.childCount) yieldAll(views(view.getChildAt(index)))
    }
}
