package com.minirp.app

import android.os.SystemClock
import android.graphics.Bitmap
import android.view.InputDevice
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.inspector.WindowInspector
import android.webkit.WebView
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.isDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollToKey
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.click
import androidx.compose.ui.unit.dp
import androidx.test.filters.SdkSuppress
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.ui.OpenUiInteractions
import com.minirp.app.ui.AssetDownloader
import com.minirp.app.ui.DisplayAssets
import com.minirp.app.ui.TraceBlock
import com.minirp.app.ui.TraceLog
import com.mikepenz.markdown.model.State
import com.mikepenz.markdown.model.parseMarkdown
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class OpenUiRendererTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun downloadsReplyLinksAndRendersAnAssetImageInsideTheCard() {
        val url = "minirp://assets/minirp.agent/12345678-1234-1234-1234-123456789abc"
        val png = ByteArrayOutputStream().also { output ->
            val bitmap = Bitmap.createBitmap(12, 8, Bitmap.Config.ARGB_8888)
            try { bitmap.compress(Bitmap.CompressFormat.PNG, 100, output) }
            finally { bitmap.recycle() }
        }.toByteArray()
        val request = AtomicReference<JSONObject?>()
        val loaded = AtomicReference<JSONObject?>()
        val assets = DisplayAssets(AssetDownloader(request::set), "preset", "room", "conversation") { info, accepts ->
            loaded.set(info)
            if (accepts("image/png")) "image/png" to ByteArrayInputStream(png) else null
        }
        val markdown = "[Save report]($url)"
        val program = """root = Col([Image("$url", "Report preview"), Link("Save from card", "$url")])"""
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(
                    listOf(
                        TraceBlock("report-reply", content = markdown, markdown = parseMarkdown(markdown) as State.Success),
                        TraceBlock("report-card", openUi = program),
                    ),
                    false,
                    displayAssets = assets,
                )
            }
        }
        compose.waitUntil(15_000) {
            js("document.querySelector('img')?.naturalWidth", cardId = "report-card") == "12"
        }
        assertEquals("report-card", loaded.get()!!.getString("block"))
        assertEquals(url, loaded.get()!!.getString("url"))
        click("Save from card", cardId = "report-card")
        compose.waitUntil(3_000) { request.get()?.optString("block") == "report-card" }
        assertEquals(url, request.get()!!.getString("url"))
        compose.onNodeWithText("Save report").performTouchInput { click() }
        compose.waitUntil(3_000) { request.get()?.optString("block") == "report-reply" }
        assertEquals(url, request.get()!!.getString("url"))
    }

    @Test
    fun openUiTablesWrapLongValuesAndKeepWideColumnsReachable() {
        val longText = "x".repeat(120) + "-visible-ending"
        val markdown = "| Header | Result |\n| --- | --- |\n| $longText | Done |"
        val program = """
            root = Col([wide, markdown])
            wide = Table(["One", "Two", "Three", "Four", "Five"], [["$longText", "B", "C", "D", "Last column"]])
            markdown = Markdown(${JSONObject.quote(markdown)})
        """.trimIndent()
        compose.setContent {
            MaterialExpressiveTheme {
                Box(Modifier.width(320.dp)) { TraceLog(listOf(TraceBlock("tables", openUi = program)), false) }
            }
        }
        compose.waitUntil(10_000) {
            js("document.querySelectorAll('table').length", cardId = "tables") == "2"
        }
        val measurements = js(
            """
            (() => {
                const [wide, markdown] = document.querySelectorAll("table");
                function wraps(table) {
                    const cell = table.querySelector("td");
                    const text = document.createRange();
                    text.selectNodeContents(cell);
                    const style = getComputedStyle(cell);
                    return style.whiteSpace === "normal" && style.textOverflow === "clip" &&
                        text.getClientRects().length > 1 && cell.textContent.endsWith("-visible-ending");
                }
                const viewport = wide.parentElement;
                const last = wide.querySelector("tbody td:last-child");
                last.scrollIntoView({block:"nearest", inline:"end"});
                const bounds = viewport.getBoundingClientRect();
                const cellBounds = last.getBoundingClientRect();
                const reachable = viewport.scrollWidth > viewport.clientWidth &&
                    cellBounds.left >= bounds.left - 1 && cellBounds.right <= bounds.right + 1;
                return [wraps(wide), wraps(markdown), reachable];
            })()
            """.trimIndent(),
            cardId = "tables",
        )
        assertEquals("[true,true,true]", measurements)
    }

    @Test
    fun pluginCardRefreshesFromItsStoredStateWithoutReloadingTheWebView() {
        val program =
            """
            root = Col([Title("Station", "md"), Text(%line)])
            %line = "No change"
            """
                .trimIndent()
                .replace('%', '$')
        val card =
            mutableStateOf(
                TraceBlock(
                    "station",
                    openUi = program,
                    openUiState = """{"line":"Gate open."}""",
                )
            )
        compose.setContent { MaterialExpressiveTheme { TraceLog(listOf(card.value), false) } }
        compose.waitUntil(15_000) { textContains("Gate open.") }
        assertEquals("true", js("window.retainedDocument = true"))
        compose.runOnIdle {
            card.value =
                card.value.copy(openUiState = """{"line":"Gate closed."}""", openUiRevision = 1)
        }
        compose.waitUntil(8_000) { textContains("Gate closed.") }
        assertEquals("true", js("window.retainedDocument === true"))
    }

    @Test
    fun streamsAReplyAndSupportsLocalComputationAndAConversationAction() {
        val program =
            """
            root = Col([title, controls, total, apply])
            title = Title("Trip calculator", "lg")
            controls = Button("Add a day", @Set(%days, %days + 1))
            total = Text("Total " + (%days * 125))
            apply = Button("Use this budget", @ToAssistant("Plan my trip", {days: %days}))
            %days = 2
            """
                .trimIndent()
                .replace('%', '$')
        val block =
            mutableStateOf(
                TraceBlock("trip", openUi = program.substringBefore("controls ="), streaming = true)
            )
        val answer = AtomicReference<Pair<String, JSONObject>?>()
        val interactions =
            OpenUiInteractions(
                reply = { _, text, data ->
                    answer.set(text to data)
                    true
                },
                restore = { "" },
                save = { _, _ -> },
            )
        compose.setContent {
            MaterialExpressiveTheme { TraceLog(listOf(block.value), false, openUi = interactions) }
        }
        compose.waitUntil(15000) { textContains("Trip calculator") }
        compose.runOnIdle { block.value = block.value.copy(openUi = program, streaming = false) }
        compose.waitUntil(15000) { textContains("Total 250") }
        click("Add a day")
        compose.waitUntil(10000) { textContains("Total 375") }
        assertNull(answer.get())
        click("Use this budget")
        compose.waitUntil(10000) { answer.get() != null }
        assertEquals("Plan my trip", answer.get()!!.first)
        assertEquals(3, answer.get()!!.second.getJSONObject("context").getInt("days"))
    }

    @Test
    fun scrollingPastAnotherCardKeepsTheLoadedDocumentAndFormState() {
        val program =
            """
            root = Col([Title("Trip calculator", "lg"), Button("Add a day", @Set(%days, %days + 1)), Text("Days " + %days)])
            %days = 2
            """
                .trimIndent()
                .replace('%', '$')
        val blocks = buildList {
            add(TraceBlock("other", openUi = "root = Col([Title(\"Another card\", \"lg\")])"))
            repeat(60) { add(TraceBlock("message-$it", content = "Message $it", user = true)) }
            add(TraceBlock("trip", openUi = program))
        }
        val scroll = LazyListState()
        compose.setContent { MaterialExpressiveTheme { TraceLog(blocks, false, list = scroll) } }
        compose.waitUntil(15_000) { textContains("Trip calculator") }
        click("Add a day")
        compose.waitUntil(5_000) { textContains("Days 3") }
        assertEquals("true", js("window.retainedDocument = true"))
        val transcript = compose.onNodeWithTag("transcript")
        transcript.performScrollToKey("open-ui:other")
        compose.runOnIdle {
            val visibleKeys = scroll.layoutInfo.visibleItemsInfo.map { it.key }
            assertTrue(visibleKeys.toString(), "open-ui:other" in visibleKeys)
            assertTrue(visibleKeys.toString(), "open-ui:trip" !in visibleKeys)
        }
        val otherRendered = "document.body.innerText.includes('Another card')"
        runCatching { compose.waitUntil(8_000) { js(otherRendered, cardId = "other") == "true" } }
            .getOrElse { failure ->
                val summary = "[location.href,document.readyState,document.body?.innerText]"
                throw AssertionError(
                    "Other card: ${js(summary, cardId = "other")}. First WebView: ${js(summary)}",
                    failure,
                )
            }
        assertEquals("false", js("window.retainedDocument === true", cardId = "other"))
        transcript.performScrollToKey("open-ui:trip")
        compose.waitUntil(5_000) { textContains("Days 3", cardId = "trip") }
        assertEquals("true", js("window.retainedDocument === true", cardId = "trip"))
        click("Add a day", cardId = "trip")
        compose.waitUntil(5_000) { textContains("Days 4", cardId = "trip") }
    }

    @Test
    fun filtersAChartAndSubmitsOnlyTheSelectedPlan() {
        val program =
            """
            root = Col([Title("Compare plans", "lg"), form, chart])
            form = Form([SegmentedControl(%plan, ["Basic", "Plus"]), Button("Compare this plan", @ToAssistant("Compare my plan", {plan: %plan}))])
            chart = Chart("bar", @Filter(data, "group", "==", %plan), "name", ["cost"])
            data = [{group: "Basic", name: "Basic monthly", cost: 15}, {group: "Plus", name: "Plus monthly", cost: 25}]
            %plan = "Basic"
            %unrelated = "Keep this local"
            """
                .trimIndent()
                .replace('%', '$')
        val answer = AtomicReference<JSONObject?>()
        val interactions =
            OpenUiInteractions(
                reply = { _, _, data ->
                    answer.set(data)
                    true
                },
                restore = { "" },
                save = { _, _ -> },
            )
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(
                    listOf(TraceBlock("plans", openUi = program)),
                    false,
                    openUi = interactions,
                )
            }
        }
        compose.waitUntil(15000) { textContains("Basic monthly") }
        click("Plus")
        compose.waitUntil(10000) { textContains("Plus monthly") }
        assertNull(answer.get())
        click("Compare this plan")
        compose.waitUntil(10000) { answer.get() != null }
        val context = answer.get()!!.getJSONObject("context")
        assertEquals("Plus", context.getString("plan"))
        assertEquals(1, context.length())
    }

    @SdkSuppress(minSdkVersion = 29)
    @Test
    fun expandedPageScrollsIndependentlyAndSheetStillDragsFromItsHeader() {
        val rows = (1..48).joinToString { "Text(\"Row $it\")" }
        val program = "root = Col([$rows])"
        compose.setContent {
            MaterialExpressiveTheme {
                TraceLog(listOf(TraceBlock("long", openUi = program)), false)
            }
        }
        val expand = compose.onNodeWithText(compose.activity.getString(R.string.expand))
        val sheet = compose.onNodeWithText(compose.activity.getString(R.string.interactive_reply))
        compose.waitUntil(15_000) { expand.isDisplayed() }
        expand.performClick()
        compose.waitUntil(15_000) {
            js("document.scrollingElement.scrollHeight > innerHeight", expanded = true) == "true"
        }
        swipeExpanded(from = 0.75f, to = 0.25f)
        compose.waitUntil(5_000) { js("scrollY > 40", expanded = true) == "true" }
        swipeExpanded(from = 0.22f, to = 0.85f)
        swipeExpanded(from = 0.22f, to = 0.85f)
        compose.waitUntil(5_000) { js("scrollY === 0", expanded = true) == "true" }
        sheet.assertIsDisplayed()
        swipeExpanded(from = 0f, to = 0.8f, fromHeader = true)
        compose.waitUntil(5_000) { !sheet.isDisplayed() }
        compose.waitUntil(8_000) { expand.isDisplayed() }
    }

    private fun textContains(text: String, cardId: String? = null): Boolean =
        js("document.body.innerText.includes(${JSONObject.quote(text)})", cardId = cardId) == "true"

    private fun click(label: String, cardId: String? = null) {
        compose.waitForIdle()
        val center =
            JSONArray(
                checkNotNull(
                    js(
                        """
            (() => {
                const button = [...document.querySelectorAll('button,a[href]')]
                    .find(element => element.innerText.trim() === ${JSONObject.quote(label)});
                if (!button) return null;
                        window.lastTestClick = null;
                        button.addEventListener("click", () => { window.lastTestClick = button.innerText; }, { once: true });
                const { x, y, width, height } = button.getBoundingClientRect();
                return [(x + width / 2) * devicePixelRatio, (y + height / 2) * devicePixelRatio];
            })()
        """
                            .trimIndent(),
                        cardId = cardId,
                    )
                )
            )
        val origin = IntArray(2)
        compose.activityRule.scenario.onActivity { activity ->
            checkNotNull(findWebView(activity.window.decorView, cardId)).getLocationOnScreen(origin)
        }
        val x = origin[0] + center.getDouble(0).toFloat()
        val y = origin[1] + center.getDouble(1).toFloat()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val pressedAt = SystemClock.uptimeMillis()
        for (action in listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP)) {
            val event = MotionEvent.obtain(pressedAt, SystemClock.uptimeMillis(), action, x, y, 0)
            try {
                event.source = InputDevice.SOURCE_TOUCHSCREEN
                check(automation.injectInputEvent(event, true))
            } finally {
                event.recycle()
            }
        }
        compose.waitUntil(3000) { js("window.lastTestClick != null", cardId = cardId) == "true" }
    }

    private fun swipeExpanded(from: Float, to: Float, fromHeader: Boolean = false) {
        compose.waitForIdle()
        val frame = IntArray(4)
        compose.activityRule.scenario.onActivity {
            val view = checkNotNull(expandedWebView(it))
            view.getLocationOnScreen(frame)
            frame[2] = view.width
            frame[3] = view.height
        }
        val x = frame[0] + frame[2] / 2f
        val start = frame[1] + if (fromHeader) -frame[2] * 0.05f else frame[3] * from
        val end = frame[1] + frame[3] * to
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val downTime = SystemClock.uptimeMillis()
        for (step in 0..14) {
            val action =
                when (step) {
                    0 -> MotionEvent.ACTION_DOWN
                    14 -> MotionEvent.ACTION_UP
                    else -> MotionEvent.ACTION_MOVE
                }
            val y = start + (end - start) * step / 14f
            val event = MotionEvent.obtain(downTime, SystemClock.uptimeMillis(), action, x, y, 0)
            try {
                event.source = InputDevice.SOURCE_TOUCHSCREEN
                check(automation.injectInputEvent(event, true))
            } finally {
                event.recycle()
            }
            SystemClock.sleep(16)
        }
    }

    private fun js(code: String, expanded: Boolean = false, cardId: String? = null): String? {
        val result = AtomicReference<String?>()
        val complete = CountDownLatch(1)
        compose.activityRule.scenario.onActivity { activity ->
            val view =
                if (expanded) expandedWebView(activity)
                else findWebView(activity.window.decorView, cardId)
            if (view == null) complete.countDown()
            else
                view.evaluateJavascript(code) { value ->
                    result.set(value)
                    complete.countDown()
                }
        }
        return if (complete.await(3, TimeUnit.SECONDS)) result.get() else null
    }

    private fun expandedWebView(activity: ComponentActivity): WebView? =
        WindowInspector.getGlobalWindowViews()
            .asSequence()
            .filter { it != activity.window.decorView && it.isShown }
            .mapNotNull(::findWebView)
            .firstOrNull()

    private fun findWebView(view: View, cardId: String? = null): WebView? =
        when (view) {
            is WebView -> view.takeIf { cardId == null || it.tag == cardId }
            is ViewGroup ->
                (0 until view.childCount).firstNotNullOfOrNull {
                    findWebView(view.getChildAt(it), cardId)
                }
            else -> null
        }
}
