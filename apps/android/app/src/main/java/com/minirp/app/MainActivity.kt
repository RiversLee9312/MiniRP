package com.minirp.app

import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.ViewModelProvider
import com.minirp.app.ui.ChatViewModel
import com.minirp.app.ui.MiniRP

class MainActivity : AppCompatActivity() {
    private val app get() = application as MiniRPApplication
    private val model by lazy {
        ViewModelProvider.create(
            app.chatModels, defaultViewModelProviderFactory, defaultViewModelCreationExtras,
        )[ChatViewModel::class.java]
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { MiniRP(model) }
    }

    override fun onResume() {
        super.onResume()
        app.turnProtection.updateService()
    }
}
