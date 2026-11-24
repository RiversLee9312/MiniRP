package com.minirp.app

import android.content.ContextWrapper
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
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.minirp.app.data.Preferences
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
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
    fun newRoomWaitsForConcurrentPreparationAndEachSceneOpening() {
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
        val traceReason = "Checking the station readings."
        directory.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json").writeText("""
            {"name":"Station", "chat_mode":"ensemble", "agentic_search":false,
             "chat_agent":{"greeting_system_message":"Open the watch in both scenes."}, "ensemble":{
             "agents":[{"id":"ada", "name":"Ada"},{"id":"bryn", "name":"Bryn"}],
             "chats":[{"name":"Bridge", "members":["ada","bryn"]},{"name":"Lab", "members":["bryn"]}],
             "direct_messages_in_story":false,
             "loading_messages":["$first","$second"]}}
        """.trimIndent())
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val requests = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        val adaReady = CountDownLatch(1)
        val brynReady = CountDownLatch(1)
        val brynStarted = CountDownLatch(1)
        val labOpening = CountDownLatch(1)
        val publicReply = CountDownLatch(1)
        val openingCount = AtomicInteger()
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
                    val scene = Regex("The active chat is (Bridge|Lab) \\(([0-9a-f-]+)\\)")
                    val direct = Regex("only permitted recipient is this private chat \\(([0-9a-f-]+)\\)")
                    fun post(chat: String, text: String): String = JSONObject()
                        .put("name", "chat_send").put("arguments", JSONObject()
                            .put("chat", chat).put("text", text).toString()).toString()
                    var reasoning = ""
                    val call = when {
                        names.isEmpty() -> null // Optional room title generation.
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
                        "room_ready" !in names -> {
                            assertTrue("chat_send" in names)
                            assertTrue(prompt.contains("Give your opening in this chat"))
                            val chat = checkNotNull(scene.findAll(prompt).lastOrNull())
                            val name = if (prompt.contains("Your character is Ada")) "Ada" else "Bryn"
                            openingCount.incrementAndGet()
                            if (chat.groupValues[1] == "Lab") assertTrue(labOpening.await(15, TimeUnit.SECONDS))
                            post(chat.groupValues[2], "$name opened the ${chat.groupValues[1]}.")
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
                            """{"name":"room_progress","arguments":"{\"percent\":50}"}"""
                        }
                        else -> error("Unexpected room preparation request")
                    }
                    val delta = when {
                        call != null -> JSONObject().put("reasoning_content", reasoning).put("tool_calls", JSONArray().put(
                            JSONObject().put("index", 0).put("id", "tool-$order").put("type", "function").put("function", JSONObject(call))
                        )).toString()
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
                waitFor { model.state.value.roomLoading?.progress == 0.1f }
                val bridge = compose.onNodeWithText("Bridge")
                bridge.assertDoesNotExist()
                compose.onNodeWithText("0/2").assertDoesNotExist()
                waitFor { compose.onNodeWithContentDescription(second).isDisplayed() }
                adaReady.countDown()
                waitFor { model.state.value.roomLoading?.ready == 1 }
                bridge.assertDoesNotExist()
                compose.onNodeWithText("1/2").assertDoesNotExist()
                brynReady.countDown()
                waitFor { model.state.value.roomLoading?.progress == 0.8f && openingCount.get() == 3 }
                bridge.assertDoesNotExist()
                labOpening.countDown()
                waitFor { model.state.value.roomLoading == null && bridge.isDisplayed() }
                compose.onNodeWithText("Lab").performClick()
                waitFor {
                    compose.onNodeWithText("Bryn opened the Lab.").isDisplayed() &&
                        compose.onNodeWithTag("composer").isDisplayed()
                }
                assertEquals(3, openingCount.get())
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

                compose.onNodeWithContentDescription(compose.activity.getString(R.string.back)).performClick()
                val fab = compose.onNodeWithTag("ensemble-private-fab")
                waitFor { fab.isDisplayed() }
                val location = fab.fetchSemanticsNode().boundsInRoot.center
                val center = compose.onRoot().fetchSemanticsNode().boundsInRoot.center
                val rtl = compose.activity.resources.configuration.layoutDirection == View.LAYOUT_DIRECTION_RTL
                assertTrue(location.y > center.y && ((location.x > center.x) != rtl))
                fab.performClick()
                val warning = compose.onNodeWithText(compose.activity.getString(R.string.ensemble_out_of_story))
                waitFor { warning.isDisplayed() }
                compose.onNodeWithTag("ensemble-private-member:bryn").performClick()
                waitFor { model.state.value.ensemblePrivateParticipant == "bryn" && composer.isDisplayed() }
                warning.assertIsDisplayed()
                compose.onNodeWithContentDescription(compose.activity.getString(R.string.add_attachment)).assertDoesNotExist()
                compose.onNodeWithText("Public confirmation.").assertDoesNotExist()
                composer.performTextInput(privateInput)
                send.performClick()
                waitFor { compose.onNodeWithText("Private confirmation.").isDisplayed() }

                compose.onNodeWithContentDescription(compose.activity.getString(R.string.ensemble_member_trace)).performClick()
                waitFor { compose.onNodeWithText(traceReason).isDisplayed() }
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
}
