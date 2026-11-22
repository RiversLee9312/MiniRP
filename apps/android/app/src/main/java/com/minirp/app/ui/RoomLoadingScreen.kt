package com.minirp.app.ui

import android.icu.text.BreakIterator
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Easing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.MotionDurationScale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.progressBarRangeInfo
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.minirp.app.R
import kotlin.math.exp
import kotlinx.coroutines.delay

private const val CaptionInterval = 3_000L
private const val ProgressBeforeReady = 0.98f
private val ProgressPredictionEasing = Easing { fraction ->
    (1f - exp(-5f * fraction)) / (1f - exp(-5f))
}

@Composable
internal fun RoomLoadingScreen(loading: RoomLoading, stop: () -> Unit) {
    val motionScale = rememberCoroutineScope().coroutineContext[MotionDurationScale]
    val animate = (motionScale?.scaleFactor ?: 1f) > 0f
    Dialog(
        onDismissRequest = stop,
        properties = DialogProperties(dismissOnClickOutside = false),
    ) {
        Surface(shape = MaterialTheme.shapes.extraLarge, color = MaterialTheme.colorScheme.surfaceContainerHigh) {
            Column(
                modifier = Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(24.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(
                    stringResource(R.string.running),
                    modifier = Modifier.semantics { heading() },
                    style = MaterialTheme.typography.headlineSmall,
                )
                key(loading.id) {
                    RoomLoadingProgress(loading, animate)
                    if (loading.messages.isNotEmpty()) RoomLoadingCaption(loading.messages, animate)
                }
                TextButton(onClick = stop) { Text(stringResource(R.string.stop)) }
            }
        }
    }
}

@Composable
private fun RoomLoadingProgress(loading: RoomLoading, animate: Boolean) {
    val total = loading.total.coerceAtLeast(1)
    val allReady = loading.total > 0 && loading.ready == loading.total
    val confirmed = if (allReady) 1f else loading.progress.coerceIn(0f, ProgressBeforeReady)
    val nextMilestone = (loading.ready + 0.9f) / total
    val predicted = (confirmed + (nextMilestone - confirmed).coerceAtLeast(0f) * 0.8f)
        .coerceAtMost(ProgressBeforeReady)
    val visual = remember { Animatable(confirmed) }

    LaunchedEffect(confirmed, predicted, animate) {
        if (!animate) {
            visual.snapTo(confirmed)
        } else {
            if (confirmed > visual.value) visual.animateTo(confirmed, tween(240))
            if (predicted > visual.value) {
                // Five time constants let the forecast settle naturally while leaving room for confirmation.
                visual.animateTo(predicted, tween(19_000, easing = ProgressPredictionEasing))
            }
        }
    }

    Box(Modifier.clearAndSetSemantics { progressBarRangeInfo = ProgressBarRangeInfo(confirmed, 0f..1f) }) {
        LinearProgressIndicator(
            progress = { if (animate) visual.value else confirmed },
            modifier = Modifier.fillMaxWidth().height(6.dp).testTag("room-loading-track"),
            drawStopIndicator = {},
        )
    }
}

@Composable
private fun RoomLoadingCaption(messages: List<String>, animate: Boolean) {
    val locale = LocalConfiguration.current.locales[0]
    val boundaries = remember(messages, locale) {
        val iterator = BreakIterator.getCharacterInstance(locale)
        messages.map { message ->
            iterator.setText(message)
            generateSequence(iterator.next().takeUnless { it == BreakIterator.DONE }) {
                iterator.next().takeUnless { it == BreakIterator.DONE }
            }.toList()
        }
    }
    var index by remember(messages) { mutableIntStateOf(0) }
    var visibleEnd by remember(messages) { mutableIntStateOf(boundaries.first().firstOrNull() ?: 0) }
    LaunchedEffect(messages, boundaries, animate) {
        while (true) {
            val ends = boundaries[index]
            val step = if (animate && ends.isNotEmpty()) {
                (ends.size * 31L).coerceIn(280L, 1_280L) / ends.size
            } else 0L
            if (step > 0L) {
                for (end in ends) {
                    visibleEnd = end
                    delay(step)
                }
            }
            if (messages.size == 1) break
            delay(CaptionInterval - step * ends.size)
            index = (index + 1) % messages.size
        }
    }
    val caption = messages[index]
    Box(Modifier.clearAndSetSemantics {
        contentDescription = caption
        liveRegion = LiveRegionMode.Polite
    }) {
        // Keep line breaks and alignment stable while revealing the caption.
        Text(
            buildAnnotatedString {
                append(caption)
                if (animate && visibleEnd < caption.length) {
                    addStyle(SpanStyle(color = Color.Transparent), visibleEnd, caption.length)
                }
            },
            modifier = Modifier.fillMaxWidth(),
            minLines = 2,
            textAlign = TextAlign.Center,
            style = MaterialTheme.typography.bodyMedium,
        )
    }
}
