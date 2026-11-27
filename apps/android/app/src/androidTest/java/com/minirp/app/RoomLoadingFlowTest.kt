package com.minirp.app

import android.content.ContextWrapper
import android.graphics.Bitmap
import android.graphics.Paint
import android.view.View
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.core.content.res.ResourcesCompat
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.navigationevent.DirectNavigationEventInput
import androidx.navigationevent.NavigationEvent
import com.minirp.app.data.Preferences
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class RoomLoadingFlowTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun newRoomWaitsForConcurrentPreparationAndEachCharacterOpening() = runRoom(usePluginProgress = false)

    @Test
    fun pluginProgressTracksTheDeclaredWeightsThroughRoomOpening() = runRoom(usePluginProgress = true)

    private fun runRoom(usePluginProgress: Boolean) {
        val application = compose.activity.application
        val directory = temporary.newFolder()
        val workspace = object : ContextWrapper(application) {
            override fun getFilesDir() = directory
        }
        val preset = UUID.randomUUID().toString()
        val first = "Navigation lights drift past the window."
        val second = "The next shift checks the gate."
        val publicInput = "Check the movement outside."
        val privateInput = "Please confirm the motion outside the story."
        val archiveInput = "Archive the lab after the crew leaves."
        val stopInput = "Ask Bryn to stop."
        val startInput = "Ask Bryn to start."
        val inspectInput = "Inspect the availability of the crew."
        val resumedInput = "Check the bridge instruments after returning."
        val activationNote = "The new shift is now on watch."
        val traceReason = "Checking the station readings."
        val backgroundUrl = if (usePluginProgress) "minirp://assets/test.loading_fixture/station" else ""
        directory.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json").writeText("""
            {"name":"Station", "chat_mode":"ensemble", "agentic_search":false,
             "appearance":{"background_url":"$backgroundUrl"},
             "chat_agent":{"greeting_system_message":"Open the watch in both scenes."}, "ensemble":{
             "agents":[{"id":"ada", "name":"Ada"},{"id":"bryn", "name":"Bryn"}],
             "chats":[{"name":"Bridge", "members":["ada","bryn"]},{"name":"Lab", "members":["bryn"]}],
             "direct_messages_in_story":false,
             "loading_messages":["$first","$second"]}}
        """.trimIndent())
        if (usePluginProgress) writeProgressPlugin(directory.resolve("workspace"), preset)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val requests = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        val adaReady = CountDownLatch(1)
        val brynReady = CountDownLatch(1)
        val brynStarted = CountDownLatch(1)
        val labOpening = CountDownLatch(1)
        val publicReply = CountDownLatch(1)
        val openingCount = AtomicInteger()
        val labScene = AtomicReference<String>()
        val brynAvailability = AtomicReference<String>()
        val confirmations = AtomicInteger()
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse = try {
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    val order = requests.incrementAndGet()
                    val tools = body.optJSONArray("tools") ?: JSONArray()
                    val names = (0 until tools.length()).map {
                        tools.getJSONObject(it).getJSONObject("function").getString("name")
                    }.toSet()
                    val messages = body.getJSONArray("messages")
                    val prompt = messages.toString()
                    val last = messages.getJSONObject(messages.length() - 1)
                    val ready = """{"name":"room_ready","arguments":"{}"}"""
                    val scene = Regex("The triggering chat is (Bridge|Lab) \\(([0-9a-f-]+)\\)")
                    val direct = Regex("only permitted recipient is this private chat \\(([0-9a-f-]+)\\)")
                    fun post(chat: String, text: String): String = JSONObject()
                        .put("name", "chat_send").put("arguments", JSONObject()
                            .put("chat", chat).put("text", text).toString()).toString()
                    fun tool(name: String, args: JSONObject = JSONObject()) = JSONObject()
                        .put("name", name).put("arguments", args.toString()).toString()
                    var reasoning = ""
                    val call = when {
                        names.isEmpty() -> null // Optional room title generation.
                        "disable_chat_agent" in names && last.getString("role") == "tool" -> {
                            val result = JSONObject(last.getString("content"))
                            when (last.getString("tool_call_id").substringBefore(':')) {
                                "disable_chat_agent", "enable_chat_agent" -> assertTrue(result.getBoolean("accepted"))
                                "inspect_chats" -> {
                                    val agents = result.getJSONArray("agents")
                                    val bryn = (0 until agents.length()).map(agents::getJSONObject)
                                        .single { it.getString("id") == "bryn" }
                                    brynAvailability.set(bryn.getString("lifecycle"))
                                }
                            }
                            null
                        }
                        "disable_chat_agent" in names && stopInput in last.optString("content") ->
                            tool("disable_chat_agent", JSONObject().put("participant", "bryn"))
                        "enable_chat_agent" in names && startInput in last.optString("content") ->
                            tool("enable_chat_agent", JSONObject().put("participant", "bryn").put("message", activationNote))
                        "inspect_chats" in names && inspectInput in last.optString("content") -> tool("inspect_chats")
                        "delete_ensemble_chat" in names && archiveInput in prompt -> {
                            if (last.getString("role") == "tool") null
                            else JSONObject().put("name", "delete_ensemble_chat")
                                .put("arguments", JSONObject().put("chat", checkNotNull(labScene.get())).toString())
                                .toString()
                        }
                        "confirm_chat_agent_stopped" in names -> {
                            assertTrue("Pending replies: []" in prompt)
                            confirmations.incrementAndGet()
                            tool("confirm_chat_agent_stopped")
                        }
                        "confirm_chat_agent_enabled" in names -> {
                            assertTrue("no queued replies" in prompt)
                            assertTrue(activationNote !in prompt)
                            confirmations.incrementAndGet()
                            tool("confirm_chat_agent_enabled")
                        }
                        resumedInput in prompt -> {
                            if (last.getString("role") == "tool" || "Your character is Bryn" !in prompt) null
                            else {
                                assertTrue(activationNote in prompt)
                                val conversation = checkNotNull(scene.findAll(prompt).lastOrNull())
                                post(conversation.groupValues[2], "Bryn confirmed the new shift.")
                            }
                        }
                        "my_chats" in names && "This is your room opening." in prompt &&
                            !scene.containsMatchIn(prompt) && !direct.containsMatchIn(prompt) -> {
                            val name = if (prompt.contains("Your character is Ada")) "Ada" else "Bryn"
                            val desired = if (name == "Ada") "Bridge" else "Lab"
                            val result = last.optString("content")
                            when {
                                last.getString("role") != "tool" -> {
                                    openingCount.incrementAndGet()
                                    """{"name":"my_chats","arguments":"{}"}"""
                                }
                                result.startsWith("[") -> {
                                    val chats = JSONArray(result)
                                    val chat = (0 until chats.length()).map(chats::getJSONObject)
                                        .single { it.getString("name") == desired }
                                    if (desired == "Lab") {
                                        labScene.set(chat.getString("id"))
                                        assertTrue(labOpening.await(15, TimeUnit.SECONDS))
                                    }
                                    post(chat.getString("id"), "$name opened the $desired.")
                                }
                                else -> null
                            }
                        }
                        "room_ready" !in names && last.getString("role") == "tool" -> null
                        privateInput in prompt -> {
                            val notice = (0 until messages.length()).map { messages.getJSONObject(it) }
                                .single { privateInput in it.optString("content") }
                            assertEquals("system", notice.getString("role"))
                            assertTrue(notice.getString("content").contains("\"role\":\"system\""))
                            assertTrue(publicInput !in prompt)
                            val conversation = checkNotNull(direct.findAll(prompt).lastOrNull())
                            post(conversation.groupValues[1], "Private confirmation.")
                        }
                        publicInput in prompt -> {
                            assertTrue(publicReply.await(15, TimeUnit.SECONDS))
                            reasoning = traceReason
                            val conversation = checkNotNull(scene.findAll(prompt).lastOrNull())
                            post(conversation.groupValues[2], "Public confirmation.")
                        }
                        prompt.contains("Your character is Bryn") -> {
                            assertEquals(setOf("room_progress", "room_ready"), names)
                            brynStarted.countDown()
                            assertTrue(brynReady.await(15, TimeUnit.SECONDS))
                            ready
                        }
                        prompt.contains("Your character is Ada") && last.getString("role") == "tool" -> {
                            assertTrue(adaReady.await(15, TimeUnit.SECONDS))
                            ready
                        }
                        prompt.contains("Your character is Ada") -> {
                            assertTrue(brynStarted.await(10, TimeUnit.SECONDS))
                            val range = (0 until tools.length()).map { tools.getJSONObject(it).getJSONObject("function") }
                                .single { it.getString("name") == "room_progress" }
                                .getJSONObject("parameters").getJSONObject("properties")
                            val field = if (usePluginProgress) "progress" else "percent"
                            assertEquals(if (usePluginProgress) 7 else 100, range.getJSONObject(field).getInt("maximum"))
                            JSONObject().put("name", "room_progress")
                                .put("arguments", JSONObject().put(field, if (usePluginProgress) 3 else 50).toString()).toString()
                        }
                        else -> error("Unexpected room preparation request")
                    }
                    val delta = when {
                        call != null -> {
                            val function = JSONObject(call)
                            JSONObject().put("reasoning_content", reasoning).put("tool_calls", JSONArray().put(
                                JSONObject().put("index", 0).put("id", "${function.getString("name")}:$order")
                                    .put("type", "function").put("function", function)
                            )).toString()
                        }
                        names.isEmpty() -> """{"content":"Station watch"}"""
                        else -> "{}"
                    }
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body("""data: {"choices":[{"delta":$delta}]}""" + "\n\ndata: [DONE]\n\n").build()
                } catch (error: Throwable) {
                    failure.compareAndSet(null, error)
                    MockResponse.Builder().code(500).build()
                }
            }
            server.start()
            try {
                val preferences = Preferences(PreferenceDataStoreFactory.create(scope = scope) {
                    directory.resolve("room-loading.preferences_pb")
                })
                runBlocking {
                    preferences.save(
                        JSONArray().put(JSONObject().put("id", "fixture").put("model", "fixture")
                            .put("protocol", "openai").put("url", server.url("/v1").toString())),
                        JSONObject().put("preset", preset).put("preset_home_hint_seen", true),
                    )
                }
                val model = compose.runOnIdle {
                    ViewModelProvider.create(compose.activity, viewModelFactory {
                        initializer { ChatViewModel(application, workspace, preferences) }
                    })[ChatViewModel::class.java]
                }
                compose.setContent { MiniRP(model) }
                fun waitFor(condition: () -> Boolean) = compose.waitUntil(12_000) {
                    failure.get()?.let { throw AssertionError("Room model fixture failed", it) }
                    condition()
                }
                val newRoom = compose.onNodeWithText(compose.activity.getString(R.string.new_room))
                waitFor { model.state.value.preset == preset && newRoom.isDisplayed() }
                newRoom.performClick()
                waitFor {
                    model.state.value.roomLoading?.let {
                        it.declaredProgress == usePluginProgress && it.progress == (if (usePluginProgress) 3f / 17f else 0.125f)
                    } == true
                }
                val bridge = compose.onNodeWithText("Bridge")
                bridge.assertDoesNotExist()
                compose.onNodeWithText("0/2").assertDoesNotExist()
                waitFor { compose.onNodeWithContentDescription(second).isDisplayed() }
                adaReady.countDown()
                waitFor { model.state.value.roomLoading?.ready == 1 }
                bridge.assertDoesNotExist()
                compose.onNodeWithText("1/2").assertDoesNotExist()
                brynReady.countDown()
                waitFor {
                    model.state.value.roomLoading?.progress == (if (usePluginProgress) 1f else 0.75f) && openingCount.get() == 2
                }
                bridge.assertDoesNotExist()
                labOpening.countDown()
                waitFor { model.state.value.roomLoading == null && bridge.isDisplayed() }
                val backdrop = compose.onNodeWithTag("preset-background")
                if (usePluginProgress) waitFor { backdrop.isDisplayed() } else backdrop.assertDoesNotExist()
                compose.onNodeWithText("Lab").performClick()
                waitFor {
                    compose.onNodeWithText("Bryn opened the Lab.").isDisplayed() &&
                        compose.onNodeWithTag("composer").isDisplayed()
                }
                if (usePluginProgress) waitFor { backdrop.isDisplayed() }
                assertEquals(2, openingCount.get())
                val composer = compose.onNodeWithTag("composer")
                val send = compose.onNodeWithTag("send")
                composer.performTextInput(publicInput)
                send.performClick()
                val thinking = compose.onNodeWithText(compose.activity.getString(R.string.ensemble_member_thinking, "Bryn"))
                waitFor { thinking.isDisplayed() }
                compose.onNodeWithText(traceReason).assertDoesNotExist()
                publicReply.countDown()
                waitFor { compose.onNodeWithText("Public confirmation.").isDisplayed() && !thinking.isDisplayed() }
                compose.onNodeWithText(traceReason).assertDoesNotExist()

                val back = compose.onNodeWithContentDescription(compose.activity.getString(R.string.back))
                back.performClick()
                val fab = compose.onNodeWithTag("ensemble-private-fab")
                waitFor { fab.isDisplayed() }
                val location = fab.fetchSemanticsNode().boundsInRoot.center
                val center = compose.onRoot().fetchSemanticsNode().boundsInRoot.center
                val rtl = compose.activity.resources.configuration.layoutDirection == View.LAYOUT_DIRECTION_RTL
                assertTrue(location.y > center.y && ((location.x > center.x) != rtl))
                fab.performClick()
                val warning = compose.onNodeWithText(compose.activity.getString(R.string.ensemble_out_of_story))
                val member = compose.onNodeWithTag("ensemble-private-member:bryn")
                waitFor { warning.isDisplayed() }
                member.performClick()
                waitFor { model.state.value.ensemblePrivateParticipant == "bryn" && composer.isDisplayed() }
                member.assertDoesNotExist()
                warning.assertIsDisplayed()
                compose.onNodeWithContentDescription(compose.activity.getString(R.string.add_attachment)).assertDoesNotExist()
                compose.onNodeWithText("Public confirmation.").assertDoesNotExist()
                composer.performTextInput(privateInput)
                send.performClick()
                val privateReply = compose.onNodeWithText("Private confirmation.")
                waitFor { privateReply.isDisplayed() }

                compose.onNodeWithContentDescription(compose.activity.getString(R.string.ensemble_member_trace)).performClick()
                waitFor { compose.onNodeWithText(traceReason).isDisplayed() }
                back.performClick()
                waitFor { privateReply.isDisplayed() && composer.isDisplayed() }
                member.assertDoesNotExist()

                back.performClick()
                waitFor { member.isDisplayed() && model.state.value.ensemblePrivateParticipant.isBlank() }
                member.performClick()
                waitFor { privateReply.isDisplayed() && model.state.value.ensemblePrivateParticipant == "bryn" }
                if (usePluginProgress) {
                    val input = DirectNavigationEventInput()
                    compose.runOnIdle { compose.activity.navigationEventDispatcher.addInput(input) }
                    fun startBackGesture() = compose.runOnIdle {
                        input.backStarted(NavigationEvent(swipeEdge = NavigationEvent.EDGE_LEFT))
                        input.backProgressed(NavigationEvent(progress = 0.4f, swipeEdge = NavigationEvent.EDGE_LEFT))
                    }
                    try {
                        startBackGesture()
                        waitFor { bridge.isDisplayed() }
                        member.assertDoesNotExist()
                        compose.runOnIdle { input.backCancelled() }
                        waitFor { privateReply.isDisplayed() && !bridge.isDisplayed() }
                        member.assertDoesNotExist()
                        startBackGesture()
                        waitFor { bridge.isDisplayed() }
                        compose.runOnIdle { input.backCompleted() }
                        waitFor { member.isDisplayed() && model.state.value.ensemblePrivateParticipant.isBlank() }
                    } finally {
                        compose.runOnIdle { compose.activity.navigationEventDispatcher.removeInput(input) }
                    }
                } else {
                    compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
                    waitFor { member.isDisplayed() && model.state.value.ensemblePrivateParticipant.isBlank() }
                }
                compose.onNodeWithContentDescription(compose.activity.getString(R.string.close)).performClick()
                waitFor { !member.isDisplayed() && bridge.isDisplayed() }
                if (usePluginProgress) {
                    val lab = compose.onNodeWithText("Lab")
                    lab.performClick()
                    waitFor { composer.isDisplayed() }
                    assertTrue(runBlocking { model.send(archiveInput, emptyList(), AgentRole.World) })
                    waitFor { !composer.isDisplayed() && bridge.isDisplayed() && !lab.isDisplayed() }
                    compose.onNodeWithContentDescription(compose.activity.getString(R.string.more_options)).performClick()
                    val archiveTitle = compose.onNodeWithText(compose.activity.getString(R.string.ensemble_archived_chats))
                    val archiveSymbol = Paint().apply {
                        typeface = ResourcesCompat.getFont(compose.activity, R.font.material_symbols_rounded)
                    }
                    assertTrue(archiveSymbol.hasGlyph("archive"))
                    archiveTitle.performClick()
                    waitFor { lab.isDisplayed() }
                    lab.performClick()
                    val savedMessage = compose.onNodeWithText("Public confirmation.")
                    waitFor { savedMessage.isDisplayed() }
                    composer.assertDoesNotExist()
                    back.performClick()
                    waitFor { archiveTitle.isDisplayed() && lab.isDisplayed() }
                    lab.performClick()
                    waitFor { savedMessage.isDisplayed() }
                    compose.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
                    waitFor { archiveTitle.isDisplayed() && lab.isDisplayed() }
                    compose.onNodeWithContentDescription(compose.activity.getString(R.string.close)).performClick()
                    waitFor { bridge.isDisplayed() && !lab.isDisplayed() }

                    fun requestLifecycle(input: String, state: String, confirmationCount: Int) {
                        assertTrue(runBlocking { model.send(input, emptyList(), AgentRole.World) })
                        waitFor { confirmations.get() == confirmationCount && !model.state.value.busy }
                        repeat(5) {
                            brynAvailability.set(null)
                            assertTrue(runBlocking { model.send(inspectInput, emptyList(), AgentRole.World) })
                            waitFor { brynAvailability.get() != null && !model.state.value.busy }
                            if (brynAvailability.get() == state) return
                        }
                        assertEquals(state, brynAvailability.get())
                    }
                    requestLifecycle(stopInput, "disabled", 1)
                    requestLifecycle(startInput, "enabled", 2)
                    bridge.performClick()
                    waitFor { composer.isDisplayed() }
                    composer.performTextInput(resumedInput)
                    send.performClick()
                    waitFor { compose.onNodeWithText("Bryn confirmed the new shift.").isDisplayed() }
                }
            } finally {
                adaReady.countDown()
                brynReady.countDown()
                labOpening.countDown()
                publicReply.countDown()
                compose.runOnIdle { compose.activity.viewModelStore.clear() }
                scope.cancel()
            }
        }
    }

    private fun writeProgressPlugin(workspace: File, preset: String) {
        val pixels = Bitmap.createBitmap(32, 32, Bitmap.Config.ARGB_8888)
        pixels.eraseColor(android.graphics.Color.BLUE)
        val background = ByteArrayOutputStream().apply { pixels.compress(Bitmap.CompressFormat.PNG, 100, this) }
        pixels.recycle()
        val files = mapOf(
            "index.js" to "globalThis.handlers = {};".toByteArray(),
            "station.png" to background.toByteArray(),
            "manifest.json" to """
                {"id":"loading-fixture","packageName":"test.loading_fixture","name":"Loading fixture",
                 "apiVersion":2,"entry":"index.js","presetId":"$preset","roomLoading":{"progress":{"ada":7,"bryn":10}},
                 "assets":[{"id":"station","type":"file","path":"station.png","mimeType":"image/png"}]}
            """.trimIndent().toByteArray(),
        )
        writeSignedPlugin(workspace, preset, "loading-fixture", files)
    }
}
