package com.minirp.app

import android.app.Application
import androidx.lifecycle.ViewModelStore
import com.minirp.app.core.TurnProtection
import com.minirp.app.data.Preferences

class MiniRPApplication : Application() {
    // Keep the native session and its event consumer together across Activity instances.
    internal val chatModels = ViewModelStore()
    internal val turnProtection by lazy { TurnProtection(this, Preferences(this)) }
}
