package com.minirp.app.ui

import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.ui.compose.state.rememberPlayPauseButtonState
import androidx.media3.ui.compose.state.rememberProgressStateWithTickCount
import com.minirp.app.R

@OptIn(UnstableApi::class)
@Composable
internal fun AttachmentAudioControls(player: Player) {
    val play = rememberPlayPauseButtonState(player)
    val progress = rememberProgressStateWithTickCount(player, totalTickCount = 320)
    var scrub by remember(player) { mutableStateOf<Float?>(null) }
    val fraction = scrub ?: progress.currentPositionProgress
    val elapsed = playbackTime(progress.progressToPosition(fraction))
    val duration = progress.durationMs.takeIf { it > 0 }?.let(::playbackTime) ?: "–:–"
    val playLabel = stringResource(if (play.showPlay) R.string.audio_play else R.string.audio_pause)
    val seekLabel = stringResource(R.string.audio_position)

    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Row(
            Modifier.widthIn(max = 420.dp).fillMaxWidth().padding(horizontal = 14.dp).testTag("attachment-audio"),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            FilledIconButton(
                onClick = play::onClick,
                enabled = play.isEnabled,
                modifier = Modifier.size(48.dp).semantics { contentDescription = playLabel },
            ) {
                if (play.showPlay) Symbol("play_arrow")
                else Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    repeat(2) {
                        Box(Modifier.size(4.dp, 16.dp).background(LocalContentColor.current, RoundedCornerShape(1.dp)))
                    }
                }
            }
            CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
                Column(Modifier.weight(1f)) {
                    Slider(
                        value = fraction,
                        onValueChange = { scrub = it },
                        onValueChangeFinished = {
                            scrub?.let(progress::updateCurrentPositionProgress)
                            scrub = null
                        },
                        enabled = progress.changingProgressEnabled,
                        modifier = Modifier.fillMaxWidth().semantics {
                            contentDescription = seekLabel
                            stateDescription = elapsed
                        },
                    )
                    val style = MaterialTheme.typography.labelSmall.copy(fontFeatureSettings = "tnum")
                    val color = MaterialTheme.colorScheme.onSurfaceVariant
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(elapsed, style = style, color = color)
                        Text(duration, style = style, color = color)
                    }
                }
            }
        }
    }
}

private fun playbackTime(milliseconds: Long): String {
    val total = milliseconds.coerceAtLeast(0) / 1000
    val hours = total / 3600
    val minutes = total / 60 % 60
    val seconds = (total % 60).toString().padStart(2, '0')
    return if (hours > 0) "$hours:" + minutes.toString().padStart(2, '0') + ":$seconds" else "$minutes:$seconds"
}
