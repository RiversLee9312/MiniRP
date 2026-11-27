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
import android.view.WindowManager
import android.widget.ImageView
import androidx.activity.ComponentActivity
import androidx.activity.enableEdgeToEdge
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
import androidx.compose.ui.test.click
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.text.TextLayoutResult
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
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
import com.minirp.app.ui.accentFromPixels
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
    fun prepareScreenshotActivity() {
        compose.activityRule.scenario.onActivity { activity ->
            if (Build.VERSION.SDK_INT >= 29) activity.window.decorView.isForceDarkAllowed = false
            if (InstrumentationRegistry.getArguments().getString("capturePresetHome") == "true") {
                activity.setShowWhenLocked(true)
                activity.setTurnScreenOn(true)
            }
        }
    }

    @Test
    fun presetAccentUsesTheBroadColorFamilyAndIgnoresNeutralNoise() {
        val ochre = android.graphics.Color.rgb(178, 141, 104)
        val cyan = android.graphics.Color.CYAN
        val pixels = IntArray(100) { if (it < 88) ochre else cyan }
        assertEquals(Color(ochre), accentFromPixels(pixels))
        assertNull(accentFromPixels(IntArray(100) { if (it == 99) cyan else android.graphics.Color.GRAY }))
    }

    @Test
    fun homeTitleUsesOneInkAndACenteredHaloOnMixedWallpaper() {
        val file = File.createTempFile("background-color", ".png", compose.activity.cacheDir)
        try {
            val pixels = Bitmap.createBitmap(64, 64, Bitmap.Config.ARGB_8888)
            pixels.eraseColor(android.graphics.Color.BLUE)
            Canvas(pixels).drawRect(32f, 0f, 64f, 64f, Paint().apply { color = android.graphics.Color.WHITE })
            file.outputStream().use { pixels.compress(Bitmap.CompressFormat.PNG, 100, it) }
            pixels.recycle()
            val media = decodePresetBackground(file, "image/png")
            val seed = checkNotNull(media.seedColor)
            assertTrue(seed.blue > 0.9f && seed.red < 0.1f)
            val dark = mutableStateOf(false)
            val override = mutableStateOf<Color?>(null)
            val state = mutableStateOf(ChatState(preset = "image", presetName = "LLLL RRRR", ready = true))
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
            val heading = compose.onNodeWithTag("preset-home-heading")
            heading.assertIsDisplayed()
            val title = compose.onNodeWithText("LLLL RRRR", useUnmergedTree = true)
            val caption = compose.onNodeWithText(compose.activity.getString(R.string.welcome_to_preset), useUnmergedTree = true)
            fun textLayout(node: androidx.compose.ui.test.SemanticsNodeInteraction) = mutableListOf<TextLayoutResult>().also { layouts ->
                node.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
            }.single().layoutInput
            fun backgroundPixel(xFraction: Float = .10f): Color {
                val bitmap = backdrop.captureToImage().toPixelMap()
                return bitmap[(bitmap.width * xFraction).toInt(), bitmap.height / 10]
            }
            fun wallpaperAtTitle(xFraction: Float): Color {
                val row = heading.fetchSemanticsNode().boundsInRoot.center.y - backdrop.fetchSemanticsNode().boundsInRoot.top
                val pixels = backdrop.captureToImage().toPixelMap()
                return pixels[(pixels.width * xFraction).toInt(), row.toInt()]
            }
            val composer = compose.onNodeWithTag("composer")
            val card = compose.onNodeWithTag("composer-card")
            fun assertWallpaperComposer(): Color {
                card.assertIsDisplayed()
                val wallpaperBounds = backdrop.fetchSemanticsNode().boundsInRoot
                val cardBounds = card.fetchSemanticsNode().boundsInRoot
                assertTrue(wallpaperBounds.bottom > cardBounds.bottom)
                val fieldBounds = composer.fetchSemanticsNode().boundsInRoot
                val modelsBounds = compose.onNodeWithTag("agent-models").fetchSemanticsNode().boundsInRoot
                val sendBounds = compose.onNodeWithTag("send").fetchSemanticsNode().boundsInRoot
                assertTrue(fieldBounds.bottom <= modelsBounds.top + 1)
                assertTrue(fieldBounds.bottom <= sendBounds.top + 1)
                assertTrue(cardBounds.bottom >= maxOf(modelsBounds.bottom, sendBounds.bottom))
                val wallpaperPixels = backdrop.captureToImage().toPixelMap()
                val gutter = ((cardBounds.left - wallpaperBounds.left) / 2).toInt()
                val above = wallpaperPixels[gutter, (cardBounds.top - wallpaperBounds.top).toInt() - 4]
                val beside = wallpaperPixels[gutter, (cardBounds.center.y - wallpaperBounds.top).toInt()]
                assertEquals(above.red, beside.red, .03f)
                assertEquals(above.green, beside.green, .03f)
                assertEquals(above.blue, beside.blue, .03f)
                val inputPixels = card.captureToImage().toPixelMap()
                val fill = inputPixels[inputPixels.width / 2, maxOf(2, inputPixels.height / 24)]
                val neutral = compose.runOnIdle { theme.surfaceContainerLow }
                assertEquals(neutral.red, fill.red, .03f)
                assertEquals(neutral.green, fill.green, .03f)
                assertEquals(neutral.blue, fill.blue, .03f)
                return fill
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
            compose.waitUntil(5_000) {
                textLayout(title).style.shadow != null && textLayout(caption).style.shadow != null
            }
            val lightStyle = textLayout(title).style
            assertTrue(lightStyle.color == Color.Black || lightStyle.color == Color.White)
            assertEquals(lightStyle.color, textLayout(caption).style.color)
            val halo = checkNotNull(lightStyle.shadow)
            assertEquals(halo, textLayout(caption).style.shadow)
            assertEquals(androidx.compose.ui.geometry.Offset.Zero, halo.offset)
            assertTrue(halo.blurRadius >= 23f * compose.activity.resources.displayMetrics.density)
            assertEquals((if (lightStyle.color == Color.Black) Color.White else Color.Black).copy(alpha = .80f), halo.color)
            assertEquals(androidx.compose.ui.text.font.FontWeight.Black, lightStyle.fontWeight)
            assertEquals(androidx.compose.ui.text.font.FontWeight.Black, textLayout(caption).style.fontWeight)
            assertTrue(textLayout(title).text.spanStyles.isEmpty())
            assertTrue(backgroundPixel().red < 0.04f)
            assertScrimContrast(false)
            val fromImage = compose.runOnIdle { theme.primary }
            val imageSurface = compose.runOnIdle { theme.surface }
            compose.runOnIdle {
                val baseline = minirpColors(compose.activity, false)
                assertNotEquals(baseline.surface, theme.surface)
                override.value = Color.Red
            }
            compose.runOnIdle {
                assertNotEquals(fromImage, theme.primary)
                assertNotEquals(imageSurface, theme.surface)
                dark.value = true
            }
            assertEquals(lightStyle.color, textLayout(title).style.color)
            assertEquals(lightStyle.color, textLayout(caption).style.color)
            assertEquals(halo, textLayout(title).style.shadow)
            assertEquals(0f, wallpaperAtTitle(.10f).red, .025f)
            assertEquals(1f, wallpaperAtTitle(.90f).red, .025f)
            if (InstrumentationRegistry.getArguments().getString("capturePresetHome") == "true") {
                compose.activity.cacheDir.resolve("preset-home-title-halo.png").outputStream().use {
                    compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it)
                }
            }
            assertScrimContrast(true)
            val homeComposer = assertWallpaperComposer()

            compose.runOnIdle {
                state.value = state.value.copy(chatId = "chat", appearance = PresetAppearance(blurBackground = false))
            }
            heading.assertDoesNotExist()
            assertEquals(homeComposer, assertWallpaperComposer())
            val darkPixel = backgroundPixel()
            assertTrue(darkPixel.blue < 0.85f)
            val sharp = backgroundPixel(.47f)
            compose.runOnIdle { state.value = state.value.copy(appearance = PresetAppearance()) }
            compose.waitUntil(5_000) {
                val paneGlass = backgroundPixel()
                paneGlass.blue < .55f && paneGlass.red > .07f
            }
            assertTrue(backgroundPixel(.90f).luminance() < .15f)
            if (Build.VERSION.SDK_INT >= 31) {
                assertTrue(kotlin.math.abs(backgroundPixel(.47f).red - sharp.red) > 0.01f)
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
    fun homeTitleTracksMovingWallpaper() {
        val file = File.createTempFile("wallpaper-split", ".gif", compose.activity.cacheDir)
        try {
            // Alternating solid black and white frames can always use unshadowed ink.
            val gif = "R0lGODlhEAAQAIEAAAAAAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQASwAAACwAAAAAEAAQAAAIHQABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFgQEBACH5BAFLAAEALAAAAAAQABAAgf///wAAAAAAAAAAAAgdAAEIHEiwoMGDCBMqXMiwocOHECNKnEixosWBAQEAOw=="
            file.writeBytes(android.util.Base64.decode(gif, android.util.Base64.DEFAULT))
            val media = decodePresetBackground(file, "image/gif")
            compose.setContent {
                MiniRPTheme(darkTheme = true) {
                    AgentConversation(
                        ChatState(preset = "image", presetName = "LLLL RRRR", ready = true),
                        AgentRole.Chat, Modifier.fillMaxSize(), {}, { false }, {}, background = media,
                    )
                }
            }
            val title = compose.onNodeWithText("LLLL RRRR", useUnmergedTree = true)
            for (ink in listOf(Color.White, Color.Black, Color.White)) {
                compose.waitUntil(8_000) {
                    val layouts = mutableListOf<TextLayoutResult>()
                    title.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
                    val style = layouts.single().layoutInput.style
                    style.color == ink && style.shadow == null
                }
            }
        } finally { file.delete() }
    }

    @Test
    @SdkSuppress(minSdkVersion = 28)
    fun signedPresetAssetPlaysGifAndLoopsVideoFromTheRealHome() {
        val arguments = InstrumentationRegistry.getArguments()
        val fixture = arguments.getString("backgroundFixture")
        assumeTrue("Pass a signed background fixture with -e backgroundFixture", fixture != null)
        compose.activityRule.scenario.onActivity {
            it.enableEdgeToEdge()
            it.window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        }
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
            val heading = compose.onNodeWithTag("preset-home-heading")
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
            heading.assertIsDisplayed()
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
            heading.assertIsDisplayed()
            val titleRow = (heading.fetchSemanticsNode().boundsInRoot.center.y -
                backdrop.fetchSemanticsNode().boundsInRoot.top).toInt()
            var firstWallpaper: List<Color>? = null
            compose.waitUntil(10_000) {
                val pixels = backdrop.captureToImage().toPixelMap()
                val current = listOf(pixels.width / 20, pixels.width / 8, pixels.width * 7 / 8, pixels.width * 19 / 20)
                    .map { x -> pixels[x, titleRow] }
                val first = firstWallpaper
                if (first == null) {
                    firstWallpaper = current
                    false
                } else {
                    current.zip(first).any { (now, before) ->
                        maxOf(kotlin.math.abs(before.red - now.red), kotlin.math.abs(before.green - now.green),
                            kotlin.math.abs(before.blue - now.blue)) > .006f
                    }
                }
            }
            capture("video-light")
            if (capturesEnabled) {
                setBackground("still")
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.appearance.backgroundUrl.endsWith("/still") && heading.isDisplayed() }
                capture("image-light")
                setBackground("still", greeting = true)
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatGreeting && heading.isDisplayed() }
                compose.runOnIdle { model.createChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatId.isNotEmpty() && backdrop.isDisplayed() && !heading.isDisplayed() }
                capture("conversation-light")
                compose.runOnIdle { dark.value = true }
                capture("conversation-dark")
                compose.runOnIdle { dark.value = false }
                val composer = compose.onNodeWithTag("composer")
                val composerCard = compose.onNodeWithTag("composer-card")
                val decor = compose.activity.window.decorView
                val rootBottom = compose.onRoot().fetchSemanticsNode().boundsInRoot.bottom
                val cardMargin = 8 * compose.activity.resources.displayMetrics.density
                fun bottomInset() = compose.runOnIdle {
                    ViewCompat.getRootWindowInsets(decor)!!
                        .getInsets(WindowInsetsCompat.Type.navigationBars() or WindowInsetsCompat.Type.ime()).bottom
                }
                fun cardFollowsInset() =
                    kotlin.math.abs(rootBottom - bottomInset() - cardMargin - composerCard.fetchSemanticsNode().boundsInRoot.bottom) <= 2f
                val navigationInset = bottomInset()
                compose.waitUntil(5_000) { cardFollowsInset() }
                composer.performTouchInput { click() }
                compose.waitUntil(10_000) { bottomInset() > navigationInset && cardFollowsInset() }
                capture("conversation-keyboard-light")
                compose.runOnIdle {
                    WindowInsetsControllerCompat(compose.activity.window, decor).hide(WindowInsetsCompat.Type.ime())
                }
                compose.waitUntil(10_000) { bottomInset() == navigationInset && cardFollowsInset() }
                setBackground("still")
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatId.isEmpty() && heading.isDisplayed() }
                compose.runOnIdle { dark.value = true }
                capture("image-dark")
                setBackground("still", "Harbor at Dusk — the Last Ferry Across the Bay", blur = false, greeting = true)
                compose.runOnIdle { model.chooseChat(preset) }
                compose.waitUntil(15_000) { !model.state.value.appearance.blurBackground && heading.isDisplayed() }
                capture("image-blur-off-dark")
                compose.runOnIdle { model.createChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatId.isNotEmpty() && backdrop.isDisplayed() && !heading.isDisplayed() }
                capture("conversation-blur-off-dark")
                setBackground("still", greeting = true)
                compose.runOnIdle { dark.value = false; model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.chatGreeting && heading.isDisplayed() && !composer.isDisplayed() }
                assertEquals(rootBottom, backdrop.fetchSemanticsNode().boundsInRoot.bottom, 0.5f)
                capture("no-composer-light")
                setBackground("")
                compose.runOnIdle { dark.value = false; model.chooseChat(preset) }
                compose.waitUntil(15_000) { model.state.value.appearance.backgroundUrl.isEmpty() && !backdrop.isDisplayed() }
                capture("no-background-light")
            }
        } finally { scope.cancel() }
    }

    private fun views(view: View): Sequence<View> = sequence {
        yield(view)
        if (view is ViewGroup) for (index in 0 until view.childCount) yieldAll(views(view.getChildAt(index)))
    }
}
