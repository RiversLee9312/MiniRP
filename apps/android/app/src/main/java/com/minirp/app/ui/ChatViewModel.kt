package com.minirp.app.ui

import android.app.Application
import android.content.Context
import android.net.Uri
import android.widget.FrameLayout
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.paging.InvalidatingPagingSourceFactory
import androidx.paging.Pager
import androidx.paging.PagingConfig
import androidx.paging.PagingData
import androidx.paging.cachedIn
import com.mikepenz.markdown.model.State as MarkdownState
import com.mikepenz.markdown.model.parseMarkdown
import com.minirp.app.MiniRPApplication
import com.minirp.app.R
import com.minirp.app.core.BrowserHost
import com.minirp.app.core.CoreClient
import com.minirp.app.core.TurnProtection
import com.minirp.app.data.AttachmentStore
import com.minirp.app.data.LibraryPagingSource
import com.minirp.app.data.Preferences
import java.util.UUID
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.lang.ref.WeakReference
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import org.json.JSONArray
import org.json.JSONObject

@OptIn(ExperimentalCoroutinesApi::class)
class ChatViewModel
internal constructor(
    application: Application,
    private val workspace: Context,
    private val preferences: Preferences,
    internal val turnProtection: TurnProtection? = null,
) : AndroidViewModel(application) {
    constructor(application: Application) : this(
        application, application, Preferences(application), (application as? MiniRPApplication)?.turnProtection,
    )

    internal fun authorizeBackgroundTurns(enabled: Boolean) {
        viewModelScope.launch { turnProtection?.authorize(enabled) }
    }

    private val mutableState = MutableStateFlow(ChatState())
    val state = mutableState.asStateFlow()
    private val attachments = AttachmentStore(workspace)
    private val browser = BrowserHost(application)
    private var previewWindow: WeakReference<FrameLayout>? = null
    internal fun setPreviewWindow(window: FrameLayout?) {
        previewWindow = window?.let { WeakReference(it) }
    }
    internal val picker = PluginPicker()
    private var openUiInstructions = ""
    private val openUiDrafts = android.util.LruCache<Pair<String, String>, String>(40)
    internal val actions =
        kotlinx.coroutines.channels.Channel<String>(kotlinx.coroutines.channels.Channel.BUFFERED)
    private val engine = CompletableDeferred<CoreClient>()
    @Volatile private var core: CoreClient? = null
    private var saveJob: Job? = null
    private var presetEditorToRestore: String? = null

    internal class LibraryFeed(
        val preset: String,
        val factory: InvalidatingPagingSourceFactory<Int, Choice>,
        scope: CoroutineScope,
    ) {
        private val job = SupervisorJob(scope.coroutineContext[Job])
        val pages: Flow<PagingData<Choice>> =
            Pager(
                    PagingConfig(pageSize = 30, enablePlaceholders = false),
                    pagingSourceFactory = factory,
                )
                .flow
                .cachedIn(CoroutineScope(scope.coroutineContext + job))

        fun close() = job.cancel()
    }

    private val presetFeed = library("", listPresets = true)
    val presets = presetFeed.pages
    private val chatFeed = MutableStateFlow(library(""))
    internal val chatLibrary = chatFeed.asStateFlow()

    private fun selectLibraryPreset(preset: String) {
        val previous = chatFeed.value
        if (previous.preset == preset) return
        chatFeed.value = library(preset)
        previous.close()
    }

    init {
        viewModelScope.launch {
            try {
                turnProtection?.initialize()
                val (profiles, options) = withContext(Dispatchers.IO) { preferences.load() }
                val providers = profiles.objects().map(::decodeProvider)
                val saved =
                    options.optJSONArray("models")?.objects()?.map(::decodeModel)
                        ?: providers
                            .filter { it.model.isNotBlank() }
                            .map {
                                SavedModel(
                                    it.id,
                                    Choice(it.model, it.model, it.contextWindow, it.maxOutput),
                                )
                            }
                val assignments =
                    AgentRole.entries
                        .mapNotNull { role ->
                            val ref = options.optJSONObject("assignments")?.optJSONObject(role.id)
                            val selection =
                                if (ref != null)
                                    saved.find {
                                        it.provider == ref.optString("provider") &&
                                            it.choice.id == ref.optString("id")
                                    }
                                else if (role == AgentRole.Chat)
                                    saved.find {
                                        it.provider == options.optString("provider")
                                    } ?: saved.firstOrNull()
                                else null
                            selection?.let { role to it }
                        }
                        .toMap()
                val savedPreset = options.optString("preset")
                mutableState.update {
                    it.copy(
                        providers = providers,
                        savedModels = saved,
                        assignments = assignments,
                        presetHomeHintSeen = options.optBoolean("preset_home_hint_seen"),
                        presetChatHintSeen = options.optBoolean("preset_chat_hint_seen"),
                        expandThoughts = options.optBoolean("cot"),
                        fetchMode = options.optString("fetch", "api_then_local"),
                        research = decodeResearchSettings(options),
                    )
                }
                val client =
                    withContext(Dispatchers.IO) {
                        openUiInstructions =
                            runCatching {
                                    application.assets
                                        .open("openui/prompt.txt")
                                        .bufferedReader()
                                        .use { it.readText() }
                                }
                                .getOrDefault("")
                        CoreClient(workspace, viewModelScope)
                    }
                client.browser = browser::execute
                client.picker = picker::request
                client.previewWindow = { previewWindow?.get() }
                core = client
                engine.complete(client)
                configure()
                client.events.collect { event ->
                    when (event.optString("kind")) {
                        "ready" -> {
                            val showOnboarding = !options.optBoolean(
                                "onboarding_complete",
                                providers.isNotEmpty() || savedPreset.isNotBlank() ||
                                    event.optString("preset").isNotBlank(),
                            )
                            receiveReady(event, showOnboarding)
                            val initial = event.optString("preset")
                            val preferred = savedPreset.ifBlank { initial }
                            if (savedPreset.isNotBlank() && options.optBoolean("preset_editor_open")) {
                                presetEditorToRestore = savedPreset
                            }
                            if (preferred != initial || event.optJSONObject("config").presetSetupPending()) {
                                command("home") {
                                    put("preset", preferred)
                                    put("restore", true)
                                    put("locale", workspace.resources.configuration.locales[0].toLanguageTag())
                                }
                            } else {
                                restorePresetEditor(initial)
                            }
                        }
                        "preset_opened" -> {
                            if (event.optBoolean("selected")) {
                                presetEditorToRestore = null
                                val blocks = withContext(Dispatchers.Default) {
                                    event.getJSONArray("blocks").objects().map { decodeBlock(it).finishMarkdown() }
                                }
                                showHome(event.getString("preset"), event.getJSONObject("config"), blocks, editPreset = true)
                            }
                        }
                        "loaded" -> {
                            val decoded =
                                withContext(Dispatchers.Default) {
                                    event
                                        .getJSONObject("chat")
                                        .optJSONArray("blocks")
                                        ?.objects()
                                        ?.map { decodeBlock(it).finishMarkdown() }
                                        .orEmpty()
                                        .withLegacyReplyActions()
                                }
                            receive(event, decoded)
                        }
                        "ensemble_chat_loaded" -> {
                            val decoded = withContext(Dispatchers.Default) {
                                val private = event.optString("private_participant").takeUnless { event.isNull("private_participant") }
                                event.getJSONArray("blocks").objects().asSequence()
                                    .filter { !private.isNullOrBlank() || it.isEnsembleMessage() }
                                    .map { decodeBlock(it).finishMarkdown() }
                                    .toList()
                            }
                            receive(event, decoded)
                        }
                        "turn_end" -> {
                            val block =
                                (if (event.has("conversation")) state.value.ensembleBlocks
                                    else state.value.blocks)
                                    .lastOrNull { it.id == event.optString("block") }
                            val finished =
                                withContext(Dispatchers.Default) { block?.finishMarkdown() }
                            receiveTrace(event, finished)
                        }
                        "generating" -> {
                            turnProtection?.turnChanged(
                                "conversation",
                                running = event.optBoolean("value"),
                                interrupted = event.optBoolean("interrupted"),
                                userInitiated = event.optBoolean("user_initiated"),
                            )
                            receive(event)
                        }
                        "plugin_agent_status" -> {
                            val run = event.optString("plugin_run")
                            val status = event.optString("status")
                            if (run.isNotBlank()) {
                                turnProtection?.turnChanged(
                                    run,
                                    running = status == "running",
                                    interrupted = status == "failed",
                                    userInitiated = false,
                                )
                            }
                            receive(event)
                        }
                        else -> receive(event)
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                engine.completeExceptionally(error)
                report(error)
            }
        }
    }

    private fun library(preset: String, listPresets: Boolean = false): LibraryFeed {
        val factory = InvalidatingPagingSourceFactory {
            LibraryPagingSource { offset, limit ->
                if (preset.isEmpty() && !listPresets) return@LibraryPagingSource emptyList()
                withContext(Dispatchers.Default) {
                    val result =
                        engine
                            .await()
                            .query(
                                JSONObject()
                                    .put("op", "library_page")
                                    .put("preset", preset)
                                    .put("offset", offset)
                                    .put("limit", limit)
                            )
                    result.getJSONArray("items").objects().map(::decodeChoice)
                }
            }
        }
        return LibraryFeed(preset, factory, viewModelScope)
    }

    private fun receiveReady(event: JSONObject, showOnboarding: Boolean) {
        val initial = event.optString("preset")
        val config = event.optJSONObject("config")
        selectLibraryPreset(initial)
        mutableState.update {
            it.copy(
                ready = true,
                showOnboarding = showOnboarding,
                preset = initial,
                presetSetupPending = config.presetSetupPending(),
                presetName = event.optString("preset_name", initial),
                appearance = config.presetAppearance(),
                chatMode = config?.optString("chat_mode", "character") ?: "character",
                chatGreeting = config.hasChatGreeting(),
                agenticSearch = config?.optBoolean("agentic_search", true) ?: true,
                catalog = event.optJSONArray("catalog")?.objects()?.map(::decodeProvider).orEmpty(),
            )
        }
        command("plugins_list")
        command("plugin_settings")
    }

    private fun receive(event: JSONObject, loadedBlocks: List<TraceBlock>? = null) {
        when (event.optString("kind")) {
            "library_changed" -> {
                val preset = event.optString("preset")
                if (preset.isBlank() || event.optBoolean("presets_changed"))
                    presetFeed.factory.invalidate()
                chatFeed.value.takeIf { it.preset == preset }?.factory?.invalidate()
            }
            "preset_removed" ->
                if (chatFeed.value.preset == event.optString("id"))
                    selectLibraryPreset(event.optString("fallback", state.value.preset))
            "home" -> {
                val preset = event.getString("preset")
                showHome(preset, event.getJSONObject("config"))
                restorePresetEditor(preset)
            }
            "plugin_action" -> actions.trySend(event.getString("url"))
            "plugin_settings" ->
                mutableState.update {
                    if (event.optString("preset") != it.preset) return@update it
                    it.copy(
                        pluginSettings =
                            event.getJSONArray("items").objects().map(::decodePluginSetting)
                    )
                }
            "plugins" ->
                mutableState.update {
                    it.copy(
                        installedPlugins = event.getJSONArray("items").objects().map(::decodePlugin)
                    )
                }
            "plugin_error" ->
                if (event.optString("plugin") != "base")
                    report(IllegalStateException(event.optString("message")))
            "generating" -> {
                val role =
                    AgentRole.entries.find {
                        event.optBoolean("value") && it.id == event.optString("agent")
                    }
                mutableState.update { it.copy(generating = role) }
            }
            "room_loading" -> {
                val messages = event.optJSONArray("messages")?.let { List(it.length(), it::getString) }.orEmpty()
                val loading = if (event.optBoolean("value")) RoomLoading(
                    event.getString("room"),
                    event.getInt("ready"),
                    event.getInt("total"),
                    event.getDouble("progress").toFloat(),
                    messages,
                ) else null
                mutableState.update { it.copy(roomLoading = loading) }
            }
            "loaded" -> {
                val chat = event.getJSONObject("chat")
                val preset = event.getString("preset")
                val changedPreset = preset != state.value.preset
                val config = event.optJSONObject("config")
                selectLibraryPreset(preset)
                mutableState.update {
                    it.copy(
                        preset = preset,
                        presetSetupPending = config.presetSetupPending(),
                        presetName = config?.optString("name", preset) ?: preset,
                        appearance = config.presetAppearance(),
                        chatName = chat.optString("name"),
                        chatId = chat.optString("id"),
                        conversationViewKey =
                            if (event.optBoolean("from_home_send")) it.conversationViewKey
                            else chat.optString("id"),
                        blocks = loadedBlocks.orEmpty(),
                        ensembleBlocks = emptyList(),
                        ensembleChatId = "",
                        ensemblePrivateParticipant = "",
                        ensemblePrivateInStory = event.optBoolean("private_in_story", true),
                        ensembleActivity = event.optJSONArray("activity")?.objects()?.mapNotNull(::decodeEnsembleActivity).orEmpty(),
                        ensembleChats =
                            event
                                .optJSONArray("ensemble_chats")
                                ?.objects()
                                ?.map(::decodeEnsembleChat),
                        participants =
                            event
                                .optJSONArray("participants")
                                ?.objects()
                                ?.map(::decodeChoice)
                                .orEmpty(),
                        chatMode = config?.optString("chat_mode", "character") ?: "character",
                        chatGreeting = config.hasChatGreeting(),
                        branchDraft = event.optJSONObject("draft")?.branchDraft(),
                        replyError = null,
                        agenticSearch = config?.optBoolean("agentic_search", true) ?: true,
                    )
                }
                if (changedPreset) command("plugin_settings")
                persist()
            }
            "title" ->
                mutableState.update {
                    if (it.chatId == event.optString("id"))
                        it.copy(chatName = event.optString("name"))
                    else it
                }
            "preset_config" ->
                mutableState.update {
                    val config = event.getJSONObject("config")
                    it.copy(
                        presetName = config.optString("name", it.presetName),
                        presetSetupPending = config.presetSetupPending(),
                        appearance = config.presetAppearance(),
                        agenticSearch = config.optBoolean("agentic_search", true),
                        chatMode = config.optString("chat_mode", "character"),
                        chatGreeting = config.hasChatGreeting(),
                    )
                }
            "busy" ->
                mutableState.update {
                    val busy = event.optBoolean("value")
                    it.copy(
                        busy = busy,
                        blocks =
                            if (busy) it.blocks
                            else
                                it.blocks.map { block ->
                                    if (block.streaming) block.copy(streaming = false) else block
                                },
                    )
                }
            "error" ->
                mutableState.update {
                    val conversation = event.optString("conversation")
                    if (conversation.isNotEmpty() &&
                        (it.chatId != event.optString("chat") || it.ensembleChatId != conversation)
                    ) return@update it
                    val message = event.optString("message")
                    val agent =
                        it.generating
                            ?: AgentRole.entries.find { role ->
                                role.id == event.optString("agent")
                            }
                    if (agent != null && (it.generating != null || event.optBoolean("model_turn")))
                        it.copy(replyError = ReplyError(agent, message, event.optBoolean("preset_interview")))
                    else it.copy(error = message)
                }
            "models",
            "models_loading",
            "models_error" -> receiveModels(event)
            "ensemble_chat_loaded" ->
                mutableState.update {
                    if (it.chatId != event.optString("chat") || it.preset != event.optString("preset")) it
                    else {
                        val conversation = event.optString("conversation")
                        val selectedActivity = event.optJSONArray("activity")?.objects()?.mapNotNull(::decodeEnsembleActivity)
                        it.copy(
                            ensembleChatId = conversation,
                            ensembleBlocks = loadedBlocks.orEmpty(),
                            ensemblePrivateParticipant = event.optString("private_participant")
                                .takeUnless { event.isNull("private_participant") }.orEmpty(),
                            ensemblePrivateInStory = event.optBoolean("private_in_story", it.ensemblePrivateInStory),
                            ensembleActivity = if (conversation.isBlank() || selectedActivity == null) it.ensembleActivity
                                else it.ensembleActivity.filterNot { active -> active.conversation == conversation } + selectedActivity,
                        )
                    }
                }
            "ensemble_activity" ->
                mutableState.update {
                    if (it.chatId != event.optString("chat") || it.preset != event.optString("preset")) return@update it
                    val activity = decodeEnsembleActivity(event) ?: return@update it
                    val others = it.ensembleActivity.filterNot { existing ->
                        existing.conversation == activity.conversation && existing.participant == activity.participant
                    }
                    it.copy(ensembleActivity = if (event.optBoolean("running")) others + activity else others)
                }
            "ensemble_chats" ->
                mutableState.update {
                    if (it.chatId != event.optString("chat")) it
                    else
                        it.copy(
                            ensembleChats =
                                event.getJSONArray("items").objects().map(::decodeEnsembleChat)
                        )
                }
            "steer_queued",
            "ensemble_message" ->
                mutableState.update {
                    val conversation = event.optString("conversation")
                    val preset = event.optString("preset")
                    when {
                        it.chatId != event.optString("chat") || (preset.isNotEmpty() && it.preset != preset) -> it
                        conversation.isEmpty() -> it.copy(
                            blocks = it.blocks.withMessage(decodeBlock(event.getJSONObject("item")))
                        )
                        conversation == it.ensembleChatId -> it.copy(
                            ensembleBlocks =
                                it.ensembleBlocks.withMessage(decodeBlock(event.getJSONObject("item")))
                        )
                        else -> it
                    }
                }
            "reply_end" -> mutableState.update { it.copy(blocks = it.blocks + decodeBlock(event)) }
            "user",
            "system" ->
                mutableState.update {
                    it.copy(
                        blocks =
                            it.blocks.withMessage(
                                TraceBlock(
                                    event.getString("block"),
                                    event.getString("agent"),
                                    user = true,
                                    content = event.optString("text"),
                                    version = event.optString("version"),
                                    attachmentNames = event.attachmentNames(),
                                    system = event.optString("kind") == "system",
                                    thumbnails = event.thumbnails(),
                                    quotes = event.quoteAttachments(),
                                )
                            )
                    )
                }
            "delta",
            "open_ui_delta",
            "open_ui_end",
            "open_ui_card",
            "open_ui_card_update",
            "context_trace",
            "plugin_agent_status",
            "tool_start",
            "tool_end",
            "tool_trace_update" -> receiveTrace(event)
        }
    }

    private fun List<TraceBlock>.withMessage(block: TraceBlock): List<TraceBlock> {
        val index = indexOfLast { it.id == block.id }
        return if (index < 0) this + block else toMutableList().apply { set(index, block) }
    }

    private var presetOpeningTrace: MutableList<JSONObject>? = null

    private fun receiveTrace(event: JSONObject, finished: TraceBlock? = null) {
        if (event.has("plugin_sequence")) presetOpeningTrace?.add(event)
        applyTrace(event, finished)
    }

    private fun applyTrace(event: JSONObject, finished: TraceBlock? = null) =
        mutableState.update { current ->
            val kind = event.optString("kind")
            val refresh = kind == "tool_trace_update"
            if (event.has("plugin_agent") && event.optString("preset") != current.preset)
                return@update current
            if (refresh) {
                val preset = event.optString("preset").takeUnless { event.isNull("preset") }.orEmpty()
                val room = event.optString("chat").takeUnless { event.isNull("chat") }.orEmpty()
                if (preset.isNotBlank() && preset != current.preset || room.isNotBlank() && room != current.chatId)
                    return@update current
            }
            val id = event.getString("block")
            val inRoom =
                !event.isNull("conversation") && event.optString("conversation").isNotBlank()
            if (
                inRoom &&
                    (event.optString("conversation") != current.ensembleChatId ||
                        event.optString("chat") != current.chatId ||
                        current.ensemblePrivateParticipant.isBlank())
            )
                return@update current
            val blocks = if (inRoom) current.ensembleBlocks else current.blocks
            val index = blocks.indexOfLast { it.id == id }
            if (
                index < 0 &&
                    kind in setOf("turn_end", "open_ui_end", "open_ui_card_update", "tool_trace_update")
            )
                return@update current
            val old =
                blocks.getOrNull(index)
                    ?: TraceBlock(
                        id,
                        event.optString("agent"),
                        speaker = event.optString("speaker"),
                        pluginAgentId = event.optString("plugin_agent"),
                        pluginRun = event.optString("plugin_run", event.optString("plugin_agent")),
                        pluginAgentName = event.optString("plugin_agent_name"),
                    )
            val sequence = event.optInt("plugin_sequence")
            // Refresh events can repeat the sequence of the recorded tool call.
            if (!refresh && sequence > 0 && sequence <= old.pluginSequence) return@update current
            val changed =
                when (kind) {
                    "turn_end" -> finished ?: old.copy(streaming = false)
                    "open_ui_delta" ->
                        old.copy(
                            openUi = old.openUi.orEmpty() + event.optString("text"),
                            streaming = true,
                        )
                    "open_ui_end" -> old.copy(streaming = false)
                    "plugin_agent_status" -> old.copy(
                        pluginStatus = event.optString("status"),
                        running = event.optString("status") == "running",
                        content = event.optString("text"),
                    )
                    "open_ui_card",
                    "open_ui_card_update" ->
                        old.copy(
                            openUi =
                                event.optString("open_ui").takeUnless {
                                    event.isNull("open_ui") || it.isBlank()
                                },
                            openUiTitle = event.optString("open_ui_title"),
                            openUiState = event.optJSONObject("open_ui_state")?.toString(),
                            openUiRevision = event.optInt("open_ui_revision"),
                            openUiHostAction = event.optString("open_ui_host_action", old.openUiHostAction),
                        )
                    "context_trace" ->
                        old.copy(compaction = true, result = event.optString("text"), running = event.optBoolean("running"))
                    "tool_start" ->
                        old.copy(
                            tool = event.optString("name"),
                            arguments = event.optString("arguments"),
                            running = true,
                        ).withToolTraceMetadata(event)
                    "tool_end" ->
                        old.copy(
                            tool = event.optString("name"),
                            result = event.optString("text"),
                            running = false,
                        ).withToolTraceMetadata(event)
                    "tool_trace_update" -> old.withToolTraceMetadata(event)
                    else ->
                        if (event.optString("type") == "reasoning")
                            old.copy(
                                reasoning = old.reasoning + event.optString("text"),
                                streaming = true,
                            )
                        else
                            old.copy(
                                content = old.content + event.optString("text"),
                                streaming = true,
                            )
                }.let { block ->
                    if (!refresh && sequence > 0) block.copy(pluginSequence = sequence) else block
                }
            if (changed === old) return@update current
            val updated =
                if (index < 0) blocks + changed
                else blocks.toMutableList().apply { set(index, changed) }
            val settled = if (changed.pluginStatus.isNotBlank() && changed.pluginStatus != "running") {
                updated.map { block ->
                    if (block.pluginRun == changed.pluginRun && (block.running || block.streaming))
                        block.copy(running = false, streaming = false)
                    else block
                }
            } else updated
            if (inRoom) current.copy(ensembleBlocks = settled) else current.copy(blocks = settled)
        }

    private fun TraceBlock.finishMarkdown() =
        if (!user && content.isNotBlank())
            copy(streaming = false, markdown = parseMarkdown(content) as? MarkdownState.Success)
        else if (streaming) copy(streaming = false) else this

    private fun receiveModels(event: JSONObject) {
        val id = event.optString("provider")
        val before = state.value.savedModels
        mutableState.update { current ->
            val catalog = current.modelCatalogs[id] ?: return@update current
            if (catalog.request != event.optString("request")) return@update current
            val changed =
                when (event.optString("kind")) {
                    "models" ->
                        catalog.copy(
                            choices =
                                event
                                    .getJSONArray("items")
                                    .objects()
                                    .map(::decodeChoice)
                                    .distinctBy { it.id },
                            error = null,
                        )
                    "models_error" -> catalog.copy(error = event.optString("message"))
                    else -> catalog.copy(loading = event.optBoolean("value"))
                }
            val discovered = changed.choices.associateBy { it.id }
            val ollama = current.providers.find { it.id == id }?.protocol == "ollama"
            fun refresh(model: SavedModel): SavedModel {
                if (model.provider != id) return model
                val choice = discovered[model.choice.id] ?: return model
                return model.copy(
                    choice = choice.copy(
                        contextWindow = choice.contextWindow.takeIf { it > 0 } ?: model.choice.contextWindow,
                        maxOutput = choice.maxOutput.takeIf { it > 0 } ?: model.choice.maxOutput,
                        effortLevels = if (ollama && choice.effortLevels.isEmpty()) model.choice.effortLevels else choice.effortLevels,
                    ),
                    reasoningEffort = model.reasoningEffort.takeIf {
                        ollama || it in choice.effortLevels
                    }.orEmpty(),
                )
            }
            current.copy(
                modelCatalogs = current.modelCatalogs + (id to changed),
                savedModels = current.savedModels.map(::refresh),
                assignments = current.assignments.mapValues { refresh(it.value) },
            )
        }
        if (state.value.savedModels != before) persist()
    }

    suspend fun send(
        text: String,
        documents: List<String>,
        role: AgentRole,
        system: Boolean = false,
        pluginAttachment: PluginAttachmentType? = null,
        openUiAction: JSONObject? = null,
        quotes: List<QuoteAttachment> = emptyList(),
    ): Boolean {
        val current = state.value
        if (!current.ready || current.preparing || current.preset.isBlank()) return false
        if (
            role == AgentRole.Chat &&
                current.ensembleChats != null &&
                current.ensembleChatId.isBlank()
        )
            return false
        if (openUiAction != null && current.busy) return false
        val privateMessage = role == AgentRole.Chat && current.ensemblePrivateParticipant.isNotBlank()
        if (privateMessage && system == current.ensemblePrivateInStory) return false
        if (current.generating == role && !privateMessage) {
            if (text.isBlank() || documents.isNotEmpty() || quotes.isNotEmpty()) return false
            return try {
                engine
                    .await()
                    .query(
                        JSONObject()
                            .put("op", "steer")
                            .put("chat", current.chatId)
                            .put("conversation", current.ensembleChatId)
                            .put("agent", role.id)
                            .put("locale", appLocale())
                            .put("role", if (system) "system" else "user")
                            .put("text", text)
                    )
                true
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                report(failure)
                false
            }
        }
        if (current.busy) return false
        if (system && (text.isBlank() || documents.isNotEmpty() || quotes.isNotEmpty())) return false
        if (quotes.size + documents.size > maxMessageAttachments || quotes.any {
                it.text.isBlank() || it.text.length > maxQuoteLength
            }) {
            mutableState.update { it.copy(error = getApplication<Application>().getString(R.string.attachment_too_large)) }
            return false
        }
        val provider = current.providers.find { it.id == current.assigned(role)?.provider }
        if (provider == null) {
            mutableState.update {
                it.copy(
                    error = getApplication<Application>().getString(R.string.no_provider_configured)
                )
            }
            return false
        }
        mutableState.update { it.copy(submitting = role, replyError = null) }
        try {
            val draft = current.branchDraft.takeIf { role == AgentRole.Chat }
            val imported =
                attachments.import(
                    documents,
                    pluginAttachment,
                    draft?.attachments.orEmpty(),
                )
            quotes.forEach { imported.put(it.toJson()) }
            val action = openUiAction ?: draft?.takeIf { !system && it.text == text }?.openUiAction
            currentCoroutineContext().ensureActive()
            val result =
                engine
                    .await()
                    .query(
                        JSONObject()
                            .put("op", "message")
                            .put("chat", current.chatId)
                            .put("conversation", current.ensembleChatId)
                            .put("preset", current.preset)
                            .put("agent", role.id)
                            .put("locale", appLocale())
                            .put("role", if (system) "system" else "user")
                            .put("text", text)
                            .put("editor_turn", role == AgentRole.Preset)
                            .put("open_ui_action", action)
                            .put("attachments", imported),
                        timeoutMillis = null,
                    )
            val accepted = result.optString("kind") == "message_accepted"
            if (accepted && draft != null) mutableState.update { it.copy(branchDraft = null) }
            return accepted
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            report(error)
            return false
        } finally {
            mutableState.update { it.copy(submitting = null) }
        }
    }

    internal suspend fun sendFromPanel(role: AgentRole, draft: ComposerSubmission): Boolean =
        viewModelScope.async { send(draft, role) }.await()

    internal suspend fun send(draft: ComposerSubmission, role: AgentRole): Boolean =
        send(draft.text, draft.files, role, draft.system, draft.pluginAttachment, quotes = draft.quotes)

    internal fun openUiDraft(chatId: String, id: String): String =
        openUiDrafts.get(chatId to id).orEmpty()

    internal fun saveOpenUiDraft(chatId: String, id: String, value: String) {
        if (value.length <= 32768) openUiDrafts.put(chatId to id, value)
    }

    internal fun respondToOpenUi(
        chatId: String,
        id: String,
        text: String,
        data: JSONObject,
    ): Boolean {
        val current = state.value
        if (
            current.chatId != chatId ||
                current.busy ||
                current.preparing ||
                !(if (current.ensembleChats == null) current.blocks else current.ensembleBlocks)
                    .any {
                        it.id == id && it.openUi != null
                    } ||
                text.length !in 1..2000 ||
                data.toString().length > 16384
        )
            return false
        viewModelScope.launch {
            send(
                text,
                emptyList(),
                AgentRole.Chat,
                openUiAction = JSONObject().put("block", id).put("data", data),
            )
        }
        return true
    }

    fun chooseChat(preset: String, id: String = "") =
        command(if (id.isEmpty()) "home" else "select_chat") {
            put("preset", preset)
            put("id", id)
            put("locale", workspace.resources.configuration.locales[0].toLanguageTag())
        }

    fun createEnsembleRoom(preset: String = state.value.preset) =
        command("select_chat") {
            put("preset", preset)
            put("locale", workspace.resources.configuration.locales[0].toLanguageTag())
        }

    fun createChat(preset: String = state.value.preset) =
        command("start_chat") {
            put("preset", preset)
            put("locale", workspace.resources.configuration.locales[0].toLanguageTag())
        }

    private fun greetActiveChat(current: ChatState = state.value) {
        if (!current.chatGreeting || current.chatId.isBlank() || current.ensembleChats != null) return
        command("greet_chat") {
            put("chat", current.chatId)
        }
    }

    fun leaveEnsembleChat() {
        val current = state.value
        if (current.ensembleChats == null) return
        if (current.ensembleChatId.isNotBlank())
            mutableState.update { it.copy(ensembleChatId = "", ensembleBlocks = emptyList(), ensemblePrivateParticipant = "") }
        command("select_ensemble_chat") {
            put("preset", current.preset)
            put("chat", current.chatId)
            put("conversation", "")
        }
    }

    suspend fun openEnsembleChat(room: String, conversation: String, privateParticipant: String = "") {
        val current = state.value
        if (current.chatId != room) return
        try {
            // Consume each snapshot and subsequent posts from the same event stream.
            val selection = JSONObject().put("preset", current.preset).put("chat", room)
            if (privateParticipant.isBlank()) selection
                .put("op", "select_ensemble_chat").put("conversation", conversation)
            else selection.put("op", "open_ensemble_dm").put("participant", privateParticipant)
            engine.await().send(
                selection
            )
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            report(error)
        }
    }

    internal suspend fun loadEnsembleTrace(room: String, participant: String): List<EnsembleTraceScene> {
        val current = state.value
        check(current.chatId == room)
        val response = engine.await().query(
            JSONObject().put("op", "ensemble_member_trace")
                .put("preset", current.preset).put("chat", room).put("participant", participant)
        )
        return withContext(Dispatchers.Default) {
            response.optJSONArray("blocks")?.objects().orEmpty()
                .groupBy { it.optString("conversation") }
                .map { (id, blocks) ->
                    EnsembleTraceScene(id, blocks.first().optString("scene_name"), blocks.map { decodeBlock(it).finishMarkdown() })
                }
        }
    }

    fun selectPreset(preset: String) {
        val current = state.value
        if (current.chatId.isNotBlank() || current.preset != preset || current.presetSetupPending) chooseChat(preset)
    }

    private fun restorePresetEditor(preset: String) {
        if (presetEditorToRestore == null) return
        val reopen = preset == presetEditorToRestore
        presetEditorToRestore = null
        if (reopen) openPresetEditor()
    }

    private fun showHome(
        preset: String,
        config: JSONObject,
        blocks: List<TraceBlock> = emptyList(),
        editPreset: Boolean = false,
    ) {
        selectLibraryPreset(preset)
        val changedPreset = preset != state.value.preset
        mutableState.update {
            val keepDraft =
                changedPreset && it.chatId.isBlank() && preset.isNotBlank() && !editPreset
            it.copy(
                chatId = "",
                conversationViewKey =
                    if (keepDraft) it.conversationViewKey else "home:${UUID.randomUUID()}",
                chatName = "",
                blocks = blocks,
                ensembleChats = null,
                participants = emptyList(),
                ensembleChatId = "",
                ensembleBlocks = emptyList(),
                ensembleActivity = emptyList(),
                ensemblePrivateParticipant = "",
                ensemblePrivateInStory = true,
                chatMode = config.optString("chat_mode", "character"),
                chatGreeting = config.hasChatGreeting(),
                branchDraft = null,
                replyError = null,
                preset = preset,
                presetSetupPending = config.presetSetupPending(),
                presetName = config.optString("name", preset),
                appearance = config.presetAppearance(),
                presetEditor = if (editPreset) PresetEditorPhase.Open else PresetEditorPhase.Closed,
                agenticSearch = config.optBoolean("agentic_search", true),
            )
        }
        if (changedPreset) command("plugin_settings")
        persist()
    }

    fun openPresetEditor(create: Boolean = false) {
        val current = state.value
        if (!current.ready || current.busy || current.preparing || (!create && current.presetEditorOpen)) return
        if (!create && current.preset.isBlank()) return
        mutableState.update { it.copy(presetEditor = PresetEditorPhase.Opening) }
        viewModelScope.launch {
            val trace = mutableListOf<JSONObject>()
            presetOpeningTrace = trace
            try {
                val command =
                    if (create)
                        JSONObject()
                            .put("op", "create_preset")
                            .put("open", true)
                            .put(
                                "name",
                                getApplication<Application>().getString(R.string.create_preset),
                            )
                    else
                        JSONObject()
                            .put("op", "open_preset")
                            .put("preset", current.preset)
                            .put("chat", current.chatId)
                val locale = getApplication<Application>().resources.configuration.locales[0]
                val result = engine.await().query(command.put("locale", locale.toLanguageTag()))
                val preset = result.getString("preset")
                val config = result.getJSONObject("config")
                val blocks =
                    withContext(Dispatchers.Default) {
                        result.getJSONArray("blocks").objects().map {
                            decodeBlock(it).finishMarkdown()
                        }
                    }
                if (result.optBoolean("created")) {
                    showHome(
                        preset,
                        config,
                        blocks,
                        editPreset = state.value.presetEditor == PresetEditorPhase.Opening,
                    )
                } else {
                    mutableState.update {
                        if (it.presetEditor != PresetEditorPhase.Opening) return@update it
                        it.copy(
                            blocks =
                                it.blocks.filterNot { block -> block.role == AgentRole.Preset } +
                                    blocks,
                            replyError =
                                it.replyError?.takeUnless { error ->
                                    error.agent == AgentRole.Preset
                                },
                            presetName = config.optString("name", preset),
                            presetSetupPending = config.presetSetupPending(),
                            appearance = config.presetAppearance(),
                            presetEditor = PresetEditorPhase.Open,
                            agenticSearch = config.optBoolean("agentic_search", true),
                        )
                    }
                }
                if (state.value.presetEditorOpen) {
                    trace.forEach { applyTrace(it) }
                    if (!result.optBoolean("created")) persist()
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                report(error)
            } finally {
                if (presetOpeningTrace === trace) presetOpeningTrace = null
                mutableState.update {
                    if (it.presetEditor == PresetEditorPhase.Opening)
                        it.copy(presetEditor = current.presetEditor)
                    else it
                }
            }
        }
    }

    fun closePresetEditor(): Boolean {
        if (state.value.presetExitLocked) return false
        mutableState.update { it.copy(presetEditor = PresetEditorPhase.Closed) }
        persist()
        return true
    }

    internal fun retryPresetInterview() {
        val current = state.value
        if (!current.presetEditorOpen || !current.presetSetupPending || current.presetWorking ||
            current.replyError?.presetInterview != true) return
        mutableState.update { it.copy(replyError = null) }
        resumePresetInterview(current.preset)
    }

    private fun resumePresetInterview(preset: String) = command("resume_preset_interview") {
        put("preset", preset)
        put("locale", workspace.resources.configuration.locales[0].toLanguageTag())
    }

    internal fun returnHomeFromPresetCard(preset: String, blockId: String): PresetHomeResult {
        val current = state.value
        if (current.preset != preset || !current.presetEditorOpen ||
            current.blocks.none { it.id == blockId && it.openUiHostAction == PresetHomeAction }) {
            return PresetHomeResult.Unavailable
        }
        if (current.presetSetupPending) return PresetHomeResult.SetupPending
        if (!closePresetEditor()) return PresetHomeResult.Working
        chooseChat(preset)
        return PresetHomeResult.Returned
    }

    fun markPresetHintSeen(inChat: Boolean) {
        val current = state.value
        if (if (inChat) current.presetChatHintSeen else current.presetHomeHintSeen) return
        mutableState.update {
            if (inChat) it.copy(presetChatHintSeen = true) else it.copy(presetHomeHintSeen = true)
        }
        persist()
    }

    private fun JSONObject?.presetSetupPending(): Boolean = this?.optString("setup_status") == "draft"

    fun deleteChoice(preset: String, id: String, isPreset: Boolean) {
        command(if (isPreset) "delete_preset" else "delete_chat") {
            put("id", id)
            put("preset", preset)
        }
    }

    fun updateSetting(setting: PluginSetting, value: Any) {
        when {
            setting.binding == "thoughts" -> {
                thoughts(value == true)
                return
            }
            setting.binding == "fetch" -> {
                fetchMode(value.toString())
                return
            }
        }
        viewModelScope.launch {
            try {
                val result =
                    engine
                        .await()
                        .query(
                            JSONObject()
                                .put("op", "plugin_setting")
                                .put("id", setting.plugin)
                                .put("key", setting.key)
                                .put("value", value)
                        )
                receive(result)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                report(failure)
            }
        }
    }

    internal suspend fun inspectPlugin(file: java.io.File) =
        decodePlugin(
            engine
                .await()
                .query(JSONObject().put("op", "plugin_inspect").put("path", file.path).put("locale", appLocale()))
                .getJSONObject("item")
        )

    internal suspend fun pluginPage(id: String, key: String): PluginSetting =
        decodePluginSetting(
            engine
                .await()
                .query(JSONObject().put("op", "plugin_page").put("id", id).put("key", key).put("locale", appLocale()))
                .getJSONObject("item")
        )

    internal suspend fun installPlugin(file: java.io.File) {
        engine.await().query(JSONObject().put("op", "plugin_install").put("path", file.path).put("locale", appLocale()))
        command("plugins_list")
        command("plugin_settings")
    }

    private fun appLocale(): String =
        getApplication<Application>().resources.configuration.locales[0].toLanguageTag()

    internal suspend fun refreshPluginLocale(locale: String) {
        try {
            engine.await().send(JSONObject().put("op", "plugin_locale").put("locale", locale))
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            report(failure)
        }
    }

    internal fun exportArchive(uri: Uri, request: JSONObject) = transferArchive { file ->
        engine.await().query(request.put("op", "archive_export").put("path", file.path), timeoutMillis = null)
        withContext(Dispatchers.IO) {
            val resolver = getApplication<Application>().contentResolver
            checkNotNull(resolver.openOutputStream(uri, "wt")) { "Could not open the export destination." }.use { output ->
                file.inputStream().use { input -> copyArchive(input, output, Long.MAX_VALUE) }
            }
        }
        mutableState.update { it.copy(archiveSaved = true) }
    }

    internal fun importArchive(uri: Uri, type: String) = transferArchive { file ->
        withContext(Dispatchers.IO) {
            val resolver = getApplication<Application>().contentResolver
            checkNotNull(resolver.openInputStream(uri)) { "Could not open the archive." }.use { input ->
                file.outputStream().use { output -> copyArchive(input, output, 9L * 1024 * 1024 * 1024) }
            }
        }
        engine.await().query(JSONObject().put("op", "archive_import").put("type", type).put("path", file.path), timeoutMillis = null)
    }

    private fun transferArchive(operation: suspend (File) -> Unit) {
        if (state.value.archiveBusy) return
        mutableState.update { it.copy(archiveBusy = true) }
        viewModelScope.launch {
            val file = getApplication<Application>().cacheDir.resolve("transfer-${UUID.randomUUID()}")
            try {
                operation(file)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                report(failure)
            } finally {
                file.delete()
                mutableState.update { it.copy(archiveBusy = false) }
            }
        }
    }

    private suspend fun copyArchive(input: InputStream, output: OutputStream, limit: Long) {
        val buffer = ByteArray(65536)
        var size = 0L
        while (true) {
            currentCoroutineContext().ensureActive()
            val count = input.read(buffer)
            if (count < 0) return
            size += count
            check(size <= limit) { "The workspace archive exceeds its size limit." }
            output.write(buffer, 0, count)
        }
    }

    internal fun clearArchiveSaved() = mutableState.update { it.copy(archiveSaved = false) }

    internal fun removePlugin(id: String) =
        changePlugin(JSONObject().put("op", "plugin_uninstall").put("id", id))

    internal fun setPluginEnabled(id: String, enabled: Boolean) =
        changePlugin(JSONObject().put("op", "plugin_enabled").put("id", id).put("enabled", enabled))

    private fun changePlugin(command: JSONObject) = viewModelScope.launch {
        try {
            receive(engine.await().query(command))
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            pluginFailure(failure)
        }
    }

    internal suspend fun setPluginScope(id: String, scope: PluginScope) {
        val result =
            engine
                .await()
                .query(
                    JSONObject()
                        .put("op", "plugin_scope")
                        .put("id", id)
                        .put("scope", scope.encode())
                )
        receive(result)
    }

    internal fun openSettingLink(setting: PluginSetting) =
        command("plugin_action") {
            put("id", setting.plugin)
            put("key", setting.key)
        }

    internal suspend fun settingMarkdown(url: String): String =
        engine.await().readAsset(url, 256 * 1024).decodeToString(throwOnInvalidSequence = true)

    internal suspend fun settingIcon(url: String): ByteArray =
        engine.await().readAsset(url, 256 * 1024)

    internal suspend fun thumbnail(role: AgentRole, preview: AttachmentThumbnail): ByteArray =
        engine.await().readAsset(preview.url, 1024 * 1024, role.id, preview.attachment)

    internal suspend fun savePresetBackground(preset: String, url: String, destination: File): String =
        engine.await().savePresetBackground(preset, url, destination)

    internal suspend fun displayAssetInfo(request: JSONObject) = engine.await().displayAssetInfo(request)

    internal suspend fun saveDisplayAsset(request: JSONObject, output: java.io.OutputStream) =
        engine.await().saveDisplayAsset(request, output)

    internal suspend fun openDisplayMedia(request: JSONObject, accepts: (String) -> Boolean) =
        engine.await().openDisplayMedia(request, accepts)

    internal fun runPlugin(id: String) = command("plugin_command") { put("id", id) }

    internal fun pluginFailure(failure: Exception) = report(failure)

    fun branch(id: String, afterReply: Boolean = false) =
        command("branch") {
            put("block", id)
            put("chat", state.value.chatId)
            put("mode", if (afterReply) "reply" else "edit")
        }

    fun generateTitle() = command("generate_title")

    fun rename(title: String) = command("rename") { put("name", title.trim()) }

    fun cancel() = command("cancel")

    fun clearError() = mutableState.update { it.copy(error = null) }

    suspend fun memory(
        role: AgentRole,
        tool: String,
        arguments: JSONObject = JSONObject(),
        participant: String = "",
    ): JSONObject =
        engine
            .await()
            .query(
                JSONObject()
                    .put("op", "memory")
                    .put("agent", role.id)
                    .put("participant", participant)
                    .put("tool", tool)
                    .put("arguments", arguments)
            )
            .getJSONObject("result")

    fun refreshModels(config: ProviderConfig) {
        val request = UUID.randomUUID().toString()
        mutableState.update {
            val existing = it.modelCatalogs[config.id] ?: ModelCatalog()
            it.copy(
                modelCatalogs =
                    it.modelCatalogs +
                        (config.id to
                            existing.copy(loading = true, error = null, request = request))
            )
        }
        command("models") {
            put("request", request)
            put("provider", encodeProvider(config))
        }
    }

    internal suspend fun fetchModelEffortLevels(config: ProviderConfig, model: String): List<String> =
        engine.await().query(
            JSONObject().put("op", "model_effort_levels")
                .put("provider", encodeProvider(config.copy(model = model)))
        ).effortLevels()

    fun saveProvider(config: ProviderConfig) {
        mutableState.update {
            it.copy(providers = it.providers.filterNot { p -> p.id == config.id } + config)
        }
        persist()
        refreshModels(config)
    }

    fun finishOnboarding(model: SavedModel) {
        mutableState.update {
            if (!it.showOnboarding || model.choice.id.isBlank() || it.providers.none { provider -> provider.id == model.provider }) {
                it
            } else {
                it.withSavedModel(model, enabled = true).copy(
                    assignments = it.assignments + (AgentRole.Chat to model),
                    showOnboarding = false,
                )
            }
        }
        persist()
    }

    fun deleteProvider(id: String) {
        mutableState.update {
            it.copy(
                providers = it.providers.filterNot { p -> p.id == id },
                assignments = it.assignments.filterValues { model -> model.provider != id },
                savedModels = it.savedModels.filterNot { model -> model.provider == id },
                modelCatalogs = it.modelCatalogs - id,
            )
        }
        persist()
    }

    fun enableModel(model: SavedModel, enabled: Boolean) {
        mutableState.update { it.withSavedModel(model, enabled) }
        persist()
        val provider = state.value.providers.find { it.id == model.provider }
        if (enabled && provider?.protocol == "ollama" && model.choice.effortLevels.isEmpty()) {
            viewModelScope.launch {
                val levels = try {
                    fetchModelEffortLevels(provider, model.choice.id)
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (_: Exception) {
                    return@launch
                }
                if (levels.isNotEmpty() && provider in state.value.providers &&
                    state.value.savedModels.any { it.sameModel(model) }) {
                    updateModelSettings(model) { it.copy(choice = it.choice.copy(effortLevels = levels)) }
                }
            }
        }
    }

    private fun ChatState.withSavedModel(model: SavedModel, enabled: Boolean): ChatState {
        val models = savedModels.filterNot { it.sameModel(model) }
        return copy(
            savedModels = if (enabled) models + model else models,
            assignments =
                if (enabled && AgentRole.Chat !in assignments) assignments + (AgentRole.Chat to model)
                else if (enabled) assignments
                else assignments.filterValues { !it.sameModel(model) },
        )
    }

    fun saveModelSettings(model: SavedModel, settings: CompactionSettings, reasoningEffort: String, effortLevels: List<String>?) =
        updateModelSettings(model) {
            it.copy(
                choice = effortLevels?.let { levels -> it.choice.copy(effortLevels = levels) } ?: it.choice,
                compaction = settings, reasoningEffort = reasoningEffort,
            )
        }

    private fun updateModelSettings(model: SavedModel, transform: (SavedModel) -> SavedModel) {
        fun update(entry: SavedModel) =
            if (entry.sameModel(model)) transform(entry) else entry
        mutableState.update {
            it.copy(
                savedModels = it.savedModels.map(::update),
                assignments = it.assignments.mapValues { update(it.value) },
            )
        }
        persist()
    }

    fun assign(role: AgentRole, model: SavedModel?) {
        mutableState.update {
            it.copy(
                assignments =
                    if (model == null) it.assignments - role else it.assignments + (role to model)
            )
        }
        persist()
        if (model != null && role == AgentRole.Chat) greetActiveChat()
        if (
            model != null &&
                (role == AgentRole.Preset || role == AgentRole.Chat) &&
                state.value.presetEditorOpen
        ) resumePresetInterview(state.value.preset)
    }

    fun thoughts(value: Boolean) {
        mutableState.update { it.copy(expandThoughts = value) }
        persist()
    }

    fun fetchMode(mode: String) {
        mutableState.update { it.copy(fetchMode = mode) }
        persist()
    }

    fun researchSettings(settings: ResearchSettings) {
        mutableState.update { it.copy(research = settings) }
        persist()
    }

    private fun configure() {
        val current = state.value
        fun configuration(model: SavedModel?): JSONObject? {
            val provider = current.providers.find { it.id == model?.provider } ?: return null
            val choice = model?.choice ?: return null
            return encodeProvider(
                provider.copy(
                    model = choice.id,
                    contextWindow = choice.contextWindow.takeIf { it > 0 } ?: provider.contextWindow,
                    maxOutput = choice.maxOutput.takeIf { it > 0 } ?: provider.maxOutput,
                )
            ).put("input_modalities", choice.inputModalities?.let(::JSONArray))
                .put("reasoning_effort", model.reasoningEffort.ifEmpty { choice.effortLevels.highestEffort() })
                .putCompaction(model.compaction)
        }
        val assignments = JSONObject()
        current.assignments.forEach { (role, model) ->
            configuration(model)?.let { assignments.put(role.id, it) }
        }
        val mainlandChina = usesMainlandChina(workspace.resources.configuration.locales[0], workspace.deviceLocale())
        command("configure") {
            put(
                "provider",
                configuration(current.assignments[AgentRole.Chat])
                    ?: JSONObject().put("url", "").put("model", ""),
            )
            put("agents", assignments)
            put(
                "settings",
                JSONObject()
                    .put("fetch_backend", current.fetchMode)
                    .put("search_provider", current.research.search.id)
                    .put("fetch_provider", current.research.fetch.id)
                    .put("research_keys", current.research.encodeKeys())
                    .put("search_region", if (mainlandChina) "CN" else "")
                    .put("android_open_ui", openUiInstructions)
                    .put(
                        "search_key",
                        current.providers.find { it.url.contains("deepseek") }?.key.orEmpty(),
                    ),
            )
        }
    }

    private fun persist() {
        configure()
        val current = state.value
        val reopenEditor = current.presetEditorOpen || current.preset == presetEditorToRestore
        saveJob?.cancel()
        saveJob =
            viewModelScope.launch(Dispatchers.IO) {
                try {
                    val assignments = JSONObject()
                    current.assignments.forEach { (role, model) ->
                        assignments.put(role.id, encodeModel(model))
                    }
                    preferences.save(
                        JSONArray(current.providers.map(::encodeProvider)),
                        JSONObject()
                            .put("models", JSONArray(current.savedModels.map(::encodeModel)))
                            .put("assignments", assignments)
                            .put("preset", current.preset)
                            .put("preset_editor_open", reopenEditor)
                            .put("chat", current.chatId)
                            .put("cot", current.expandThoughts)
                            .put("onboarding_complete", !current.showOnboarding)
                            .put("preset_home_hint_seen", current.presetHomeHintSeen)
                            .put("preset_chat_hint_seen", current.presetChatHintSeen)
                            .put("search_provider", current.research.search.id)
                            .put("fetch_provider", current.research.fetch.id)
                            .put("research_keys", current.research.encodeKeys())
                            .put("fetch", current.fetchMode),
                    )
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (error: Exception) {
                    report(error)
                }
            }
    }

    private fun command(op: String, build: JSONObject.() -> Unit = {}) {
        try {
            core?.send(JSONObject().put("op", op).apply(build))
        } catch (error: Exception) {
            report(error)
        }
    }

    private fun report(error: Exception) = mutableState.update {
        it.copy(error = error.message ?: "Error")
    }

    override fun onCleared() {
        core?.close()
        browser.close()
    }
}

private fun JSONObject?.hasChatGreeting(): Boolean =
    this?.optJSONObject("chat_agent")?.optString("greeting_system_message")?.isNotBlank() == true
