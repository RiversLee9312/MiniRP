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
import androidx.compose.ui.text.style.TextDirection
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

private sealed interface AgentPanelRoute {
    @Serializable data object Conversation : AgentPanelRoute

    @Serializable data class Subagents(val owner: String = "") : AgentPanelRoute

    @Serializable data class Trace(val owner: String, val workerId: String) : AgentPanelRoute
}

internal val TraceBlock.subagentRole: AgentRole?
    get() = when {
        role == AgentRole.Search -> AgentRole.Search
        role == AgentRole.Browser || role in setOf(AgentRole.World, AgentRole.Preset, AgentRole.Plugin) &&
            speaker in setOf("BrowserAgent", "FetchAgent") -> AgentRole.Browser
        pluginRun.isNotBlank() || pluginAgentId.isNotBlank() || agent == "PluginAgent" || role == AgentRole.Plugin -> AgentRole.Plugin
        else -> null
    }

private val TraceBlock.browserOwner: AgentRole
    get() = if (pluginRun.isNotBlank()) AgentRole.Plugin else
        AgentRole.entries.firstOrNull { it.id == parentAgent || it.agentName == parentAgent }
            ?: role.takeIf { it == AgentRole.World || it == AgentRole.Preset }
            ?: AgentRole.Chat

private data class SubagentRun(
    val id: String, val blocks: List<TraceBlock>, val status: String, val children: List<Subagent> = emptyList(),
)

private data class Subagent(val id: String, val role: AgentRole, val name: String, val runs: List<SubagentRun>) {
    val latest = runs.last()
    val running = latest.status == "running"
    val icon = when (role) {
        AgentRole.Search -> "search"
        AgentRole.Browser -> "language"
        else -> "smart_toy"
    }
}

private fun subagents(parent: AgentRole, blocks: List<TraceBlock>): List<Subagent> {
    val byRole = blocks.filter { it.subagentRole != null }.groupBy { it.subagentRole }
    val browser = byRole[AgentRole.Browser].orEmpty()
    return buildList {
        if (parent == AgentRole.Chat) {
            val search = byRole[AgentRole.Search].orEmpty()
            val nested = researchWorker(AgentRole.Browser, browser.filter { it.browserOwner == AgentRole.Search })
            researchWorker(AgentRole.Search, search, listOfNotNull(nested))?.let(::add)
        }
        researchWorker(AgentRole.Browser, browser.filter { it.browserOwner == parent })?.let(::add)
        if (parent == AgentRole.Preset) {
            val nested = browser.filter { it.browserOwner == AgentRole.Plugin }.groupBy { it.pluginRun }
            val pluginRows = blocks.filter { it.pluginRun.isNotBlank() }
            addAll(pluginRows.groupBy { it.pluginAgentId.ifBlank { it.pluginRun } }.map { (id, steps) ->
                Subagent(
                    id, AgentRole.Plugin,
                    steps.firstNotNullOfOrNull { it.pluginAgentName.takeIf(String::isNotBlank) }.orEmpty(),
                    steps.groupBy { it.pluginRun }.map { (run, trace) ->
                        val status = trace.firstNotNullOfOrNull { it.pluginStatus.takeIf(String::isNotBlank) }.orEmpty()
                        val children = researchWorker(AgentRole.Browser, nested[run].orEmpty())
                        SubagentRun(run, trace.filter { it.subagentRole != AgentRole.Browser }, status, listOfNotNull(children))
                    },
                )
            }.asReversed().sortedByDescending { it.running })
        }
    }
}

private fun researchWorker(role: AgentRole, trace: List<TraceBlock>, children: List<Subagent> = emptyList()): Subagent? {
    if (trace.isEmpty() && children.isEmpty()) return null
    val status = if (trace.any { it.running || it.streaming } || children.any { it.running }) "running" else ""
    return Subagent(role.id, role, "", listOf(SubagentRun(role.id, trace, status, children)))
}

internal fun subagentCount(parent: AgentRole, blocks: List<TraceBlock>): Int = subagents(parent, blocks).size

private fun List<Subagent>.at(owner: String): List<Subagent> = if (owner.isBlank()) this else
    owner.split('/').chunked(2).fold(this) { children, path ->
        children.find { it.id == path[0] }?.runs?.find { it.id == path.getOrNull(1) }?.children.orEmpty()
    }

