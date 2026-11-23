package com.minirp.app

import android.content.ContextWrapper
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
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
    fun newRoomWaitsForEachCharacterToConfirmReadiness() {
        val application = compose.activity.application
        val directory = temporary.newFolder()
        val workspace = object : ContextWrapper(application) {
            override fun getFilesDir() = directory
        }
        val preset = UUID.randomUUID().toString()
        val first = "Navigation lights drift past the window."
        val second = "The next shift checks the gate."
        directory.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json").writeText("""
            {"name":"Station", "chat_mode":"ensemble", "ensemble":{
             "agents":[{"id":"ada", "name":"Ada"},{"id":"bryn", "name":"Bryn"}],
             "chats":[{"name":"Bridge", "members":["ada","bryn"]}],
             "loading_messages":["$first","$second"]}}
        """.trimIndent())
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val requests = AtomicInteger()
        val failure = AtomicReference<Throwable>()
        val adaReady = CountDownLatch(1)
        val brynReady = CountDownLatch(1)
        val brynStarted = CountDownLatch(1)
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse = try {
                    val body = JSONObject(checkNotNull(request.body).utf8())
                    val order = requests.incrementAndGet()
                    val tools = body.getJSONArray("tools")
                    assertEquals(
                        setOf("room_progress", "room_ready"),
                        (0 until tools.length()).map { tools.getJSONObject(it).getJSONObject("function").getString("name") }.toSet(),
                    )
                    val messages = body.getJSONArray("messages")
                    val prompt = messages.toString()
                    val ready = """{"name":"room_ready","arguments":"{}"}"""
                    val call = when {
                        prompt.contains("Your character is Bryn") -> {
                            brynStarted.countDown()
                            assertTrue(brynReady.await(15, TimeUnit.SECONDS))
                            ready
                        }
                        prompt.contains("Your character is Ada") && messages.getJSONObject(messages.length() - 1).getString("role") == "tool" -> {
                            assertTrue(adaReady.await(15, TimeUnit.SECONDS))
                            ready
                        }
                        prompt.contains("Your character is Ada") -> {
                            assertTrue(brynStarted.await(10, TimeUnit.SECONDS))
                            """{"name":"room_progress","arguments":"{\"percent\":50}"}"""
                        }
                        else -> error("Unexpected room preparation request")
                    }
                    MockResponse.Builder().setHeader("Content-Type", "text/event-stream").body(
                        """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"tool-$order","type":"function","function":$call}]}}]}""" +
                            "\n\ndata: [DONE]\n\n"
                    ).build()
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
                waitFor { model.state.value.roomLoading?.progress == 0.25f }
                val bridge = compose.onNodeWithText("Bridge")
                bridge.assertDoesNotExist()
                compose.onNodeWithText("0/2").assertDoesNotExist()
                waitFor { compose.onNodeWithContentDescription(second).isDisplayed() }
                adaReady.countDown()
                waitFor { model.state.value.roomLoading?.ready == 1 }
                bridge.assertDoesNotExist()
                compose.onNodeWithText("1/2").assertDoesNotExist()
                brynReady.countDown()
                waitFor { model.state.value.roomLoading == null && bridge.isDisplayed() }
                assertEquals(3, requests.get())
            } finally {
                adaReady.countDown()
                brynReady.countDown()
                compose.runOnIdle { compose.activity.viewModelStore.clear() }
                scope.cancel()
            }
        }
    }
}
