package com.minichat.app.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.paging.Pager
import androidx.paging.PagingConfig
import androidx.paging.cachedIn
import com.minichat.app.R
import com.minichat.app.core.BrowserHost
import com.minichat.app.core.CoreClient
import com.minichat.app.data.AttachmentStore
import com.minichat.app.data.LibraryPagingSource
import com.minichat.app.data.Preferences
import java.util.UUID
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import org.json.JSONArray
import org.json.JSONObject

@OptIn(ExperimentalCoroutinesApi::class)
class ChatViewModel(application: Application) : AndroidViewModel(application) {
    private val mutableState = MutableStateFlow(ChatState())
    val state = mutableState.asStateFlow()
    private val preferences = Preferences(application)
    private val attachments = AttachmentStore(application)
    private val browser = BrowserHost(application)
    private val engine = CompletableDeferred<CoreClient>()
    @Volatile private var core: CoreClient? = null
    private var saveJob: Job? = null
    private val libraryRevision = MutableStateFlow(0)
    val libraryPreset = MutableStateFlow("default")
    val presets = libraryRevision.flatMapLatest { library("") }.cachedIn(viewModelScope)

    fun chatsFor(preset: String) = libraryRevision.flatMapLatest { library(preset) }

    init {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                val root = application.filesDir.resolve("workspace/presets").apply { mkdirs() }
                for (name in application.assets.list("presets").orEmpty()) {
                    val file = root.resolve(name)
                    if (!file.exists())
                        application.assets.open("presets/$name").use { source ->
                            file.outputStream().use(source::copyTo)
                        }
                }
                val (profiles, options) = preferences.load()
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
                val preset = options.optString("preset", "default")
                libraryPreset.value = preset
                mutableState.update {
                    it.copy(
                        preset = preset,
                        providers = providers,
                        savedModels = saved,
                        assignments = assignments,
                        expandThoughts = options.optBoolean("cot"),
                        fetchMode = options.optString("fetch", "api_then_local"),
                    )
                }
                val client = CoreClient(application, viewModelScope)
                client.browser = browser::execute
                core = client
                engine.complete(client)
                configure()
                options.optString("chat").takeIf(String::isNotBlank)?.let { chooseChat(preset, it) }
                client.events.collect(::receive)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                engine.completeExceptionally(error)
                report(error)
            }
        }
    }

    private fun library(preset: String) =
        Pager(PagingConfig(pageSize = 30, enablePlaceholders = false)) {
                LibraryPagingSource { offset, limit ->
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
            .flow

    private fun receive(event: JSONObject) {
        when (event.optString("kind")) {
            "ready" -> {
                val initial = event.optString("preset", "default")
                libraryPreset.value = initial
                mutableState.update {
                    it.copy(
                        ready = true,
                        preset = initial,
                        presetName = event.optString("preset_name", initial),
                        catalog =
                            event
                                .optJSONArray("catalog")
                                ?.objects()
                                ?.map(::decodeProvider)
                                .orEmpty(),
                    )
                }
            }
            "library_changed" -> libraryRevision.update { it + 1 }
            "loaded" -> {
                val chat = event.getJSONObject("chat")
                val preset = event.getString("preset")
                val config = event.optJSONObject("config")
                libraryPreset.value = preset
                mutableState.update {
                    it.copy(
                        preset = preset,
                        presetName = config?.optString("name", preset) ?: preset,
                        chatName = chat.optString("name"),
                        chatId = chat.optString("id"),
                        blocks =
                            chat.optJSONArray("blocks")?.objects()?.map(::decodeBlock).orEmpty(),
                        systemPrompt =
                            config
                                ?.optJSONObject("chat_agent")
                                ?.optString("system_prompt")
                                .orEmpty(),
                        agenticSearch = config?.optBoolean("agentic_search", true) ?: true,
                    )
                }
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
                        systemPrompt =
                            config
                                .optJSONObject("chat_agent")
                                ?.optString("system_prompt")
                                .orEmpty(),
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
            "error" -> mutableState.update { it.copy(error = event.optString("message")) }
            "models",
            "models_loading",
            "models_error" -> receiveModels(event)
            "user" ->
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
                                )
                    )
                }
            "delta",
            "tool_start",
            "tool_end",
            "turn_end" -> receiveTrace(event)
        }
    }

    private fun receiveTrace(event: JSONObject) = mutableState.update { current ->
        val id = event.getString("block")
        val index = current.blocks.indexOfLast { it.id == id }
        if (index < 0 && event.optString("kind") == "turn_end") return@update current
        val old = current.blocks.getOrNull(index) ?: TraceBlock(id, event.optString("agent"))
        val changed =
            when (event.optString("kind")) {
                "turn_end" -> old.copy(streaming = false)
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
                    else old.copy(content = old.content + event.optString("text"), streaming = true)
            }
        current.copy(
            blocks =
                if (index < 0) current.blocks + changed
                else current.blocks.toMutableList().apply { set(index, changed) }
        )
    }

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
    ): Boolean {
        val current = state.value
        if (!current.ready || current.busy || current.preparing) return false
        val provider = current.providers.find { it.id == current.assigned(role)?.provider }
        if (provider == null) {
            mutableState.update {
                it.copy(
                    error = getApplication<Application>().getString(R.string.no_provider_configured)
                )
            }
            return false
        }
        mutableState.update { it.copy(preparing = true) }
        try {
            val imported = attachments.import(documents, provider.protocol)
            currentCoroutineContext().ensureActive()
            if (current.chatId.isBlank()) chooseChat(current.preset)
            command("message") {
                put("agent", role.id)
                put("text", text)
                put("stage", stage)
                put("attachments", imported)
            }
            return true
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            report(error)
            return false
        } finally {
            mutableState.update { it.copy(preparing = false) }
        }
    }

    fun chooseChat(preset: String, id: String = "") =
        command("select_chat") {
            put("preset", preset)
            put("id", id)
        }

    fun branch(id: String) =
        command("branch") {
            put("block", id)
            put("chat", state.value.chatId)
        }

    fun generateTitle() = command("generate_title")

    fun rename(title: String) = command("rename") { put("name", title.trim()) }

    fun searchMode(enabled: Boolean) = command("save_search_mode") { put("enabled", enabled) }

    fun cancel() = command("cancel")

    fun clearError() = mutableState.update { it.copy(error = null) }

    fun prompt(text: String) {
        command("save_prompt") { put("text", text) }
        mutableState.update { it.copy(systemPrompt = text) }
    }

    suspend fun memory(
        role: AgentRole,
        tool: String,
        arguments: JSONObject = JSONObject(),
    ): JSONObject =
        engine
            .await()
            .query(
                JSONObject()
                    .put("op", "memory")
                    .put("agent", role.id)
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
