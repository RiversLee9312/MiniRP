package com.minirp.app

import android.accessibilityservice.AccessibilityServiceInfo
import android.app.NotificationManager
import android.os.Build
import android.os.ParcelFileDescriptor
import android.view.accessibility.AccessibilityNodeInfo
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.core.app.NotificationCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.data.Preferences
import com.minirp.app.ui.ChatViewModel
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.runBlocking
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test

class BackgroundTurnsTest {
    @get:Rule val compose = createEmptyComposeRule()

    @Test
    fun cleanStartAsksForConsentAndOnlyUnexpectedInterruptionAsksAgain() {
        assumeTrue(BuildConfig.APPLICATION_ID.endsWith(".qa"))
        val app = ApplicationProvider.getApplicationContext<MiniRPApplication>()
        val preferences = Preferences(app)
        assumeTrue("Requires a clean test installation", !runBlocking { preferences.loadTurnProtection().asked })
        val notificationManager = app.getSystemService(NotificationManager::class.java)
        fun notification() =
            notificationManager.activeNotifications.firstOrNull { it.notification.channelId == "background_turns" }
                ?.notification
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        automation.serviceInfo = automation.serviceInfo.apply {
            flags = flags or AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS
        }
        fun shell(command: String) {
            ParcelFileDescriptor.AutoCloseInputStream(automation.executeShellCommand(command)).use { output ->
                output.readBytes()
            }
        }
        val cancellableStarted = CountDownLatch(1)
        val releaseCancelled = CountDownLatch(1)
        val backgroundStarted = CountDownLatch(1)
        val releaseBackground = CountDownLatch(1)
        val cancelled = "Wait until I stop"
        val failing = "Trigger fixture failure"
        val background = "Reply while MiniRP is in the background"
        val answer = "Finished in the background."
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    val input = request.body?.utf8().orEmpty()
                    if (input.contains(cancelled)) {
                        cancellableStarted.countDown()
                        releaseCancelled.await(30, TimeUnit.SECONDS)
                    }
                    if (input.contains(failing)) return MockResponse.Builder().code(400).body("Fixture failure").build()
                    if (input.contains(background)) {
                        backgroundStarted.countDown()
                        releaseBackground.await(30, TimeUnit.SECONDS)
                    }
                    val delta = JSONObject().put("content", if (input.contains(background)) answer else "Fixture")
                    val data = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                    return MockResponse.Builder()
                        .setHeader("Content-Type", "text/event-stream")
                        .body("data: $data\n\ndata: [DONE]\n\n")
                        .build()
                }
            }
            server.start()
            val preset = UUID.randomUUID().toString()
            app.filesDir.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json")
                .writeText("""{"name":"Background reply fixture"}""")
            runBlocking {
                preferences.save(
                    JSONArray().put(
                        JSONObject().put("id", "fixture").put("name", "Fixture").put("protocol", "openai")
                            .put("model", "fixture").put("url", server.url("/v1").toString())
                    ),
                    JSONObject().put("preset", preset).put("onboarding_complete", true),
                )
            }
            try {
                ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                    lateinit var model: ChatViewModel
                    lateinit var activity: MainActivity
                    scenario.onActivity {
                        activity = it
                        model = ViewModelProvider.create(
                            app.chatModels, it.defaultViewModelProviderFactory, it.defaultViewModelCreationExtras,
                        )[ChatViewModel::class.java]
                    }
                    fun label(id: Int) = activity.getString(id)
                    fun send(text: String) {
                        compose.onNodeWithTag("composer").performTextInput(text)
                        compose.onNodeWithContentDescription(label(R.string.send)).performClick()
                    }
                    val prompt = compose.onNodeWithText(label(R.string.background_turns_prompt_title))
                    compose.waitUntil(15_000) { prompt.isDisplayed() }
                    assertFalse(app.turnProtection.state.value.enabled)
                    compose.onNodeWithText(label(R.string.background_turns_later)).performClick()
                    compose.waitUntil(10_000) { !prompt.isDisplayed() && model.state.value.preset == preset }

                    send(cancelled)
                    compose.waitUntil(10_000) {
                        model.state.value.replyError?.let { error(it.message) }
                        cancellableStarted.count == 0L
                    }
                    assertTrue(notification() == null)
                    compose.onNodeWithContentDescription(label(R.string.stop)).performClick()
                    compose.waitUntil(10_000) { model.state.value.generating == null && !model.state.value.busy }
                    releaseCancelled.countDown()
                    prompt.assertDoesNotExist()

                    send(failing)
                    val interrupted = compose.onNodeWithText(label(R.string.background_turns_interrupted))
                    compose.waitUntil(10_000) { interrupted.isDisplayed() }
                    compose.onNodeWithText(label(R.string.background_turns_enable)).performClick()
                    if (Build.VERSION.SDK_INT >= 33) {
                        fun allowNotifications(): AccessibilityNodeInfo? {
                            val root = automation.rootInActiveWindow ?: return null
                            return root.findAccessibilityNodeInfosByViewId(
                                "${root.packageName}:id/permission_allow_button"
                            ).firstOrNull()
                        }
                        compose.waitUntil(10_000) { allowNotifications() != null }
                        assertFalse(app.turnProtection.state.value.enabled)
                        assertTrue(checkNotNull(allowNotifications()).performAction(AccessibilityNodeInfo.ACTION_CLICK))
                    }
                    compose.waitUntil(10_000) { app.turnProtection.state.value.enabled && !model.state.value.busy }
                    assertTrue(notification() == null)

                    send(background)
                    compose.waitUntil(10_000) { backgroundStarted.count == 0L }
                    compose.waitUntil(10_000) { notification() != null }
                    if (Build.VERSION.SDK_INT >= 36)
                        assertTrue(NotificationCompat.isRequestPromotedOngoing(checkNotNull(notification())))
                    shell("input keyevent KEYCODE_HOME")
                    automation.waitForIdle(500, 5_000)
                    assertTrue(notification() != null)
                    releaseBackground.countDown()
                    compose.waitUntil(10_000) {
                        notification() == null && model.state.value.blocks.any { it.content == answer }
                    }
                    shell("am start --user 0 -n ${app.packageName}/com.minichat.app.MainActivity")
                    compose.waitUntil(10_000) { compose.onNodeWithText(answer, substring = true).isDisplayed() }
                    prompt.assertDoesNotExist()

                    compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
                    val behavior = compose.onNodeWithText(label(R.string.behavior_settings))
                    compose.waitUntil(10_000) { behavior.isDisplayed() }
                    behavior.performClick()
                    compose.onNodeWithText(label(R.string.background_turns)).performClick()
                    fun opensSystemSettings(id: Int) {
                        val row = compose.onNodeWithText(label(id))
                        compose.waitUntil(10_000) { row.isDisplayed() }
                        row.performClick()
                        compose.waitUntil(10_000) {
                            val activePackage = automation.rootInActiveWindow?.packageName?.toString()
                            activePackage != null && activePackage != app.packageName &&
                                scenario.state != Lifecycle.State.RESUMED
                        }
                        shell("input keyevent KEYCODE_BACK")
                        compose.waitUntil(10_000) { row.isDisplayed() }
                    }
                    opensSystemSettings(R.string.background_turn_battery)
                    if (Build.VERSION.SDK_INT >= 36)
                        opensSystemSettings(R.string.background_turn_live_updates)
                }
            } finally {
                releaseCancelled.countDown()
                releaseBackground.countDown()
            }
        }
    }
}
