package com.minirp.app.ui

import com.mikepenz.markdown.model.State as MarkdownState
import org.json.JSONObject

data class AttachmentThumbnail(val url: String, val attachment: JSONObject)

data class QuoteAttachment(val id: String, val text: String)

internal const val maxMessageAttachments = 20
internal const val maxQuoteLength = 32_768

data class BranchDraft(
    val text: String,
    val attachments: Map<String, JSONObject>,
    val openUiAction: JSONObject?,
    val quotes: List<QuoteAttachment> = emptyList(),
)

data class ReplyEnd(val text: String, val firstBlock: String)

data class ToolTraceUi(val program: String, val title: String = "", val state: String? = null)

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
    val system: Boolean = false,
    val thumbnails: List<AttachmentThumbnail> = emptyList(),
    val markdown: MarkdownState.Success? = null,
    val openUi: String? = null,
    val replyEnd: ReplyEnd? = null,
    val speaker: String = "",
    val openUiTitle: String = "",
    val openUiState: String? = null,
    val openUiRevision: Int = 0,
    val openUiHostAction: String = "",
    val compaction: Boolean = false,
    val pluginAgentId: String = "",
    val pluginRun: String = "",
    val pluginAgentName: String = "",
    val pluginStatus: String = "",
    val pluginSequence: Int = 0,
    val toolArgumentsUi: ToolTraceUi? = null,
    val toolResultUi: ToolTraceUi? = null,
    val toolTitle: String = "",
    val skillTitle: String = "",
    val quotes: List<QuoteAttachment> = emptyList(),
) {
    val role: AgentRole
        get() =
            if (agent == "PluginAgent") AgentRole.Preset
            else
                AgentRole.entries.firstOrNull { it.id == agent || it.agentName == agent }
                    ?: AgentRole.Browser.takeIf { agent == "FetchAgent" }
                    ?: AgentRole.Chat
}

data class Choice(
    val id: String,
    val name: String,
    val contextWindow: Int = 0,
    val maxOutput: Int = 0,
    val ensemble: Boolean = false,
    val inputModalities: List<String>? = null,
    val effortLevels: List<String> = emptyList(),
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
    Chat("chat", "ChatAgent", com.minirp.app.R.string.chat),
    Search("search", "SearchAgent", com.minirp.app.R.string.search_agent),
    World("env", "EnvAgent", com.minirp.app.R.string.world),
    Preset("preset", "PresetAgent", com.minirp.app.R.string.preset),
    Plugin("plugin", "PluginAgent", com.minirp.app.R.string.plugin_agent),
    Browser("fetch", "BrowserAgent", com.minirp.app.R.string.fetch_agent),
    Title("title", "TitleAgent", com.minirp.app.R.string.title_model),
    ImageDescription("image_description", "ImageDescription", com.minirp.app.R.string.image_description_model),
    AudioDescription("audio_description", "AudioDescription", com.minirp.app.R.string.audio_description_model);

    val descriptionKind: String?
        get() = when (this) {
            ImageDescription -> "image"
            AudioDescription -> "audio"
            else -> null
        }
}

internal const val DEFAULT_COMPACTION_PERCENT = 80
internal const val DEFAULT_BACKGROUND_COMPACTION_PERCENT = 70

data class CompactionSettings(
    val threshold: Int = DEFAULT_COMPACTION_PERCENT,
    val background: Boolean = true,
    val backgroundThreshold: Int = DEFAULT_BACKGROUND_COMPACTION_PERCENT,
)

data class SavedModel(
    val provider: String,
    val choice: Choice,
    val compaction: CompactionSettings = CompactionSettings(),
    val reasoningEffort: String = "",
    val mediaInput: MediaInputSettings = MediaInputSettings(),
)

data class MediaInputSettings(val describeImages: Boolean = false, val describeAudio: Boolean = false)

data class ModelCatalog(
    val choices: List<Choice> = emptyList(),
    val loading: Boolean = false,
    val error: String? = null,
    val request: String = "",
)

data class ReplyError(val agent: AgentRole, val message: String, val presetInterview: Boolean = false)

data class EnsembleChat(val id: String, val name: String, val members: List<String>)

data class EnsembleActivity(val conversation: String, val participant: String, val speaker: String)

internal data class EnsembleTraceScene(val id: String, val name: String, val blocks: List<TraceBlock>)

data class RoomLoading(
    val id: String,
    val ready: Int,
    val total: Int,
    val progress: Float,
    val messages: List<String>,
    val declaredProgress: Boolean = false,
)

enum class PresetEditorPhase {
    Closed,
    Opening,
    Open,
}

data class ChatState(
    val busy: Boolean = false,
    val generating: AgentRole? = null,
    val ready: Boolean = false,
    val showOnboarding: Boolean = false,
    val submitting: AgentRole? = null,
    val preset: String = "",
    val presetSetupPending: Boolean = false,
    val presetHomeHintSeen: Boolean = false,
    val presetChatHintSeen: Boolean = false,
    val chatName: String = "",
    val chatId: String = "",
    val conversationViewKey: String = "home",
    val blocks: List<TraceBlock> = emptyList(),
    val ensembleBlocks: List<TraceBlock> = emptyList(),
    val ensembleChats: List<EnsembleChat>? = null,
    val roomLoading: RoomLoading? = null,
    val participants: List<Choice> = emptyList(),
    val ensembleChatId: String = "",
    val ensembleActivity: List<EnsembleActivity> = emptyList(),
    val ensemblePrivateParticipant: String = "",
    val ensemblePrivateInStory: Boolean = true,
    val chatMode: String = "character",
    val chatGreeting: Boolean = false,
    val providers: List<ProviderConfig> = emptyList(),
    val savedModels: List<SavedModel> = emptyList(),
    val assignments: Map<AgentRole, SavedModel> = emptyMap(),
    val modelCatalogs: Map<String, ModelCatalog> = emptyMap(),
    val catalog: List<ProviderConfig> = emptyList(),
    val presetName: String = "",
    val appearance: PresetAppearance = PresetAppearance(),
    val presetEditor: PresetEditorPhase = PresetEditorPhase.Closed,
    val branchDraft: BranchDraft? = null,
    val expandThoughts: Boolean = false,
    val fetchMode: String = "api_then_local",
    val research: ResearchSettings = ResearchSettings(),
    val agenticSearch: Boolean = true,
    val error: String? = null,
    val replyError: ReplyError? = null,
    val pluginSettings: List<PluginSetting> = emptyList(),
    val installedPlugins: List<PluginInfo> = emptyList(),
    val archiveBusy: Boolean = false,
    val archiveSaved: Boolean = false,
) {
    val preparing
        get() = submitting != null || presetEditor == PresetEditorPhase.Opening

    val canExportTranscript
        get() = ready && generating == null && !preparing && !archiveBusy

    val presetEditorOpen
        get() = presetEditor == PresetEditorPhase.Open

    val presetWorking
        get() = generating == AgentRole.Preset || submitting != null ||
            blocks.any { it.pluginStatus == "running" }

    val presetExitLocked
        get() = presetSetupPending || presetWorking

    fun assigned(role: AgentRole): SavedModel? =
        assignments[role]
            ?: assignments[AgentRole.Preset].takeIf { role == AgentRole.Plugin }
            ?: assignments[AgentRole.Chat].takeIf { role.descriptionKind == null }
}
