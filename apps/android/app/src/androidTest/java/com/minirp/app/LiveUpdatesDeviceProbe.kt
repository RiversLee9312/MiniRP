package com.minirp.app

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.content.Intent
import android.graphics.Bitmap
import android.os.Build
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import androidx.annotation.RequiresApi
import androidx.core.app.NotificationCompat
import androidx.lifecycle.ViewModelProvider
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.data.Preferences
import com.minirp.app.core.TurnProtection
import com.minirp.app.ui.AgentRole
import com.minirp.app.ui.ChatViewModel
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test

/** Uses public instrumentation APIs on devices where Espresso cannot initialize. */
class LiveUpdatesDeviceProbe {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val automation get() = instrumentation.uiAutomation
    private val app get() = ApplicationProvider.getApplicationContext<MiniRPApplication>()
    private val notifications get() = app.getSystemService(NotificationManager::class.java)

    @Before
    fun requireExplicitProbe() {
        assumeTrue(Build.VERSION.SDK_INT >= 36)
        assumeTrue(InstrumentationRegistry.getArguments().getString("inspectLiveUpdates") == "true")
    }

    @Test
    @RequiresApi(36)
    fun observeBackgroundTurn() {
        check(BuildConfig.APPLICATION_ID.endsWith(".qa")) { "Requires a clean-start test installation." }
        val preferences = Preferences(app)
        val firstUse = !runBlocking { preferences.loadTurnProtection().asked }
        val started = CountDownLatch(1)
        val release = CountDownLatch(1)
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (request.body?.utf8()?.contains(PROMPT) == true) {
                        started.countDown()
                        release.await(30, TimeUnit.SECONDS)
                    }
                    val delta = JSONObject().put("content", ANSWER)
                    val data = JSONObject().put("choices", JSONArray().put(JSONObject().put("delta", delta)))
                    return MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                        .body("data: $data\n\ndata: [DONE]\n\n").build()
                }
            }
            server.start()
            val preset = UUID.randomUUID().toString()
            app.filesDir.resolve("workspace/presets").apply { mkdirs() }.resolve("$preset.json")
                .writeText("""{"name":"Live Update fixture"}""")
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
                val activity = instrumentation.startActivitySync(
                    Intent(Intent.ACTION_MAIN)
                        .addCategory(Intent.CATEGORY_LAUNCHER)
                        .setClassName(app.packageName, "com.minichat.app.MainActivity")
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                ) as MainActivity
                try {
                    lateinit var model: ChatViewModel
                    instrumentation.runOnMainSync {
                        model = ViewModelProvider.create(
                            app.chatModels, activity.defaultViewModelProviderFactory, activity.defaultViewModelCreationExtras,
                        )[ChatViewModel::class.java]
                    }
                    await("turn settings and preset ready") {
                        (!firstUse || app.turnProtection.state.value.prompt == TurnProtection.Prompt.FirstUse) &&
                            model.state.value.preset == preset && model.state.value.ready && !model.state.value.busy
                    }
                    if (!notifications.areNotificationsEnabled()) {
                        instrumentation.runOnMainSync {
                            activity.requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 4827)
                        }
                    }
                    await("isolated test notification permission") { notifications.areNotificationsEnabled() }
                    assertTrue(runBlocking {
                        withContext(Dispatchers.Main) {
                            app.turnProtection.authorize(true)
                            model.send(PROMPT, emptyList(), AgentRole.Chat)
                        }
                    })
                    assertTrue("Mock model should receive the turn", started.await(10, TimeUnit.SECONDS))
                    fun posted() = notifications.activeNotifications
                        .firstOrNull { it.notification.channelId == "background_turns" }?.notification
                    await("foreground notification") { posted() != null }
                    report("foregroundTurn", checkNotNull(posted()))
                    shell("input keyevent KEYCODE_HOME")
                    SystemClock.sleep(4_000)
                    report("backgroundTurn", checkNotNull(posted()))
                    captureStatusBar("background-turn")
                    release.countDown()
                    await("reply completion and notification removal") {
                        posted() == null && model.state.value.blocks.any { it.content == ANSWER }
                    }
                } finally {
                    instrumentation.runOnMainSync { activity.finish() }
                }
            } finally {
                release.countDown()
            }
        }
    }

    @RequiresApi(36)
    private fun report(sample: String, notification: Notification) {
        instrumentation.sendStatus(0, Bundle().apply {
            putString("sample", sample)
            putBoolean("liveUpdatesAllowed", notifications.canPostPromotedNotifications())
            putBoolean("liveUpdateRequested", NotificationCompat.isRequestPromotedOngoing(notification))
            putBoolean("liveUpdateEligible", notification.hasPromotableCharacteristics())
            putBoolean("liveUpdatePromoted", notification.flags and Notification.FLAG_PROMOTED_ONGOING != 0)
        })
    }

    private fun captureStatusBar(sample: String) {
        val screenshot = automation.takeScreenshot() ?: return
        try {
            val height = minOf(screenshot.height, (app.resources.displayMetrics.density * 54).toInt())
            val top = Bitmap.createBitmap(screenshot, 0, 0, screenshot.width, height)
            try {
                app.cacheDir.resolve("live-update-$sample.png").outputStream().use {
                    top.compress(Bitmap.CompressFormat.PNG, 100, it)
                }
            } finally { if (top !== screenshot) top.recycle() }
        } finally { screenshot.recycle() }
    }

    private fun await(description: String, condition: () -> Boolean) {
        val until = SystemClock.elapsedRealtime() + 12_000
        while (!condition()) {
            check(SystemClock.elapsedRealtime() < until) { "Timed out waiting for $description." }
            SystemClock.sleep(100)
        }
    }

    private fun shell(command: String) {
        ParcelFileDescriptor.AutoCloseInputStream(automation.executeShellCommand(command)).use { it.readBytes() }
    }

    private companion object {
        const val PROMPT = "Continue the fixture after Home."
        const val ANSWER = "Background fixture completed."
    }
}
