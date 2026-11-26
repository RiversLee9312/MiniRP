package com.minirp.app.data

import android.content.Context
import android.os.Process
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.MutablePreferences
import androidx.datastore.preferences.core.Preferences as StoredPreferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import java.security.KeyStore
import java.security.MessageDigest
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlinx.coroutines.flow.first
import org.json.JSONArray
import org.json.JSONObject

private val Context.preferences by preferencesDataStore("preferences")

/** Provider credentials are encrypted with a nonexportable Android Keystore key. */
class Preferences internal constructor(private val store: DataStore<StoredPreferences>) {
    constructor(context: Context) : this(context.preferences)

    private val providersKey = stringPreferencesKey("providers_encrypted")
    private val optionsKey = stringPreferencesKey("options")
    private val researchKeys = stringPreferencesKey("research_keys_encrypted")
    private val backgroundTurnsKey = booleanPreferencesKey("background_turns_enabled")
    private val backgroundTurnsAskedKey = booleanPreferencesKey("background_turns_asked")
    private val activeTurnPidKey = intPreferencesKey("active_turn_pid")
    private val activeTurnStartedKey = longPreferencesKey("active_turn_started")
    private val researchProviders = listOf("deepseek", "tavily", "exa", "brave")

    internal data class TurnProtectionSettings(
        val enabled: Boolean,
        val asked: Boolean,
        val previousPid: Int?,
        val previousStartedAt: Long?,
    )

    internal suspend fun loadTurnProtection(): TurnProtectionSettings {
        val stored = store.data.first()
        return TurnProtectionSettings(
            stored[backgroundTurnsKey] == true,
            stored[backgroundTurnsAskedKey] == true,
            stored[activeTurnPidKey],
            stored[activeTurnStartedKey],
        )
    }

    internal suspend fun saveTurnProtection(enabled: Boolean) {
        store.edit {
            it[backgroundTurnsKey] = enabled
            it[backgroundTurnsAskedKey] = true
        }
    }

    internal suspend fun saveActiveTurn(startedAt: Long?) {
        store.edit {
            if (startedAt == null) {
                it.remove(activeTurnPidKey)
                it.remove(activeTurnStartedKey)
            } else {
                it[activeTurnPidKey] = Process.myPid()
                it[activeTurnStartedKey] = startedAt
            }
        }
    }

    private companion object {
        const val BASE_SECRETS = "builtin-base"

        val secretKey: SecretKey by lazy {
            val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            (store.getKey("providers", null) as? SecretKey)
                ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
                    .apply {
                        init(
                            KeyGenParameterSpec.Builder(
                                    "providers",
                                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                                )
                                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                                .build()
                        )
                    }
                    .generateKey()
        }
    }

    private fun encrypt(text: String, scope: String? = null): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, secretKey)
        scope?.let { cipher.updateAAD(it.toByteArray()) }
        return Base64.encodeToString(cipher.iv + cipher.doFinal(text.toByteArray()), Base64.NO_WRAP)
    }

    private fun decrypt(text: String, scope: String? = null): String {
        val bytes = Base64.decode(text, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, secretKey, GCMParameterSpec(128, bytes.copyOfRange(0, 12)))
        scope?.let { cipher.updateAAD(it.toByteArray()) }
        return cipher.doFinal(bytes.copyOfRange(12, bytes.size)).toString(Charsets.UTF_8)
    }

    private fun secretPrefix(owner: String): String {
        val hash = MessageDigest.getInstance("SHA-256").digest(owner.toByteArray())
        return "plugin_secret_" + Base64.encodeToString(hash, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING) + ":"
    }

    private fun secretEntry(owner: String, id: String): StoredPreferences.Key<String> {
        require(id.length in 1..128 && id.all { it in 'a'..'z' || it in '0'..'9' || it == '-' }) {
            "Invalid secret ID."
        }
        return stringPreferencesKey(secretPrefix(owner) + id)
    }

    internal suspend fun pluginSecret(request: JSONObject): JSONObject {
        val owner = request.getString("owner")
        val operation = request.getString("operation")
        if (operation == "list") {
            val prefix = secretPrefix(owner)
            val ids = store.data.first().asMap().keys.mapNotNull { key ->
                key.name.takeIf { it.startsWith(prefix) }?.removePrefix(prefix)
            }.sorted()
            return JSONObject().put("ids", JSONArray(ids))
        }
        val key = secretEntry(owner, request.getString("key"))
        val result = JSONObject()
        when (operation) {
            "get" -> store.data.first()[key]?.let { result.put("value", decrypt(it, key.name)) }
            "set" -> store.edit { it[key] = encrypt(request.getString("value"), key.name) }
            "delete" -> store.edit { it.remove(key) }
            else -> error("Unknown secret storage operation.")
        }
        return result
    }

    suspend fun load(): Pair<JSONArray, JSONObject> {
        var stored = store.data.first()
        if (stored[researchKeys] != null) stored = store.edit { data ->
            data[researchKeys]?.let { legacy ->
                val keys = JSONObject(decrypt(legacy))
                for (provider in researchProviders) {
                    val entry = secretEntry(BASE_SECRETS, provider)
                    if (data[entry] == null && keys.has(provider)) {
                        data[entry] = encrypt(keys.getString(provider), entry.name)
                    }
                }
                data.remove(researchKeys)
            }
        }
        val providers = stored[providersKey]?.let { JSONArray(decrypt(it)) } ?: JSONArray()
        val options = JSONObject(stored[optionsKey] ?: "{}")
        val keys = JSONObject()
        for (provider in researchProviders) {
            val entry = secretEntry(BASE_SECRETS, provider)
            stored[entry]?.let { keys.put(provider, decrypt(it, entry.name)) }
        }
        options.put("research_keys", keys)
        return providers to options
    }

    suspend fun save(providers: JSONArray, options: JSONObject) {
        val encrypted = encrypt(providers.toString())
        val credentials = options.optJSONObject("research_keys")
        val public = JSONObject(options.toString()).apply { remove("research_keys") }
        store.edit {
            it[providersKey] = encrypted
            it[optionsKey] = public.toString()
            if (credentials != null) setResearchKeys(it, credentials)
        }
    }

    internal suspend fun saveResearchKeys(keys: JSONObject) {
        store.edit { setResearchKeys(it, keys) }
    }

    private fun setResearchKeys(stored: MutablePreferences, keys: JSONObject) {
        for (provider in researchProviders) {
            val entry = secretEntry(BASE_SECRETS, provider)
            if (keys.has(provider)) stored[entry] = encrypt(keys.getString(provider), entry.name)
            else stored.remove(entry)
        }
        stored.remove(researchKeys)
    }
}
