package com.minirp.app.ui

import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.PlainTooltip
import androidx.compose.material3.Text
import androidx.compose.material3.TooltipAnchorPosition
import androidx.compose.material3.TooltipBox
import androidx.compose.material3.TooltipDefaults
import androidx.compose.material3.rememberTooltipState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PresetEntryHint(text: String, onSeen: (() -> Unit)?, anchor: @Composable () -> Unit) {
    if (onSeen == null) {
        anchor()
        return
    }
    val state = rememberTooltipState(isPersistent = true)
    val seen by rememberUpdatedState(onSeen)
    TooltipBox(
        positionProvider = TooltipDefaults.rememberTooltipPositionProvider(TooltipAnchorPosition.Below),
        tooltip = { PlainTooltip { Text(text) } },
        state = state,
        focusable = false,
        enableUserInput = false,
        content = anchor,
    )
    LaunchedEffect(state) {
        delay(400)
        coroutineScope {
            val popup = launch { state.show() }
            try {
                delay(6_000)
            } finally {
                state.dismiss()
                popup.cancel()
                seen()
            }
        }
    }
}
