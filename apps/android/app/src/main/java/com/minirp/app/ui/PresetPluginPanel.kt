package com.minirp.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.navigation.NavDestination.Companion.hasRoute
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.toRoute
import com.minirp.app.R
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable

private sealed interface PresetRoute {
    @Serializable data object Conversation : PresetRoute

    @Serializable data object PluginActivity : PresetRoute

    @Serializable data class PluginTrace(val workerId: String) : PresetRoute
}

private data class PluginRun(val id: String, val blocks: List<TraceBlock>) {
    val status = blocks.firstNotNullOfOrNull { it.pluginStatus.takeIf(String::isNotBlank) }.orEmpty()
}

private data class PluginWorker(val id: String, val name: String, val runs: List<PluginRun>) {
    val latest = runs.last()
    val running = latest.status == "running"
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PresetPluginPanel(
    blocks: List<TraceBlock>,
    displayAssets: DisplayAssets,
    onClose: () -> Unit,
    locked: Boolean,
    canSwitchPreset: Boolean,
    canExportTranscript: Boolean,
    onExportTranscript: (String, String) -> Unit,
    onSettings: () -> Unit,
    library: @Composable (DrawerState) -> Unit,
    snackbarHost: @Composable () -> Unit,
    conversation: @Composable (Modifier) -> Unit,
) {
    val workers by rememberUpdatedState(remember(blocks) {
        blocks.filter { it.pluginRun.isNotBlank() }
            .groupBy { it.pluginAgentId.ifBlank { it.pluginRun } }
            .map { (id, steps) ->
                PluginWorker(
                    id,
                    steps.firstNotNullOfOrNull { it.pluginAgentName.takeIf(String::isNotBlank) }.orEmpty(),
                    steps.groupBy { it.pluginRun }.map { (run, trace) -> PluginRun(run, trace) },
                )
            }
            .asReversed()
            .sortedByDescending { it.running }
    })
    val currentConversation by rememberUpdatedState(conversation)
    val currentAssets by rememberUpdatedState(displayAssets)
    val exportTranscript by rememberUpdatedState(onExportTranscript)
    val exportEnabled by rememberUpdatedState(canExportTranscript)
    val navigation = rememberNavController()
    val entry by navigation.currentBackStackEntryAsState()
    val showActivity = entry?.destination?.hasRoute<PresetRoute.PluginActivity>() == true
    val trace = entry?.takeIf { it.destination.hasRoute<PresetRoute.PluginTrace>() }
        ?.toRoute<PresetRoute.PluginTrace>()
    val title = when {
        trace != null -> workers.find { it.id == trace.workerId }?.name.orEmpty()
            .ifBlank { stringResource(R.string.plugin_agent) }
        showActivity -> stringResource(R.string.plugin_agent_activity)
        else -> stringResource(R.string.preset)
    }
    val back: () -> Unit = { navigation.popBackStack() }
    val drawer = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    LaunchedEffect(locked, canSwitchPreset) {
        if ((!locked || !canSwitchPreset) && drawer.isOpen) drawer.close()
    }
    FullSheet(
        title,
        onClose,
        onBack = back.takeIf { showActivity || trace != null },
        actions = {
            if (!showActivity && trace == null) {
                ExportTranscriptButton({ onExportTranscript("", "") }, canExportTranscript && canSwitchPreset)
            }
        },
        draftControls = DraftPresetControls(
            locked,
            canSwitchPreset,
            drawer,
            { library(drawer) },
            { scope.launch { drawer.open() } },
            onSettings,
        ),
    ) {
        PageNavHost(navigation, PresetRoute.Conversation, Modifier.weight(1f).fillMaxWidth()) {
            composable<PresetRoute.Conversation> {
                val focus = LocalFocusManager.current
                val keyboard = LocalSoftwareKeyboardController.current
                Column(Modifier.fillMaxSize()) {
                    if (workers.isNotEmpty()) {
                        val running = workers.any { it.running }
                        ListItem(
                            content = { Text(stringResource(R.string.plugin_agent_activity)) },
                            supportingContent = if (running) {
                                {
                                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                        CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                                        Text(stringResource(R.string.running))
                                    }
                                }
                            } else null,
                            leadingContent = { Symbol("extension") },
                            trailingContent = { Symbol("chevron_right") },
                            colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                            modifier = Modifier.testTag("plugin-agent-activity").clickable {
                                focus.clearFocus()
                                keyboard?.hide()
                                navigation.navigate(PresetRoute.PluginActivity) { launchSingleTop = true }
                            },
                        )
                        HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                    }
                    currentConversation(Modifier.weight(1f).fillMaxWidth())
                }
            }
            composable<PresetRoute.PluginActivity> {
                LazyColumn(Modifier.fillMaxSize()) {
                    items(workers, key = { it.id }) { worker ->
                        ListItem(
                            content = {
                                Text(
                                    worker.name.ifBlank { stringResource(R.string.plugin_agent) },
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            },
                            supportingContent = { Text(pluginRunStatus(worker.latest.status)) },
                            leadingContent = { Symbol("extension") },
                            trailingContent = { Symbol("chevron_right") },
                            colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                            modifier = Modifier.testTag("plugin-agent:${worker.id}").clickable {
                                navigation.navigate(PresetRoute.PluginTrace(worker.id)) { launchSingleTop = true }
                            },
                        )
                        HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                    }
                }
            }
            composable<PresetRoute.PluginTrace> { page ->
                val id = page.toRoute<PresetRoute.PluginTrace>().workerId
                workers.find { it.id == id }?.let { PluginTrace(it, currentAssets, exportEnabled, exportTranscript) }
            }
        }
        snackbarHost()
    }
}

@Composable
private fun PluginTrace(
    worker: PluginWorker,
    displayAssets: DisplayAssets,
    canExportTranscript: Boolean,
    onExportTranscript: (String, String) -> Unit,
) {
    var selectedRun by rememberSaveable { mutableStateOf<String?>(null) }
    var choosingRun by remember { mutableStateOf(false) }
    val run = worker.runs.find { it.id == selectedRun } ?: worker.latest
    val status = pluginRunStatus(run.status)
    val steps = remember(run) {
        if (run.status == "running") run.blocks
        else run.blocks.map { block ->
            if (block.running || block.streaming) block.copy(running = false, streaming = false)
            else block
        }
    }
    Column(Modifier.fillMaxSize().testTag("plugin-agent-trace")) {
        if (worker.runs.size > 1) {
            Box(Modifier.padding(horizontal = 16.dp)) {
                val currentNumber = worker.runs.indexOf(run) + 1
                OutlinedButton(onClick = { choosingRun = true }) {
                    Text(stringResource(R.string.plugin_run, currentNumber))
                    Symbol("expand_more", size = 18)
                }
                DropdownMenu(choosingRun, { choosingRun = false }) {
                    worker.runs.indices.reversed().forEach { index ->
                        val option = worker.runs[index]
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.plugin_run, index + 1)) },
                            trailingIcon = { Text(pluginRunStatus(option.status)) },
                            onClick = {
                                selectedRun = option.id
                                choosingRun = false
                            },
                        )
                    }
                }
            }
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(status, Modifier.weight(1f), style = MaterialTheme.typography.labelLarge)
            ExportTranscriptButton(
                onClick = { onExportTranscript(worker.name, run.id) },
                enabled = canExportTranscript && run.status != "running",
            )
        }
        key(run.id) {
            TraceLog(
                steps,
                expandThoughts = true,
                modifier = Modifier.weight(1f).fillMaxWidth(),
                displayAssets = displayAssets,
                standalonePluginTrace = true,
            )
        }
    }
}

@Composable
private fun pluginRunStatus(status: String): String = when (status) {
    "running" -> stringResource(R.string.running)
    "finished" -> stringResource(R.string.plugin_run_finished)
    "failed" -> stringResource(R.string.plugin_run_failed)
    "cancelled" -> stringResource(R.string.plugin_run_cancelled)
    "interrupted" -> stringResource(R.string.plugin_run_interrupted)
    "archived" -> stringResource(R.string.plugin_run_archived)
    else -> status
}
