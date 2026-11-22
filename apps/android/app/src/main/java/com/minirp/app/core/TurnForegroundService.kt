package com.minirp.app.core

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.minirp.app.MainActivity
import com.minirp.app.MiniRPApplication
import com.minirp.app.R

class TurnForegroundService : Service() {
    private val protection get() = (application as MiniRPApplication).turnProtection
    private val notifications get() = getSystemService(NotificationManager::class.java)
    private var requestPromotion = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val requested = protection.serviceStarted(this)
        try {
            val strings = ContextCompat.getContextForLanguage(this)
            notifications.createNotificationChannel(
                NotificationChannel(
                    CHANNEL, strings.getString(R.string.background_turns), NotificationManager.IMPORTANCE_LOW
                )
            )
            requestPromotion = requested && shouldPromote()
            // A turn may have ended while Android was creating the service. Satisfy the promotion
            // deadline before stopping the service in that case.
            ServiceCompat.startForeground(
                this, NOTIFICATION, notification(immediate = requested), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
            )
            if (!requested) finish()
        } catch (error: IllegalStateException) {
            Log.d(TAG, "Background turn service could not enter the foreground", error)
            finish()
        } catch (error: SecurityException) {
            Log.w(TAG, "Background turn service permission unavailable", error)
            finish()
        }
        return START_NOT_STICKY
    }

    private fun shouldPromote(): Boolean = Build.VERSION.SDK_INT >= 36 && protection.hasUserInitiatedTurn

    internal fun updatePromotion() {
        val requested = shouldPromote()
        if (requestPromotion == requested) return
        requestPromotion = requested
        notifications.notify(NOTIFICATION, notification())
    }

    private fun notification(immediate: Boolean = true): Notification {
        val strings = ContextCompat.getContextForLanguage(this)
        val openApp = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.sym_action_chat)
            .setContentTitle(strings.getString(R.string.background_turn_running))
            .setContentIntent(openApp)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setOngoing(true)
            .setShowWhen(false)
            .setOnlyAlertOnce(true)
            .apply {
                if (immediate) setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
                if (requestPromotion) {
                    setStyle(NotificationCompat.ProgressStyle().setProgressIndeterminate(true))
                    setRequestPromotedOngoing(true)
                }
            }
            .build()
    }

    override fun onTimeout(startId: Int, fgsType: Int) = finish()

    internal fun finish() {
        protection.serviceStopped(this)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        protection.serviceStopped(this)
        super.onDestroy()
    }

    private companion object {
        const val TAG = "TurnForegroundService"
        const val CHANNEL = "background_turns"
        const val NOTIFICATION = 1
    }
}
