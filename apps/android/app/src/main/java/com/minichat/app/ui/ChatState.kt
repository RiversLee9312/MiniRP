package com.minichat.app.ui

data class TraceBlock(
    val id: String,
    val agent: String = "chat",
    val user: Boolean = false,
    val content: String = "",
    val reasoning: String = "",
    val tool: String = "",
    val arguments: String = "",
    val result: String = "",
    val running: Boolean = false,
    val streaming: Boolean = false,
    val version: String = "",
    val attachmentNames: List<String> = emptyList(),
) {
    val role: AgentRole
        get() =
            AgentRole.entries.firstOrNull { it.id == agent || it.agentName == agent }
                ?: AgentRole.Chat
}

data class Choice(
    val id: String,
    val name: String,
    val contextWindow: Int = 0,
    val maxOutput: Int = 0,
)

data class ProviderConfig(
    val id: String = java.util.UUID.randomUUID().toString(),
    val name: String = "",
    val protocol: String = "openai",
    val url: String = "",
    val key: String = "",
    val model: String = "",
    val thinking: Boolean = false,
    val contextWindow: Int = 128000,
    val maxOutput: Int = 8192,
)

enum class AgentRole(val id: String, val agentName: String, val label: Int) {
    Chat("chat", "ChatAgent", com.minichat.app.R.string.chat),
    Search("search", "SearchAgent", com.minichat.app.R.string.search_agent),
    World("env", "EnvAgent", com.minichat.app.R.string.world),
    Preset("preset", "PresetAgent", com.minichat.app.R.string.preset),
    Fetch("fetch", "FetchAgent", com.minichat.app.R.string.fetch_agent),
    Title("title", "TitleAgent", com.minichat.app.R.string.title_model),
}

data class SavedModel(val provider: String, val choice: Choice)

data class ModelCatalog(
    val choices: List<Choice> = emptyList(),
    val loading: Boolean = false,
    val error: String? = null,
    val request: String = "",
)

data class ChatState(
    val busy: Boolean = false,
    val ready: Boolean = false,
    val preparing: Boolean = false,
    val preset: String = "default",
    val chatName: String = "",
    val chatId: String = "",
    val blocks: List<TraceBlock> = emptyList(),
    val providers: List<ProviderConfig> = emptyList(),
    val savedModels: List<SavedModel> = emptyList(),
    val assignments: Map<AgentRole, SavedModel> = emptyMap(),
    val modelCatalogs: Map<String, ModelCatalog> = emptyMap(),
    val catalog: List<ProviderConfig> = emptyList(),
    val presetName: String = "",
    val expandThoughts: Boolean = false,
    val fetchMode: String = "api_then_local",
    val systemPrompt: String = "",
    val agenticSearch: Boolean = true,
    val error: String? = null,
) {
    fun assigned(role: AgentRole): SavedModel? = assignments[role] ?: assignments[AgentRole.Chat]
}
