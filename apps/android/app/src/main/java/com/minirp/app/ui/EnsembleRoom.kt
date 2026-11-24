package com.minirp.app.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.core.text.BidiFormatter
import com.minirp.app.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

@Composable
internal fun EnsembleRoom(
    room: String,
    chats: List<EnsembleChat>,
    participants: List<Choice>,
    activity: List<EnsembleActivity>,
    privateInStory: Boolean,
    openChat: (String) -> Unit,
    openPrivate: (String) -> Unit,
) {
    var choosePrivate by rememberSaveable(room) { mutableStateOf(false) }
    val membersByScene = remember(activity, participants) {
        activity.groupBy(EnsembleActivity::conversation).mapValues { (_, members) ->
            orderedThinkingMembers(members, participants)
        }
    }
    Box(Modifier.fillMaxSize()) {
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(top = 8.dp, bottom = if (participants.isEmpty()) 8.dp else 92.dp),
        ) {
            item {
                Text(
                    stringResource(R.string.conversations),
                    modifier = Modifier.padding(start = 20.dp, top = 12.dp, bottom = 8.dp).semantics { heading() },
                    style = MaterialTheme.typography.titleMedium,
                )
            }
            items(chats, key = { it.id }, contentType = { "conversation" }) { chat ->
                ListItem(
                    content = { Text(chat.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = {
                        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(chat.members.joinToString(" · "), maxLines = 2, overflow = TextOverflow.Ellipsis)
                            EnsembleThinking(membersByScene[chat.id].orEmpty())
                        }
                    },
                    trailingContent = { Symbol("chevron_right") },
                    modifier = Modifier.clickable(role = Role.Button) { openChat(chat.id) },
                )
            }
        }
        if (participants.isNotEmpty()) {
            ExtendedFloatingActionButton(
                onClick = { choosePrivate = true },
                icon = { Symbol("reply") },
                text = { Text(stringResource(R.string.ensemble_private_messages)) },
                modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp).testTag("ensemble-private-fab"),
            )
        }
    }
    if (choosePrivate) {
        FullSheet(stringResource(R.string.ensemble_private_messages), { choosePrivate = false }) {
            if (!privateInStory) PrivateMessageNotice(Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
            LazyColumn(Modifier.weight(1f).fillMaxWidth(), contentPadding = PaddingValues(bottom = 16.dp)) {
                items(participants, key = { it.id }, contentType = { "member" }) { member ->
                    ListItem(
                        content = { Text(member.name, maxLines = 2, overflow = TextOverflow.Ellipsis) },
                        leadingContent = { Symbol("smart_toy") },
                        trailingContent = { Symbol("chevron_right") },
                        modifier = Modifier.testTag("ensemble-private-member:${member.id}")
                            .clickable(role = Role.Button) {
                                choosePrivate = false
                                openPrivate(member.id)
                            },
                    )
                }
            }
        }
    }
}

