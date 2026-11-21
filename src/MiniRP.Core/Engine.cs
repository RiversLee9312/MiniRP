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
    private int operationFlowDepth;
    private string operationEnsembleChat = "";
    private sealed record Routing(Provider Default, JsonObject Agents, JsonObject Settings)
    {
        public Provider For(string role) => Agents[role] is JsonObject value ? Provider.From(value) : Default;
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
    private bool curlAvailable;
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
        agentSessions = new(Port, plugins, attachmentRoot, () => turnRouting.Settings.GetString("android_open_ui"), () => Workspace, containers,
            () => curlAvailable);
        pluginAgents = new(Workspace.Root, agentSessions, plugins, () => Volatile.Read(ref routing).For("preset"),
            () =>
            {
                Emit(new() { ["kind"] = "plugins", ["items"] = plugins.List() });
                EmitPluginSettings();
            }, lifetime.Token);
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
                if (targetAgent.Length == 0 || Volatile.Read(ref inbox)?.Agent == targetAgent)
                    try { operation?.Cancel(); } catch (ObjectDisposedException) { }
                break;
            case "steer":
                var role = command.GetString("agent", "chat");
                if (command.GetString("text").Trim().Length == 0 || command.GetString("role", "user") is not ("user" or "system"))
                    throw new ArgumentException("A user or system text message is required.");
                if (Volatile.Read(ref inbox)?.TryPost(command.GetString("chat"), role, (JsonObject)command.DeepClone(),
                        role == "chat" ? command.GetString("conversation") : "") != true)
                    throw new InvalidOperationException("That conversation is no longer generating.");
                break;
            case "interaction_open":
                plugins.OpenUrl(command.GetString("url"), PluginEnvironment());
                break;
            case "configure":
                Volatile.Write(ref routing, new(Provider.From(command.GetObject("provider")), command.GetObject("agents"), command.GetObject("settings")));
                break;
            case "models":
                _ = DiscoverModels(Provider.From(command.GetObject("provider")), command.GetString("request"));
                break;
            case "library_page":
                _ = Task.Run(() => LibraryPage(command));
                break;
            case "asset_open":
            case "display_asset_open":
            case "preset_background_open":
            case "asset_pull":
            case "asset_close":
                _ = Task.Run(() => ReadAsset(command));
                break;
            default:
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
        events.Enqueue(item);
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
                if (workerOwner is null) Emit(item, sessionInfo);
                else events.Enqueue(item);
                var owner = workerOwner ?? presetConversation!;
                if (!RecordTrace(item, workerOwner is null ? trace : owner.WorkerTrace, owner.Blocks)) return;
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
        Emit(item, sessionInfo);
        if (destination is null) Workspace.Chat["blocks"] ??= new JsonArray();
        RecordTrace(item, trace, destination ?? Workspace.Chat.GetArray("blocks"));
    }

    private static bool RecordTrace(JsonObject item, Dictionary<string, JsonObject> blocks, JsonArray destination)
    {
        var kind = item.GetString("kind");
        if (kind is not ("delta" or "tool_start" or "tool_end" or "context_trace" or "open_ui_delta" or "open_ui_card" or "plugin_agent_status") &&
            item["asset_links"] is not JsonObject)
            return false;
        var id = item.GetString("block");
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
            blocks[id] = block;
            destination.AddNode(block);
        }
        if (kind == "open_ui_card")
        {
            foreach (var key in new[] { "open_ui", "open_ui_title", "open_ui_state", "open_ui_revision", "plugin_card" })
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

    private JsonArray PresetBlocks(PresetConversation conversation)
    {
        lock (presetTraceGate) return (JsonArray)conversation.Blocks.DeepClone();
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
            var showsBusy = modifiesChat || op is "select_chat" or "create_preset" or "delete_chat" or "delete_preset";
            turnRouting = Volatile.Read(ref routing);
            operationFlowDepth = command.GetInt32("_flow_depth");
            operationEnsembleChat = command.GetString("conversation");
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
                Emit(new()
                {
                    ["kind"] = "error",
                    ["request"] = command.GetString("request"),
                    ["model_turn"] = op is "message" or "_preset_interview" or "start_chat" or "greet_chat",
                    ["agent"] = command.GetString("agent", "chat"),
                    ["message"] = error.Message
                });
            }
            finally
            {
                active = null;
                operationFlowDepth = 0;
                operationEnsembleChat = "";
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
        index.Chat(workspace.PresetId, workspace.ChatId, workspace.Chat.GetString("name"), workspace.Chat.GetString("updated_at"));
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

    private void SelectConversation(string preset, string? id, bool fromHomeSend = false)
    {
        var nextWorkspace = new Workspace(Workspace.Root, seedDefaultPreset: false);
        nextWorkspace.Select(preset, id);
        if (string.IsNullOrEmpty(id) && nextWorkspace.ChatGreeting.Length > 0)
            nextWorkspace.Chat[GreetingPending] = true;
        nextWorkspace.Chat["mode"] ??= nextWorkspace.Mode;
        var nextEnsemble = nextWorkspace.Mode == "ensemble" ? new EnsembleConversation(nextWorkspace) : null;
        ConversationTitle.ClearLegacyTitle(nextWorkspace.Chat);
        var nextPreset = LoadPresetConversation(nextWorkspace);
        var nextSessions = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        try
        {
            foreach (var role in new[] { "chat", "search", "env", "preset" })
            {
                var database = role == "preset" ? nextPreset.Database : SessionFiles.Conversation(nextWorkspace.ChatRoot, role);
                var previous = role == "preset" ? nextPreset.LegacySession : nextWorkspace.Chat.GetObject(role);
                nextSessions[role] = CreateWorkspaceSession(role, database, previous, nextWorkspace);
            }
        }
        catch
        {
            foreach (var session in nextSessions.Values)
            {
                session.Dispose();
            }

            throw;
        }
        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }

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

        var fetch = new FetchAgent(Port, job =>
        {
            var context = PluginEnvironment("fetch");
            context["session"] = "fetch-" + job;
            return agentSessions.Open("fetch", SessionFiles.Fetch(Workspace.ChatRoot, job),
                () => turnRouting.For("fetch"), () => context, (item, info) =>
                {
                    item["session"] = "fetch-" + job;
                    Record(item, sessionInfo: info);
                });
        }, () => Workspace.AgentPrompt("fetch"));
        ensembleWeb = new PageSource(Port, fetch, () => turnRouting.Settings);
        search = new(Workspace, sessions["search"], ensembleWeb);
        chat = new(Workspace, sessions["chat"], search);
        environment = new(Workspace, sessions["env"]);
        presets = new(Workspace, sessions["preset"], plugins);
        if (string.IsNullOrEmpty(id)) Workspace.Save();
        lock (presetTraceGate)
        {
            Emit(new()
            {
                ["kind"] = "loaded",
                ["from_home_send"] = fromHomeSend,
                ["draft"] = JsonValues.Clone(Workspace.Chat["draft"]),
                ["ensemble_chats"] = ensemble?.List(),
                ["participants"] = ensemble?.Agents.DeepClone(),
                ["chat"] = new JsonObject
                {
                    ["id"] = Workspace.ChatId,
                    ["name"] = Workspace.Chat.GetString("name"),
                    ["blocks"] = new JsonArray(Workspace.Chat.GetArray("blocks").OfType<JsonObject>()
                        .Where(block => !PresetConversation.IsPreset(block))
                        .Concat(nextPreset.Blocks.OfType<JsonObject>()).Select(LoadBlock).ToArray())
                },
                ["preset"] = Workspace.PresetId,
                ["config"] = Workspace.Config.DeepClone()
            });
        }
    }

    private AgentSession CreateWorkspaceSession(string role, string database, JsonObject? previous, Workspace workspace)
    {
        var session = agentSessions.Open(role, database, () => turnRouting.For(role), () => PluginEnvironment(role),
            (item, info) => Record(item, sessionInfo: info),
            () => ModeSkills(role, workspace),
            role == "env" ? target => ensemble?.List().Any(chat => chat.GetString("id") == target) == true : null);
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
        presets = new(Workspace, session, plugins);
    }

    private void OpenPresetEditor(string request, bool created = false)
    {
        EnsurePresetSession();
        lock (presetTraceGate)
        {
            Emit(new()
            {
                ["kind"] = "preset_opened",
                ["request"] = request,
                ["created"] = created,
                ["preset"] = Workspace.PresetId,
                ["config"] = Workspace.Config.DeepClone(),
                ["blocks"] = new JsonArray(presetConversation!.Blocks.OfType<JsonObject>().Select(LoadBlock).ToArray())
            });
        }
    }

    private static JsonObject LoadedBlock(JsonObject block)
    {
        var cleaned = (JsonObject)block.DeepClone();
        if (string.IsNullOrWhiteSpace(cleaned.GetString("id"))) cleaned["id"] = JsonValues.NewId();
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
            case "list_chats":
                Emit(new()
                {
                    ["kind"] = "chats",
                    ["preset"] = command.GetString("preset"),
                    ["items"] = Workspace.Chats(command.GetString("preset"))
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
                SelectConversation(command.GetString("preset"), command.GetString("id"));
                await GreetChat("", token);
                return;
            case "start_chat":
                await StartChat(command.GetString("preset", Workspace.PresetId), token);
                return;
            case "select_ensemble_chat":
                SelectEnsembleChat(command);
                if (!command.GetBoolean("defer_greeting")) await GreetChat(activeEnsembleChat, token);
                return;
            case "greet_chat":
                if (Workspace.ChatId.Length > 0 && command.GetString("chat") == Workspace.ChatId &&
                    command.GetString("conversation") == activeEnsembleChat)
                    await GreetChat(activeEnsembleChat, token);
                return;
            case "rename_ensemble_chat":
                RequireActiveChat(command);
                var hub = ensemble ?? throw new InvalidOperationException("Choose an ensemble room.");
                hub.Chat(command.GetString("conversation"))["name"] = PresetDesign.Name(command.GetString("name"));
                Workspace.Save();
                EnsembleChatsChanged();
                return;
            case "create_preset":
                var name = command.GetString("name", "Preset");
                var open = command.GetBoolean("open");
                var preset = Workspace.CreatePreset(name, interview: open && command.GetBoolean("interview", true));
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
                return;
            case "_preset_interview":
                if (command.GetString("preset") != Workspace.PresetId || Workspace.Config.GetString("setup_status") != "draft")
                    return;
                EnsurePresetSession();
                if (PresetBlocks(presetConversation!).Any(block => block.GetString("content").Length > 0) ||
                    turnRouting.For("preset").Model.Length == 0 || turnRouting.For("preset").Url.Length == 0) return;
                var bootstrap = await plugins.InvokeBaseHook("preset.created",
                    new() { ["locale"] = command.GetString("locale", "en") }, PluginEnvironment("preset"), token);
                await RunMessage("preset", bootstrap.GetString("text"), false, new(), token,
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
            SelectConversation(target, null, fromHomeSend: command.GetString("op") == "message");
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
        if (command.GetString("op") == "save_date_mode")
        {
            RequirePreset(command);
            EnsurePresetSelected();
            var config = (JsonObject)Workspace.Config.DeepClone();
            WorldCalendar.Configure(config, command.GetString("mode"), WorldCalendar.Today);
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
                    await GreetChat(command.GetString("conversation", activeEnsembleChat), token);
                if (messageAgent == "chat" && ensemble is not null)
                {
                    await RunEnsembleChat(command, token);
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
                SelectConversation(Workspace.PresetId, branch.Id);
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
                    using var database = new MemoryDb(group.Database(member, memoryAgent));
                    memory = database.Call(tool, arguments);
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
                Workspace.Chat["name"] = command.GetString("name");
                Workspace.Chat["title_source"] = "manual";
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
                if (WorldCalendar.Render(date) == Workspace.WorldDate) break;
                var state = (JsonObject)Workspace.State.DeepClone();
                WorldCalendar.Set(Workspace.Config, state, date, WorldCalendar.Today);
                Workspace.Commit(state);
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
        if (Workspace.Config.GetString("setup_status") == "draft")
            Dispatch(new() { ["op"] = "_preset_interview", ["preset"] = Workspace.PresetId,
                ["agent"] = "preset", ["locale"] = command.GetString("locale", "en") });
    }

    private async Task RunMessage(string role, string input, bool stage, JsonArray attachments, CancellationToken token,
        string inputRole = "user", string request = "", JsonObject? openUiAction = null, bool editorTurn = false, bool hiddenInput = false)
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
            RecordInput(role, message.GetString("text"), message.GetString("role", "user"), new(), false);
            return null;
        });
        session.Inbox = turn;
        BeginInteractiveTurn(turn, editorTurn);
        var turnStart = Workspace.Chat.GetArray("blocks").Count;
        string answer;
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
                "env" => await environment!.Run(input, stage, token, attachments, inputRole),
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
        }
        finally
        {
            EndInteractiveTurn(turn, session);
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

    private void BeginInteractiveTurn(TurnInbox turn, bool editorTurn = false)
    {
        Volatile.Write(ref inbox, turn);
        Emit(new() { ["kind"] = "generating", ["agent"] = turn.Agent, ["value"] = true, ["editor_turn"] = editorTurn });
    }

    private void EndInteractiveTurn(TurnInbox turn, AgentSession? session = null)
    {
        try
        {
            if (session is null) turn.Complete();
            else session.CompleteInbox();
        }
        finally
        {
            Volatile.Write(ref inbox, null);
            Emit(new() { ["kind"] = "generating", ["agent"] = turn.Agent, ["value"] = false });
        }
    }

    private void RecordReplyEnd(int turnStart, string answer)
    {
        var blocks = Workspace.Chat.GetArray("blocks");
        var prose = blocks.Skip(turnStart)
            .Where(block => block.GetString("agent") == "ChatAgent")
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
        blocks.AddNode(end);
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

    private void RecordInput(string agent, string input, string role, JsonArray attachments, bool checkpoint,
        JsonObject? openUiAction = null, bool consumeDraft = false)
    {
        Workspace.Chat["messages"] ??= new JsonArray();
        Workspace.Chat["blocks"] ??= new JsonArray();
        var messages = Workspace.Chat.GetArray("messages");
        var block = new JsonObject { ["id"] = JsonValues.NewId(), ["kind"] = role, ["agent"] = agent, ["content"] = input };
        if (agent == "chat" && checkpoint)
        {
            block["version"] = ConversationVersions.Capture(Workspace, sessions);
            block["message_index"] = messages.Count;
        }
        var message = new JsonObject { ["role"] = role, ["agent"] = agent, ["content"] = input };
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
            Workspace.Chat.GetArray("blocks").AddNode(block);
            Workspace.Save();
        }
        Emit(new() { ["kind"] = role, ["agent"] = agent, ["text"] = input, ["block"] = block.GetString("id"), ["version"] = block.GetString("version"), ["attachments"] = attachments.DeepClone() });
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var session = agentSessions.Open("title", ":memory:", () => selected,
                () => new() { ["preset"] = preset, ["room"] = id }, _ => { });
            result["title"] = await ConversationTitle.Generate(session, transcript, timeout.Token, instructions);
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
        target.Chat["name"] = title;
        target.Chat["title_source"] = "generated";
        target.Save();
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
        await containers.Shutdown();
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