@Composable
internal fun SubagentAction(count: Int, hideWhenEmpty: Boolean = false, onClick: () -> Unit) {
    if (hideWhenEmpty && count == 0) return
    val colors = MaterialTheme.colorScheme
    IconButton(onClick = onClick, modifier = Modifier.testTag("subagents-action")) {
        BadgedBox(badge = {
            if (count > 0) {
                Badge(
                    Modifier.wrapContentSize(unbounded = true).testTag("subagents-count"),
                    containerColor = colors.secondary,
                    contentColor = colors.onSecondary,
                ) {
                    Text(
                        text = if (count > 99) "99+" else count.toString(),
                        color = colors.onSecondary,
                        style = LocalTextStyle.current.copy(textDirection = TextDirection.Ltr),
                        softWrap = false,
                        maxLines = 1,
                    )
                }
            }
        }) {
            Symbol("smart_toy", stringResource(R.string.subagents))
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun AgentPanel(
    parent: AgentRole,
    blocks: List<TraceBlock>,
    displayAssets: DisplayAssets?,
    onClose: () -> Unit,
    locked: Boolean = false,
    canSwitchPreset: Boolean = true,
    canExportTranscript: Boolean = false,
    onExportTranscript: (String, String) -> Unit = { _, _ -> },
    onSettings: () -> Unit = {},
    library: @Composable (DrawerState) -> Unit = {},
    snackbarHost: @Composable () -> Unit = {},
    conversation: (@Composable (Modifier) -> Unit)? = null,
) {
    val workers by rememberUpdatedState(remember(parent, blocks) { subagents(parent, blocks) })
    val currentConversation by rememberUpdatedState(conversation)
    val currentAssets by rememberUpdatedState(displayAssets)
    val exportTranscript by rememberUpdatedState(onExportTranscript)
    val exportEnabled by rememberUpdatedState(canExportTranscript)
    val navigation = rememberNavController()
    val entry by navigation.currentBackStackEntryAsState()
    val activity = entry?.takeIf { it.destination.hasRoute<AgentPanelRoute.Subagents>() }
        ?.toRoute<AgentPanelRoute.Subagents>()
    val trace = entry?.takeIf { it.destination.hasRoute<AgentPanelRoute.Trace>() }
        ?.toRoute<AgentPanelRoute.Trace>()
    var runSelections by rememberSaveable { mutableStateOf(hashMapOf<String, String>()) }
    fun selectionKey(route: AgentPanelRoute.Trace) = route.owner + "/" + route.workerId
    fun selectedRun(worker: Subagent, route: AgentPanelRoute.Trace) =
        worker.runs.find { it.id == runSelections[selectionKey(route)] } ?: worker.latest
    val selected = trace?.let { item -> workers.at(item.owner).find { it.id == item.workerId } }
    val focus = LocalFocusManager.current
    val keyboard = LocalSoftwareKeyboardController.current
    fun openSubagents(owner: String = "") {
        focus.clearFocus()
        keyboard?.hide()
        navigation.navigate(AgentPanelRoute.Subagents(owner)) { launchSingleTop = true }
    }
    val title = when {
        selected != null -> selected.name.ifBlank { stringResource(selected.role.label) }
        activity != null || conversation == null -> stringResource(R.string.subagents)
        else -> stringResource(parent.label)
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
        onBack = back.takeIf { trace != null || activity != null && (conversation != null || activity.owner.isNotBlank()) },
        actions = {
            if (selected != null) {
                val run = selectedRun(selected, trace)
                SubagentAction(run.children.size, hideWhenEmpty = true) {
                    openSubagents(listOf(trace.owner, selected.id, run.id).filter(String::isNotBlank).joinToString("/"))
                }
            } else if (conversation != null && activity == null && trace == null) {
                SubagentAction(workers.size, hideWhenEmpty = true) { openSubagents() }
                ExportTranscriptButton({ onExportTranscript("", "") }, canExportTranscript && canSwitchPreset)
            }
        },
        draftControls = if (parent == AgentRole.Preset && conversation != null) DraftPresetControls(
            locked,
            canSwitchPreset,
            drawer,
            { library(drawer) },
            { scope.launch { drawer.open() } },
            onSettings,
        ) else null,
    ) {
        val start = if (conversation == null) AgentPanelRoute.Subagents() else AgentPanelRoute.Conversation
        PageNavHost(navigation, start, Modifier.weight(1f).fillMaxWidth()) {
            composable<AgentPanelRoute.Conversation> {
                Column(Modifier.fillMaxSize()) {
                    currentConversation?.invoke(Modifier.weight(1f).fillMaxWidth())
                }
            }
            composable<AgentPanelRoute.Subagents> { page ->
                val owner = page.toRoute<AgentPanelRoute.Subagents>().owner
                val children = workers.at(owner)
                LazyColumn(Modifier.fillMaxSize()) {
                    if (children.isEmpty()) item {
                        Text(stringResource(R.string.ensemble_no_trace), Modifier.padding(16.dp))
                    }
                    items(children, key = { it.id }, contentType = { it.role }) { worker ->
                        ListItem(
                            content = {
                                Text(
                                    worker.name.ifBlank { stringResource(worker.role.label) },
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            },
                            supportingContent = if (worker.latest.status.isNotBlank()) {
                                { Text(pluginRunStatus(worker.latest.status)) }
                            } else null,
                            leadingContent = { Symbol(worker.icon) },
                            trailingContent = { Symbol("chevron_right") },
                            colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                            modifier = Modifier.testTag("plugin-agent:${worker.id}").clickable {
                                navigation.navigate(AgentPanelRoute.Trace(owner, worker.id)) { launchSingleTop = true }
                            },
                        )
                        HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                    }
                }
            }
            composable<AgentPanelRoute.Trace> { page ->
                val route = page.toRoute<AgentPanelRoute.Trace>()
                workers.at(route.owner).find { it.id == route.workerId }?.let { worker ->
                    SubagentTrace(worker, selectedRun(worker, route), { run ->
                        runSelections = HashMap(runSelections).apply { put(selectionKey(route), run) }
                    }, currentAssets, exportEnabled, exportTranscript)
                }
            }
        }
        snackbarHost()
    }
}

@Composable
private fun SubagentTrace(
    worker: Subagent,
    run: SubagentRun,
    onSelectRun: (String) -> Unit,
    displayAssets: DisplayAssets?,
    canExportTranscript: Boolean,
    onExportTranscript: (String, String) -> Unit,
) {
    var choosingRun by remember { mutableStateOf(false) }
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
                                onSelectRun(option.id)
                                choosingRun = false
                            },
                        )
                    }
                }
            }
        }
        if (worker.role == AgentRole.Plugin) Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
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
                expandActivity = true,
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