internal fun orderedThinkingMembers(activity: List<EnsembleActivity>, participants: List<Choice>): List<EnsembleActivity> {
    val actors = activity.associateBy(EnsembleActivity::participant)
    return participants.mapNotNull { member ->
        actors[member.id]?.let { it.copy(speaker = it.speaker.ifBlank { member.name }) }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun EnsembleThinking(activity: List<EnsembleActivity>, modifier: Modifier = Modifier) {
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val bidi = remember(rtl) { BidiFormatter.getInstance(rtl) }
    val motion = MaterialTheme.motionScheme
    var lastVisibleActivity by remember { mutableStateOf(activity) }
    SideEffect { if (activity.isNotEmpty()) lastVisibleActivity = activity }
    AnimatedVisibility(
        visible = activity.isNotEmpty(),
        modifier = modifier,
        enter = expandVertically(motion.fastSpatialSpec(), expandFrom = Alignment.Bottom) + fadeIn(motion.fastEffectsSpec()),
        exit = shrinkVertically(motion.fastSpatialSpec(), shrinkTowards = Alignment.Bottom) + fadeOut(motion.fastEffectsSpec()),
    ) {
        FlowRow(
            Modifier
                .fillMaxWidth()
                .heightIn(max = 96.dp)
                .verticalScroll(rememberScrollState())
                .testTag("ensemble-thinking")
                .semantics { liveRegion = LiveRegionMode.Polite },
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            activity.ifEmpty { lastVisibleActivity }.forEach { member ->
                key(member.participant) {
                    Surface(
                        shape = CircleShape,
                        color = MaterialTheme.colorScheme.secondaryContainer,
                        contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
                    ) {
                        Row(
                            Modifier.padding(horizontal = 10.dp, vertical = 5.dp),
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Spacer(Modifier.size(5.dp).background(MaterialTheme.colorScheme.secondary, CircleShape))
                            Text(
                                stringResource(R.string.ensemble_member_thinking, bidi.unicodeWrap(member.speaker)),
                                style = MaterialTheme.typography.labelMedium,
                                overflow = TextOverflow.Ellipsis,
                                maxLines = 1,
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
internal fun PrivateMessageNotice(modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.large,
        color = MaterialTheme.colorScheme.secondaryContainer,
        contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
    ) {
        Row(
            Modifier.padding(horizontal = 14.dp, vertical = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Symbol("info", size = 20)
            Text(stringResource(R.string.ensemble_out_of_story), style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun EnsembleMemberToolbar(
    title: String,
    back: () -> Unit,
    subtitle: String? = null,
    onTrace: (() -> Unit)? = null,
) {
    TopAppBar(
        title = {
            Column {
                Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (!subtitle.isNullOrBlank()) Text(
                    subtitle,
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        },
        navigationIcon = { IconButton(onClick = back) { Symbol("arrow_back", stringResource(R.string.back)) } },
        actions = {
            if (onTrace != null) IconButton(onClick = onTrace) {
                Symbol("visibility", stringResource(R.string.ensemble_member_trace))
            }
        },
    )
}

@Composable
internal fun EnsembleMemberTrace(
    load: suspend () -> List<EnsembleTraceScene>,
    activeScene: String?,
    displayAssets: (String) -> DisplayAssets?,
) {
    var scenes by remember { mutableStateOf<List<EnsembleTraceScene>?>(null) }
    var failed by remember { mutableStateOf(false) }
    var attempt by remember { mutableIntStateOf(0) }
    var selected by rememberSaveable { mutableStateOf("") }
    val newestLoad by rememberUpdatedState(load)
    LaunchedEffect(activeScene, attempt) {
        failed = false
        do {
            try {
                scenes = newestLoad()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                failed = true
                break
            }
            if (activeScene != null) delay(1100)
        } while (activeScene != null)
    }
    val snapshot = scenes
    when {
        failed && snapshot == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.ensemble_trace_load_failed))
                TextButton(onClick = { attempt++ }) { Text(stringResource(R.string.retry)) }
            }
        }
        snapshot == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        snapshot.isEmpty() -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(stringResource(R.string.ensemble_no_trace), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        else -> {
            val scene = snapshot.firstOrNull { it.id == selected }
                ?: snapshot.firstOrNull { it.id == activeScene }
                ?: snapshot.last()
            Column(Modifier.fillMaxSize()) {
                if (snapshot.size > 1) {
                    Row(
                        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        snapshot.forEach { item ->
                            FilterChip(
                                selected = item.id == scene.id,
                                onClick = { selected = item.id },
                                label = { Text(item.name.ifBlank { stringResource(R.string.chat) }) },
                            )
                        }
                    }
                }
                if (failed) {
                    TextButton(onClick = { attempt++ }) { Text(stringResource(R.string.retry)) }
                }
                key(scene.id) {
                    TraceLog(
                        scene.blocks,
                        expandThoughts = true,
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                        displayAssets = displayAssets(scene.id),
                        standalonePluginTrace = true,
                    )
                }
            }
        }
    }
}
