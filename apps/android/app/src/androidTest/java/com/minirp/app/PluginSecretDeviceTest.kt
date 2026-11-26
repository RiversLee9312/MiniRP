package com.minirp.app

import android.content.Context
import android.content.ContextWrapper
import android.util.Base64
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import com.minirp.app.core.CoreClient
import com.minirp.app.data.Preferences
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PluginSecretDeviceTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test
    fun legacyBaseResearchCredentialsMigrateToTheNamedStore() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val path = temporary.newFolder().resolve("migration.preferences_pb")
        val store = PreferenceDataStoreFactory.create(scope = scope) { path }
        val preferences = Preferences(store)
        val legacy = stringPreferencesKey("research_keys_encrypted")
        val oldToken = "fixture-old-search-token"
        try {
            preferences.save(JSONArray(), JSONObject())
            val key = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }.getKey("providers", null)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, key)
            val oldJson = JSONObject().put("exa", oldToken).toString().toByteArray()
            store.edit { it[legacy] = Base64.encodeToString(cipher.iv + cipher.doFinal(oldJson), Base64.NO_WRAP) }
            val (_, options) = preferences.load()
            assertEquals(oldToken, options.getJSONObject("research_keys").getString("exa"))
            val request = JSONObject().put("owner", "builtin-base").put("operation", "get").put("key", "exa")
            assertEquals(oldToken, preferences.pluginSecret(request).getString("value"))
            assertFalse(store.data.first().contains(legacy))
            assertFalse(path.readBytes().toString(Charsets.UTF_8).contains(oldToken))
        } finally { scope.cancel() }
    }

    @Test
    fun signedPluginUsesEncryptedStorageAcrossCoreRestarts() = runBlocking {
        val directory = temporary.newFolder()
        val workspace = directory.resolve("workspace")
        val assets = InstrumentationRegistry.getInstrumentation().context.assets
        val files = listOf("manifest.json", "index.js").associateWith { name ->
            assets.open("secret-storage/$name").use { it.readBytes() }
        }
        fun sign(signer: String) = writeSignedPlugin(workspace, signer,
            "secret-probe", files, directory.resolve("$signer.plugin"))
        val primary = sign(UUID.randomUUID().toString())
        val differentSigner = sign(UUID.randomUUID().toString())
        val app = ApplicationProvider.getApplicationContext<Context>()
        val context = object : ContextWrapper(app) { override fun getFilesDir() = directory }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val data = directory.resolve("secrets.preferences_pb")
        val preferences = Preferences(PreferenceDataStoreFactory.create(scope = scope) { data })
        suspend fun CoreClient.probe(id: String) {
            val bytes = readAsset("minirp://assets/test.secret_probe/$id", 4096)
            assertEquals("secret-storage-ok", bytes.toString(Charsets.UTF_8))
        }
        suspend fun CoreClient.install(path: String) {
            val installed = query(JSONObject().put("op", "plugin_install").put("path", path))
            assertEquals("secret-probe", installed.getJSONObject("item").getString("id"))
        }
        try {
            CoreClient(context, scope, seedDefaultPreset = true, preferences = preferences).use { core ->
                core.install(primary.path)
                core.probe("write")
                core.probe("read")
            }
            assertFalse(data.readBytes().toString(Charsets.UTF_8).contains("fixture-secret-storage-value"))
            CoreClient(context, scope, seedDefaultPreset = true, preferences = preferences).use { core ->
                withTimeout(8_000) {
                    while (true) {
                        val items = core.query(JSONObject().put("op", "plugins_list")).getJSONArray("items")
                        if ((0 until items.length()).any { items.getJSONObject(it).getString("id") == "secret-probe" }) break
                        delay(20)
                    }
                }
                core.probe("read")
                core.probe("remove")
                core.probe("missing")
                core.probe("write")
                core.query(JSONObject().put("op", "plugin_uninstall").put("id", "secret-probe"))
                core.install(differentSigner.path)
                core.probe("missing")
            }
        } finally { scope.cancel() }
    }
}
