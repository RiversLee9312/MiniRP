package com.minirp.app.ui

import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.core.EaseInCubic
import androidx.compose.animation.core.EaseOutCubic
import androidx.compose.animation.core.tween
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.navigation.NavGraphBuilder
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost

private const val PageTransitionMillis = 360

private fun backEnter() =
    slideInHorizontally(tween(PageTransitionMillis, easing = EaseOutCubic)) { width -> -width / 4 } +
        fadeIn(tween(PageTransitionMillis, easing = EaseOutCubic))

private fun backExit() =
    slideOutHorizontally(tween(PageTransitionMillis, easing = EaseInCubic)) { width -> width }

@Composable
internal fun PageNavHost(
    navController: NavHostController,
    startDestination: Any,
    modifier: Modifier = Modifier,
    builder: NavGraphBuilder.() -> Unit,
) {
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
        popEnterTransition = { backEnter() },
        popExitTransition = { backExit() },
        // Matching specs let Navigation continue from the gesture's current position on release.
        predictivePopEnterTransition = { _ -> backEnter() },
        predictivePopExitTransition = { _ -> backExit() },
        builder = builder,
    )
}
