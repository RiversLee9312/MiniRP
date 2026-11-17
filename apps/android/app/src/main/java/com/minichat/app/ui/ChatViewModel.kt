package com.minichat.app.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.minichat.app.core.BrowserHost
import com.minichat.app.core.CoreClient
import com.minichat.app.data.Preferences
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONArray
import org.json.JSONObject

class ChatViewModel(application: Application) : AndroidViewModel(application) {
    private val mutableState = MutableStateFlow(ChatState())
    val state = mutableState.asStateFlow()
    private val preferences = Preferences(application)
    private var core: CoreClient? = null
    private var saveJob: Job? = null
    private val browser = BrowserHost(application)

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
                val (providers, options) = preferences.load()
                mutableState.update {
                    it.copy(
                        providers = providers.objects().map(::provider),
                        selectedProvider = options.optString("provider"),
                        expandThoughts = options.optBoolean("cot"),
                        fetchMode = options.optString("fetch", "api_then_local"),
                    )
                }
                val engine = CoreClient(application, viewModelScope)
                engine.browser = browser::execute
                core = engine
                configure()
                engine.events.collect(::receive)
            } catch (error: Exception) {
                showError(error)
            }
        }
    }

    private fun JSONArray.objects(): List<JSONObject> =
        (0 until length()).mapNotNull { optJSONObject(it) }

    private fun JSONObject.str(key: String, default: String = "") = optString(key, default)

    private fun choices(array: JSONArray?): List<Choice> =
        array
            ?.objects()
            ?.map {
                Choice(
                    it.str("id"),
                    it.str("name", it.str("id")),
                    it.optInt("context_length", it.optInt("inputTokenLimit")),
                    it.optInt(
                        "outputTokenLimit",
                        it.optInt(
                            "max_output_tokens",
                            it.optJSONObject("top_provider")?.optInt("max_completion_tokens") ?: 0,
                        ),
                    ),
                )
            }
            .orEmpty()

    private fun provider(p: JSONObject) =
        ProviderConfig(
            p.str("id"),
            p.str("name"),
            p.str("protocol", "openai"),
            p.str("url"),
            p.str("key"),
            p.str("model"),
            p.optBoolean("thinking"),
            p.optInt("context_window", 128000),
            p.optInt("max_output", 8192),
        )

    private fun providerJson(p: ProviderConfig) =
        JSONObject()
            .put("id", p.id)
            .put("name", p.name)
            .put("protocol", p.protocol)
            .put("url", p.url)
            .put("key", p.key)
            .put("model", p.model)
            .put("thinking", p.thinking)
            .put("context_window", p.contextWindow)
            .put("max_output", p.maxOutput)

    private fun block(b: JSONObject) =
        TraceBlock(
            b.str("id"),
            b.str("agent"),
            b.str("kind") == "user",
            b.str("content"),
            b.str("reasoning"),
            b.str("tool"),
            b.str("arguments"),
            b.str("result"),
            b.optBoolean("running"),
        )

    private fun receive(event: JSONObject) {
        when (event.str("kind")) {
            "ready" ->
                mutableState.update { current ->
                    current.copy(
                        ready = true,
                        presets = choices(event.optJSONArray("presets")),
                        catalog =
                            event.optJSONArray("catalog")?.objects()?.map(::provider).orEmpty(),
                    )
                }
            "presets" ->
                mutableState.update { it.copy(presets = choices(event.optJSONArray("items"))) }
            "chats" ->
                mutableState.update {
                    it.copy(
                        chats = choices(event.optJSONArray("items")),
                        chatPicker = true,
                        preset = event.str("preset"),
                    )
                }
            "loaded" -> {
                val chat = event.getJSONObject("chat")
                mutableState.update {
                    it.copy(
                        chatPicker = false,
                        preset = event.str("preset"),
                        chatName = chat.str("name"),
                        chatId = chat.str("id"),
                        blocks = chat.optJSONArray("blocks")?.objects()?.map(::block).orEmpty(),
                        systemPrompt =
                            event
                                .optJSONObject("config")
                                ?.optJSONObject("chat_agent")
                                ?.str("system_prompt")
                                .orEmpty(),
                    )
                }
            }
            "busy" -> mutableState.update { it.copy(busy = event.optBoolean("value")) }
            "error" -> mutableState.update { it.copy(error = event.str("message")) }
            "models" ->
                mutableState.update {
                    if (event.optString("provider") == it.provider?.id)
                        it.copy(models = choices(event.optJSONArray("items")))
                    else it
                }
            "models_loading" ->
                mutableState.update {
                    if (event.optString("provider") == it.provider?.id)
                        it.copy(modelsLoading = event.optBoolean("value"))
                    else it
                }
            "memory" ->
                mutableState.update {
                    it.copy(memory = event.optJSONObject("result")?.toString(2).orEmpty())
                }
            "user" ->
                mutableState.update {
                    it.copy(
                        chatName = it.chatName.ifBlank { event.str("text").take(40) },
                        blocks =
                            it.blocks +
                                TraceBlock(
                                    java.util.UUID.randomUUID().toString(),
                                    event.str("agent"),
                                    user = true,
                                    content = event.str("text"),
                                ),
                    )
                }
            "delta",
            "tool_start",
            "tool_end" ->
                mutableState.update { current ->
                    val id = event.str("block")
                    val old =
                        current.blocks.find { it.id == id } ?: TraceBlock(id, event.str("agent"))
                    val changed =
                        when (event.str("kind")) {
                            "tool_start" ->
                                old.copy(
                                    tool = event.str("name"),
                                    arguments = event.str("arguments"),
                                    running = true,
                                )
                            "tool_end" ->
                                old.copy(
                                    tool = event.str("name"),
                                    result = event.str("text"),
                                    running = false,
                                )
                            else ->
                                if (event.str("type") == "reasoning")
                                    old.copy(reasoning = old.reasoning + event.str("text"))
                                else old.copy(content = old.content + event.str("text"))
                        }
                    current.copy(
                        blocks =
                            if (current.blocks.any { it.id == id })
                                current.blocks.map { if (it.id == id) changed else it }
                            else current.blocks + changed
                    )
                }
        }
    }

    fun send(text: String, agent: String = "chat", stage: Boolean = false) {
        if (text.isBlank() || state.value.busy) return
        command("message") {
            put("text", text)
            put("agent", agent)
            put("stage", stage)
        }
    }

    fun cancel() = command("cancel")

    fun choosePreset(id: String) = command("list_chats") { put("preset", id) }

    fun chooseChat(id: String = "") =
        command("select_chat") {
            put("preset", state.value.preset)
            put("id", id)
        }

    fun dismissChatPicker() = mutableState.update { it.copy(chatPicker = false) }

    fun clearError() = mutableState.update { it.copy(error = null) }

    fun prompt(text: String) {
        command("save_prompt") { put("text", text) }
        mutableState.update { it.copy(systemPrompt = text) }
    }

    fun createPreset(name: String) = command("create_preset") { put("name", name) }

    fun memory(query: String, agent: String) =
        command("memory") {
            put("agent", agent)
            put("tool", if (query.isBlank()) "db_list" else "db_search")
            put("arguments", JSONObject().put("query", query))
        }

    fun resetWorld() = command("reset_world")

    fun refreshModels(config: ProviderConfig? = state.value.provider) {
        if (config != null) command("models") { put("provider", providerJson(config)) }
    }

    fun saveProvider(config: ProviderConfig) {
        mutableState.update {
            it.copy(
                providers = it.providers.filterNot { p -> p.id == config.id } + config,
                selectedProvider = config.id,
            )
        }
        persist()
        refreshModels(config)
    }

    fun deleteProvider(id: String) {
        mutableState.update { it.copy(providers = it.providers.filterNot { p -> p.id == id }) }
        persist()
    }

    fun selectProvider(id: String) {
        mutableState.update { it.copy(selectedProvider = id, models = emptyList()) }
        persist()
        refreshModels()
    }

    fun selectModel(model: String) {
        val capabilities = state.value.models.find { it.id == model }
        state.value.provider?.let { provider ->
            mutableState.update { state ->
                state.copy(
                    providers =
                        state.providers.map { current ->
                            if (current.id == provider.id)
                                current.copy(
                                    model = model,
                                    contextWindow =
                                        capabilities?.contextWindow?.takeIf { it > 0 }
                                            ?: current.contextWindow,
                                    maxOutput =
                                        capabilities?.maxOutput?.takeIf { it > 0 }
                                            ?: current.maxOutput,
                                )
                            else current
                        }
                )
            }
            persist()
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
        val selected = current.provider ?: return
        val search = current.providers.find { it.url.contains("deepseek") }
        command("configure") {
            put("provider", providerJson(selected))
            put(
                "settings",
                JSONObject()
                    .put("fetch_backend", current.fetchMode)
                    .put("search_key", search?.key.orEmpty()),
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
                    preferences.save(
                        JSONArray(current.providers.map(::providerJson)),
                        JSONObject()
                            .put("provider", current.selectedProvider)
                            .put("cot", current.expandThoughts)
                            .put("fetch", current.fetchMode),
                    )
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (error: Exception) {
                    showError(error)
                }
            }
    }

    private fun command(op: String, build: JSONObject.() -> Unit = {}) {
        val message = JSONObject().put("op", op).apply(build)
        try {
            core?.send(message)
        } catch (error: Exception) {
            showError(error)
        }
    }

    private fun showError(error: Exception) {
        mutableState.update { it.copy(error = error.message ?: "Error") }
    }

    override fun onCleared() {
        core?.close()
        browser.close()
    }
}
