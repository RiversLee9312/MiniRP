package com.minichat.app

import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.appcompat.app.AppCompatActivity
import com.minichat.app.ui.ChatViewModel
import com.minichat.app.ui.Minichat

class MainActivity : AppCompatActivity() {
    private val model: ChatViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { Minichat(model) }
    }
}
