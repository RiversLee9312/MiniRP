package com.minirp.app

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.caoccao.javet.interop.V8Host
import com.caoccao.javet.interop.V8Runtime
import com.minirp.app.core.PluginScripts
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PluginScriptsLifecycleTest {
    @Test
    fun modulesResolveRelativeImportsAndReportMissingFiles() = runBlocking {
        val scripts = PluginScripts()
        try {
            fun load(id: String, entry: String) = request(id, "load")
                .put("bootstrap", "")
                .put("entry", "src/index.mjs")
                .put("modules", JSONObject()
                    .put("src/index.mjs", entry)
                    .put("src/lib/count.js", "export let count = 40; export function increment() { count++; }")
                    .put("step.js", "export function step(increment) { increment(); increment(); }"))
            scripts.execute(load("valid", """
                import { count, increment } from './lib/count.js';
                import { step } from '../step.js';
                globalThis.__minirp = {
                    start() { step(increment); },
                    poll() { return JSON.stringify({ count }); },
                };
            """.trimIndent()))
            assertEquals(42, scripts.run("valid"))
            val failure = runCatching { scripts.execute(load("missing", "import './missing.js';")) }.exceptionOrNull()
            assertTrue(failure.toString(), failure?.message.orEmpty().contains("./missing.js"))
            assertEquals(44, scripts.run("valid"))
        } finally {
            scripts.close()
        }
    }

    @Test
    fun cancellingAnActiveV8StepDoesNotInterruptAnotherIsolate() = runBlocking {
        val runtimes = mutableListOf<V8Runtime>()
        val scripts = PluginScripts {
            V8Host.getV8Instance().createV8Runtime<V8Runtime>().also(runtimes::add)
        }
        try {
            scripts.load("old")
            scripts.load("other")
            val running =
                async(Dispatchers.IO) {
                    scripts.execute(request("old", "start").put("payload", """{"spin":true}"""))
                }
            try {
                withTimeout(1_500) {
                    while (!runtimes.first().isInUse) delay(5)
                }
                assertEquals(1, withTimeout(1_500) { scripts.run("other") })
            } finally {
                running.cancel()
            }
            withTimeout(1_500) { running.cancelAndJoin() }
            assertTrue(runCatching { scripts.run("old") }.isFailure)
            assertEquals(2, scripts.run("other"))
            scripts.load("replacement")
            assertEquals(1, scripts.run("replacement"))
        } finally {
            scripts.close()
        }
    }

    @Test
    fun cancellationWhileReadingBinarySkipsThePendingJavaScriptReply() = runBlocking {
        val scripts = PluginScripts()
        val entered = CompletableDeferred<Unit>()
        val release = CountDownLatch(1)
        try {
            scripts.load("old")
            scripts.load("other")
            val resume =
                request("old", "resume")
                    .put(
                        "replies",
                        JSONArray().put(JSONObject().put("transfer", "bytes")),
                    )
            val running =
                async(Dispatchers.IO) {
                    scripts
                        .prepare(
                            resume,
                            {
                                entered.complete(Unit)
                                check(release.await(5, TimeUnit.SECONDS))
                                byteArrayOf(7)
                            },
                            { _, _ -> error("Unexpected binary output") },
                        )
                        .invoke()
                }
            withTimeout(5_000) { entered.await() }
            try {
                assertEquals(1, withTimeout(1_500) { scripts.run("other") })
            } finally {
                running.cancel()
                release.countDown()
            }
            withTimeout(1_500) { running.cancelAndJoin() }
            assertTrue(runCatching { scripts.run("old") }.isFailure)
            assertEquals(2, scripts.run("other"))
            scripts.load("replacement")
            assertEquals(1, scripts.run("replacement"))
        } finally {
            release.countDown()
            scripts.close()
        }
    }

    @Test
    fun timedOutCleanupRetiresOnlyItsOwnIsolate() = runBlocking {
        val scripts = PluginScripts()
        try {
            scripts.load("old")
            scripts.load("other")
            assertEquals(1, scripts.run("old"))
            scripts.execute(request("old", "finish"))
            assertEquals(2, scripts.run("old"))
            val pending =
                scripts.prepare(request("old", "start"), { error("Unexpected read") }, { _, _ -> })
            val cleanup =
                async(Dispatchers.IO) {
                    runCatching { scripts.execute(request("old", "finish").put("abort", true)) }
                }
            assertEquals(1, withTimeout(1_500) { scripts.run("other") })
            assertTrue(withTimeout(5_000) { cleanup.await() }.isFailure)
            assertTrue(runCatching { pending() }.isFailure)
            scripts.detach("old")?.invoke()
            scripts.load("replacement")
            assertEquals(1, scripts.run("replacement"))
            assertEquals(2, scripts.run("other"))
        } finally {
            scripts.close()
        }
    }

    private suspend fun PluginScripts.load(isolate: String) {
        execute(
            request(isolate, "load")
                .put("bootstrap", "")
                .put(
                    "source",
                    """
                    let count = 0;
                    globalThis.__minirp = {
                        start(_, payload) {
                            if (JSON.parse(payload).spin) for (;;) {}
                            ++count;
                        },
                        reply() { for (;;) {} },
                        finish(_, abort) { if (abort) for (;;) {} },
                        poll() { return JSON.stringify({ count }); },
                    };
                    """
                        .trimIndent(),
                )
        )
    }

    private suspend fun PluginScripts.run(isolate: String): Int =
        execute(request(isolate, "start")).getInt("count")

    private suspend fun PluginScripts.execute(request: JSONObject): JSONObject =
        prepare(
                request,
                { error("Unexpected binary input") },
                { _, _ -> error("Unexpected binary output") },
            )
            .invoke()

    private fun request(isolate: String, action: String): JSONObject =
        JSONObject()
            .put("isolate", isolate)
            .put("action", action)
            .put("run", "test")
            .put("payload", "{}")
}
