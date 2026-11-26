using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MiniRP.Core;

/// <summary>Coordinates serialized workspace changes; platform I/O replies never wait in that queue.</summary>
public sealed partial class Engine : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonObject> events = new();
    private readonly Channel<JsonObject> commands = Channel.CreateUnbounded<JsonObject>(new() { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, AgentSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> trace = new(StringComparer.Ordinal);
    private readonly object presetTraceGate = new();
    private readonly Dictionary<string, WeakReference<PresetConversation>> presetTraces = new(StringComparer.Ordinal);
    private readonly Task worker;
    private CancellationTokenSource? active;
    private TurnInbox? inbox;
    private string operationEnsembleChat = "";
    private readonly AsyncLocal<string?> operationLocale = new();
    private sealed record Routing(Provider Default, JsonObject Agents, JsonObject Settings)
    {
        public Provider For(string role) => Agents[role] is JsonObject value
            ? Provider.From(value)
            : role == "plugin" ? For("preset") : Default;
        public (Provider? Image, Provider? Audio) DescriptionModels => (Description("image_description"), Description("audio_description"));

        private Provider? Description(string role) =>
            Agents[role] is JsonObject value && Provider.From(value) is { Url.Length: > 0, Model.Length: > 0 } provider
                ? provider with { DescribeImages = false, DescribeAudio = false } : null;
    }

    private readonly Task<WorkspaceCatalog> catalog;
    private readonly string attachmentRoot;
    private readonly bool seedDefaultPreset;
    private readonly Dictionary<string, string> titleRequests = new(StringComparer.Ordinal);
    private Routing routing = new(Provider.From(new()), new(), new());
    private Routing turnRouting;
    private ChatAgent? chat;
    private SearchAgent? search;
    private EnvAgent? environment;
    private PresetAgent? presets;
    private PresetConversation? presetConversation;
    private readonly AgentSession.Factory agentSessions;
    private readonly AgentContainer.Rooms containers;
    private readonly AgentContainer.Rooms presetContainers;
    private bool curlAvailable;
    private bool mediaAvailable;
    private readonly PluginAgentCoordinator pluginAgents;

    public Workspace Workspace
    {
        get; private set;
    }
    public HostPort Port
    {
        get;
    }
    public ModelClient Model
    {
        get;
    }

    public Engine(string root, bool seedDefaultPreset = true) : this(root, seedDefaultPreset, TimeProvider.System) { }

    internal Engine(string root, bool seedDefaultPreset, TimeProvider clock)
    {
        this.seedDefaultPreset = seedDefaultPreset;
        WorkspaceIdMigration.Run(Path.GetFullPath(root));
        Workspace = new(root, seedDefaultPreset);
        var initialPreset = Workspace.InitialPreset();
        if (initialPreset.Id.Length > 0) Workspace.UsePreset(initialPreset.Id);
        attachmentRoot = Path.Combine(Workspace.Root, "attachments");
        var initialWorkspace = Workspace;
        catalog = Task.Run(() => new WorkspaceCatalog(initialWorkspace));
        Port = new(Emit, lifetime.Token);
        plugins = new(Workspace.Root, Port, Emit, PluginApi, stateChanged: change =>
            Dispatch(new() { ["op"] = "_plugin_state_changed", ["change"] = change.DeepClone() }), clock: clock);
        turnRouting = routing;
        containers = new(Port, Workspace.Root);
        presetContainers = new(Port, Workspace.Root);
        agentSessions = new(Port, plugins, attachmentRoot, () => turnRouting.Settings.GetString("android_open_ui"), () => Workspace, containers,
            () => curlAvailable, presetContainers, () => operationLocale.Value, () => mediaAvailable,
            () => Volatile.Read(ref routing).DescriptionModels);
        pluginAgents = new(Workspace.Root, agentSessions, plugins, () => Volatile.Read(ref routing).For("plugin"),
            (world, trace) => CreatePageSource(world, SessionFiles.PresetDirectory(world.Root, world.PresetId), owner: "PluginAgent", workerTrace: trace),
            () =>
            {
                EmitPluginList();
                EmitPluginSettings();
            }, PluginAgentCompleted,
            lifetime.Token);
        Model = new(Port, () => Volatile.Read(ref routing).Default);
        worker = Task.Run(ProcessCommands);
        Dispatch(new() { ["op"] = "plugins_start" });
        Dispatch(new() { ["op"] = "_maintain_storage" });
        Emit(new()
        {
            ["kind"] = "ready",
            ["preset"] = initialPreset.Id,
            ["preset_name"] = initialPreset.Name,
            ["config"] = Workspace.Config.DeepClone(),
            ["catalog"] = Provider.Catalog()
        });
    }

    public void Dispatch(JsonObject command)
    {
        var operationName = command.GetString("op");
        if (IsTranscriptExport(command)) RequireIdleTranscript();
        if (operationName == "home" && command.GetBoolean("restore") &&
            command.GetString("preset") is { Length: > 0 } savedPreset && !WorkspaceIds.IsValid(savedPreset))
            command["preset"] = WorkspaceIds.Preset(savedPreset);
        foreach (var key in new[] { "preset", "chat", "conversation" })
            if (command.GetString(key) is { Length: > 0 } identifier) WorkspaceIds.Require(identifier);
        if (operationName is "delete_chat" or "delete_preset") WorkspaceIds.Require(command.GetString("id"));
        switch (operationName)
        {
            case "io_reply":
                Port.Reply(command);
                break;
            case "cancel":
                var operation = Volatile.Read(ref active);
                var targetAgent = command.GetString("agent");
                var roomPreparation = Volatile.Read(ref preparingRoom);
                if (targetAgent.Length == 0 || Volatile.Read(ref inbox)?.Agent == targetAgent ||
                    targetAgent == "chat" && roomPreparation is not null)
                    try { operation?.Cancel(); } catch (ObjectDisposedException) { }
                if (targetAgent is "" or "chat")
                    Volatile.Read(ref roomActivity)?.Cancel(command.GetString("conversation"));
                break;
            case "steer":
                var role = command.GetString("agent", "chat");
                if (command.GetString("text").Trim().Length == 0 || command.GetString("role", "user") is not ("user" or "system"))
                    throw new ArgumentException("A user or system text message is required.");
                if (role == "chat" && Volatile.Read(ref roomActivity) is not null)
                {
                    var message = (JsonObject)command.DeepClone();
                    message["op"] = "message";
                    message["chat"] = command.GetString("chat", Workspace.ChatId);
                    Dispatch(message);
                    break;
                }
                if (Volatile.Read(ref inbox)?.TryPost(command.GetString("chat"), role, (JsonObject)command.DeepClone(),
                        role == "chat" ? command.GetString("conversation") : "") != true)
                    throw new InvalidOperationException("That conversation is no longer generating.");
                break;
            case "interaction_open":
                plugins.OpenUrl(command.GetString("url"), PluginEnvironment());
                break;
            case "configure":
                var updated = new Routing(Provider.From(command.GetObject("provider")), command.GetObject("agents"), command.GetObject("settings"));
                var previous = Interlocked.Exchange(ref routing, updated);
                if (PresetConnection(previous.For("preset")) != PresetConnection(updated.For("preset")))
                    QueuePresetNotifications("");
                break;
            case "models":
                _ = DiscoverModels(Provider.From(command.GetObject("provider")), command.GetString("request"));
                break;
            case "model_effort_levels":
                _ = DiscoverModelEffortLevels(Provider.From(command.GetObject("provider")), command.GetString("request"));
                break;
            case "plugin_page" when command.GetString("id") == "base" && !command.ContainsKey("_plugin") &&
                                          Volatile.Read(ref inbox) is not null:
                // Built-in settings are global and can be read while a model is replying.
                EmitPluginPage(command);
                break;
            case "library_page":
                _ = Task.Run(() => LibraryPage(command));
                break;
            case "transcript_page":
                _ = Task.Run(() => TranscriptPage(command));
                break;
            case "ensemble_archive":
                _ = Task.Run(() => ReadEnsembleArchive(command));
                break;
            case "asset_open":
            case "display_asset_open":
            case "preset_background_open":
            case "asset_pull":
            case "asset_close":
                _ = Task.Run(() => ReadAsset(command));
                break;
            default:
                if (operationName is "home" or "select_chat" or "start_chat")
                    try { Volatile.Read(ref preparingRoom)?.Cancel(); } catch (ObjectDisposedException) { }
                if (!commands.Writer.TryWrite((JsonObject)command.DeepClone()))
                {
                    throw new ObjectDisposedException(nameof(Engine));
                }
                break;
        }
    }

    public JsonArray Poll()
    {
        var batch = new JsonArray();
        while (batch.Count < 100 && events.TryDequeue(out var item))
        {
            batch.AddNode(item);
        }
        return batch;
    }

    private void Emit(JsonObject item) => Emit(item, null);

    private void Emit(JsonObject item, SessionModelInfo? sessionInfo)
    {
        events.Enqueue(ToolTraceEvent(item, Workspace.PresetId));
        if (item.ContainsKey("asset_links"))
        {
            var visible = (JsonObject)item.DeepClone();
            visible.Remove("asset_links");
            PublishPluginEvent(visible, sessionInfo);
        }
        else PublishPluginEvent(item, sessionInfo);
    }

    private void Record(JsonObject item, JsonArray? destination = null, SessionModelInfo? sessionInfo = null,
        PresetConversation? workerOwner = null)
    {
        IndexDisplayAssets(item, item.GetString("block"), item.GetString("conversation"), workerOwner?.PresetId);
        if (workerOwner is not null || PresetConversation.IsPreset(item))
        {
            lock (presetTraceGate)
            {
                var owner = workerOwner ?? presetConversation!;
                if (workerOwner is null) Emit(item, sessionInfo);
                else events.Enqueue(ToolTraceEvent(item, owner.PresetId));
                var blocks = workerOwner is null ? trace : owner.WorkerTrace;
                if (!RecordTrace(item, blocks, owner.Blocks)) return;
                if (owner.PresetId == Workspace.PresetId) RecordWebDisplay(item, blocks, "", Workspace);
                if (workerOwner is not null && item.GetString("kind") == "plugin_agent_status" && item.GetString("status") != "running")
                {
                    var run = item.GetString("plugin_run");
                    foreach (var id in owner.WorkerTrace.Keys.Where(id => id == run || id.StartsWith(run + "-", StringComparison.Ordinal)).ToArray())
                        owner.WorkerTrace.Remove(id);
                    owner.Save();
                }
            }
            return;
        }
        lock (Workspace.RoomGate)
        {
            Emit(item, sessionInfo);
            if (destination is null) Workspace.Chat["blocks"] ??= new JsonArray();
            RecordTrace(item, trace, destination ?? Workspace.Chat.GetArray("blocks"));
            RecordWebDisplay(item, trace, item.GetString("conversation"), Workspace);
        }
    }

    private static bool RecordTrace(JsonObject item, Dictionary<string, JsonObject> blocks, JsonArray destination)
    {
        var kind = item.GetString("kind");
        var id = item.GetString("block");
        if (kind is "turn_end" or "open_ui_end" && item.GetBoolean("retrying") && blocks.TryGetValue(id, out var interrupted))
            interrupted["retrying"] = true;
        if (kind is not ("delta" or "tool_start" or "tool_end" or "context_trace" or "open_ui_delta" or "open_ui_card" or "plugin_agent_status") &&
            item["asset_links"] is not JsonObject)
            return false;
        if (!blocks.TryGetValue(id, out var block))
        {
            block = new()
            {
                ["id"] = id,
                ["agent"] = item.GetString("agent"),
                ["speaker"] = JsonValues.Clone(item["speaker"]),
                ["chat"] = JsonValues.Clone(item["chat"]),
                ["conversation"] = JsonValues.Clone(item["conversation"]),
                ["content"] = "",
                ["reasoning"] = ""
            };
            if (item.ContainsKey("plugin_agent"))
                foreach (var key in new[] { "plugin_agent", "plugin_agent_name", "plugin_run" })
                    block[key] = JsonValues.Clone(item[key]);
            if (item["participant"] is { } participant)
            {
                block["participant"] = participant.DeepClone();
                if (item["session"] is { } session) block["session"] = session.DeepClone();
            }
            blocks[id] = block;
            destination.AddNode(block);
        }
        if (kind == "open_ui_card")
        {
            foreach (var key in new[] { "open_ui", "open_ui_title", "open_ui_state", "open_ui_revision", "open_ui_host_action", "plugin_card" })
                block[key] = JsonValues.Clone(item[key]);
        }
        else if (kind == "open_ui_delta")
        {
            block["open_ui"] = block.GetString("open_ui") + item.GetString("text");
        }
        else if (kind == "delta")
        {
            var type = item.GetString("type");
            block[type] = block.GetString(type) + item.GetString("text");
        }
        else if (kind == "context_trace")
        {
            block["compaction"] = true;
            block["running"] = item.GetBoolean("running");
            block["result"] = item.GetString("text");
        }
        else if (kind == "plugin_agent_status")
        {
            block["plugin_status"] = item.GetString("status");
            block["content"] = item.GetString("text");
        }
        else if (kind is "tool_start" or "tool_end")
        {
            block["tool"] = item.GetString("name");
            block["running"] = kind == "tool_start";
            block[kind == "tool_start" ? "arguments" : "result"] = kind == "tool_start" ? item.GetString("arguments") : item.GetString("text");
            var phase = kind == "tool_start" ? ToolPresentation.Arguments : ToolPresentation.Result;
            var referenceKey = ToolPresentation.ReferenceKey(phase);
            if (item[ToolTrace.Source] is JsonObject source) block[ToolTrace.Source] = source.DeepClone();
            if (kind == "tool_end" && item[ToolTrace.ResultPlain] is { } plain) block[ToolTrace.ResultPlain] = plain.DeepClone();
            if (item[referenceKey] is JsonObject reference) block[referenceKey] = reference.DeepClone();
            else block.Remove(referenceKey);
        }
        if (item["asset_links"] is JsonObject links) block["asset_links"] = links.DeepClone();
        if (item.ContainsKey("plugin_sequence")) block["plugin_sequence"] = JsonValues.Clone(item["plugin_sequence"]);
        return true;
    }

    private PresetConversation LoadPresetConversation(Workspace workspace)
    {
        var preset = workspace.PresetId;
        if (presetTraces.TryGetValue(preset, out var previous) && previous.TryGetTarget(out var existing)) return existing;
        var conversation = new PresetConversation(workspace);
        conversation.Load(workspace);
        presetTraces[preset] = new(conversation);
        return conversation;
    }

    private bool HasPresetTrace()
    {
        lock (presetTraceGate) return presetConversation?.Blocks.Count > 0;
    }

    private void SavePresetTrace()
    {
        lock (presetTraceGate) presetConversation?.Save();
    }

    private async Task ProcessCommands()
    {
        await foreach (var command in commands.Reader.ReadAllAsync())
        {
            if (lifetime.IsCancellationRequested)
            {
                break;
            }
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var op = command.GetString("op");
            var modifiesChat = op is "message" or "system_message" or "start_chat" or "branch" or "rename" or "save_prompt" or "save_search_mode" or "reset_world" or "save"
                || op == "select_chat" && command.GetString("id").Length == 0;
            var ensemblePost = ensemble is not null && op is ("message" or "system_message") && command.GetString("agent", "chat") == "chat";
            var showsBusy = modifiesChat && !ensemblePost || op is "select_chat" or "create_preset" or "delete_chat" or "delete_preset";
            turnRouting = Volatile.Read(ref routing);
            operationEnsembleChat = command.GetString("conversation");
            operationLocale.Value = AgentLanguage.Normalize(command.GetString("locale"));
            active = operation;
            if (showsBusy)
            {
                Emit(new() { ["kind"] = "busy", ["value"] = true });
            }
            try
            {
                if (op == "_maintain_storage") ConversationStorage.Maintain(Workspace.Root, operation.Token);
                else await Execute(command, operation.Token);
            }
            catch (OperationCanceledException)
            {
                FinishPendingTrace("Cancelled.");
                Emit(new()
                {
                    ["kind"] = "cancelled",
                    ["request"] = command.GetString("request")
                });
            }
            catch (Exception error)
            {
                FinishPendingTrace(error.Message);
                var failure = new JsonObject
                {
                    ["kind"] = "error",
                    ["request"] = command.GetString("request"),
                    ["model_turn"] = op is "message" or "_preset_interview" or "_preset_plugin_notifications" or "start_chat" or "greet_chat",
                    ["agent"] = command.GetString("agent", "chat"),
                    ["message"] = error.Message
                };
                if (op == "_preset_interview") failure["preset_interview"] = true;
                Emit(failure);
            }
            finally
            {
                active = null;
                operationEnsembleChat = "";
                operationLocale.Value = null;
                try
                {
                    if (op is "message" or "_preset_interview" && command.GetString("agent") == "preset")
                    {
                        SavePresetTrace();
                    }
                    if (modifiesChat && Workspace.ChatId.Length > 0)
                    {
                        Workspace.Save();
                        await Index(Workspace);
                    }
                }
                catch (Exception error)
                {
                    Emit(new() { ["kind"] = "error", ["message"] = error.Message });
                }
                if (showsBusy)
                {
                    Emit(new() { ["kind"] = "busy", ["value"] = false });
                }
            }
        }
    }

    private async Task LibraryPage(JsonObject command)
    {
        var reply = new JsonObject { ["kind"] = "library_page", ["request"] = command.GetString("request") };
        try
        {
            var index = await catalog;
            var preset = command.GetString("preset");
            var page = index.Page(preset, Math.Max(0, command.GetInt32("offset")), Math.Clamp(command.GetInt32("limit"), 1, 100));
            if (preset.Length == 0)
            {
                foreach (var item in page.OfType<JsonObject>())
                    item["ensemble"] = IsEnsemblePreset(item.GetString("id"));
            }
            reply["items"] = page;
        }
        catch (Exception error)
        {
            reply["error"] = error.Message;
        }
        Emit(reply);
    }

    private async Task Index(Workspace workspace)
    {
        var index = await catalog;
        index.Preset(workspace.PresetId, workspace.Config.GetString("name", workspace.PresetId));
        string title, updated;
        lock (workspace.RoomGate)
        {
            title = workspace.Chat.GetString("name");
            updated = workspace.Chat.GetString("updated_at");
        }
        index.Chat(workspace.PresetId, workspace.ChatId, title, updated);
        Emit(new() { ["kind"] = "library_changed", ["preset"] = workspace.PresetId });
    }

    private async Task DiscoverModels(Provider requestedProvider, string request)
    {
        Emit(new() { ["kind"] = "models_loading", ["provider"] = requestedProvider.Id, ["request"] = request, ["value"] = true });
        try
        {
            var models = await Model.Models(requestedProvider, lifetime.Token);
            Emit(new() { ["kind"] = "models", ["provider"] = requestedProvider.Id, ["request"] = request, ["items"] = models });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Emit(new() { ["kind"] = "models_error", ["provider"] = requestedProvider.Id, ["request"] = request, ["message"] = error.Message });
            if (request.Length == 0)
            {
                Emit(new() { ["kind"] = "error", ["message"] = error.Message });
            }
        }
        finally
        {
            Emit(new() { ["kind"] = "models_loading", ["provider"] = requestedProvider.Id, ["request"] = request, ["value"] = false });
        }
    }

    private async Task DiscoverModelEffortLevels(Provider provider, string request)
    {
        var reply = new JsonObject { ["kind"] = "model_effort_levels", ["request"] = request };
        try
        {
            reply["effort_levels"] = await Model.EffortLevels(provider, lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { reply["error"] = error.Message; }
        Emit(reply);
    }

    private void FinishPendingTrace(string message)
    {
        foreach (var block in trace.Values.Where(block => block.GetBoolean("running")).ToArray())
        {
            Record(new()
            {
                ["kind"] = block.GetBoolean("compaction") ? "context_trace" : "tool_end",
                ["agent"] = block.GetString("agent"),
                ["block"] = block.GetString("id"),
                ["chat"] = JsonValues.Clone(block["chat"]),
                ["conversation"] = JsonValues.Clone(block["conversation"]),
                ["name"] = block.GetString("tool"),
                ["text"] = message
            });
        }
    }

    private async Task SelectConversation(string preset, string? id, CancellationToken token, bool fromHomeSend = false)
    {
        if (ensemble is not null && id == Workspace.ChatId && preset == Workspace.PresetId)
        {
            activeEnsembleChat = "";
            EmitLoaded(presetConversation!, fromHomeSend);
            QueuePresetNotifications(preset);
            return;
        }
        Volatile.Read(ref roomActivity)?.Cancel();
        var nextWorkspace = new Workspace(Workspace.Root, seedDefaultPreset: false);
        nextWorkspace.Select(preset, id);
        if (string.IsNullOrEmpty(id) && nextWorkspace.Mode != "ensemble" && nextWorkspace.ChatGreeting.Length > 0)
            nextWorkspace.Chat[GreetingPending] = true;
        nextWorkspace.Chat["mode"] ??= nextWorkspace.Mode;
        var nextEnsemble = nextWorkspace.Mode == "ensemble" ? new EnsembleConversation(nextWorkspace) : null;
        ConversationTitle.ClearLegacyTitle(nextWorkspace.Chat);
        var nextPreset = LoadPresetConversation(nextWorkspace);
        var nextSessions = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        var nextCharacters = new Dictionary<string, RoomCharacter>(StringComparer.Ordinal);
        var nextWeb = CreatePageSource(nextWorkspace, nextWorkspace.ChatRoot);
        try
        {
            foreach (var role in new[] { "chat", "search", "env", "preset" })
            {
                var database = role == "preset" ? nextPreset.Database : SessionFiles.Conversation(nextWorkspace.ChatRoot, role);
                var previous = role == "preset" ? nextPreset.LegacySession : nextWorkspace.Chat.GetObject(role);
                nextSessions[role] = CreateWorkspaceSession(role, database, previous, nextWorkspace);
            }
            if (nextEnsemble is not null)
            {
                if (nextWorkspace.ChatGreeting.Length > 0)
                    containers.Select(new(nextWorkspace.PresetId, nextWorkspace.ChatId, nextSessions["chat"].Db.Path));
                await PrepareRoom(nextWorkspace, nextEnsemble, nextCharacters, string.IsNullOrEmpty(id), token);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            if (nextEnsemble is not null && nextWorkspace.ChatGreeting.Length > 0)
                containers.Select(Workspace.ChatId.Length > 0
                    ? new(Workspace.PresetId, Workspace.ChatId, sessions["chat"].Db.Path) : null);
            foreach (var character in nextCharacters.Values) character.Dispose();
            foreach (var session in nextSessions.Values)
            {
                session.Dispose();
            }

            throw;
        }
        CloseRoomCharacters();
        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }
        Volatile.Write(ref roomCharacters, nextCharacters);
        sessions.Clear();
        trace.Clear();
        displayAssets.Clear();
        Workspace = nextWorkspace;
        ensemble = nextEnsemble;
        activeEnsembleChat = "";
        presetConversation = nextPreset;
        foreach (var session in nextSessions)
        {
            sessions[session.Key] = session.Value;
        }
        containers.Select(new(Workspace.PresetId, Workspace.ChatId, sessions["chat"].Db.Path));

        search = new(Workspace, sessions["search"], nextWeb);
        chat = new(Workspace, sessions["chat"], search);
        environment = new(Workspace, sessions["env"], CreatePageSource(Workspace, Workspace.ChatRoot, owner: "EnvAgent"));
        presets = CreatePresetAgent(sessions["preset"]);
        if (string.IsNullOrEmpty(id)) Workspace.Save();
        if (nextEnsemble is not null)
        {
            var activity = new RoomActivity(this, nextEnsemble, nextCharacters);
            activity.Start();
            Volatile.Write(ref roomActivity, activity);
        }
        EmitLoaded(nextPreset, fromHomeSend);
        QueuePresetNotifications(preset);
    }

    private void EmitLoaded(PresetConversation conversation, bool fromHomeSend)
    {
        lock (Workspace.RoomGate)
        lock (presetTraceGate)
        {
            var history = WebHistory ? new JsonObject() : null;
            var item = new JsonObject
            {
                ["kind"] = "loaded",
                ["from_home_send"] = fromHomeSend,
                ["draft"] = JsonValues.Clone(Workspace.Chat["draft"]),
                ["ensemble_chats"] = ensemble?.List(),
                ["participants"] = ensemble?.Agents.DeepClone(),
                ["activity"] = ensemble is null ? null : Volatile.Read(ref roomActivity)?.Snapshot() ?? new JsonArray(),
                ["private_in_story"] = ensemble?.DirectMessagesInStory ?? true,
                ["chat"] = new JsonObject
                {
                    ["id"] = Workspace.ChatId,
                    ["name"] = Workspace.Chat.GetString("name"),
                    ["blocks"] = ProjectRoomTranscript(conversation, history)
                },
                ["preset"] = Workspace.PresetId,
                ["config"] = Workspace.Config.DeepClone()
            };
            if (history is not null) item["history"] = history;
            Emit(item);
        }
    }

    private PageSource CreatePageSource(Workspace workspace, string sessionRoot, string? owner = null, Action<JsonObject>? workerTrace = null)
    {
        var workerRouting = workerTrace is null ? null : Volatile.Read(ref routing);
        Routing Selected() => workerRouting ?? turnRouting;
        var browser = new BrowserAgent(Port, job =>
        {
            var context = owner is "PresetAgent" or "PluginAgent"
                ? new JsonObject { ["agent"] = "fetch", ["preset"] = workspace.PresetId }
                : PluginEnvironment("fetch");
            var session = "fetch-" + job;
            context["session"] = session;
            return agentSessions.Open("fetch", SessionFiles.Fetch(sessionRoot, job), () => Selected().For("fetch"),
                () => context, (item, info) =>
                {
                    item["session"] = session;
                    if (owner is not null)
                    {
                        item["agent"] = owner;
                        item["speaker"] = "BrowserAgent";
                    }
                    if (workerTrace is null) Record(item, sessionInfo: info);
                    else workerTrace(item);
                });
        }, () => workspace.AgentPrompt("fetch"));
        return new(Port, browser, () => Selected().Settings);
    }

    private PresetAgent CreatePresetAgent(AgentSession session)
    {
        var preset = Workspace.PresetId;
        var web = CreatePageSource(Workspace, SessionFiles.PresetDirectory(Workspace.Root, preset), owner: "PresetAgent");
        return new(Workspace, session, plugins, web);
    }

    private AgentSession CreateWorkspaceSession(string role, string database, JsonObject? previous, Workspace workspace)
    {
        var session = agentSessions.Open(role, database, () => turnRouting.For(role), () => PluginEnvironment(role),
            (item, info) => Record(item, sessionInfo: info),
            () => EnsembleSkills(role),
            role == "env" ? target => ensemble?.List().Any(chat => chat.GetString("id") == target) == true : null,
            presetWorkspace: role == "preset" ? workspace : null);
        try
        {
            if (!session.HasHistory && previous?["history"] is JsonArray legacy)
                session.Restore(legacy, previous.GetString("summary"));
            session.ConfigureWorldMode(role == "chat" ? workspace.AgenticSearch : null);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void EnsurePresetSession()
    {
        if (presetConversation is not null) return;
        EnsurePresetSelected();
        var conversation = LoadPresetConversation(Workspace);
        var session = CreateWorkspaceSession("preset", conversation.Database, conversation.LegacySession, Workspace);
        sessions.Add("preset", session);
        presetConversation = conversation;
        presets = CreatePresetAgent(session);
    }

    private void OpenPresetEditor(string request, bool created = false, bool selected = false)
    {
        EnsurePresetSession();
        lock (presetTraceGate)
        {
            var history = WebHistory ? new JsonObject() : null;
            var item = new JsonObject
            {
                ["kind"] = "preset_opened",
                ["request"] = request,
                ["created"] = created,
                ["selected"] = selected,
                ["preset"] = Workspace.PresetId,
                ["config"] = Workspace.Config.DeepClone(),
                ["blocks"] = ProjectPresetTranscript(presetConversation!, history)
            };
            if (history is not null) item["history"] = history;
            Emit(item);
        }
    }

    private static JsonObject LoadedBlock(JsonObject block, string? fallbackId = null)
    {
        var cleaned = (JsonObject)block.DeepClone();
        if (string.IsNullOrWhiteSpace(cleaned.GetString("id"))) cleaned["id"] = fallbackId ?? JsonValues.NewId();
        if (!cleaned.ContainsKey("kind") && cleaned.GetString("role") is "user" or "system")
            cleaned["kind"] = cleaned.GetString("role");
        cleaned["running"] = false;
        return cleaned;
    }

    private async Task Execute(JsonObject command, CancellationToken token)
    {
        if (command.GetString("_plugin") is { Length: > 0 } pluginId)
        {
            plugins.RequireGeneration(pluginId, command.GetString("_plugin_generation"), command.GetString("preset"));
            if (command.GetString("action") is not ("createChat" or "createRoom" or "createPreset")) RequirePreset(command);
            if (command.GetString("op") == "system_message") RequireChat(command);
            if (command["_character_origin"] is JsonObject actor)
                RequirePluginMembership(actor, command.GetString("chat"), command.GetString("conversation"), command);
        }
        if (await ExecuteExtensionCommand(command, token)) return;
        switch (command.GetString("op"))
        {
            case "_container_available":
                containers.EnableWarmStart();
                return;
            case "_curl_available":
                curlAvailable = true;
                return;
            case "_ffmpeg_available":
                mediaAvailable = true;
                return;
            case "_preset_plugin_notifications":
                await RunPresetNotifications(command.GetString("preset"), token);
                return;
            case "_transcript_checkpoint":
                TranscriptCheckpoint();
                return;
            case "list_chats":
                var listedPreset = command.GetString("preset");
                Emit(new()
                {
                    ["kind"] = "chats",
                    ["preset"] = listedPreset,
                    ["setup_pending"] = Workspace.PresetSetupPending(listedPreset),
                    ["items"] = Workspace.Chats(listedPreset)
                });
                return;
            case "list_presets":
                Emit(new()
                {
                    ["kind"] = "presets",
                    ["items"] = Workspace.Presets()
                });
                return;
            case "select_chat":
                var selectedPreset = command.GetString("preset");
                if (RedirectToPresetSetup(selectedPreset, command)) return;
                await SelectConversation(selectedPreset, command.GetString("id"), token);
                await GreetChat(token);
                return;
            case "start_chat":
                var startingPreset = command.GetString("preset", Workspace.PresetId);
                if (!RedirectToPresetSetup(startingPreset, command)) await StartChat(startingPreset, token);
                return;
            case "select_ensemble_chat":
                SelectEnsembleChat(command);
                return;
            case "open_ensemble_dm":
                OpenDirectEnsembleChat(command);
                return;
            case "ensemble_member_trace":
                ReadEnsembleMemberTrace(command);
                return;
            case "greet_chat":
                if (Workspace.ChatId.Length > 0 && command.GetString("chat") == Workspace.ChatId &&
                    command.GetString("conversation") == activeEnsembleChat)
                    await GreetChat(token);
                return;
            case "rename_ensemble_chat":
                RequireActiveChat(command);
                var hub = ensemble ?? throw new InvalidOperationException("Choose an ensemble room.");
                lock (hub.SyncRoot)
                {
                    var conversation = command.GetString("conversation");
                    if (hub.PrivateParticipant(conversation).Length > 0)
                        throw new ArgumentException("A direct chat uses the character's name.");
                    hub.Chat(conversation)["name"] = PresetDesign.Name(command.GetString("name"));
                    Workspace.Save();
                    EnsembleChatsChanged();
                }
                return;
            case "create_preset":
                var name = command.GetString("name", "Preset");
                var open = command.GetBoolean("open");
                var preset = Workspace.CreatePreset(name, interview: command.GetBoolean("interview", true));
                (await catalog).Preset(preset, name);
                plugins.Publish("preset.created", new() { ["preset"] = preset, ["title"] = name },
                    new() { ["preset"] = preset }, exclude: "base");
                Emit(new() { ["kind"] = "library_changed" });
                if (open)
                {
                    ShowHome(preset, notify: false);
                    OpenPresetEditor(command.GetString("request"), created: true);
                    QueuePresetInterview(command);
                    return;
                }
                Emit(new()
                {
                    ["kind"] = "presets",
                    ["items"] = Workspace.Presets()
                });
                return;
            case "open_preset":
                RequireChat(command);
                RequirePreset(command);
                OpenPresetEditor(command.GetString("request"));
                QueuePresetInterview(command);
                QueuePresetNotifications(Workspace.PresetId);
                return;
            case "_preset_interview":
                if (command.GetString("preset") != Workspace.PresetId || !Workspace.SetupPending)
                    return;
                EnsurePresetSession();
                // A crash can leave partial prose or plugin status records in the
                // display trace. The session history is committed after a model turn.
                if (sessions["preset"].HasAssistantReply ||
                    turnRouting.For("preset").Model.Length == 0 || turnRouting.For("preset").Url.Length == 0) return;
                var bootstrap = await plugins.InvokeBaseHook("preset.created",
                    new() { ["locale"] = operationLocale.Value ?? AgentLanguage.Default }, PluginEnvironment("preset"), token);
                var setupInput = bootstrap.GetString("text");
                if (HasPresetTrace())
                    setupInput += "\n\nA previous setup attempt was interrupted. Inspect the existing preset, then resume from its current configuration. Avoid repeating completed changes.";
                await RunMessage("preset", setupInput, false, new(), token,
                    inputRole: "system", editorTurn: true, hiddenInput: true);
                return;
            case "resume_preset_interview":
                RequirePreset(command);
                QueuePresetInterview(command);
                return;
            case "title_result":
                await ApplyTitle(command);
                return;
        }
        if (chat is null && command.GetString("op") is "message" or "system_message" && command.GetString("agent", "chat") == "chat")
        {
            var target = command.GetString("preset", Workspace.PresetId);
            if (Workspace.PresetSetupPending(target))
                throw new InvalidOperationException("Finish setting up the preset with PresetAgent before starting a chat.");
            await SelectConversation(target, null, token, fromHomeSend: command.GetString("op") == "message");
        }
        if (command.GetString("op") == "save_prompt")
        {
            RequireChat(command);
            EnsurePresetSelected();
            var agent = command.GetString("agent", "chat");
            var text = command.GetString("text");
            if (agent is not ("chat" or "env" or "preset" or "search" or "fetch" or "title") || text.Length > 16000)
                throw new ArgumentException("A supported agent and up to 16,000 characters are required.");
            var config = (JsonObject)Workspace.Config.DeepClone();
            var key = agent + "_agent";
            var settings = config.GetObject(key);
            if (settings.Parent is null) config[key] = settings;
            settings["system_prompt"] = text;
            Workspace.CommitConfig(config);
            EmitConfig();
            return;
        }
        switch (command.GetString("op"))
        {
            case "message":
                var messageAgent = command.GetString("agent", "chat");
                RequireAgent(command, messageAgent);
                if (messageAgent == "chat")
                    await GreetChat(token);
                if (messageAgent == "chat" && ensemble is not null)
                {
                    await SubmitEnsembleMessage(command, token);
                    return;
                }
                await RunMessage(messageAgent, command.GetString("text"), command.GetBoolean("stage"),
                    command.GetArray("attachments"), token, command.GetString("role", "user"), command.GetString("request"),
                    command["open_ui_action"] as JsonObject, command.GetBoolean("editor_turn"));
                break;
            case "system_message":
                var systemAgent = command.GetString("agent", "chat");
                var systemText = command.GetString("text").Trim();
                if (systemAgent is not ("chat" or "env" or "preset") || systemText.Length == 0)
                    throw new ArgumentException("A system message requires a conversation agent and text.");
                RequireAgent(command, systemAgent);
                if (systemAgent == "chat" && (ensemble is not null || command.GetBoolean("_hidden_notice")))
                {
                    NotifyChat(command);
                    return;
                }
                sessions[systemAgent].Insert(systemText, "system");
                RecordInput(systemAgent, systemText, "system", new(), false);
                SavePresetTrace();
                break;
            case "branch":
                if (command.GetString("chat") != Workspace.ChatId)
                {
                    throw new InvalidOperationException("The conversation has changed.");
                }
                var mode = command.GetString("mode", "replay");
                if (mode is not ("replay" or "edit" or "reply"))
                    throw new ArgumentException("Unknown branch mode.");
                var branch = ConversationVersions.Fork(Workspace, command.GetString("block"),
                    sessions.Keys.Where(role => role != "preset"), afterReply: mode == "reply", seedDraft: mode == "edit");
                await SelectConversation(Workspace.PresetId, branch.Id, token);
                if (mode == "replay")
                    await RunMessage("chat", branch.Input, false, branch.Attachments, token, openUiAction: branch.OpenUiAction);
                break;
            case "generate_title":
                RequireActiveChat(command);
                StartTitle(force: true);
                break;
            case "memory":
                var memoryAgent = command.GetString("agent", "chat");
                RequireAgent(command, memoryAgent);
                var tool = command.GetString("tool", "db_list");
                var arguments = command.GetObject("arguments");
                JsonObject memory;
                if (ensemble is { } group && memoryAgent is "chat" or "search")
                {
                    var member = group.Agent(command.GetString("participant")).GetString("id");
                    if (LoadedCharacterSession(member, memoryAgent) is { } memberSession)
                        memory = memberSession.Db.Call(tool, arguments);
                    else
                    {
                        using var database = new MemoryDb(group.Database(member, memoryAgent));
                        memory = database.Call(tool, arguments);
                    }
                }
                else memory = sessions[memoryAgent].Db.Call(tool, arguments);
                Emit(new()
                {
                    ["kind"] = "memory",
                    ["request"] = command.GetString("request"),
                    ["result"] = memory
                });
                break;
            case "rename":
                RequireActiveChat(command);
                titleRequests.Remove(Workspace.ChatId);
                lock (Workspace.RoomGate)
                {
                    Workspace.Chat["name"] = command.GetString("name");
                    Workspace.Chat["title_source"] = "manual";
                }
                Emit(new() { ["kind"] = "title", ["id"] = Workspace.ChatId, ["preset"] = Workspace.PresetId,
                    ["ensemble"] = ensemble is not null, ["name"] = Workspace.Chat.GetString("name") });
                break;
            case "save_search_mode":
                RequireChat(command);
                RequirePreset(command);
                EnsurePresetSelected();
                var updated = (JsonObject)Workspace.Config.DeepClone();
                updated["agentic_search"] = command.GetBoolean("enabled", true);
                Workspace.CommitConfig(updated);
                if (sessions.TryGetValue("chat", out var chatSession))
                    chatSession.ConfigureWorldMode(Workspace.AgenticSearch);
                EmitConfig();
                break;
            case "reset_world":
                RequireActiveChat(command);
                Workspace.Commit(Workspace.InitialWorld());
                break;
            case "set_world_date" when command.GetString("_plugin").Length > 0:
                RequireActiveChat(command);
                WorldCalendar.RequirePluginMode(Workspace.Config);
                var date = WorldCalendar.RequireDate(command.GetString("date"));
                lock (Workspace.RoomGate)
                {
                    if (WorldCalendar.Render(date) == Workspace.WorldDate) break;
                    var state = (JsonObject)Workspace.State.DeepClone();
                    WorldCalendar.Set(Workspace.Config, state, date, WorldCalendar.Today);
                    Workspace.Commit(state);
                }
                EmitWorldChanged(command.GetString("request"));
                break;
            case "save":
                break;
            default:
                throw new ArgumentException("Unknown operation: " + command.GetString("op"));
        }
    }

    private void QueuePresetInterview(JsonObject command)
    {
        if (Workspace.SetupPending)
            Dispatch(new() { ["op"] = "_preset_interview", ["preset"] = Workspace.PresetId,
                ["agent"] = "preset", ["locale"] = AgentLanguage.Normalize(command.GetString("locale")) ??
                    AgentLanguage.Normalize(PluginEnvironment("preset").GetString("locale")) ?? AgentLanguage.Default });
    }

    private bool RedirectToPresetSetup(string preset, JsonObject command)
    {
        if (!Workspace.PresetSetupPending(preset)) return false;
        OpenPresetHome(preset, command);
        return true;
    }

    private async Task RunMessage(string role, string input, bool advanceRequested, JsonArray attachments, CancellationToken token,
        string inputRole = "user", string request = "", JsonObject? openUiAction = null, bool editorTurn = false,
        bool hiddenInput = false, bool userInitiated = true)
    {
        if (role is not ("chat" or "env" or "preset"))
        {
            throw new ArgumentException("Unknown conversation agent.");
        }
        if (inputRole is not ("user" or "system") || (inputRole == "system" && attachments.Count > 0) ||
            (string.IsNullOrWhiteSpace(input) && attachments.Count == 0))
            throw new ArgumentException("A message must contain text or supported attachments.");
        var modelInput = input;
        if (openUiAction is not null)
        {
            var source = openUiAction.GetString("block");
            var data = openUiAction.GetObject("data");
            if (role != "chat" || inputRole != "user" || source.Length == 0 ||
                !Workspace.Chat.GetArray("blocks").Any(block => block.GetString("id") == source &&
                    block.GetString("open_ui").Length > 0))
                throw new ArgumentException("The interactive reply is no longer available.");
            var serialized = JsonValues.Serialize(data);
            if (serialized.Length > 16384) throw new ArgumentException("The interactive reply data is too large.");
            if (data.Count > 0)
                modelInput += "\n\nData from the user's interactive reply (JSON):\n" + serialized;
        }
        attachments = await PrepareAttachments(attachments, PluginEnvironment(role), token);
        var session = sessions[role];
        if (role == "chat") session.ConfigureWorldMode(Workspace.AgenticSearch);
        var turn = new TurnInbox(Workspace.ChatId, role, message =>
        {
            RecordInput(role, message.GetString("text"), message.GetString("role", "user"), new(), false,
                blockId: message[TurnInbox.QueuedBlockId]?.GetValue<string>(),
                interjection: message.GetString("role", "user") == "user");
            return null;
        }, preset: Workspace.PresetId, queued: message => PreviewSteer(role, message));
        session.Inbox = turn;
        BeginInteractiveTurn(turn, editorTurn, userInitiated);
        var turnStart = Workspace.Chat.GetArray("blocks").Count;
        string answer;
        var completed = false;
        try
        {
            if (!hiddenInput)
                RecordInput(role, input, inputRole, attachments, inputRole == "user", openUiAction, consumeDraft: role == "chat");
            if (request.Length > 0)
                Emit(new() { ["kind"] = "message_accepted", ["request"] = request, ["chat"] = Workspace.ChatId });
            var worldBefore = Workspace.State;
            var presetNameBefore = Workspace.Config.GetString("name", Workspace.PresetId);
            answer = role switch
            {
                "env" => await environment!.Run(input, advanceRequested, token, attachments, inputRole),
                "preset" => await presets!.Run(input, token, attachments, inputRole, privateInput: hiddenInput),
                _ => await chat!.Run(modelInput, token, attachments, inputRole, privateInput: hiddenInput)
            };
            if (string.IsNullOrWhiteSpace(answer))
                throw new IOException("The model finished without a visible reply.");
            if (!ReferenceEquals(worldBefore, Workspace.State))
                EmitWorldChanged(request, role);
            if (role != "preset")
            {
                Workspace.Chat.GetArray("messages").AddNode(new JsonObject { ["role"] = "assistant", ["agent"] = role, ["content"] = answer });
            }
            Emit(new() { ["kind"] = "completed", ["agent"] = role, ["text"] = answer }, session.SessionInfo);
            if (role == "preset")
            {
                var name = Workspace.Config.GetString("name", Workspace.PresetId);
                if (name != presetNameBefore)
                {
                    (await catalog).Preset(Workspace.PresetId, name);
                    Emit(new() { ["kind"] = "library_changed", ["preset"] = Workspace.PresetId, ["presets_changed"] = true });
                }
                EmitConfig();
            }
            if (role == "chat" && !hiddenInput) StartTitle(force: false);
            completed = true;
        }
        finally
        {
            EndInteractiveTurn(turn, session, interrupted: !completed && !token.IsCancellationRequested);
        }
        if (role == "chat") RecordReplyEnd(turnStart, answer);
    }

    private async Task<JsonArray> PrepareAttachments(JsonArray files, JsonObject context, CancellationToken ct)
    {
        new MessageAttachments(attachmentRoot).ValidateAssets(files);
        if (files.OfType<JsonObject>().Any(item => item.GetString("type").Length > 0))
            files = await plugins.ProcessAttachments(files, context, ct);
        return files;
    }

    private void BeginInteractiveTurn(TurnInbox turn, bool editorTurn = false, bool userInitiated = true)
    {
        Volatile.Write(ref inbox, turn);
        Emit(new() { ["kind"] = "generating", ["agent"] = turn.Agent, ["value"] = true, ["editor_turn"] = editorTurn,
            ["user_initiated"] = userInitiated });
    }

    private void EndInteractiveTurn(TurnInbox turn, AgentSession? session = null, bool interrupted = false)
    {
        try
        {
            if (session is null) turn.Complete();
            else session.CompleteInbox();
        }
        finally
        {
            Volatile.Write(ref inbox, null);
            Emit(new() { ["kind"] = "generating", ["agent"] = turn.Agent, ["value"] = false, ["interrupted"] = interrupted });
        }
    }

    private void RecordReplyEnd(int turnStart, string answer)
    {
        var blocks = Workspace.Chat.GetArray("blocks");
        var prose = blocks.Skip(turnStart)
            .Where(block => block.GetString("agent") == "ChatAgent" && !block.GetBoolean("retrying"))
            .Select(block => block.GetString("content"))
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToArray();
        var visible = prose.Length switch
        {
            0 => answer,
            1 => prose[0],
            _ => string.Join("\n\n", prose.Select(content => content.Trim('\r', '\n')))
        };
        var end = new JsonObject
        {
            ["id"] = JsonValues.NewId(),
            ["kind"] = "reply_end",
            ["agent"] = "chat",
            ["reply"] = visible,
            ["first_block"] = blocks[turnStart].GetString("id"),
            ["message_index"] = Workspace.Chat.GetArray("messages").Count,
            ["version"] = ConversationVersions.Capture(Workspace, sessions)
        };
        lock (Workspace.RoomGate) blocks.AddNode(end);
        Emit((JsonObject)end.DeepClone());
    }

    private void RequireChat(JsonObject command)
    {
        if (command.GetString("chat") is { Length: > 0 } target && target != Workspace.ChatId)
            throw new InvalidOperationException("The conversation has changed.");
    }

    private void RequireActiveChat(JsonObject command)
    {
        RequireChat(command);
        if (Workspace.ChatId.Length == 0) throw new InvalidOperationException("Choose a chat first.");
    }

    private void EnsurePresetSelected()
    {
        if (Workspace.PresetId.Length == 0) throw new InvalidOperationException("Select a preset first.");
    }

    private void RequirePreset(JsonObject command)
    {
        if (command.GetString("preset") is { Length: > 0 } target && target != Workspace.PresetId)
            throw new InvalidOperationException("The preset has changed.");
    }

    private void RequireAgent(JsonObject command, string role)
    {
        if (role == "preset")
        {
            RequireChat(command);
            RequirePreset(command);
            EnsurePresetSession();
        }
        else RequireActiveChat(command);
    }

    private void PreviewSteer(string agent, JsonObject message, string conversation = "") => events.Enqueue(new()
    {
        ["kind"] = "steer_queued", ["chat"] = Workspace.ChatId, ["preset"] = Workspace.PresetId,
        ["conversation"] = conversation,
        ["item"] = new JsonObject
        {
            ["id"] = message.GetString(TurnInbox.QueuedBlockId), ["kind"] = message.GetString("role", "user"),
            ["agent"] = agent, ["content"] = message.GetString("text"),
            ["interjection"] = message.GetString("role", "user") == "user"
        }
    });

    private void RecordInput(string agent, string input, string role, JsonArray attachments, bool checkpoint,
        JsonObject? openUiAction = null, bool consumeDraft = false, string? blockId = null, bool interjection = false)
    {
        Workspace.Chat["messages"] ??= new JsonArray();
        lock (Workspace.RoomGate) Workspace.Chat["blocks"] ??= new JsonArray();
        var messages = Workspace.Chat.GetArray("messages");
        var block = new JsonObject { ["id"] = blockId ?? JsonValues.NewId(), ["kind"] = role, ["agent"] = agent, ["content"] = input };
        if (agent == "chat" && checkpoint)
        {
            block["version"] = ConversationVersions.Capture(Workspace, sessions);
            block["message_index"] = messages.Count;
        }
        var message = new JsonObject { ["role"] = role, ["agent"] = agent, ["content"] = input };
        if (interjection)
        {
            block["interjection"] = true;
            message["interjection"] = true;
        }
        if (openUiAction is not null) block["open_ui_action"] = openUiAction.DeepClone();
        if (attachments.Count > 0)
        {
            block["attachments"] = attachments.DeepClone();
            message["attachments"] = attachments.DeepClone();
        }
        if (agent == "preset")
        {
            lock (presetTraceGate)
            {
                presetConversation!.Blocks.AddNode(block);
                presetConversation.Save();
            }
        }
        else
        {
            if (consumeDraft) Workspace.Chat.Remove("draft");
            messages.AddNode(message);
            lock (Workspace.RoomGate) Workspace.Chat.GetArray("blocks").AddNode(block);
            Workspace.Save();
        }
        var item = new JsonObject { ["kind"] = role, ["agent"] = agent, ["text"] = input, ["block"] = block.GetString("id"),
            ["version"] = block.GetString("version"), ["attachments"] = attachments.DeepClone() };
        if (interjection) item["interjection"] = true;
        Emit(item);
    }

    private void EmitConfig() => Emit(new() { ["kind"] = "preset_config", ["config"] = Workspace.Config.DeepClone() });

    private void EmitWorldChanged(string request, string agent = "") => Emit(new()
    {
        ["kind"] = "world_changed", ["request"] = request, ["agent"] = agent,
        ["calendar"] = WorldCalendar.Snapshot(Workspace.Config, Workspace.State, WorldCalendar.Today)
    });

    private void StartTitle(bool force)
    {
        if (!force && (Workspace.Chat.GetString("name").Length != 0 || titleRequests.ContainsKey(Workspace.ChatId)))
        {
            return;
        }
        var transcript = ConversationTitle.Transcript(Workspace.Chat.GetArray("messages"));
        if (transcript.Length == 0)
        {
            return;
        }
        var request = JsonValues.NewId();
        titleRequests[Workspace.ChatId] = request;
        var selected = turnRouting.For("title") with { Thinking = false, MaxOutput = 256, ReasoningEffort = "" };
        _ = GenerateTitle(Workspace.ChatId, Workspace.PresetId, request, transcript, selected, force, Workspace.AgentPrompt("title"), ensemble is not null);
    }

    private async Task GenerateTitle(string id, string preset, string request, string transcript, Provider selected, bool force, string instructions, bool ensembleRoom)
    {
        var result = new JsonObject { ["op"] = "title_result", ["id"] = id, ["preset"] = preset, ["request"] = request,
            ["ensemble"] = ensembleRoom };
        try
        {
            using var session = agentSessions.Open("title", ":memory:", () => selected,
                () => new() { ["preset"] = preset, ["room"] = id }, _ => { });
            result["title"] = await ConversationTitle.Generate(session, transcript, lifetime.Token, instructions);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (force)
            {
                result["error"] = error.Message;
            }
        }
        commands.Writer.TryWrite(result);
    }

    private async Task ApplyTitle(JsonObject result)
    {
        var id = result.GetString("id");
        if (titleRequests.GetValueOrDefault(id) != result.GetString("request"))
        {
            return;
        }
        titleRequests.Remove(id);
        if (result.GetString("error") is { Length: > 0 } error)
        {
            Emit(new() { ["kind"] = "error", ["message"] = error });
        }
        if (result.GetString("title") is not { Length: > 0 } title)
        {
            return;
        }
        var target = Workspace;
        if (target.ChatId != id)
        {
            target = new(Workspace.Root);
            target.Select(result.GetString("preset"), id);
        }
        lock (target.RoomGate)
        {
            target.Chat["name"] = title;
            target.Chat["title_source"] = "generated";
            target.Save();
        }
        await Index(target);
        Emit(new() { ["kind"] = "title", ["id"] = id, ["preset"] = result.GetString("preset"),
            ["ensemble"] = result.GetBoolean("ensemble"), ["name"] = title });
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
        commands.Writer.TryComplete();
        await worker;
        CloseRoomCharacters();
        await Task.WhenAll(containers.Shutdown(), presetContainers.Shutdown());
        await pluginAgents.Drain();
        await plugins.DisposeAsync();
        await CloseTemporarySessions();
        if (!catalog.IsFaulted)
        {
            (await catalog).Dispose();
        }
        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }
        lifetime.Dispose();
    }
}
