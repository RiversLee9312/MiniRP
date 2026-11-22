package com.minirp.app.data

import android.content.Context
import android.os.Process
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences as StoredPreferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import java.security.KeyStore
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

    private val secretKey: SecretKey by lazy {
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

    private fun encrypt(text: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, secretKey)
        return Base64.encodeToString(cipher.iv + cipher.doFinal(text.toByteArray()), Base64.NO_WRAP)
    }

    private fun decrypt(text: String): String {
        val bytes = Base64.decode(text, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, secretKey, GCMParameterSpec(128, bytes.copyOfRange(0, 12)))
        return cipher.doFinal(bytes.copyOfRange(12, bytes.size)).toString(Charsets.UTF_8)
    }

    suspend fun load(): Pair<JSONArray, JSONObject> {
        val stored = store.data.first()
        val providers = stored[providersKey]?.let { JSONArray(decrypt(it)) } ?: JSONArray()
        val options = JSONObject(stored[optionsKey] ?: "{}")
        stored[researchKeys]?.let { options.put("research_keys", JSONObject(decrypt(it))) }
        return providers to options
    }

    suspend fun save(providers: JSONArray, options: JSONObject) {
        val encrypted = encrypt(providers.toString())
        val credentials = options.optJSONObject("research_keys")
            ?.takeIf { it.length() > 0 }
            ?.let { encrypt(it.toString()) }
        val public = JSONObject(options.toString()).apply { remove("research_keys") }
        store.edit {
            it[providersKey] = encrypted
            it[optionsKey] = public.toString()
            if (credentials == null) it.remove(researchKeys) else it[researchKeys] = credentials
        }
    }
}
