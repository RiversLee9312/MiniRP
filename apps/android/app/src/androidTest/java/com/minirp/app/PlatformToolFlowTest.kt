package com.minirp.app

import android.content.ContextWrapper
import android.app.Activity
import android.content.Intent
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.pdf.PdfDocument
import android.net.Uri
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.view.inspector.WindowInspector
import android.webkit.WebView
import androidx.activity.ComponentActivity
import androidx.activity.compose.LocalActivityResultRegistryOwner
import androidx.activity.result.ActivityResultRegistry
import androidx.activity.result.ActivityResultRegistryOwner
import androidx.activity.result.contract.ActivityResultContract
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performImeAction
import androidx.compose.ui.test.performScrollToKey
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.printToString
import androidx.compose.ui.test.swipeDown
import androidx.core.app.ActivityOptionsCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.data.Preferences
import com.minirp.app.core.CoreClient
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP
import java.io.File
import java.security.MessageDigest
import java.util.Base64
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.ZipFile
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
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PlatformToolFlowTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun newPresetDelegatesFirstSigningDebuggingAndPublishingThenRollsDiceWithPlugin() = checkPluginFlow(steer = false)

    @Test
    fun pluginResultSteersPresetAfterTheFinalWait() = checkPluginFlow(steer = true)

    private fun checkPluginFlow(steer: Boolean) {
        val app = compose.activity.application
        val directory = temporary.newFolder()
        val context = object : ContextWrapper(app) {
            override fun getFilesDir(): File = directory
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val fixture = ToolModel(directory.resolve("workspace"), steer)
        MockWebServer().use { server ->
            server.dispatcher = fixture
            server.start()
            try {
                val preferences = Preferences(
                    PreferenceDataStoreFactory.create(scope = scope) {
                        directory.resolve("flow.preferences_pb")
                    }
                )
                runBlocking {
                    preferences.save(
                        JSONArray().put(json(
                            "id" to "fixture",
                            "name" to "Fixture",
                            "protocol" to "openai",
                            "model" to "fixture",
                            "url" to server.url("/v1").toString(),
                        )),
                        json("models" to JSONArray()
                            .put(json("provider" to "fixture", "id" to "fixture", "name" to "Parent model",
                                "input_modalities" to JSONArray().put("text").put("image/jpeg")))
                            .put(json("provider" to "fixture", "id" to "plugin-fixture", "name" to "Worker model",
                                "input_modalities" to JSONArray().put("text").put("image/png")))),
                    )
                }
                val model = compose.runOnIdle {
                    ViewModelProvider.create(
                        compose.activity,
                        viewModelFactory { initializer { ChatViewModel(app, context, preferences) } },
                    )[ChatViewModel::class.java]
                }
                val document = AtomicReference<File>()
                val registry = object : ActivityResultRegistry(), ActivityResultRegistryOwner {
                    override val activityResultRegistry: ActivityResultRegistry get() = this

                    override fun <I, O> onLaunch(
                        requestCode: Int, contract: ActivityResultContract<I, O>, input: I,
                        options: ActivityOptionsCompat?,
                    ) {
                        val selected = checkNotNull(document.getAndSet(null))
                        val chooser = contract.createIntent(compose.activity, input)
                        if (chooser.action == Intent.ACTION_CREATE_DOCUMENT) {
                            assertEquals(selected.extension, chooser.getStringExtra(Intent.EXTRA_TITLE)?.substringAfterLast('.'))
                            if (selected.extension == "jsonl") assertEquals("application/x-ndjson", chooser.type)
                        } else {
                            assertEquals(Intent.ACTION_OPEN_DOCUMENT, chooser.action)
                        }
                        dispatchResult(requestCode, Activity.RESULT_OK, Intent().setData(Uri.fromFile(selected)))
                    }
                }
                compose.setContent {
                    CompositionLocalProvider(LocalActivityResultRegistryOwner provides registry) { MiniRP(model) }
                }
                fun label(id: Int) = compose.activity.getString(id)
                fun waitFor(condition: () -> Boolean) {
                    compose.waitUntil(30_000) {
                        fixture.failure.get()?.let { throw AssertionError("Tool flow failed", it) }
                        condition()
                    }
                }
                fun exportTranscript(scope: String, button: SemanticsNodeInteraction): List<JSONObject> {
                    val file = directory.resolve("$scope-transcript.jsonl")
                    document.set(file)
                    button.assertIsEnabled().performClick()
                    waitFor { file.isFile && file.length() > 0 && !model.state.value.archiveBusy }
                    return file.readLines().map(::JSONObject).also {
                        assertEquals(scope, it.first().getString("scope"))
                    }
                }
                fun send(message: String, inPresetEditor: Boolean = false) {
                    val composer = hasTestTag("composer")
                    val field = compose.onNode(
                        if (inPresetEditor) composer and hasAnyAncestor(isDialog()) else composer
                    )
                    field.performTextInput(message)
                    field.performImeAction()
                }

                val create = compose.onNodeWithText(label(R.string.create_preset))
                waitFor { model.state.value.ready && create.isDisplayed() }
                assertEquals("", model.state.value.preset)
                create.performClick()
                waitFor {
                    model.state.value.presetEditorOpen && !model.state.value.busy &&
                        model.state.value.blocks.any { it.content == Interview }
                }
                val savedPreset = directory.resolve("workspace/presets/${model.state.value.preset}.json")
                assertEquals("real", JSONObject(savedPreset.readText()).getString("date_mode"))
                fun pressedBack() = InstrumentationRegistry.getInstrumentation().sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
                fun sheetIcon(id: Int) = compose.onNode(hasContentDescription(label(id)) and hasAnyAncestor(isDialog()))
                val sheetHandle = compose.onNodeWithTag("sheet-drag-handle", useUnmergedTree = true)
                if (!steer) {
                    sheetIcon(R.string.close).assertDoesNotExist()
                    sheetHandle.assertDoesNotExist()
                    compose.onNode(hasTestTag("transcript") and hasAnyAncestor(isDialog()))
                        .performTouchInput { swipeDown() }
                    assertTrue(model.state.value.presetEditorOpen)
                    pressedBack()
                    waitFor { sheetIcon(R.string.settings).isDisplayed() }
                    assertTrue(model.state.value.presetEditorOpen)
                    sheetIcon(R.string.settings).performClick()
                    val settingsTitle = compose.onNodeWithText(label(R.string.settings))
                    waitFor { settingsTitle.isDisplayed() }
                    assertTrue(model.state.value.presetSetupPending && model.state.value.presetEditorOpen)
                    compose.onNodeWithContentDescription(label(R.string.back)).performClick()
                    waitFor { sheetIcon(R.string.open_menu).isDisplayed() }
                    sheetIcon(R.string.open_menu).performClick()
                    val presetLibrary = compose.onNode(hasTestTag("library-presets") and hasAnyAncestor(isDialog()))
                    waitFor { presetLibrary.isDisplayed() }
                    pressedBack()
                    waitFor { !presetLibrary.isDisplayed() }
                    assertTrue(model.state.value.presetEditorOpen)
                }
                val activity = compose.onNodeWithTag("plugin-agent-activity")
                activity.assertDoesNotExist()
                val pluginModels = compose.onNodeWithTag("agent-model:plugin")
                pluginModels.assertTextContains("Parent model").performClick()
                compose.onNodeWithText("Worker model").performClick()
                waitFor { pluginModels.isDisplayed() }
                pluginModels.assertTextContains("Worker model")
                send(SetupRequest, inPresetEditor = true)
                waitFor { activity.isDisplayed() }
                activity.assertTextContains(label(R.string.running)).performClick()
                val running = model.state.value.blocks.single { it.pluginStatus == "running" }
                assertTrue(running.pluginAgentId.isNotBlank())
                val workerRow = compose.onNodeWithTag("plugin-agent:${running.pluginAgentId}")
                waitFor { workerRow.isDisplayed() }
                workerRow.assertTextContains(ProjectName)
                    .assertTextContains(label(R.string.running)).performClick()
                val trace = compose.onNodeWithTag("plugin-agent-trace")
                waitFor { trace.isDisplayed() }
                val exportRun = compose.onNode(hasContentDescription(label(R.string.export_transcript)) and
                    hasAnyAncestor(hasTestTag("plugin-agent-trace")))
                exportRun.assertIsNotEnabled()
                waitFor {
                    if (steer) fixture.parentAnswerStarted.count == 0L && model.state.value.generating?.id == "preset"
                    else !model.state.value.busy && model.state.value.blocks.any { it.content == WaitingForWorker }
                }
                fixture.continueWorker.countDown()
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == SetupReply } &&
                        model.state.value.installedPlugins.any { it.name == ProjectName }
                }
                if (steer) assertFalse(model.state.value.blocks.any { it.content == WaitingForWorker })
                val workerReply = compose.onNodeWithText("Plugin published.")
                val finishedStatus = compose.onNodeWithText(label(R.string.plugin_run_finished))
                try {
                    waitFor { workerReply.isDisplayed() && finishedStatus.isDisplayed() }
                } catch (failure: androidx.compose.ui.test.ComposeTimeoutException) {
                    val latest = compose.onNodeWithContentDescription(label(R.string.latest_message))
                    throw AssertionError(
                        "Worker transcript after completion. Reply visible=${workerReply.isDisplayed()}, " +
                            "status visible=${finishedStatus.isDisplayed()}, return-to-latest visible=${latest.isDisplayed()}",
                        failure,
                    )
                }
                val transcriptRecords = exportTranscript("plugin", exportRun)
                assertEquals(running.pluginRun, transcriptRecords.first().getString("run"))
                assertTrue(transcriptRecords.drop(1).any { it.optString("content") == "Plugin published." })
                assertTrue(transcriptRecords.drop(1).all { it.optString("plugin_run") == running.pluginRun })
                pressedBack()
                try {
                    compose.waitUntil(5_000) { workerRow.isDisplayed() }
                } catch (failure: androidx.compose.ui.test.ComposeTimeoutException) {
                    throw AssertionError(
                        "Back from worker trace. Editor open=${model.state.value.presetEditorOpen}, " +
                            "trace visible=${trace.isDisplayed()}, setup pending=${model.state.value.presetSetupPending}",
                        failure,
                    )
                }
                workerRow.assertTextContains(label(R.string.plugin_run_finished))
                sheetIcon(R.string.back).performClick()
                try {
                    compose.waitUntil(5_000) { activity.isDisplayed() }
                } catch (failure: androidx.compose.ui.test.ComposeTimeoutException) {
                    throw AssertionError(
                        "Back from the worker list. Editor open=${model.state.value.presetEditorOpen}, " +
                            "list visible=${workerRow.isDisplayed()}, close visible=${sheetIcon(R.string.close).isDisplayed()}",
                        failure,
                    )
                }
                val calendar = JSONObject(savedPreset.readText())
                assertEquals("plugin", calendar.getString("date_mode"))
                assertEquals("2032-02-29", calendar.getString("sim_date"))
                val worker = model.state.value.blocks.single { it.pluginStatus == "finished" }
                assertEquals(ProjectName, worker.pluginAgentName)
                assertTrue(model.state.value.blocks.any { it.pluginRun == worker.pluginRun && it.content == "Plugin published." })
                waitFor { sheetIcon(R.string.close).isDisplayed() }
                sheetHandle.assertIsDisplayed()
                sheetIcon(R.string.open_menu).assertDoesNotExist()
                sheetIcon(R.string.settings).assertDoesNotExist()
                if (!steer) {
                    val card = model.state.value.blocks.single { it.openUiHostAction == "preset-home" }
                    fun findCard(view: View): WebView? = when (view) {
                        is WebView -> view.takeIf { it.tag == card.id }
                        is ViewGroup -> (0 until view.childCount).firstNotNullOfOrNull { findCard(view.getChildAt(it)) }
                        else -> null
                    }
                    fun cardButton(click: Boolean): Boolean {
                        val complete = CountDownLatch(1)
                        val result = AtomicReference<String>()
                        val expected = JSONObject.quote(label(R.string.preset_return_home))
                        compose.activityRule.scenario.onActivity {
                            val view = WindowInspector.getGlobalWindowViews().firstNotNullOfOrNull(::findCard)
                            if (view == null) complete.countDown()
                            else view.evaluateJavascript(
                                "(() => { const b = document.querySelector('button'); " +
                                    "if (b?.textContent.trim() !== $expected) return false; " +
                                    (if (click) "b.click(); " else "") + "return true; })()"
                            ) { value -> result.set(value); complete.countDown() }
                        }
                        return complete.await(3, TimeUnit.SECONDS) && result.get() == "true"
                    }
                    val transcript = compose.onNode(hasTestTag("transcript") and hasAnyAncestor(isDialog()))
                    transcript.performScrollToKey("open-ui:${card.id}")
                    waitFor { cardButton(click = false) }
                    send(PresetLockRequest, inPresetEditor = true)
                    waitFor { fixture.presetLockStarted.count == 0L && model.state.value.generating?.id == "preset" }
                    sheetIcon(R.string.close).assertDoesNotExist()
                    pressedBack()
                    transcript.performScrollToKey("open-ui:${card.id}")
                    waitFor { cardButton(click = true) }
                    compose.waitForIdle()
                    assertTrue(model.state.value.presetEditorOpen)
                    fixture.releasePresetLock.countDown()

                    waitFor { !model.state.value.busy && model.state.value.generating == null &&
                        model.state.value.blocks.any { it.pluginStatus == "running" } }
                    sheetIcon(R.string.close).assertDoesNotExist()
                    sheetHandle.assertDoesNotExist()
                    pressedBack()
                    transcript.performScrollToKey("open-ui:${card.id}")
                    waitFor { cardButton(click = true) }
                    compose.waitForIdle()
                    assertTrue(model.state.value.presetEditorOpen)
                    fixture.releaseWorkerLock.countDown()
                    waitFor { model.state.value.blocks.any { it.content == PresetLockReply } &&
                        !model.state.value.presetExitLocked }
                    transcript.performScrollToKey("open-ui:${card.id}")
                    waitFor { cardButton(click = true) }
                } else sheetIcon(R.string.close).performClick()
                waitFor { !model.state.value.presetEditorOpen }
                if (!steer) {
                    waitFor { compose.onNodeWithText(label(R.string.preset_home_hint)).isDisplayed() }
                    compose.onNode(
                        hasText(model.state.value.presetName) and hasClickAction() and
                            SemanticsMatcher.keyIsDefined(SemanticsProperties.Heading)
                    ).performClick()
                    waitFor { sheetIcon(R.string.close).isDisplayed() }
                    sheetIcon(R.string.close).performClick()
                    waitFor { !model.state.value.presetEditorOpen }
                    assertTrue(model.state.value.presetHomeHintSeen)

                    val finishedPreset = model.state.value.preset
                    compose.onNodeWithContentDescription(label(R.string.open_menu)).performClick()
                    val library = compose.onNodeWithTag("library-presets")
                    waitFor { library.isDisplayed() }
                    library.performClick()
                    val createDraft = compose.onNodeWithContentDescription(label(R.string.create_preset))
                    waitFor { createDraft.isDisplayed() }
                    createDraft.performClick()
                    waitFor {
                        model.state.value.preset != finishedPreset && model.state.value.presetSetupPending &&
                            model.state.value.presetEditorOpen && !model.state.value.busy
                    }
                    sheetIcon(R.string.open_menu).performClick()
                    val previousPreset = compose.onNode(
                        hasText(FinishedPresetName) and hasAnyAncestor(isDialog()) and hasClickAction()
                    )
                    waitFor { previousPreset.isDisplayed() }
                    previousPreset.performClick()
                    waitFor { model.state.value.preset == finishedPreset && !model.state.value.presetEditorOpen }
                }
                send(DiceRequest)
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == DiceReply }
                }
                if (!steer) {
                    waitFor { compose.onNodeWithText(label(R.string.preset_chat_hint)).isDisplayed() }
                    compose.onNodeWithContentDescription(label(R.string.more_options)).performClick()
                    compose.onNodeWithText(label(R.string.preset)).performClick()
                    waitFor { sheetIcon(R.string.close).isDisplayed() }
                    val presetTranscript = exportTranscript("preset", sheetIcon(R.string.export_transcript))
                    assertTrue(presetTranscript.any { it.optString("content") == SetupReply })
                    assertFalse(presetTranscript.any { it.optString("content") == DiceReply })
                    sheetIcon(R.string.close).performClick()
                    waitFor { !model.state.value.presetEditorOpen }
                    assertTrue(model.state.value.presetChatHintSeen)
                    compose.onNodeWithContentDescription(label(R.string.more_options)).performClick()
                    compose.onNodeWithText(label(R.string.world)).performClick()
                    waitFor { sheetIcon(R.string.close).isDisplayed() }
                    send(EnvLockRequest, inPresetEditor = true)
                    waitFor { fixture.envStarted.count == 0L && model.state.value.generating?.id == "env" }
                    sheetIcon(R.string.export_transcript).assertIsNotEnabled()
                    sheetIcon(R.string.close).performClick()
                    waitFor { !sheetIcon(R.string.close).isDisplayed() }
                    assertEquals("env", model.state.value.generating?.id)
                    fixture.releaseEnv.countDown()
                    waitFor { !model.state.value.busy && model.state.value.blocks.any { it.content == EnvLockReply } }
                    compose.onNodeWithContentDescription(label(R.string.more_options)).performClick()
                    compose.onNodeWithText(label(R.string.world)).performClick()
                    waitFor { sheetIcon(R.string.close).isDisplayed() }
                    val worldTranscript = exportTranscript("env", sheetIcon(R.string.export_transcript))
                    assertTrue(worldTranscript.any { it.optString("content") == EnvLockReply })
                    assertFalse(worldTranscript.any { it.optString("content") == DiceReply })
                    sheetIcon(R.string.close).performClick()
                    waitFor { !sheetIcon(R.string.close).isDisplayed() }
                }
                val pdf = directory.resolve("asset-probe.pdf")
                val fixturePdf = PdfDocument()
                try {
                    val page = fixturePdf.startPage(PdfDocument.PageInfo.Builder(100, 200, 1).create())
                    page.canvas.drawColor(Color.RED)
                    fixturePdf.finishPage(page)
                    pdf.outputStream().use(fixturePdf::writeTo)
                } finally { fixturePdf.close() }
                document.set(pdf)
                compose.onNodeWithContentDescription(label(R.string.add_attachment)).performClick()
                compose.onNodeWithText(label(R.string.choose_files)).performClick()
                waitFor { compose.onNodeWithText(pdf.name).isDisplayed() }
                send(AssetRequest)
                waitFor {
                    !model.state.value.busy && model.state.value.blocks.any { it.content == AssetReply }
                }
                compose.onNodeWithContentDescription(label(R.string.more_options)).performClick()
                val chatTranscript = exportTranscript("chat", compose.onNodeWithText(label(R.string.export_transcript)))
                assertTrue(chatTranscript.any { it.optString("content") == AssetReply })
                val attachment = chatTranscript.single { it.has("attachments") }.getJSONArray("attachments").getJSONObject(0)
                assertEquals(pdf.name, attachment.getString("name"))
                assertEquals("application/pdf", attachment.getString("mime"))
                assertEquals(2, attachment.length())
                assertTrue(model.state.value.chatId.isNotBlank())
                val preset = model.state.value.preset
                val room = model.state.value.chatId
                val plugin = model.state.value.installedPlugins.single { it.name == ProjectName }
                val menu = compose.onNodeWithContentDescription(label(R.string.open_menu))
                val presetSelector = compose.onNodeWithTag("library-presets")
                val choosePreset = label(R.string.choose_preset)
                val importMenu = compose.onNodeWithContentDescription(label(R.string.import_menu))
                val importPreset = compose.onNodeWithText(label(R.string.import_preset))
                fun openLibrary() {
                    try {
                        compose.runOnIdle {
                            val window = compose.activity.window
                            WindowCompat.getInsetsController(window, window.decorView)
                                .hide(WindowInsetsCompat.Type.ime())
                        }
                        compose.waitForIdle()
                        if (presetSelector.isDisplayed()) return
                        waitFor { menu.isDisplayed() }
                        menu.performTouchInput { click() }
                        waitFor { presetSelector.isDisplayed() }
                    } catch (error: Throwable) {
                        throw AssertionError(compose.onRoot(useUnmergedTree = true).printToString(), error)
                    }
                }
                fun openPresetList() {
                    openLibrary()
                    if (!importMenu.isDisplayed()) presetSelector.performClick()
                    waitFor { importMenu.isDisplayed() }
                    presetSelector.assertTextContains(choosePreset)
                    importPreset.assertDoesNotExist()
                }
                fun pickFromLibrary(id: String, option: Int, file: File) {
                    val row = compose.onNodeWithTag("library-choice-$id")
                    waitFor { row.isDisplayed() }
                    row.performTouchInput { longClick() }
                    document.set(file)
                    compose.onNodeWithText(label(option)).performClick()
                    waitFor { !model.state.value.archiveBusy && file.isFile && file.length() > 0 }
                }
                val exportedRoom = directory.resolve("portable.minirp-room")
                openLibrary()
                pickFromLibrary(room, R.string.export_chat, exportedRoom)
                ZipFile(exportedRoom).use { assertNotNull(it.getEntry("conversations/$preset/$room.json")) }
                val exported = directory.resolve("portable.minirp-preset")
                openPresetList()
                pickFromLibrary(preset, R.string.export_preset, exported)
                val saved = directory.resolve("portable.minirp-save")
                openPresetList()
                pickFromLibrary(preset, R.string.export_save, saved)
                val overlay = "plugins/writable-assets/${plugin.packageName}.${plugin.signer}/$preset/notes"
                ZipFile(saved).use { archive ->
                    val edited = archive.getInputStream(archive.getEntry(overlay)).reader().use { it.readText() }
                    assertEquals("Intro\n中文 TOKEN-44\nClosing\n", edited.removePrefix("\uFEFF"))
                }
                openPresetList()
                document.set(exported)
                importMenu.performClick()
                waitFor { importPreset.isDisplayed() }
                importPreset.performClick()
                waitFor { !model.state.value.archiveBusy && model.state.value.chatId.isBlank() }
                assertEquals(preset, model.state.value.preset)
                val standalone = directory.resolve("standalone.plugin")
                val fixtureSettings = compose.onNodeWithText(PluginSettingsTitle)
                waitFor {
                    model.state.value.pluginSettings.any { it.plugin == plugin.id && it.key == "fixture-settings" }
                }
                compose.onNodeWithContentDescription(label(R.string.settings)).performClick()
                val plugins = compose.onNodeWithText(label(R.string.plugins))
                waitFor { plugins.isDisplayed() }
                fixtureSettings.assertDoesNotExist()
                plugins.performClick()
                val baseRow = compose.onNodeWithTag("plugin:base")
                waitFor { baseRow.isDisplayed() }
                baseRow.performClick()
                compose.onNodeWithTag("plugin-export:base").assertDoesNotExist()
                compose.onNodeWithTag("plugin-enabled:base").assertDoesNotExist()
                compose.onNodeWithTag("plugin-uninstall:base").assertDoesNotExist()
                val back = compose.onNodeWithContentDescription(label(R.string.back))
                back.performClick()
                val row = compose.onNodeWithTag("plugin:${plugin.id}")
                waitFor { row.isDisplayed() }
                val externalExport = compose.onNodeWithTag("plugin-export:${plugin.id}")
                externalExport.assertDoesNotExist()
                row.performClick()
                val details = compose.onNodeWithTag("plugin-details:${plugin.id}")
                details.performScrollToKey("${plugin.id}/fixture-settings")
                fixtureSettings.assertIsDisplayed().performClick()
                val diagnostic = compose.onNodeWithText(PluginSettingsToggle)
                waitFor { diagnostic.isDisplayed() }
                back.performClick()
                details.performScrollToKey("identity")
                val enabled = compose.onNodeWithTag("plugin-enabled:${plugin.id}")
                waitFor { enabled.isDisplayed() }
                enabled.assertIsOn().performClick()
                waitFor { model.state.value.installedPlugins.any { it.id == plugin.id && !it.enabled } }
                enabled.assertIsOff()
                waitFor { externalExport.isDisplayed() }
                document.set(standalone)
                externalExport.performClick()
                waitFor { !model.state.value.archiveBusy && standalone.isFile && standalone.length() > 0 }
                assertArrayEquals(directory.resolve("workspace/plugins/${plugin.id}.plugin").readBytes(), standalone.readBytes())
                enabled.performClick()
                waitFor { model.state.value.installedPlugins.any { it.id == plugin.id && it.enabled } }
                enabled.assertIsOn()
                val uninstall = compose.onNodeWithTag("plugin-uninstall:${plugin.id}")
                uninstall.performClick()
                compose.onNode(hasText(label(R.string.cancel)) and hasAnyAncestor(isDialog())).performClick()
                uninstall.performClick()
                compose.onNode(hasText(label(R.string.remove_plugin)) and hasAnyAncestor(isDialog())).performClick()
                waitFor { baseRow.isDisplayed() && model.state.value.installedPlugins.none { it.id == plugin.id } }
                row.assertDoesNotExist()
                val restored = CoreClient(object : ContextWrapper(app) {
                    override fun getFilesDir() = directory.resolve("restored").apply { mkdirs() }
                }, scope)
                try {
                    val imported = runBlocking {
                        restored.query(json("op" to "archive_import", "type" to "save", "path" to saved.path))
                    }
                    assertEquals(preset, imported.getString("preset"))
                    assertArrayEquals(standalone.readBytes(),
                        directory.resolve("restored/workspace/plugins/${plugin.id}.plugin").readBytes())
                    assertTrue(directory.resolve("restored/workspace/conversations/$preset/$room.json").isFile)
                    assertTrue(directory.resolve("restored/workspace/$overlay").readText().contains("TOKEN-44"))
                } finally {
                    restored.close()
                }
                assertNull(fixture.failure.get())
            } finally {
                fixture.continueWorker.countDown()
                fixture.releaseParentAnswer.countDown()
                fixture.releasePresetLock.countDown()
                fixture.releaseWorkerLock.countDown()
                fixture.releaseEnv.countDown()
                scope.cancel()
            }
        }
    }

    private class ToolModel(private val workspace: File, private val steer: Boolean) : Dispatcher() {
        val failure = AtomicReference<Throwable>()
        val continueWorker = CountDownLatch(1)
        val parentAnswerStarted = CountDownLatch(1)
        val releaseParentAnswer = CountDownLatch(1)
        val presetLockStarted = CountDownLatch(1)
        val releasePresetLock = CountDownLatch(1)
        val releaseWorkerLock = CountDownLatch(1)
        val envStarted = CountDownLatch(1)
        val releaseEnv = CountDownLatch(1)
        private val presetRound = AtomicInteger()
        private val workerRound = AtomicInteger()
        private val presetLockRound = AtomicInteger()
        private lateinit var workerId: String
        private val diceRound = AtomicInteger()
        private val assetRound = AtomicInteger()
        private var pageSample = ""
        private val callId = AtomicInteger()
        private lateinit var draft: File
        private lateinit var manifest: JSONObject
        private lateinit var manifestUrl: String
        private lateinit var indexUrl: String
        private lateinit var signer: String
        private lateinit var fixtureHandler: String

        override fun dispatch(request: RecordedRequest): MockResponse = try {
            val body = JSONObject(request.body?.utf8().orEmpty())
            val messages = body.getJSONArray("messages")
            val system = messages.getJSONObject(0).getString("content")
            val history = (0 until messages.length()).map(messages::getJSONObject)
            val result = history.lastOrNull { it.optString("role") == "tool" }?.optString("content").orEmpty()
            val userText = history.lastOrNull { it.optString("role") == "user" }?.optString("content")
            val delta = when {
                system.startsWith("You are PluginAgent working on") -> {
                    assertEquals("plugin-fixture", body.getString("model"))
                    if (userText == PluginLockRequest) {
                        assertTrue("The plugin lock probe was never released", releaseWorkerLock.await(30, TimeUnit.SECONDS))
                        json("content" to "Worker lock probe completed.")
                    } else {
                        if (workerRound.get() == 0)
                            assertTrue("The PluginAgent trace did not open", continueWorker.await(30, TimeUnit.SECONDS))
                        workerReply(result, history)
                    }
                }
                system.startsWith("You are EnvAgent") && userText == EnvLockRequest -> {
                    envStarted.countDown()
                    assertTrue("The environment request was never released", releaseEnv.await(30, TimeUnit.SECONDS))
                    json("content" to EnvLockReply)
                }
                system.startsWith("You are PresetAgent") && history.any { it.optString("content") == PresetLockRequest } -> when (presetLockRound.getAndIncrement()) {
                    0 -> {
                        presetLockStarted.countDown()
                        assertTrue("The preset lock probe was never released", releasePresetLock.await(30, TimeUnit.SECONDS))
                        tools(tool("subagent_send", "id" to workerId, "message" to PluginLockRequest))
                    }
                    1 -> json("content" to "The worker lock probe is running.")
                    2 -> json("content" to PresetLockReply)
                    else -> error("Unexpected lock probe request")
                }
                !body.has("tools") -> json("content" to "Tabletop test")
                history.any { it.optString("content").startsWith(AssetRequest) } -> assetReply(result, history)
                history.any { it.optString("content") == DiceRequest } -> {
                    val definitions = body.getJSONArray("tools")
                    val offered = (0 until definitions.length()).map {
                        definitions.getJSONObject(it).getJSONObject("function").getString("name")
                    }
                    assertFalse("The built-in tabletop Skill was offered", system.contains("- tabletop:"))
                    assertFalse("The built-in roll_dice was offered", "roll_dice" in offered)
                    diceReply(result, offered)
                }
                history.any { it.optString("content") == SetupRequest } -> {
                    assertEquals("fixture", body.getString("model"))
                    presetReply(result, history)
                }
                else -> json("content" to Interview)
            }
            val data = json("choices" to JSONArray().put(json("delta" to delta)))
            MockResponse.Builder().setHeader("Content-Type", "text/event-stream")
                .body("data: $data\n\ndata: [DONE]\n\n").build()
        } catch (problem: Throwable) {
            failure.compareAndSet(null, problem)
            MockResponse.Builder().code(500).body("Unexpected tool flow").build()
        }

        private fun presetReply(result: String, history: List<JSONObject>): JSONObject = when (presetRound.getAndIncrement()) {
            0 -> tools(tool("configure_experience", "mode" to "tabletop", "agentic_search" to false))
            1 -> tools(tool("configure_date", "mode" to "plugin", "initial_date" to "2032-02-29"))
            2 -> {
                assertTrue(JSONObject(result).getBoolean("staged"))
                tools(tool("activate_skill", "name" to "subagent"))
            }
            3 -> tools(tool("subagent_spawn", "name" to ProjectName, "message" to "Debug the draft, iterate on its card, then publish it."))
            4 -> {
                workerId = JSONObject(result).getString("id")
                tools(tool("subagent_wait", "ids" to JSONArray().put(workerId), "seconds" to 1))
            }
            5 -> {
                val child = JSONArray(result).getJSONObject(0)
                assertEquals("running", child.getString("status"))
                if (steer) {
                    parentAnswerStarted.countDown()
                    assertTrue("The waiting preset request was never steered",
                        releaseParentAnswer.await(30, TimeUnit.SECONDS))
                }
                json("content" to WaitingForWorker)
            }
            6 -> {
                val notice = history.single { it.optString("content").startsWith("PluginAgent task status update.") }
                assertEquals("system", notice.getString("role"))
                val child = JSONObject(notice.getString("content").substringAfter('\n'))
                assertEquals("finished", child.getString("status"))
                assertEquals("Plugin published.", child.getString("result"))
                releaseParentAnswer.countDown()
                tools(tool("set_value", "path" to "name", "value" to FinishedPresetName), tool("finish_preset_setup"))
            }
            7 -> {
                assertTrue(JSONObject(result).getBoolean("completed"))
                json("content" to SetupReply)
            }
            else -> error("Unexpected PresetAgent round")
        }

        private fun workerReply(result: String, history: List<JSONObject>): JSONObject = when (workerRound.getAndIncrement()) {
            0 -> tools(tool("activate_skill", "name" to "plugin-dev"))
            1 -> tools(tool("plugin_file_list"))
            2 -> {
                val listed = JSONObject(result).getJSONArray("files")
                val urls = (0 until listed.length()).map(listed::getJSONObject).associate {
                    it.getString("path") to it.getString("url")
                }
                manifestUrl = urls.getValue("manifest.json")
                indexUrl = urls.getValue("index.js")
                assertTrue(manifestUrl.startsWith("minirp://assets/minirp.agent/"))
                tools(tool("activate_skill", "name" to "assets"))
            }
            3 -> tools(tool("grep", "url" to manifestUrl, "pattern" to "^", "limit" to 1))
            4 -> {
                val original = JSONObject(result).getJSONArray("lines").getJSONObject(0).getString("text")
                manifest = JSONObject(original)
                fixtureHandler = "minirp://actions/${manifest.getString("packageName")}/hooks/probe"
                manifest.put("actions", json("handlers" to json("probe" to "probe")))
                    .put("permissions", (manifest.optJSONArray("permissions") ?: JSONArray()).put("harness.read"))
                    .put("assets", JSONArray().put(json(
                        "id" to "notes", "type" to "file", "path" to "notes.txt", "mimeType" to "text/plain", "writable" to true,
                    )))
                    .put("skills", JSONArray().put(json(
                        "name" to "session", "description" to "Read session information and use the fixture dice.",
                        "agents" to JSONArray().put("chat"), "tools" to JSONArray().put(json(
                            "name" to "session_info", "description" to "Read model and token usage.",
                            "handler" to fixtureHandler, "parameters" to json("type" to "object"),
                        )).put(json(
                            "name" to "notes", "description" to "Return the text fixture.", "returnType" to "asset-url",
                            "handler" to fixtureHandler, "parameters" to json("type" to "object"),
                        )).put(json(
                            "name" to "roll_dice", "description" to "Roll dice using the plugin's rules.",
                            "handler" to fixtureHandler, "parameters" to json(
                                "type" to "object",
                                "properties" to json("count" to json("type" to "integer"),
                                    "sides" to json("type" to "integer"), "modifier" to json("type" to "integer")),
                                "required" to JSONArray().put("count").put("sides"),
                            ),
                        )),
                    )))
                    .put("stateEntries", JSONArray().put(json(
                        "key" to "gate", "scope" to "room", "description" to "Gate display",
                        "initial" to json("line" to "Gate open.", "steps" to 1),
                    )))
                    .put("settings", JSONArray().put(json(
                        "key" to "fixture-settings", "type" to "page", "title" to PluginSettingsTitle,
                        "items" to JSONArray().put(json(
                            "key" to "fixture-diagnostics", "type" to "boolean",
                            "title" to PluginSettingsToggle, "default" to false,
                        )),
                    )))
                    .put("cards", JSONArray().put(json(
                        "id" to "gate", "title" to "Gate", "state" to "gate",
                        "program" to CardProgram.replace("Title(", "WrongTitle("),
                    )))
                tools(
                    tool("text_edit", "url" to manifestUrl, "old_string" to original, "new_string" to manifest.toString()),
                    tool("text_edit", "url" to indexUrl, "old_string" to "globalThis.handlers = {};",
                        "new_string" to """
                            globalThis.handlers = { probe: async (event, api) => {
                                if (event.tool?.endsWith("_notes")) return api.assets.url("notes");
                                if (event.tool?.endsWith("_roll_dice")) {
                                    const { count, sides, modifier = 0 } = event.arguments;
                                    if (!Number.isInteger(count) || count < 1 || count > 30 ||
                                            !Number.isInteger(sides) || sides < 2 || sides > 1000) {
                                        throw new RangeError("Invalid dice limits.");
                                    }
                                    const draw = new Uint32Array(1);
                                    const limit = 2 ** 32 - (2 ** 32 % sides);
                                    const rolls = Array.from({ length: count }, () => {
                                        do { crypto.getRandomValues(draw); } while (draw[0] >= limit);
                                        return draw[0] % sides + 1;
                                    });
                                    return { rolls, total: rolls.reduce((sum, roll) => sum + roll, modifier) };
                                }
                                if (event.name === "skill.tool") return api.harness.getSessionInfo();
                                if (event.line) {
                                    const state = await api.state.get({ key: "gate" });
                                    await api.state.set({ key: "gate", expected_revision: state.revision,
                                        value: { line: event.line, steps: 1 } });
                                }
                                return { echoed: event.message };
                            } };
                        """.trimIndent()),
                    tool("create_asset", "name" to "notes.txt", "text" to "Intro\n中文 TOKEN-42\nClosing\n"),
                )
            }
            5 -> {
                val notes = JSONObject(result).getString("url")
                tools(tool("plugin_file_add", "path" to "notes.txt", "url" to notes))
            }
            6 -> {
                assertEquals("notes.txt", JSONObject(result).getString("path"))
                draft = workspace.resolve("preset_sessions").listFiles()!!.single()
                    .resolve("plugin_agents").listFiles()!!.single()
                assertFalse(draft.resolve("signer.json").exists())
                tools(tool("plugin_debug", "handler" to fixtureHandler, "event" to json("message" to "debug fixture")))
            }
            7 -> {
                val debug = JSONObject(result)
                assertTrue(result, debug.getBoolean("ok"))
                assertEquals("debug fixture", debug.getJSONObject("result").getString("echoed"))
                val certificate = JSONObject(draft.resolve("signer.json").readText()).getString("certificate")
                signer = MessageDigest.getInstance("SHA-256").digest(Base64.getDecoder().decode(certificate))
                    .joinToString("") { "%02X".format(it) }
                tools(tool("plugin_card_catalog", "query" to "Button(label:", "length" to 800))
            }
            8 -> {
                val catalog = JSONObject(result)
                assertTrue(catalog.getBoolean("available"))
                assertTrue(result, catalog.getString("text").contains("Button(label:"))
                tools(tool("plugin_card_preview", "id" to "gate"))
            }
            9 -> {
                val invalid = JSONObject(result)
                assertTrue(result, invalid.getBoolean("available"))
                assertFalse(result, invalid.getBoolean("ok"))
                val errors = invalid.getJSONArray("diagnostics")
                assertTrue(result, (0 until errors.length()).map(errors::getJSONObject).any {
                    it.getString("code") == "unknown-component" && it.getString("component") == "WrongTitle"
                })
                assertFalse(workspace.resolve("plugins/${manifest.getString("id")}.plugin").exists())
                tools(tool("text_edit", "url" to manifestUrl, "old_string" to "WrongTitle(", "new_string" to "Title("))
            }
            10 -> {
                assertEquals(result, 1, JSONObject(result).getInt("changes"))
                tools(tool("plugin_card_preview",
                    "id" to "gate", "handler" to fixtureHandler, "ensemble" to true, "screenshot" to true,
                    "event" to json("message" to "room fixture", "line" to "Gate closed."), "width" to 320,
                    "steps" to JSONArray().put(json("control" to "Step count", "value" to "5"))
                        .put(json("control" to "Add step")).put(json("control" to "Inspect gate"))
                        .put(json("control" to "Manual"))))
            }
            11 -> {
                val preview = JSONObject(result)
                assertTrue(result, preview.getBoolean("ok"))
                assertEquals("room fixture", preview.getJSONObject("handler_result").getString("echoed"))
                assertTrue(result, preview.getJSONObject("initial").getString("text").contains("Gate closed."))
                val view = preview.getJSONObject("view")
                assertTrue(result, view.getString("text").contains("Steps 6"))
                assertEquals(320, view.getInt("width"))
                val actions = preview.getJSONArray("actions")
                assertEquals(2, actions.length())
                val action = actions.getJSONObject(0)
                assertEquals("reply", action.getString("type"))
                assertEquals("Check the gate", action.getString("text"))
                assertEquals(6, action.getJSONObject("context").getInt("steps"))
                val link = actions.getJSONObject(1)
                assertEquals("link", link.getString("type"))
                assertEquals("https://example.invalid/manual", link.getString("url"))
                val screenshot = preview.getJSONObject("screenshot")
                assertTrue(result, screenshot.getBoolean("captured"))
                val media = history.last().getJSONArray("content")
                val imageUrl = (0 until media.length()).map(media::getJSONObject)
                    .single { it.getString("type") == "image_url" }.getJSONObject("image_url").getString("url")
                assertTrue(imageUrl.startsWith("data:image/png;base64,"))
                val png = Base64.getDecoder().decode(imageUrl.substringAfter(','))
                val image = assertNotNullImage(png)
                try {
                    assertEquals(screenshot.getInt("width"), image.width)
                    assertEquals(screenshot.getInt("height"), image.height)
                    val colors = mutableSetOf<Int>()
                    for (y in 0 until image.height step 4) {
                        for (x in 0 until image.width step 4) colors.add(image.getPixel(x, y))
                    }
                    assertTrue("The renderer returned a blank screenshot.", colors.size > 10)
                } finally { image.recycle() }
                tools(tool("plugin_card_preview", "id" to "gate", "ensemble" to true,
                    "viewport" to json("x" to 0, "y" to 0, "width" to 128, "height" to 80)))
            }
            12 -> {
                val clean = JSONObject(result)
                assertTrue(result, clean.getBoolean("ok"))
                val text = clean.getJSONObject("view").getString("text")
                assertTrue(result, text.contains("Gate open.") && text.contains("Steps 1"))
                assertEquals(0, clean.getJSONArray("actions").length())
                val screenshot = clean.getJSONObject("screenshot")
                assertTrue(result, screenshot.getBoolean("captured") && screenshot.getBoolean("cropped"))
                assertEquals(128, screenshot.getInt("width"))
                assertEquals(80, screenshot.getInt("height"))
                assertTrue(draft.listFiles()!!.none { it.name.startsWith(".build-") })
                tools(tool("plugin_publish"))
            }
            13 -> {
                val published = JSONObject(result)
                assertTrue(result, published.getJSONObject("verification").getBoolean("ok"))
                assertEquals(result, manifest.getString("id"), published.getString("id"))
                assertEquals(signer, published.getString("signer"))
                assertEquals("include", published.getJSONObject("scope").getString("mode"))
                assertEquals(manifest.getString("presetId"), published.getString("presetId"))
                assertEquals(manifest.getString("presetId"),
                    published.getJSONObject("scope").getJSONArray("presets").getString(0))
                assertEquals(signer, JSONObject(draft.resolve("deployment.json").readText()).getString("signer"))
                assertTrue(workspace.resolve("plugins/${manifest.getString("id")}.plugin").isFile)
                json("content" to "Plugin published.")
            }
            else -> error("Unexpected PluginAgent round")
        }

        private fun assertNotNullImage(bytes: ByteArray) =
            checkNotNull(BitmapFactory.decodeByteArray(bytes, 0, bytes.size)) { "The model received an invalid image." }

        private fun assetReply(result: String, history: List<JSONObject>): JSONObject {
            val input = history.single { it.optString("content").startsWith(AssetRequest) }.getString("content")
            val url = checkNotNull(Regex("minirp://assets/minirp.attachments/[a-f0-9-]+").find(input)).value
            return when (assetRound.getAndIncrement()) {
                0 -> tools(tool("activate_skill", "name" to "assets"))
                1 -> {
                    val names = JSONObject(result).getJSONArray("tools")
                    val offered = (0 until names.length()).map(names::getString).toSet()
                    assertTrue(offered.containsAll(setOf("view_url_metadata", "view_pdf_metadata", "view_pdf_page",
                        "view_image_metadata", "view_image", "raw_grep", "watch_asset", "unwatch_asset")))
                    tools(tool("view_url_metadata", "url" to url))
                }
                2 -> {
                    val metadata = JSONObject(result)
                    assertEquals("application/pdf", metadata.getString("mime"))
                    assertEquals("asset-probe.pdf", metadata.getString("name"))
                    tools(tool("view_pdf_metadata", "url" to url))
                }
                3 -> {
                    val metadata = JSONObject(result).getJSONObject("metadata")
                    assertEquals(1, metadata.getInt("page_count"))
                    assertEquals(100, metadata.getInt("first_page_width_points"))
                    tools(tool("view_pdf_page", "url" to url, "page" to 1))
                }
                4 -> {
                    val part = history.last().getJSONArray("content").getJSONObject(1)
                    val data = part.getJSONObject("image_url").getString("url")
                    assertTrue(data.startsWith("data:image/jpeg;base64,"))
                    val bitmap = assertNotNullImage(Base64.getDecoder().decode(data.substringAfter(',')))
                    try {
                        val pixel = bitmap.getPixel(bitmap.width / 2, bitmap.height / 2)
                        assertTrue(Color.red(pixel) >= 250 && Color.green(pixel) <= 5 && Color.blue(pixel) <= 5)
                    }
                    finally { bitmap.recycle() }
                    pageSample = JSONObject(result).getString("sample_url")
                    tools(tool("view_image_metadata", "url" to pageSample))
                }
                5 -> {
                    val metadata = JSONObject(result).getJSONObject("metadata")
                    assertEquals(700, metadata.getInt("width"))
                    assertEquals(1400, metadata.getInt("height"))
                    tools(tool("view_image", "url" to pageSample,
                        "viewport" to json("x" to 50, "y" to 100, "width" to 80, "height" to 120)))
                }
                6 -> {
                    val crop = JSONObject(result)
                    assertEquals(80, crop.getInt("width"))
                    assertEquals(120, crop.getInt("height"))
                    tools(tool("raw_grep", "url" to url, "pattern" to "25 50 44 46 2d", "literal" to true, "limit" to 1))
                }
                7 -> {
                    val row = JSONObject(result).getJSONArray("lines").getJSONObject(0)
                    assertEquals(0, row.getInt("match_offset"))
                    assertTrue(row.getString("text").startsWith("25 50 44 46 2D "))
                    assertTrue(row.getString("text").contains(" | "))
                    json("content" to AssetReply)
                }
                else -> error("Unexpected Asset preview round")
            }
        }

        private fun diceReply(result: String, offered: List<String>): JSONObject = when (diceRound.getAndIncrement()) {
            0 -> tools(tool("activate_skill", "name" to "${manifest.getString("id")}-session"))
            1 -> tools(tool("roll_dice", "count" to 1, "sides" to 6))
            2 -> {
                val failure = JSONObject(result)
                assertTrue(result, failure.has("error"))
                val diagnostic = failure.getJSONObject("diagnostic")
                assertEquals("roll_dice", diagnostic.getString("tool"))
                assertEquals("tool_unavailable", diagnostic.getString("code"))
                val pluginTool = "plugin_${manifest.getString("id")}_roll_dice"
                assertTrue("The plugin roll_dice was not offered", pluginTool in offered)
                tools(tool(pluginTool, "count" to 30, "sides" to 1000, "modifier" to -7))
            }
            3 -> {
                val outcome = JSONObject(result)
                val rolls = outcome.getJSONArray("rolls")
                assertEquals(30, rolls.length())
                val values = (0 until rolls.length()).map(rolls::getInt)
                assertTrue(values.all { it in 1..1000 })
                assertEquals(values.sum() - 7, outcome.getInt("total"))
                tools(tool("plugin_${manifest.getString("id")}_session_info"))
            }
            4 -> {
                val info = JSONObject(result)
                assertEquals("fixture", info.getString("model"))
                assertEquals("fixture", info.getString("provider"))
                assertTrue(info.getInt("usedTokens") > 0)
                assertEquals(info.getInt("contextWindow") - info.getInt("usedTokens"), info.getInt("remainingTokens"))
                tools(tool("plugin_${manifest.getString("id")}_notes"))
            }
            5 -> tools(tool("activate_skill", "name" to "assets"))
            6 -> {
                val url = "minirp://assets/${manifest.getString("packageName")}/notes"
                tools(tool("grep", "url" to url, "pattern" to "[[:upper:]]+-[0-9]+", "context" to 1))
            }
            7 -> {
                val lines = JSONObject(result).getJSONArray("lines")
                assertEquals("Intro", lines.getJSONObject(0).getString("text"))
                assertEquals("中文 TOKEN-42", lines.getJSONObject(1).getString("text"))
                assertEquals(4, lines.getJSONObject(1).getInt("column"))
                assertEquals("Closing", lines.getJSONObject(2).getString("text"))
                tools(tool("text_edit", "url" to notesUrl(), "old_string" to "TOKEN-42", "new_string" to "TOKEN-43"))
            }
            8 -> {
                assertEquals(result, 1, JSONObject(result).optInt("changes"))
                tools(tool("raw_grep", "url" to notesUrl(), "pattern" to "34 33", "literal" to true))
            }
            9 -> {
                val offset = JSONObject(result).getJSONArray("lines").getJSONObject(0).getInt("match_offset")
                tools(tool("bin_patch", "url" to notesUrl(), "offset" to offset, "old_hex" to "34 33", "new_hex" to "34 34"))
            }
            10 -> {
                assertEquals(result, 1, JSONObject(result).optInt("changes"))
                tools(tool("grep", "url" to notesUrl(), "pattern" to "TOKEN-44"))
            }
            11 -> {
                assertEquals("中文 TOKEN-44", JSONObject(result).getJSONArray("lines").getJSONObject(0).getString("text"))
                json("content" to DiceReply)
            }
            else -> error("Unexpected ChatAgent round")
        }

        private fun notesUrl() = "minirp://assets/${manifest.getString("packageName")}/notes"

        private fun tools(vararg calls: JSONObject) =
            json("tool_calls" to JSONArray().apply { calls.forEachIndexed { i, call -> put(call.put("index", i)) } })

        private fun tool(name: String, vararg arguments: Pair<String, Any>) =
            json("id" to "call-${callId.incrementAndGet()}", "type" to "function",
                "function" to json("name" to name, "arguments" to json(*arguments).toString()))
    }

    private companion object {
        const val Interview = "What should your tabletop preset do?"
        const val ProjectName = "Signing flow fixture"
        const val FinishedPresetName = "Fixture tabletop"
        const val SetupRequest = "Create a tabletop preset whose plugin-managed calendar starts on 2032-02-29. Delegate, debug, and publish a small plugin."
        const val WaitingForWorker = "The plugin is still being built."
        const val SetupReply = "The tabletop preset and plugin are ready."
        const val PresetLockRequest = "Verify the preset navigation lock."
        const val PluginLockRequest = "Hold the worker navigation lock."
        const val PresetLockReply = "The navigation lock probe completed."
        const val EnvLockRequest = "Continue this work when the world panel closes."
        const val EnvLockReply = "The world work completed."
        const val DiceRequest = "Roll thirty dice with a modifier of minus seven."
        const val DiceReply = "The dice results were checked."
        const val AssetRequest = "Inspect the attached PDF using its Asset URL."
        const val AssetReply = "The PDF asset and page sample were checked."
        const val PluginSettingsTitle = "Station choices"
        const val PluginSettingsToggle = "Show diagnostic notes"
        val CardProgram = """
            root = Col([Title("Gate monitor", "md"), Text(%line), Text("Steps " + %steps), slider, add, inspect, manual])
            slider = Slider(%steps, 0, 10, 1, "Step count")
            add = Button("Add step", @Set(%steps, %steps + 1))
            inspect = Button("Inspect gate", @ToAssistant("Check the gate", {steps: %steps}))
            manual = Button("Manual", @OpenUrl("https://example.invalid/manual"))
            %line = "Missing fixture"
            %steps = 0
        """.trimIndent().replace('%', '$')

        fun json(vararg pairs: Pair<String, Any>) = JSONObject().apply { pairs.forEach { (key, value) -> put(key, value) } }
    }
}
