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
)

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

data class ChatState(
    val busy: Boolean = false,
    val ready: Boolean = false,
    val preset: String = "default",
    val chatName: String = "",
    val chatId: String = "",
    val blocks: List<TraceBlock> = emptyList(),
    val providers: List<ProviderConfig> = emptyList(),
    val selectedProvider: String = "",
    val catalog: List<ProviderConfig> = emptyList(),
    val presets: List<Choice> = emptyList(),
    val chats: List<Choice> = emptyList(),
    val models: List<Choice> = emptyList(),
    val modelsLoading: Boolean = false,
    val chatPicker: Boolean = false,
    val expandThoughts: Boolean = false,
    val fetchMode: String = "api_then_local",
    val systemPrompt: String = "",
    val error: String? = null,
    val memory: String = "",
) {
    val provider: ProviderConfig?
        get() = providers.find { it.id == selectedProvider } ?: providers.firstOrNull()
}
