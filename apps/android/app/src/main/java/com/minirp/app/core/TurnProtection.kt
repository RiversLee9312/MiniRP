package com.minirp.app.core

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import com.minirp.app.data.Preferences
import java.io.IOException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Tracks active turns on the main thread and owns the foreground-service request. */
internal class TurnProtection(private val context: Context, private val preferences: Preferences) {
    enum class Prompt { FirstUse, Interrupted }
    data class State(val enabled: Boolean = false, val prompt: Prompt? = null)

    private val mutableState = MutableStateFlow(State())
    val state = mutableState.asStateFlow()
    private val activeTurns = mutableMapOf<String, Boolean>()
    val hasUserInitiatedTurn get() = activeTurns.values.any { it }
    private var startingService = false
    private var service: TurnForegroundService? = null

    suspend fun initialize() {
        try {
            val saved = preferences.loadTurnProtection()
            val enabled = saved.enabled && canNotify()
            val prompt = when {
                enabled -> null
                interruptedByProcessExit(saved) -> Prompt.Interrupted
                !saved.asked -> Prompt.FirstUse
                else -> null
            }
            mutableState.value = State(enabled, prompt)
            if (saved.previousPid != null) storeMarker(null)
        } catch (error: IOException) {
            Log.w(TAG, "Could not load background turn settings", error)
        }
    }

    suspend fun authorize(enabled: Boolean) {
        val allowed = enabled && canNotify()
        mutableState.value = State(enabled = allowed)
        updateService()
        try {
            preferences.saveTurnProtection(allowed)
        } catch (error: IOException) {
            Log.w(TAG, "Could not save background turn settings", error)
        }
    }

    suspend fun turnChanged(id: String, running: Boolean, interrupted: Boolean, userInitiated: Boolean) {
        val wasActive = activeTurns.isNotEmpty()
        if (running) {
            if (id in activeTurns) return
            activeTurns[id] = userInitiated
        } else if (activeTurns.remove(id) == null) {
            return
        }
        updateService()
        if (interrupted && !mutableState.value.enabled)
            mutableState.value = State(prompt = Prompt.Interrupted)
        val isActive = activeTurns.isNotEmpty()
        if (wasActive != isActive) storeMarker(if (isActive) System.currentTimeMillis() else null)
    }

    private suspend fun storeMarker(startedAt: Long?) {
        try {
            preferences.saveActiveTurn(startedAt)
        } catch (error: IOException) {
            Log.w(TAG, "Could not save active turn marker", error)
        }
    }

    private fun interruptedByProcessExit(saved: Preferences.TurnProtectionSettings): Boolean {
        if (Build.VERSION.SDK_INT < 30) return false
        val pid = saved.previousPid ?: return false
        val startedAt = saved.previousStartedAt ?: return false
        val exit = context.getSystemService(ActivityManager::class.java)
            .getHistoricalProcessExitReasons(context.packageName, pid, 1)
            .firstOrNull() ?: return false
        return exit.timestamp >= startedAt && exit.reason != ApplicationExitInfo.REASON_USER_REQUESTED &&
            exit.reason != ApplicationExitInfo.REASON_USER_STOPPED
    }

    private fun canNotify(): Boolean =
        context.getSystemService(NotificationManager::class.java).areNotificationsEnabled()

    private fun protectionRequested(): Boolean = activeTurns.isNotEmpty() && mutableState.value.enabled && canNotify()

    fun updateService() {
        if (!protectionRequested()) {
            service?.finish()
            return
        }
        val running = service
        if (running != null) {
            running.updatePromotion()
            return
        }
        if (startingService) return
        try {
            startingService = context.startForegroundService(Intent(context, TurnForegroundService::class.java)) != null
        } catch (error: IllegalStateException) {
            // Android can reject a start if the user left the app before the core began the turn.
            Log.d(TAG, "Background turn service could not start", error)
        } catch (error: SecurityException) {
            Log.w(TAG, "Background turn service permission unavailable", error)
        }
    }

    fun serviceStarted(started: TurnForegroundService): Boolean {
        startingService = false
        service = started
        return protectionRequested()
    }

    fun serviceStopped(stopped: TurnForegroundService) {
        if (service === stopped) service = null
    }

    private companion object {
        const val TAG = "TurnProtection"
    }
}
