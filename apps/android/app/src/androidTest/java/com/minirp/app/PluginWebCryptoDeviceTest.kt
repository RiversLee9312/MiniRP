package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.core.CoreClient
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PluginWebCryptoDeviceTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun anInstalledPluginUsesTheAndroidWebCryptoProvider() = runBlocking {
        val directory = temporary.newFolder()
        val assets = InstrumentationRegistry.getInstrumentation().context.assets
        val files = listOf("manifest.json", "index.js").associateWith { name ->
            assets.open("web-crypto/$name").use { it.readBytes() }
        }
        val archive = writeSignedPlugin(directory.resolve("workspace"), UUID.randomUUID().toString(),
            "web-crypto-probe", files, directory.resolve("web-crypto-probe.plugin"))
        val app = ApplicationProvider.getApplicationContext<Context>()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val core = CoreClient(context, scope, seedDefaultPreset = true)
        try {
            val installed = core.query(org.json.JSONObject().put("op", "plugin_install").put("path", archive.path))
            assertEquals("web-crypto-probe", installed.getJSONObject("item").getString("id"))
            val result = core.readAsset("minirp://assets/test.web_crypto/probe", 4096)
            assertEquals("web-crypto-ok", result.toString(Charsets.UTF_8))
        } finally {
            core.close()
            scope.cancel()
        }
    }
}
