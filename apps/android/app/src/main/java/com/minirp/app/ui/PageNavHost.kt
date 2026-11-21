package com.minirp.app.ui

import androidx.compose.animation.AnimatedContentScope
import androidx.compose.animation.EnterExitState
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.EaseInCubic
import androidx.compose.animation.core.EaseOutCubic
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.layout.layout
import androidx.navigation.NavBackStackEntry
import androidx.navigation.NavGraphBuilder
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigationevent.NavigationEventTransitionState
import androidx.navigationevent.compose.LocalNavigationEventDispatcherOwner
import kotlin.math.roundToInt
import kotlinx.coroutines.flow.collectLatest

private const val PageTransitionMillis = 360
private val LocalBackMotion = staticCompositionLocalOf<BackMotion> { error("PageNavHost is missing") }

private class BackMotion {
    data class Pages(val outgoing: String, val incoming: String)

    var pages by mutableStateOf<Pages?>(null)
        private set
    var outgoing by mutableFloatStateOf(0f)
        private set
    var incoming by mutableFloatStateOf(0f)
        private set
    private var departing: String? = null
    private var disposed: String? = null
    private var settled = false

    fun preview(from: String, to: String, progress: Float) {
        val pair = Pages(from, to)
        if (pages == pair) return
        pages = pair
        departing = null
        disposed = null
        settled = false
        track(progress)
    }

    fun track(progress: Float) {
        outgoing = progress
        incoming = progress
    }

    suspend fun settle(current: String?) {
        val pair = pages ?: return
        val commit = current == pair.incoming
        departing = if (commit) pair.outgoing else pair.incoming
        val start = outgoing
        val distance = if (commit) 1f - start else start
        animate(0f, 1f, animationSpec = tween((distance * PageTransitionMillis).roundToInt(), easing = LinearEasing)) { progress, _ ->
            outgoing = if (commit) start + distance * EaseInCubic.transform(progress)
                else start * (1f - EaseOutCubic.transform(progress))
            incoming = if (commit) start + distance * EaseOutCubic.transform(progress)
                else start * (1f - EaseOutCubic.transform(progress))
        }
        settled = true
        clearIfFinished()
    }

    fun pageDisposed(id: String) {
        if (pages?.let { id == it.outgoing || id == it.incoming } != true) return
        disposed = id
        clearIfFinished()
    }

    private fun clearIfFinished() {
        if (settled && departing == disposed) pages = null
    }
}

internal inline fun <reified Route : Any> NavGraphBuilder.page(
    noinline content: @Composable AnimatedContentScope.(NavBackStackEntry) -> Unit,
) {
    composable<Route> { entry -> BackPage(entry.id) { content(entry) } }
}

@Composable
internal fun AnimatedContentScope.BackPage(id: String, content: @Composable () -> Unit) {
    val motion = LocalBackMotion.current
    DisposableEffect(motion, id) { onDispose { motion.pageDisposed(id) } }
    if (motion.pages?.let { id == it.outgoing || id == it.incoming } == true) {
        // Keep the two destinations composed through the handoff to the release animation.
        transition.animateFloat(
            transitionSpec = { tween(PageTransitionMillis, easing = LinearEasing) },
            label = "Predictive back page lifetime",
        ) { if (it == EnterExitState.Visible) 1f else 0f }
    }
    Box(
        Modifier.fillMaxSize().layout { measurable, constraints ->
            val page = measurable.measure(constraints)
            layout(page.width, page.height) {
                val pages = motion.pages
                when (id) {
                    pages?.outgoing -> page.place((page.width * motion.outgoing).roundToInt(), 0)
                    pages?.incoming -> page.placeWithLayer((-page.width * (1f - motion.incoming) / 4).roundToInt(), 0) {
                        alpha = motion.incoming
                    }
                    else -> page.place(0, 0)
                }
            }
        },
        propagateMinConstraints = true,
    ) { content() }
}

@Composable
internal fun PageNavHost(
    navController: NavHostController,
    startDestination: Any,
    modifier: Modifier = Modifier,
    builder: NavGraphBuilder.() -> Unit,
) {
    val gesture = LocalNavigationEventDispatcherOwner.current?.navigationEventDispatcher?.transitionState
    val motion = remember(navController) { BackMotion() }
    LaunchedEffect(gesture, navController) {
        gesture?.collectLatest { state ->
            when (state) {
                is NavigationEventTransitionState.InProgress -> motion.track(state.latestEvent.progress)
                NavigationEventTransitionState.Idle -> motion.settle(navController.currentBackStackEntry?.id)
            }
        }
    }
    CompositionLocalProvider(LocalBackMotion provides motion) {
        NavHost(
            navController = navController,
            startDestination = startDestination,
            modifier = modifier.clipToBounds(),
            enterTransition = {
                slideInHorizontally(tween(PageTransitionMillis, easing = EaseOutCubic)) { width -> width }
            },
            exitTransition = {
                slideOutHorizontally(tween(PageTransitionMillis, easing = EaseInCubic)) { width -> -width / 4 } +
                    fadeOut(tween(PageTransitionMillis, easing = EaseInCubic))
            },
            popEnterTransition = {
                if (motion.pages == BackMotion.Pages(initialState.id, targetState.id)) EnterTransition.None
                else slideInHorizontally(tween(PageTransitionMillis, easing = EaseOutCubic)) { width -> -width / 4 } +
                    fadeIn(tween(PageTransitionMillis, easing = EaseOutCubic))
            },
            popExitTransition = {
                if (motion.pages == BackMotion.Pages(initialState.id, targetState.id)) ExitTransition.None
                else slideOutHorizontally(tween(PageTransitionMillis, easing = EaseInCubic)) { width -> width }
            },
            predictivePopEnterTransition = { _ ->
                val progress = (gesture?.value as? NavigationEventTransitionState.InProgress)?.latestEvent?.progress ?: 0f
                motion.preview(initialState.id, targetState.id, progress)
                EnterTransition.None
            },
            predictivePopExitTransition = { _ -> ExitTransition.None },
            builder = builder,
        )
    }
}
