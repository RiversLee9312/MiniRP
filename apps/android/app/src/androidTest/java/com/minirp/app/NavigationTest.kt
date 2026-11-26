package com.minirp.app

import android.content.ContextWrapper
import android.view.KeyEvent
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performScrollToKey
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTextReplacement
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.navigationevent.DirectNavigationEventInput
import androidx.navigationevent.NavigationEvent
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.data.Preferences
import com.minirp.app.ui.AgentConversation
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatState
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.Choice
import com.minirp.app.ui.FullSheet
import com.minirp.app.ui.MemoryScreen
import com.minirp.app.ui.MiniRP
import com.minirp.app.ui.ProviderConfig
import com.minirp.app.ui.SavedModel
import java.io.File
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class NavigationTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()
    private val preferencesScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val preset = UUID.randomUUID().toString()
    private val otherPreset = UUID.randomUUID().toString()
    private lateinit var presetFile: File
    private lateinit var preferences: Preferences
    private lateinit var model: ChatViewModel

    @Before
    fun launchPresetHome() {
        val app = compose.activity.application
        val directory = temporary.newFolder()
        val context =
            object : ContextWrapper(app) {
                override fun getFilesDir(): File = directory
            }
        presetFile =
            directory.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json")
        presetFile.writeText("""{"name":"Navigation fixture"}""")
        presetFile.parentFile!!
            .resolve("$otherPreset.json")
            .writeText("""{"name":"Switch target"}""")
        preferences =
            Preferences(
                PreferenceDataStoreFactory.create(scope = preferencesScope) {
                    directory.resolve("preferences.preferences_pb")
                }
            )
        model = compose.runOnIdle {
            ViewModelProvider.create(
                    compose.activity,
                    viewModelFactory { initializer { ChatViewModel(app, context, preferences) } },
                )[ChatViewModel::class.java]
        }
        compose.setContent { MiniRP(model) }
        compose.waitUntil(10_000) { model.state.value.ready }
        val previous = model.state.value
        compose.runOnIdle { model.chooseChat(preset) }
        compose.waitUntil(10_000) {
            val current = model.state.value
            current.preset == preset &&
                (previous.preset != preset || current.conversationViewKey != previous.conversationViewKey)
        }
    }

    @After
    fun stopFixture() {
        preferencesScope.cancel()
    }

    @Test
    fun presetGreetingUsesTheCenteredCreationActionInsteadOfTheComposer() {
        presetFile.writeText(
            """{"name":"Navigation fixture","chat_agent":{"greeting_system_message":"Open with a warm welcome."}}"""
        )
        compose.runOnIdle { model.chooseChat(preset) }
        val create = compose.onNodeWithText(compose.activity.getString(R.string.new_chat))
        compose.waitUntil(10_000) { model.state.value.chatGreeting && create.isDisplayed() }
        compose.onNodeWithTag("composer").assertDoesNotExist()
        create.performClick()
        compose.waitUntil(10_000) { model.state.value.chatId.isNotBlank() }
        compose.onNodeWithTag("composer").assertIsDisplayed()
    }

    @Test
    fun switchingPresetsFromAConversationReturnsToTheSelectedHome() {
        presetFile.writeText(
            """{"name":"Navigation fixture","chat_agent":{"greeting_system_message":"Welcome the player."}}"""
        )
        switchFromCreatedConversation(R.string.new_chat)
    }

    @Test
    fun switchingPresetsFromARoomReturnsToTheSelectedHome() {
        presetFile.writeText(
            """{"name":"Navigation fixture","chat_mode":"ensemble",
               "ensemble":{"agents":[{"id":"ada","name":"Ada"},{"id":"bryn","name":"Bryn"}],
                 "chats":[{"name":"Bridge","members":["ada","bryn"]}]}}"""
        )
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (request.method == "GET")
                        return MockResponse.Builder().setHeader("Content-Type", "application/json")
                            .body("""{"data":[{"id":"fixture"}]}""").build()
                    val ready = """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"ready","type":"function","function":{"name":"room_ready","arguments":"{}"}}]}}]}"""
                    return MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body("data: $ready\n\ndata: [DONE]\n\n").build()
                }
            }
            server.start()
            compose.runOnIdle {
                val provider = ProviderConfig(id = "room-fixture", name = "Room fixture", url = server.url("/v1").toString())
                model.saveProvider(provider)
                model.enableModel(SavedModel(provider.id, Choice("fixture", "Fixture")), true)
            }
            switchFromCreatedConversation(R.string.new_room)
        }
    }

    private fun switchFromCreatedConversation(createLabel: Int) {
        fun label(resource: Int) = compose.activity.getString(resource)
        compose.runOnIdle { model.chooseChat(preset) }
        val create = compose.onNodeWithText(label(createLabel))
        compose.waitUntil(10_000) { create.isDisplayed() }
        create.performClick()
        compose.waitUntil(10_000) {
            model.state.value.chatId.isNotBlank() && !model.state.value.busy
        }
        compose.onNodeWithContentDescription(label(R.string.open_menu)).performClick()
        compose.onNodeWithText("Navigation fixture").performClick()
        val target = compose.onNodeWithText("Switch target")
        compose.waitUntil(10_000) { target.isDisplayed() }
        target.performClick()
        compose.waitUntil(10_000) {
            model.state.value.preset == otherPreset &&
                model.state.value.chatId.isBlank() &&
                model.state.value.ensembleChats == null
        }
        compose
            .onNode(SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading) and hasText("Switch target"))
            .assertIsDisplayed()
        compose.onNodeWithText("Bridge").assertDoesNotExist()
    }

    @Test
    fun settingsActionAndPluginPageReturnToTheirParent() {
        fun label(resource: Int) = compose.activity.getString(resource)
        compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
        val categories = listOf(
            R.string.model_provider_settings, R.string.behavior_settings, R.string.research_apis,
            R.string.plugins, R.string.about,
        )
        compose.waitUntil(10_000) { settingRow(categories.first()).isDisplayed() }
        val positions = categories.map { settingRow(it).fetchSemanticsNode().boundsInRoot.top }
        assertTrue(positions.zipWithNext().all { (first, next) -> first < next })
        settingRow(R.string.providers).assertDoesNotExist()
        settingRow(R.string.models).assertDoesNotExist()
        settingRow(R.string.model_provider_settings).performClick()
        settingRow(R.string.providers).performClick()
        compose.waitUntil(10_000) { compose.onNodeWithText(label(R.string.add_provider)).isDisplayed() }
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        settingRow(R.string.providers).assertIsDisplayed()
        compose.onNodeWithText(label(R.string.system_prompt)).assertDoesNotExist()
        settingRow(R.string.models).performClick()
        compose.waitUntil(10_000) { compose.onNodeWithText(label(R.string.title_model)).isDisplayed() }
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        settingRow(R.string.providers).assertIsDisplayed()
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }

        val about = runBlocking { model.pluginPage("base", "about") }
        val notices = about.children.single { it.key == "notices" }
        val noticesTitle = notices.localized(compose.activity.resources.configuration.locales[0])
        val noticesLink = compose.onNode(hasText(noticesTitle) and hasClickAction())
        settingRow(R.string.about).performClick()
        noticesLink.performClick()
        compose.onNodeWithText(noticesTitle).assertIsDisplayed()
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        compose.waitUntil(10_000) { noticesLink.isDisplayed() }
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        settingRow(R.string.about).assertIsDisplayed()
    }

    @Test
    fun descriptionModelsAreAssignedIndependentlyInModelSettings() {
        MockWebServer().use { server ->
            server.enqueue(MockResponse.Builder().setHeader("Content-Type", "application/json").body("""{"data":[]}""").build())
            server.start()
            val provider = ProviderConfig(id = "media-fixture", name = "Media fixture", url = server.url("/v1").toString())
            compose.runOnIdle {
                model.saveProvider(provider)
                model.enableModel(SavedModel(provider.id, Choice("text", "Text fixture", inputModalities = listOf("text"))), true)
                model.enableModel(SavedModel(provider.id, Choice("vision", "Vision fixture", inputModalities = listOf("image"))), true)
                model.enableModel(SavedModel(provider.id, Choice("audio", "Audio fixture", inputModalities = listOf("audio"))), true)
            }
            fun label(resource: Int) = compose.activity.getString(resource)
            openSettingsCategory(R.string.model_provider_settings)
            settingRow(R.string.models).performClick()
            fun select(role: AgentRole) {
                compose.onNodeWithTag("models-list").performScrollToKey("assignment:${role.id}")
                settingRow(role.label).performClick()
            }
            fun choice(name: String) = compose.onNode(hasText(name) and hasClickAction() and hasAnyAncestor(isDialog()))
            select(AgentRole.ImageDescription)
            compose.onNodeWithText(label(R.string.image_description_help)).assertIsDisplayed()
            choice("Text fixture").assertDoesNotExist()
            choice("Audio fixture").assertDoesNotExist()
            choice("Vision fixture").performClick()
            select(AgentRole.AudioDescription)
            compose.onNodeWithText(label(R.string.audio_description_help)).assertIsDisplayed()
            choice("Audio fixture").performClick()
            assertEquals("vision", model.state.value.assigned(AgentRole.ImageDescription)?.choice?.id)
            assertEquals("audio", model.state.value.assigned(AgentRole.AudioDescription)?.choice?.id)
            select(AgentRole.ImageDescription)
            choice(label(R.string.media_description_off)).performClick()
            assertEquals(null, model.state.value.assigned(AgentRole.ImageDescription))
            assertEquals("audio", model.state.value.assigned(AgentRole.AudioDescription)?.choice?.id)
        }
    }

    @Test
    fun behaviorContainsSharedSettingsAndOpensAndroidBackgroundReplies() {
        fun label(resource: Int) = compose.activity.getString(resource)
        fun text(resource: Int) = compose.onNodeWithText(label(resource))
        compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
        compose.waitUntil(10_000) { settingRow(R.string.behavior_settings).isDisplayed() }
        settingRow(R.string.background_turns).assertDoesNotExist()
        settingRow(R.string.behavior_settings).performClick()
        settingRow(R.string.language).assertIsDisplayed()
        text(R.string.appearance).assertDoesNotExist()
        val expanded = model.state.value.expandThoughts
        compose.onNode(SemanticsMatcher.keyIsDefined(SemanticsProperties.ToggleableState)).performClick()
        compose.waitUntil(5_000) { model.state.value.expandThoughts != expanded }
        val background = settingRow(R.string.background_turns)
        background.performClick()
        text(R.string.background_turns_description).assertIsDisplayed()
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        background.assertIsDisplayed()
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        settingRow(R.string.behavior_settings).assertIsDisplayed()
    }

    private fun settingRow(id: Int): SemanticsNodeInteraction =
        compose.onNode(hasText(compose.activity.getString(id)) and hasClickAction())

    private fun openSettingsCategory(id: Int) {
        compose.onNodeWithContentDescription(compose.activity.getString(R.string.settings)).performClick()
        val category = settingRow(id)
        compose.waitUntil(10_000) { category.isDisplayed() }
        category.performClick()
    }

    @Test
    fun webResearchChoosesIndependentServicesAndEncryptsTheirKeys() {
        fun label(resource: Int) = compose.activity.getString(resource)
        fun text(resource: Int) = compose.onNodeWithText(label(resource))
        openSettingsCategory(R.string.research_apis)
        settingRow(R.string.fetch_mode).performClick()
        text(R.string.fetch_browser).performClick()
        compose.waitUntil(5_000) { model.state.value.fetchMode == "local" }
        settingRow(R.string.providers).performClick()
        text(R.string.research_search_provider).performClick()
        compose.onNodeWithText("Brave Search").performClick()
        text(R.string.research_fetch_provider).performClick()
        compose.onNodeWithText("Exa").performClick()
        compose.onNodeWithText("Brave Search · ${label(R.string.api_key)}").performTextInput("mock-brave-key")
        compose.onNodeWithText("Exa · ${label(R.string.api_key)}").performTextInput("mock-exa-key")
        text(R.string.save).performClick()
        compose.waitUntil(5_000) {
            val options = runBlocking { preferences.load().second }
            options.optString("search_provider") == "brave" && options.optString("fetch_provider") == "exa" &&
                options.optJSONObject("research_keys")?.optString("exa") == "mock-exa-key"
        }
        val stored = presetFile.parentFile!!.parentFile!!.parentFile!!
            .resolve("preferences.preferences_pb").readBytes().toString(Charsets.ISO_8859_1)
        assertFalse(stored.contains("mock-brave-key"))
        assertFalse(stored.contains("mock-exa-key"))
    }

    @Test
    fun savedModelUsesDiscoveredWindowAndEffortLevels() {
        MockWebServer().use { server ->
            val defaultEffort = "fixture-deliberate"
            val selectedEffort = "fixture-quick"
            val chatRequest = LinkedBlockingQueue<JSONObject>()
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (request.url.encodedPath == "/v1/models") {
                        return MockResponse.Builder()
                            .setHeader("Content-Type", "application/json")
                            .body("""{"data":[{"id":"fixture","name":"Fixture model","context_length":65536,"effort":{"supported_levels":["$selectedEffort","$defaultEffort"]}}]}""")
                            .build()
                    }
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    if (body.toString().contains("Verify my model effort")) chatRequest.offer(body)
                    return MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body("data: {\"choices\":[{\"delta\":{\"content\":\"Model fixture reply\"}}]}\n\ndata: [DONE]\n\n")
                        .build()
                }
            }
            server.start()
            val provider = ProviderConfig(id = "fixture", name = "Fixture", url = server.url("/v1").toString())
            compose.runOnIdle {
                model.saveProvider(provider)
                model.enableModel(SavedModel(provider.id, Choice("fixture", "Fixture model")), true)
            }
            compose.waitUntil(10_000) { model.state.value.assigned(AgentRole.Chat)?.choice?.contextWindow == 65536 }
            fun label(resource: Int) = compose.activity.getString(resource)
            openSettingsCategory(R.string.model_provider_settings)
            settingRow(R.string.models).performClick()
            fun openModelSettings() {
                compose.onNodeWithTag("models-list").performScrollToKey("model:fixture:fixture")
                compose.onNodeWithContentDescription(label(R.string.model_settings)).performClick()
            }
            openModelSettings()
            compose.onNodeWithTag("model-thinking-effort").assertTextContains(defaultEffort).performClick()
            compose.onNodeWithText("high").assertDoesNotExist()
            compose.onNodeWithText(selectedEffort).performClick()
            compose.onNodeWithText(compose.activity.getString(R.string.compaction_model_context, 65536)).assertIsDisplayed()
            compose.onNodeWithText("80").assertIsDisplayed()
            compose.onNodeWithText("70").performScrollTo().assertIsDisplayed()
            compose.onNodeWithText(label(R.string.compaction_context_percentage)).performTextReplacement("85")
            compose.onNodeWithText(label(R.string.background_compaction_threshold)).performTextReplacement("75")
            compose.onNodeWithText(label(R.string.save)).performClick()
            val updated = model.state.value.assigned(AgentRole.Chat)!!.compaction
            assertEquals(85, updated.threshold)
            assertEquals(true, updated.background)
            assertEquals(75, updated.backgroundThreshold)
            runBlocking {
                withTimeout(5_000) {
                    while (true) {
                        val saved = preferences.load().second.optJSONArray("models")?.optJSONObject(0)
                        if (saved?.optInt("compact_at_percent") == 85 &&
                            saved.optString("reasoning_effort") == selectedEffort) break
                        delay(10)
                    }
                }
            }
            openModelSettings()
            compose.onNodeWithText("85").assertIsDisplayed()
            compose.onNodeWithText("75").performScrollTo().assertIsDisplayed()
            compose.onNodeWithTag("model-thinking-effort").performScrollTo().assertTextContains(selectedEffort)
            compose.onNodeWithText(label(R.string.cancel)).performClick()
            compose.runOnIdle {
                model.viewModelScope.launch { model.send("Verify my model effort", emptyList(), AgentRole.Chat) }
            }
            assertEquals(selectedEffort, chatRequest.poll(10, TimeUnit.SECONDS)?.optString("reasoning_effort"))
        }
    }

    @Test
    fun pluginsOpensAboveSettingsAndDoesNotReappearAfterReturningToChat() {
        fun label(resource: Int) = compose.activity.getString(resource)
        val settings = compose.onNodeWithContentDescription(label(R.string.settings))
        val plugins = compose.onNodeWithText(label(R.string.plugins))
        val install = compose.onNodeWithText(label(R.string.install_plugin))
        fun back() {
            compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        }
        repeat(2) {
            settings.performClick()
            compose.waitUntil(10_000) { plugins.isDisplayed() }
            plugins.performClick()
            compose.waitUntil(10_000) { install.isDisplayed() }
            back()
            plugins.assertIsDisplayed()
            install.assertDoesNotExist()
            back()
            settings.assertIsDisplayed()
            plugins.assertDoesNotExist()
            install.assertDoesNotExist()
        }
    }

    @Test
    fun predictiveBackFinishesItsTransitionAfterReleaseAndRestoresOnCancel() {
        val settings = compose.activity.getString(R.string.settings)
        val modelsAndProviders = compose.activity.getString(R.string.model_provider_settings)
        val gear = compose.onNodeWithContentDescription(settings)
        val composer = compose.onNodeWithTag("composer")
        compose.waitUntil(10_000) { composer.isDisplayed() }
        composer.performTextInput("Predictive back draft")
        val chatX = gear.xInRoot()
        gear.performClick()
        val toolbar = compose.onNodeWithText(settings)
        val original = toolbar.fetchSemanticsNode().boundsInRoot
        val input = DirectNavigationEventInput()
        compose.runOnIdle {
            compose.activity.navigationEventDispatcher.addInput(input)
        }
        fun drag(edge: Int, progress: Float) {
            compose.runOnIdle { input.backProgressed(NavigationEvent(progress = progress, swipeEdge = edge)) }
        }
        fun startGesture(edge: Int, progress: Float = 0.35f) {
            compose.runOnIdle {
                input.backStarted(NavigationEvent(swipeEdge = edge))
                input.backProgressed(NavigationEvent(progress = progress, swipeEdge = edge))
            }
        }
        try {
            startGesture(NavigationEvent.EDGE_LEFT, 0.2f)
            compose.waitUntil(5_000) { gear.isDisplayed() }
            toolbar.assertIsDisplayed()
            assertTrue(toolbar.xInRoot() > original.left)
            assertTrue(gear.xInRoot() < chatX)
            val outgoingWhileHeld = mutableListOf(toolbar.xInRoot())
            val incomingWhileHeld = mutableListOf(gear.xInRoot())
            for (progress in listOf(0.4f, 0.6f)) {
                drag(NavigationEvent.EDGE_LEFT, progress)
                outgoingWhileHeld += toolbar.xInRoot()
                incomingWhileHeld += gear.xInRoot()
            }
            for (positions in listOf(outgoingWhileHeld, incomingWhileHeld)) {
                val travel = positions.zipWithNext { first, next -> next - first }
                assertTrue("Equal gesture increments should travel equal distances: $positions",
                    travel[0] > 0 && kotlin.math.abs(travel[1] - travel[0]) <= 2f)
            }
            compose.runOnIdle { input.backCancelled() }
            compose.onNodeWithText(modelsAndProviders).assertIsDisplayed()
            assertEquals(original, toolbar.fetchSemanticsNode().boundsInRoot)
            gear.assertDoesNotExist()
            startGesture(NavigationEvent.EDGE_RIGHT, 0.2f)
            compose.waitUntil(5_000) { gear.isDisplayed() }
            toolbar.assertIsDisplayed()
            val outgoingAtRelease = toolbar.xInRoot()
            val incomingAtRelease = gear.xInRoot()
            compose.mainClock.autoAdvance = false
            compose.runOnUiThread { input.backCompleted() }
            compose.mainClock.advanceTimeByFrame()
            compose.mainClock.advanceTimeByFrame()
            toolbar.assertIsDisplayed()
            gear.assertIsDisplayed()
            val outgoing = listOf(outgoingAtRelease, toolbar.xInRoot()).toMutableList()
            val incoming = listOf(incomingAtRelease, gear.xInRoot()).toMutableList()
            repeat(2) {
                compose.mainClock.advanceTimeByFrame()
                outgoing += toolbar.xInRoot()
                incoming += gear.xInRoot()
            }
            val exitTravel = outgoing.zipWithNext { first, next -> next - first }
            val enterTravel = incoming.zipWithNext { first, next -> next - first }
            assertTrue("Outgoing page reversed after release: $outgoing", exitTravel.all { it >= 0 })
            assertTrue("Incoming page reversed after release: $incoming", enterTravel.all { it >= 0 })
            assertTrue("The release should already be moving on its first interval: $outgoing",
                exitTravel[1] > (outgoingAtRelease - original.left) * 0.15f)
            assertTrue("Outgoing page should speed up: $outgoing ($exitTravel)", exitTravel[2] > exitTravel[1])
            assertTrue("Incoming page should slow down: $enterTravel", enterTravel[1] > enterTravel[2] && enterTravel[2] > 0)
            compose.mainClock.advanceTimeBy(112)
            assertEquals(chatX, gear.xInRoot(), 1f)
            compose.mainClock.autoAdvance = true
            gear.assertIsDisplayed()
            toolbar.assertDoesNotExist()
            composer.assertTextContains("Predictive back draft")
        } finally {
            compose.mainClock.autoAdvance = true
            compose.runOnIdle { compose.activity.navigationEventDispatcher.removeInput(input) }
        }
    }

    @Test
    fun openingSettingsSlidesBothPagesLeftWithOppositeEasing() {
        val settings = compose.activity.getString(R.string.settings)
        val gear = compose.onNodeWithContentDescription(settings)
        val toolbar = compose.onNodeWithText(settings)
        compose.waitUntil(10_000) { gear.isDisplayed() }
        val chatX = gear.xInRoot()
        compose.mainClock.autoAdvance = false
        try {
            gear.performClick()
            compose.mainClock.advanceTimeBy(48)
            val outgoing = mutableListOf(gear.xInRoot())
            val incoming = mutableListOf(toolbar.xInRoot())
            repeat(2) {
                compose.mainClock.advanceTimeBy(48)
                outgoing += gear.xInRoot()
                incoming += toolbar.xInRoot()
            }
            val exitTravel = outgoing.zipWithNext { first, next -> first - next }
            val enterTravel = incoming.zipWithNext { first, next -> first - next }
            assertTrue("Outgoing page should move left: $outgoing", outgoing.first() <= chatX && exitTravel.all { it > 0 })
            assertTrue("Outgoing page should speed up: $exitTravel", exitTravel[1] > exitTravel[0])
            assertTrue("Incoming page should move left and slow down: $enterTravel", enterTravel[0] > enterTravel[1] && enterTravel[1] > 0)
        } finally {
            compose.mainClock.autoAdvance = true
        }
        toolbar.assertIsDisplayed()
        gear.assertDoesNotExist()
    }

    private fun SemanticsNodeInteraction.xInRoot(): Float = fetchSemanticsNode().positionInRoot.x

    @Test
    fun settingsRoundTripPreservesTheComposerDraft() {
        val input = compose.onNodeWithTag("composer")
        val gear =
            compose.onNodeWithContentDescription(compose.activity.getString(R.string.settings))
        compose.waitUntil(10_000) { input.isDisplayed() }
        input.performTextInput("A draft to keep")
        gear.performClick()
        compose
            .onNodeWithContentDescription(compose.activity.getString(R.string.back))
            .performClick()
        input.assertTextContains("A draft to keep")
    }

    @Test
    fun homePresetTitleOpensItsAgentAndReturnsToHome() {
        val heading =
            compose.onNode(
                SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading) and hasClickAction()
            )
        val presetTitle = compose.onNode(
            SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading) and
                hasText(compose.activity.getString(R.string.preset))
        )
        val chatMenu =
            compose.onNodeWithContentDescription(compose.activity.getString(R.string.more_options))
        compose.waitUntil(10_000) { heading.isDisplayed() }
        heading.performClick()
        compose.waitUntil(10_000) { presetTitle.isDisplayed() }
        compose
            .onNodeWithText(compose.activity.getString(R.string.agentic_search))
            .assertDoesNotExist()
        chatMenu.assertDoesNotExist()
        compose
            .onNodeWithContentDescription(compose.activity.getString(R.string.close))
            .performClick()
        compose.waitUntil(5_000) { !presetTitle.isDisplayed() }
        heading.assertIsDisplayed()
        chatMenu.assertDoesNotExist()
    }
}

class MemoryNavigationTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun backFromTheEditorReturnsToNotesBeforeClosingTheSheet() {
        val model = ViewModelProvider(compose.activity)[ChatViewModel::class.java]
        compose.setContent {
            MaterialExpressiveTheme {
                var open by remember { mutableStateOf(true) }
                if (open) {
                    FullSheet("Memory test", { open = false }) {
                        MemoryScreen("", model, Modifier.weight(1f).fillMaxWidth())
                    }
                }
            }
        }
        val newNote = compose.onNodeWithText(compose.activity.getString(R.string.new_note))
        val noteKey = compose.onNodeWithText(compose.activity.getString(R.string.entry_key))
        val sheet = compose.onNodeWithText("Memory test")
        compose.waitUntil(10_000) {
            newNote.isDisplayed() &&
                !newNote.fetchSemanticsNode().config.contains(SemanticsProperties.Disabled)
        }
        newNote.performClick()
        compose.waitUntil(5_000) { noteKey.isDisplayed() }
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
        compose.waitUntil(5_000) { newNote.isDisplayed() }
        noteKey.assertDoesNotExist()
        sheet.assertIsDisplayed()
        instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
        compose.waitUntil(5_000) { !sheet.isDisplayed() }
    }
}

class EmptyHomeTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun emptyHomeOnlyOffersToCreateAPresetInItsContent() {
        var created = false
        compose.setContent {
            MaterialExpressiveTheme {
                AgentConversation(
                    state = ChatState(ready = true),
                    agent = AgentRole.Chat,
                    modifier = Modifier.fillMaxSize(),
                    onCancel = {},
                    onSend = { error("An empty home must not send a message") },
                    onModels = {},
                    onCreatePreset = { created = true },
                )
            }
        }
        compose
            .onNodeWithText(compose.activity.getString(R.string.no_presets_yet))
            .assertIsDisplayed()
        compose.onNodeWithTag("composer").assertDoesNotExist()
        compose.onNodeWithText(compose.activity.getString(R.string.create_preset)).performClick()
        compose.runOnIdle { assertEquals(true, created) }
    }
}

class PluginNavigationDuringGenerationTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun cleanHomeAndSettingsStayResponsiveWhileTheChatRequestIsPending() {
        val application = compose.activity.application
        val directory = temporary.newFolder()
        val workspace =
            object : ContextWrapper(application) {
                override fun getFilesDir(): File = directory
            }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val received = CountDownLatch(1)
        val release = CountDownLatch(1)
        val message = "Wait for the navigation test"
        val firstQuestion = "What kind of world would you like to create?"
        val setupChoice = "One character without delegated search."
        fun toolCall(index: Int, name: String, arguments: String = "{}") =
            JSONObject().put("index", index).put("id", "setup-$index").put("type", "function")
                .put("function", JSONObject().put("name", name).put("arguments", arguments))
        val server = MockWebServer()
        server.dispatcher =
            object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    val body = request.body?.utf8().orEmpty()
                    val chat = body.contains(message)
                    if (chat) {
                        received.countDown()
                        release.await(30, TimeUnit.SECONDS)
                    }
                    val messages = JSONObject(body).getJSONArray("messages")
                    val lastRole = messages.getJSONObject(messages.length() - 1).getString("role")
                    val delta =
                        when {
                            chat -> JSONObject().put("content", "Chat reply after navigating")
                            body.contains(setupChoice) && lastRole != "tool" ->
                                JSONObject().put(
                                    "tool_calls",
                                    JSONArray()
                                        .put(toolCall(0, "configure_experience", """{"mode":"character","agentic_search":false}"""))
                                        .put(toolCall(1, "finish_preset_setup")),
                                )
                            body.contains(setupChoice) -> JSONObject().put("content", "Preset ready.")
                            body.contains("Start the preset setup interview now") -> JSONObject().put("content", firstQuestion)
                            else -> JSONObject().put("content", "[]")
                        }
                    val data =
                        JSONObject()
                            .put(
                                "choices",
                                JSONArray()
                                    .put(
                                        JSONObject().put("delta", delta)
                                    ),
                            )
                    return MockResponse.Builder()
                        .setHeader("Content-Type", "text/event-stream")
                        .body("data: $data\n\ndata: [DONE]\n\n")
                        .build()
                }
            }
        server.start()
        try {
            val preferences =
                Preferences(
                    PreferenceDataStoreFactory.create(scope = scope) {
                        directory.resolve("navigation.preferences_pb")
                    }
                )
            runBlocking {
                preferences.save(
                    JSONArray()
                        .put(
                            JSONObject()
                                .put("id", "fixture")
                                .put("name", "Fixture")
                                .put("protocol", "openai")
                                .put("model", "fixture")
                                .put("url", server.url("/v1").toString())
                        ),
                    JSONObject(),
                )
            }
            val model = compose.runOnIdle {
                ViewModelProvider.create(
                        compose.activity,
                        viewModelFactory {
                            initializer { ChatViewModel(application, workspace, preferences) }
                        },
                    )[ChatViewModel::class.java]
            }
            compose.setContent { MiniRP(model) }
            fun label(id: Int) = compose.activity.getString(id)
            val settings = compose.onNodeWithContentDescription(label(R.string.settings))
            val plugins = compose.onNodeWithText(label(R.string.plugins))
            val install = compose.onNodeWithText(label(R.string.install_plugin))
            val create = compose.onNodeWithText(label(R.string.create_preset))
            val editor = compose.onNode(
                hasText(label(R.string.preset)) and
                    SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading) and !hasClickAction()
            )
            fun back() {
                compose.activityRule.scenario.onActivity {
                    it.onBackPressedDispatcher.onBackPressed()
                }
            }
            fun visitPlugins() {
                settings.performClick()
                compose.waitUntil(5_000) { plugins.isDisplayed() }
                plugins.performClick()
                compose.waitUntil(5_000) { install.isDisplayed() }
                back()
                plugins.assertIsDisplayed()
                back()
                settings.assertIsDisplayed()
                install.assertDoesNotExist()
            }
            compose.waitUntil(10_000) { create.isDisplayed() }
            compose.onNodeWithText(label(R.string.no_presets_yet)).assertIsDisplayed()
            visitPlugins()

            create.performClick()
            compose.waitUntil(10_000) { editor.isDisplayed() }
            compose.waitUntil(10_000) {
                !model.state.value.busy &&
                    model.state.value.generating == null &&
                    compose.onAllNodesWithText(firstQuestion).fetchSemanticsNodes().isNotEmpty()
            }
            compose
                .onNodeWithText("Start the preset setup interview now", substring = true)
                .assertDoesNotExist()
            compose.runOnIdle {
                model.viewModelScope.launch { model.send(setupChoice, emptyList(), AgentRole.Preset) }
            }
            compose.waitUntil(5_000) {
                !model.state.value.presetSetupPending && !model.state.value.busy
            }
            compose.onNodeWithContentDescription(label(R.string.close)).performClick()
            val heading =
                compose.onNode(
                    SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading) and hasClickAction()
                )
            compose.waitUntil(5_000) { !editor.isDisplayed() && heading.isDisplayed() }
            heading.performClick()
            compose.waitUntil(5_000) { editor.isDisplayed() }
            compose.onNodeWithContentDescription(label(R.string.close)).performClick()
            compose.waitUntil(5_000) { !editor.isDisplayed() }

            compose.runOnIdle {
                model.viewModelScope.launch { model.send(message, emptyList(), AgentRole.Chat) }
            }
            check(received.await(10, TimeUnit.SECONDS)) {
                "Chat request did not arrive at the test server"
            }
            visitPlugins()
            settings.performClick()
            val behavior = compose.onNode(hasText(label(R.string.behavior_settings)) and hasClickAction())
            compose.waitUntil(5_000) { behavior.isDisplayed() }
            behavior.performClick()
            val language = compose.onNode(hasText(label(R.string.language)) and hasClickAction())
            compose.waitUntil(5_000) { language.isDisplayed() }
            assertTrue(model.state.value.busy)
            back()
            behavior.assertIsDisplayed()
            back()
            settings.assertIsDisplayed()
            release.countDown()
            compose.waitUntil(10_000) {
                !model.state.value.busy &&
                    compose
                        .onAllNodesWithText("Chat reply after navigating")
                        .fetchSemanticsNodes()
                        .isNotEmpty()
            }
            install.assertDoesNotExist()
        } finally {
            release.countDown()
            scope.cancel()
            server.close()
        }
    }
}
