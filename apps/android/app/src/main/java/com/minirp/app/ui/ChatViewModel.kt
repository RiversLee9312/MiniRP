package com.minirp.app.ui

import android.app.Application
import android.content.Context
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.paging.InvalidatingPagingSourceFactory
import androidx.paging.Pager
import androidx.paging.PagingConfig
import androidx.paging.PagingData
import androidx.paging.cachedIn
import com.mikepenz.markdown.model.State as MarkdownState
import com.mikepenz.markdown.model.parseMarkdown
import com.minirp.app.R
import com.minirp.app.core.BrowserHost
import com.minirp.app.core.CoreClient
import com.minirp.app.data.AttachmentStore
import com.minirp.app.data.LibraryPagingSource
import com.minirp.app.data.Preferences
import java.util.UUID
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
) : AndroidViewModel(application) {
    constructor(application: Application) : this(application, application, Preferences(application))

    private val mutableState = MutableStateFlow(ChatState())
    val state = mutableState.asStateFlow()
    private val attachments = AttachmentStore(workspace)
    private val browser = BrowserHost(application)
    internal val picker = PluginPicker()
    private var homeRequest = ""
    private var askedHome = false
    private var homeLocale = ""
    private var openUiInstructions = ""
    private var editorOwnsTurn = false
    private val openUiDrafts = android.util.LruCache<Pair<String, String>, String>(40)
    internal val actions =
        kotlinx.coroutines.channels.Channel<String>(kotlinx.coroutines.channels.Channel.BUFFERED)
    private val engine = CompletableDeferred<CoreClient>()
    @Volatile private var core: CoreClient? = null
    private var saveJob: Job? = null

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
                        expandThoughts = options.optBoolean("cot"),
                        fetchMode = options.optString("fetch", "api_then_local"),
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
                core = client
                engine.complete(client)
                configure()
                client.events.collect { event ->
                    when (event.optString("kind")) {
                        "ready" -> {
                            receive(event)
                            if (
                                savedPreset.isNotBlank() && savedPreset != event.optString("preset")
                            ) {
                                command("home") {
                                    put("preset", savedPreset)
                                    put("restore", true)
                                }
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
                        "turn_end" -> {
                            val block =
                                (if (event.has("conversation")) state.value.ensembleBlocks
                                    else state.value.blocks)
                                    .lastOrNull { it.id == event.optString("block") }
                            val finished =
                                withContext(Dispatchers.Default) { block?.finishMarkdown() }
                            if (finished != null) receiveTrace(event, finished)
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

    private fun receive(event: JSONObject, loadedBlocks: List<TraceBlock>? = null) {
        when (event.optString("kind")) {
            "ready" -> {
                val initial = event.optString("preset")
                selectLibraryPreset(initial)
                mutableState.update {
                    it.copy(
                        ready = true,
                        preset = initial,
                        presetName = event.optString("preset_name", initial),
                        chatMode =
                            event.optJSONObject("config")?.optString("chat_mode", "character")
                                ?: "character",
                        agenticSearch =
                            event.optJSONObject("config")?.optBoolean("agentic_search", true)
                                ?: true,
                        catalog =
                            event
                                .optJSONArray("catalog")
                                ?.objects()
                                ?.map(::decodeProvider)
                                .orEmpty(),
                    )
                }
                command("plugins_list")
                command("plugin_settings")
            }
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
                showHome(event.getString("preset"), event.getJSONObject("config"))
                requestHome()
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
            "home_suggestions" ->
                if (event.optString("request") == homeRequest)
                    mutableState.update {
                        it.copy(
                            homeSuggestions =
                                event.getJSONArray("items").let { rows ->
                                    List(rows.length()) { index -> rows.getString(index) }
                                }
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
                editorOwnsTurn = role == AgentRole.Preset && event.optBoolean("editor_turn")
                if (editorOwnsTurn && state.value.presetEditor == PresetEditorPhase.Closed)
                    cancelPresetTurn()
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
                        presetName = config?.optString("name", preset) ?: preset,
                        chatName = chat.optString("name"),
                        chatId = chat.optString("id"),
                        conversationViewKey =
                            if (event.optBoolean("from_home_send")) it.conversationViewKey
                            else chat.optString("id"),
                        blocks = loadedBlocks.orEmpty(),
                        ensembleBlocks = emptyList(),
                        ensembleChatId = "",
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
                        agenticSearch = config.optBoolean("agentic_search", true),
                        chatMode = config.optString("chat_mode", "character"),
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
                    val message = event.optString("message")
                    val agent =
                        it.generating
                            ?: AgentRole.entries.find { role ->
                                role.id == event.optString("agent")
                            }
                    if (agent != null && (it.generating != null || event.optBoolean("model_turn")))
                        it.copy(replyError = ReplyError(agent, message))
                    else it.copy(error = message)
                }
            "models",
            "models_loading",
            "models_error" -> receiveModels(event)
            "ensemble_chats" ->
                mutableState.update {
                    if (it.chatId != event.optString("chat")) it
                    else
                        it.copy(
                            ensembleChats =
                                event.getJSONArray("items").objects().map(::decodeEnsembleChat)
                        )
                }
            "ensemble_message" ->
                mutableState.update {
                    if (
                        it.chatId != event.optString("chat") ||
                            it.ensembleChatId != event.optString("conversation")
                    )
                        it
                    else
                        it.copy(
                            ensembleBlocks =
                                it.ensembleBlocks + decodeBlock(event.getJSONObject("item"))
                        )
                }
            "reply_end" -> mutableState.update { it.copy(blocks = it.blocks + decodeBlock(event)) }
            "user",
            "system" ->
                mutableState.update {
                    it.copy(
                        blocks =
                            it.blocks +
                                TraceBlock(
                                    event.getString("block"),
                                    event.getString("agent"),
                                    user = true,
                                    content = event.optString("text"),
                                    version = event.optString("version"),
                                    attachmentNames = event.attachmentNames(),
                                    system = event.optString("kind") == "system",
                                    thumbnails = event.thumbnails(),
                                )
                    )
                }
            "delta",
            "open_ui_delta",
            "open_ui_end",
            "open_ui_card",
            "open_ui_card_update",
            "tool_start",
            "tool_end" -> receiveTrace(event)
        }
    }

    private fun receiveTrace(event: JSONObject, finished: TraceBlock? = null) =
        mutableState.update { current ->
            if (event.has("plugin_agent") && event.optString("preset") != current.preset)
                return@update current
            val id = event.getString("block")
            val inRoom =
                !event.isNull("conversation") && event.optString("conversation").isNotBlank()
            if (
                inRoom &&
                    (event.optString("conversation") != current.ensembleChatId ||
                        event.optString("chat") != current.chatId)
            )
                return@update current
            val blocks = if (inRoom) current.ensembleBlocks else current.blocks
            val index = blocks.indexOfLast { it.id == id }
            if (
                index < 0 &&
                    event.optString("kind") in
                        setOf("turn_end", "open_ui_end", "open_ui_card_update")
            )
                return@update current
            val old =
                blocks.getOrNull(index)
                    ?: TraceBlock(
                        id,
                        event.optString("agent"),
                        speaker = event.optString("speaker"),
                    )
            val changed =
                when (event.optString("kind")) {
                    "turn_end" -> finished ?: old
                    "open_ui_delta" ->
                        old.copy(
                            openUi = old.openUi.orEmpty() + event.optString("text"),
                            streaming = true,
                        )
                    "open_ui_end" -> old.copy(streaming = false)
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
                        )
                    "tool_start" ->
                        old.copy(
                            tool = event.optString("name"),
                            arguments = event.optString("arguments"),
                            running = true,
                        )
                    "tool_end" ->
                        old.copy(
                            tool = event.optString("name"),
                            result = event.optString("text"),
                            running = false,
                        )
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
                }
            val updated =
                if (index < 0) blocks + changed
                else blocks.toMutableList().apply { set(index, changed) }
            if (inRoom) current.copy(ensembleBlocks = updated) else current.copy(blocks = updated)
        }

    private fun TraceBlock.finishMarkdown() =
        if (!user && content.isNotBlank())
            copy(streaming = false, markdown = parseMarkdown(content) as? MarkdownState.Success)
        else if (streaming) copy(streaming = false) else this

    private fun receiveModels(event: JSONObject) {
        val id = event.optString("provider")
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
            current.copy(modelCatalogs = current.modelCatalogs + (id to changed))
        }
    }

    suspend fun send(
        text: String,
        documents: List<String>,
        role: AgentRole,
        stage: Boolean = false,
        system: Boolean = false,
        pluginAttachment: PluginAttachmentType? = null,
        openUiAction: JSONObject? = null,
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
        if (current.generating == role) {
            if (text.isBlank() || documents.isNotEmpty()) return false
            return try {
                engine
                    .await()
                    .query(
                        JSONObject()
                            .put("op", "steer")
                            .put("chat", current.chatId)
                            .put("conversation", current.ensembleChatId)
                            .put("agent", role.id)
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
        if (system && (text.isBlank() || documents.isNotEmpty())) return false
        val provider = current.providers.find { it.id == current.assigned(role)?.provider }
        if (provider == null) {
            mutableState.update {
                it.copy(
                    error = getApplication<Application>().getString(R.string.no_provider_configured)
                )
            }
            return false
        }
        mutableState.update { it.copy(submitting = true, replyError = null) }
        try {
            val draft = current.branchDraft.takeIf { role == AgentRole.Chat }
            val imported =
                attachments.import(
                    documents,
                    provider.protocol,
                    pluginAttachment,
                    draft?.attachments.orEmpty(),
                )
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
                            .put("role", if (system) "system" else "user")
                            .put("text", text)
                            .put("stage", stage)
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
            mutableState.update { it.copy(submitting = false) }
        }
    }

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
        }

    fun createEnsembleRoom(preset: String = state.value.preset) =
        command("select_chat") {
            put("preset", preset)
        }

    fun leaveEnsembleChat() {
        val current = state.value
        if (current.ensembleChats == null) return
        if (current.ensembleChatId.isNotBlank())
            mutableState.update { it.copy(ensembleChatId = "", ensembleBlocks = emptyList()) }
        command("select_ensemble_chat") {
            put("chat", current.chatId)
            put("conversation", "")
        }
    }

    suspend fun openEnsembleChat(room: String, conversation: String) {
        if (state.value.chatId != room) return
        try {
            val result =
                engine
                    .await()
                    .query(
                        JSONObject()
                            .put("op", "select_ensemble_chat")
                            .put("chat", room)
                            .put("conversation", conversation)
                    )
            val blocks =
                withContext(Dispatchers.Default) {
                    result.getJSONArray("blocks").objects().map { decodeBlock(it).finishMarkdown() }
                }
            mutableState.update { current ->
                if (current.chatId != room) current
                else current.copy(ensembleChatId = conversation, ensembleBlocks = blocks)
            }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            report(error)
        }
    }

    fun selectPreset(preset: String) {
        val current = state.value
        if (current.chatId.isBlank() && current.preset != preset) chooseChat(preset)
        else selectLibraryPreset(preset)
    }

    private fun showHome(
        preset: String,
        config: JSONObject,
        blocks: List<TraceBlock> = emptyList(),
        editPreset: Boolean = false,
    ) {
        selectLibraryPreset(preset)
        val changedPreset = preset != state.value.preset
        if (changedPreset) {
            askedHome = false
            homeRequest = ""
        }
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
                chatMode = config.optString("chat_mode", "character"),
                branchDraft = null,
                replyError = null,
                preset = preset,
                presetName = config.optString("name", preset),
                homeSuggestions = if (it.preset == preset) it.homeSuggestions else emptyList(),
                presetEditor = if (editPreset) PresetEditorPhase.Open else PresetEditorPhase.Closed,
                agenticSearch = config.optBoolean("agentic_search", true),
            )
        }
        if (changedPreset) command("plugin_settings")
        persist()
    }

    fun openPresetEditor(create: Boolean = false) {
        val current = state.value
        if (!current.ready || current.busy || current.preparing || current.presetEditorOpen) return
        if (!create && current.preset.isBlank()) return
        mutableState.update { it.copy(presetEditor = PresetEditorPhase.Opening) }
        viewModelScope.launch {
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
                            presetEditor = PresetEditorPhase.Open,
                            agenticSearch = config.optBoolean("agentic_search", true),
                        )
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                report(error)
            } finally {
                mutableState.update {
                    if (it.presetEditor == PresetEditorPhase.Opening)
                        it.copy(presetEditor = PresetEditorPhase.Closed)
                    else it
                }
            }
        }
    }

    fun closePresetEditor() {
        mutableState.update { it.copy(presetEditor = PresetEditorPhase.Closed) }
        if (editorOwnsTurn) cancelPresetTurn()
    }

    private fun cancelPresetTurn() = command("cancel") { put("agent", AgentRole.Preset.id) }

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
                askedHome = false
                mutableState.update { it.copy(homeSuggestions = emptyList()) }
                requestHome()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                report(failure)
            }
        }
    }

    fun requestHome() {
        val current = state.value
        val locale =
            getApplication<Application>().resources.configuration.locales[0].toLanguageTag()
        if (homeLocale != locale) {
            homeLocale = locale
            askedHome = false
        }
        if (
            askedHome ||
                !current.ready ||
                current.preset.isBlank() ||
                current.presetEditorOpen ||
                current.chatId.isNotBlank() ||
                current.assigned(AgentRole.Chat) == null
        )
            return
        askedHome = true
        homeRequest = UUID.randomUUID().toString()
        command("home_request") {
            put("request", homeRequest)
            put("locale", locale)
        }
    }

    internal suspend fun inspectPlugin(file: java.io.File) =
        decodePlugin(
            engine
                .await()
                .query(JSONObject().put("op", "plugin_inspect").put("path", file.path))
                .getJSONObject("item")
        )

    internal suspend fun pluginPage(id: String, key: String): PluginSetting =
        decodePluginSetting(
            engine
                .await()
                .query(JSONObject().put("op", "plugin_page").put("id", id).put("key", key))
                .getJSONObject("item")
        )

    internal suspend fun installPlugin(file: java.io.File) {
        engine.await().query(JSONObject().put("op", "plugin_install").put("path", file.path))
        command("plugins_list")
        command("plugin_settings")
    }

    internal fun removePlugin(id: String) {
        command("plugin_uninstall") { put("id", id) }
        command("plugin_settings")
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

    internal suspend fun thumbnail(role: AgentRole, preview: AttachmentThumbnail): ByteArray =
        engine.await().readAsset(preview.url, 1024 * 1024, role.id, preview.attachment)

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

    fun saveProvider(config: ProviderConfig) {
        mutableState.update {
            it.copy(providers = it.providers.filterNot { p -> p.id == config.id } + config)
        }
        persist()
        refreshModels(config)
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
        mutableState.update {
            val models = it.savedModels.filterNot { candidate -> candidate.sameModel(model) }
            it.copy(
                savedModels = if (enabled) models + model else models,
                assignments =
                    if (enabled && AgentRole.Chat !in it.assignments)
                        it.assignments + (AgentRole.Chat to model)
                    else if (enabled) it.assignments
                    else it.assignments.filterValues { candidate -> !candidate.sameModel(model) },
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
        if (
            model != null &&
                (role == AgentRole.Preset || role == AgentRole.Chat) &&
                state.value.presetEditorOpen
        ) {
            command("resume_preset_interview") {
                put("preset", state.value.preset)
                put(
                    "locale",
                    getApplication<Application>()
                        .resources
                        .configuration
                        .locales[0]
                        .toLanguageTag(),
                )
            }
        }
    }

    fun thoughts(value: Boolean) {
        mutableState.update { it.copy(expandThoughts = value) }
        persist()
    }

    fun fetchMode(mode: String) {
        mutableState.update { it.copy(fetchMode = mode) }
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
                    contextWindow = choice.contextWindow.takeIf { it > 0 } ?: 128000,
                    maxOutput = choice.maxOutput.takeIf { it > 0 } ?: 8192,
                )
            )
        }
        val assignments = JSONObject()
        current.assignments.forEach { (role, model) ->
            configuration(model)?.let { assignments.put(role.id, it) }
        }
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
                            .put("chat", current.chatId)
                            .put("cot", current.expandThoughts)
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
