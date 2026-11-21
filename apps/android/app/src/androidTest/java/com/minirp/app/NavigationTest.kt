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
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
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
        switchFromCreatedConversation(R.string.new_room)
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
        fun waitFor(resource: Int) {
            compose.waitUntil(10_000) { compose.onNodeWithText(label(resource)).isDisplayed() }
        }
        waitFor(R.string.providers)
        compose.onNodeWithText(label(R.string.providers)).performClick()
        waitFor(R.string.add_provider)
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText(label(R.string.providers)).assertIsDisplayed()
        compose.onNodeWithText(label(R.string.system_prompt)).assertDoesNotExist()
        compose.onNodeWithText(label(R.string.models)).performClick()
        waitFor(R.string.title_model)
        compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText(label(R.string.providers)).assertIsDisplayed()
    }

    @Test
    fun savedModelUsesDiscoveredWindowAndEditableCompactionDefaults() {
        MockWebServer().use { server ->
            server.enqueue(
                MockResponse.Builder()
                    .setHeader("Content-Type", "application/json")
                    .body("""{"data":[{"id":"fixture","name":"Fixture model","context_length":65536}]}""")
                    .build()
            )
            server.start()
            val provider = ProviderConfig(id = "fixture", name = "Fixture", url = server.url("/v1").toString())
            compose.runOnIdle {
                model.saveProvider(provider)
                model.enableModel(SavedModel(provider.id, Choice("fixture", "Fixture model")), true)
            }
            compose.waitUntil(10_000) { model.state.value.assigned(AgentRole.Chat)?.choice?.contextWindow == 65536 }
            fun label(resource: Int) = compose.activity.getString(resource)
            compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
            compose.onNodeWithText(label(R.string.models)).performClick()
            fun openCompactionSettings() {
                compose.onNodeWithTag("models-list").performScrollToKey("model:fixture:fixture")
                compose.onNodeWithContentDescription(label(R.string.compaction_threshold)).performClick()
            }
            openCompactionSettings()
            compose.onNodeWithText(compose.activity.getString(R.string.compaction_model_context, 65536)).assertIsDisplayed()
            compose.onNodeWithText("80").assertIsDisplayed()
            compose.onNodeWithText("70").assertIsDisplayed()
            compose.onNodeWithText(label(R.string.compaction_context_percentage)).performTextReplacement("85")
            compose.onNodeWithText(label(R.string.background_compaction_threshold)).performTextReplacement("75")
            compose.onNodeWithText(label(R.string.save)).performClick()
            val updated = model.state.value.assigned(AgentRole.Chat)!!.compaction
            assertEquals(85, updated.threshold)
            assertEquals(true, updated.background)
            assertEquals(75, updated.backgroundThreshold)
            runBlocking {
                withTimeout(5_000) {
                    while (preferences.load().second.optJSONArray("models")?.optJSONObject(0)
                            ?.optInt("compact_at_percent") != 85
                    ) delay(10)
                }
            }
            openCompactionSettings()
            compose.onNodeWithText("85").assertIsDisplayed()
            compose.onNodeWithText("75").assertIsDisplayed()
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
        val providers = compose.activity.getString(R.string.providers)
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
        fun startGesture(edge: Int) {
            compose.runOnIdle {
                input.backStarted(NavigationEvent(swipeEdge = edge))
                input.backProgressed(NavigationEvent(progress = 0.35f, swipeEdge = edge))
            }
        }
        try {
            startGesture(NavigationEvent.EDGE_LEFT)
            compose.waitUntil(5_000) { gear.isDisplayed() }
            toolbar.assertIsDisplayed()
            assertTrue(toolbar.xInRoot() > original.left)
            assertTrue(gear.xInRoot() < chatX)
            compose.runOnIdle { input.backCancelled() }
            compose.onNodeWithText(providers).assertIsDisplayed()
            assertEquals(original, toolbar.fetchSemanticsNode().boundsInRoot)
            gear.assertDoesNotExist()
            startGesture(NavigationEvent.EDGE_RIGHT)
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
                compose.mainClock.advanceTimeBy(48)
                outgoing += toolbar.xInRoot()
                incoming += gear.xInRoot()
            }
            val exitTravel = outgoing.zipWithNext { first, next -> next - first }
            val enterTravel = incoming.zipWithNext { first, next -> next - first }
            assertTrue("Outgoing page reversed after release: $outgoing", exitTravel.all { it >= 0 })
            assertTrue("Incoming page reversed after release: $incoming", enterTravel.all { it >= 0 })
            assertTrue("Outgoing page should speed up: $exitTravel", exitTravel[2] > exitTravel[1])
            assertTrue("Incoming page should slow down: $enterTravel", enterTravel[1] > enterTravel[2] && enterTravel[2] > 0)
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
        val presetTitle = compose.onNodeWithText(compose.activity.getString(R.string.preset))
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
                    onSend = { _, _, _, _ -> error("An empty home must not send a message") },
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
    fun cleanHomeAndPluginsStayResponsiveWhileTheChatRequestIsPending() {
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
                    val text =
                        when {
                            chat -> "Chat reply after navigating"
                            body.contains("Start the preset setup interview now") -> firstQuestion
                            else -> "[]"
                        }
                    val data =
                        JSONObject()
                            .put(
                                "choices",
                                JSONArray()
                                    .put(
                                        JSONObject().put("delta", JSONObject().put("content", text))
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
            val editor = compose.onNodeWithText(label(R.string.preset))
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
