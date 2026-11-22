using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private readonly PluginRuntime plugins;
    private readonly ConcurrentDictionary<string, TemporarySession> temporary = new();
    private sealed record TemporarySession(string Owner, string Preset, AgentSession Session, SemaphoreSlim Gate);

    private async Task ReadAsset(JsonObject command)
    {
        var response = new JsonObject { ["kind"] = "asset_result", ["request"] = command.GetString("request") };
        try
        {
            switch (command.GetString("op"))
            {
                case "preset_background_open":
                    var workspace = Workspace;
                    var background = workspace.Config.GetObject("appearance").GetString("background_url");
                    if (background.Length == 0 || workspace.PresetId != command.GetString("preset") || background != command.GetString("url"))
                        throw new UnauthorizedAccessException("The requested background is not active for this preset.");
                    response["result"] = plugins.OpenPresetBackground(background, workspace.PresetId);
                    break;
                case "asset_open":
                    var url = command.GetString("url");
                    var agent = command.GetString("agent", "chat");
                    var attachment = command.GetObject("attachment");
                    if (url.Contains('?'))
                    {
                        var id = attachment.GetString("id");
                        if (id.Length == 0 || !url.EndsWith("?attachment=" + Workspace.Safe(id), StringComparison.Ordinal) ||
                            (url != attachment.GetString("thumbnail_url") &&
                                !(PluginUrls.IsLegacyThumbnail(url.Split('?', 2)[0]) && attachment["thumbnail"] is JsonObject)))
                            throw new UnauthorizedAccessException("This attachment preview is unavailable.");
                    }
                    response["result"] = plugins.OpenHostAsset(url, PluginEnvironment(agent), attachment.Count == 0 ? null : attachment);
                    break;
                case "display_asset_open":
                    response["result"] = OpenDisplayAsset(command);
                    break;
                case "asset_pull":
                    response["result"] = await plugins.PullHostAsset(command.GetString("handle"), lifetime.Token);
                    break;
                case "asset_close":
                    await plugins.CloseHostAsset(command.GetString("handle"));
                    break;
            }
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            response["kind"] = "error";
            response["message"] = error.Message;
        }
        if (!lifetime.IsCancellationRequested) Emit(response);
    }

    private JsonObject PluginEnvironment(string agent = "")
    {
        var workspace = Workspace;
        var container = workspace.ChatId;
        var ensembleChat = operationEnsembleChat.Length > 0 ? operationEnsembleChat : activeEnsembleChat;
        var chat = ensemble is null ? container : ensembleChat;
        return new()
        {
            ["session"] = agent == "preset" && workspace.PresetId.Length > 0 ? agent + "-" + workspace.PresetId :
                agent.Length == 0 || container.Length == 0 ? null : agent + "-" + container,
            ["agent"] = agent.Length == 0 ? null : agent,
            ["chat"] = chat.Length == 0 ? null : chat,
            ["room"] = container.Length == 0 ? null : container,
            ["_ensemble"] = ensemble is not null,
            ["preset"] = workspace.PresetId.Length == 0 ? null : workspace.PresetId
        };
    }

    private void PublishPluginEvent(JsonObject item, SessionModelInfo? sessionInfo)
    {
        var name = item.GetString("kind") switch
        {
            "ensemble_message" => item.GetObject("item").GetString("role") switch
                { "user" => "message.user", "system" => "message.system", _ => "message.completed" },
            "loaded" when item["ensemble_chats"] is JsonArray => "room.loaded",
            "loaded" => "chat.loaded",
            "ensemble_chat_loaded" when item.GetString("conversation").Length > 0 => "chat.loaded",
            "library_changed" => "library.changed",
            "title" => item.GetBoolean("ensemble") ? "room.title" : "chat.title",
            "user" => "message.user", "system" => "message.system", "completed" => "message.completed",
            "turn_start" => "turn.started", "delta" when item.GetString("type") == "content" => "turn.delta",
            "turn_end" => "turn.finished", "cancelled" => "turn.cancelled",
            "tool_start" => "tool.started", "tool_end" => "tool.finished", "compacted" => "context.compacted",
            "world_changed" => "world.changed", "preset_config" => "preset.changed", "home" => "chat.changed",
            _ => ""
        };
        if (name.Length == 0) return;
        var role = item.GetString("agent") switch
        {
            "ChatAgent" => "chat", "EnvAgent" => "env", "PresetAgent" => "preset",
            "SearchAgent" => "search", "FetchAgent" => "fetch", var agent => agent
        };
        var payload = item.GetString("kind") switch
        {
            "delta" => new JsonObject { ["block"] = item.GetString("block"), ["text"] = item.GetString("text") },
            "ensemble_message" => new JsonObject { ["agent"] = "chat", ["room"] = item.GetString("chat"),
                ["chat"] = item.GetString("conversation"), ["block"] = item.GetObject("item").GetString("id"),
                ["participant"] = JsonValues.Clone(item.GetObject("item")["participant"]),
                ["text"] = item.GetObject("item").GetString("content") },
            "ensemble_chat_loaded" => new JsonObject { ["room"] = item.GetString("chat"), ["chat"] = item.GetString("conversation"),
                ["preset"] = Workspace.PresetId },
            "loaded" when name == "room.loaded" => new JsonObject { ["room"] = item.GetObject("chat").GetString("id"),
                ["preset"] = item.GetString("preset"), ["config"] = JsonValues.Clone(item["config"]),
                ["chats"] = JsonValues.Clone(item["ensemble_chats"]) },
            _ => item
        };
        var context = PluginEnvironment(role);
        if (item.GetString("kind") == "ensemble_message")
        {
            context["agent"] = "chat";
            context["participant"] = JsonValues.Clone(payload["participant"]);
            var speaker = payload.GetString("participant");
            if (speaker.Length > 0) context["session"] = "chat-" + item.GetString("chat") + "-" + speaker;
        }
        if (item.GetString("preset") is { Length: > 0 } targetPreset) context["preset"] = targetPreset;
        if (name is "room.title" or "chat.title")
        {
            context["room"] = item.GetString("id");
            context["chat"] = item.GetBoolean("ensemble") ? null : item.GetString("id");
            context["_ensemble"] = item.GetBoolean("ensemble");
        }
        if (item.GetString("session") is { Length: > 0 } session) context["session"] = session;
        if (item.GetString("conversation") is { Length: > 0 } conversation) context["chat"] = conversation;
        if (item.GetString("participant") is { Length: > 0 } participant) context["participant"] = participant;
        plugins.Publish(name, payload, PluginRuntime.WithSessionInfo(context, sessionInfo));
    }

    private async Task<bool> ExecuteExtensionCommand(JsonObject command, CancellationToken token)
    {
        var request = command.GetString("request");
        switch (command.GetString("op"))
        {
            case "archive_export":
                if (IsTranscriptExport(command))
                {
                    await ExportTranscript(command, token);
                    Emit(new() { ["kind"] = "archive_exported", ["request"] = request });
                    return true;
                }
                if (command.GetString("type") is "room" or "save")
                    await containers.Flush(command.GetString("preset"), command.GetString("room"), token);
                if (command.GetString("type") == "save")
                    await presetContainers.Flush(command.GetString("preset"), "", token);
                await new WorkspaceTransfer(Workspace, plugins).Export(command.GetString("type"),
                    command.GetString("preset"), command.GetString("room"), command.GetString("id"), command.GetString("path"), token);
                Emit(new() { ["kind"] = "archive_exported", ["request"] = request });
                return true;
            case "archive_import":
                var imported = await new WorkspaceTransfer(Workspace, plugins)
                    .Import(command.GetString("type"), command.GetString("path"), token);
                var index = await catalog;
                var importedPreset = new Workspace(Workspace.Root, seedDefaultPreset: false);
                importedPreset.UsePreset(imported);
                index.Preset(imported, importedPreset.Config.GetString("name", imported));
                foreach (var item in importedPreset.ChatEntries(imported))
                    index.Chat(imported, item.GetString("id"), item.GetString("name"), item.GetString("updated_at"));
                OpenPresetHome(imported, command);
                Emit(new() { ["kind"] = "library_changed", ["preset"] = imported, ["presets_changed"] = true });
                Emit(new() { ["kind"] = "plugins", ["items"] = plugins.List() });
                Emit(new() { ["kind"] = "archive_imported", ["request"] = request, ["preset"] = imported });
                return true;
            case "_plugin_state_changed":
                RefreshCards(command.GetObject("change"));
                return true;
            case "plugins_start":
                plugins.Load();
                plugins.Publish("app.ready", new(), new());
                return true;
            case "plugins_list":
                Emit(new() { ["kind"] = "plugins", ["request"] = request, ["items"] = plugins.List() });
                return true;
            case "plugin_inspect":
            case "plugin_install":
                var inspect = command.GetString("op") == "plugin_inspect";
                var package = plugins.Install(command.GetString("path"), inspect, environment: PluginEnvironment());
                Emit(new() { ["kind"] = "plugin", ["request"] = request, ["item"] = plugins.Descriptor(package) });
                return true;
            case "plugin_uninstall":
                plugins.Uninstall(command.GetString("id"));
                Emit(new() { ["kind"] = "plugins", ["request"] = request, ["items"] = plugins.List() });
                EmitPluginSettings();
                return true;
            case "plugin_command":
                plugins.RequireScope(command.GetString("id"), Workspace.PresetId);
                plugins.Publish("plugin.command", command.GetObject("arguments"), PluginEnvironment(), command.GetString("id"));
                return true;
            case "plugin_scope":
                plugins.SetScope(command.GetString("id"), PluginScope.Parse(command.GetObject("scope")));
                Emit(new() { ["kind"] = "plugins", ["request"] = request, ["items"] = plugins.List() });
                EmitPluginSettings();
                return true;
            case "plugin_enabled":
                if (command["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled))
                    throw new ArgumentException("Choose whether to enable this plugin.");
                plugins.SetEnabled(command.GetString("id"), enabled, PluginEnvironment());
                Emit(new() { ["kind"] = "plugins", ["request"] = request, ["items"] = plugins.List() });
                EmitPluginSettings();
                return true;
            case "plugin_settings":
                EmitPluginSettings(request);
                return true;
            case "plugin_page":
                EmitPluginPage(command);
                return true;
            case "plugin_setting":
                plugins.SetSetting(command.GetString("id"), command.GetString("key"), command["value"], PluginEnvironment());
                EmitPluginSettings(request);
                return true;
            case "plugin_action":
            case "plugin_url":
                plugins.OpenSettingLink(command.GetString("id"), command.GetString("key"), PluginEnvironment());
                return true;
            case "_plugin_workspace":
                PluginWorkspace(command);
                return true;
            case "home":
                var preset = command.GetString("preset");
                if (!command.GetBoolean("restore") || Workspace.PresetEntries().Any(item => item.GetString("id") == preset))
                    OpenPresetHome(preset, command);
                else if (Workspace.SetupPending)
                    OpenPresetHome(Workspace.PresetId, command);
                return true;
            case "delete_chat":
            case "delete_preset":
                await Remove(command);
                Emit(new() { ["kind"] = "deleted", ["request"] = request, ["id"] = command.GetString("id") });
                return true;
        }
        return false;
    }

    private void EmitPluginSettings(string request = "") => Emit(new()
    {
        ["kind"] = "plugin_settings", ["request"] = request,
        ["preset"] = Workspace.PresetId, ["items"] = plugins.Settings(Workspace.PresetId)
    });

    private void EmitPluginPage(JsonObject command) => Emit(new()
    {
        ["kind"] = "plugin_page", ["request"] = command.GetString("request"),
        ["item"] = plugins.Page(command.GetString("id"), command.GetString("key"), Workspace.PresetId)
    });

    private void OpenPresetHome(string preset, JsonObject command)
    {
        ShowHome(preset, notify: false);
        if (Workspace.SetupPending)
        {
            OpenPresetEditor("", selected: true);
            QueuePresetInterview(command);
        }
        else EmitHome();
    }

    private void EmitHome() =>
        Emit(new() { ["kind"] = "home", ["preset"] = Workspace.PresetId, ["config"] = Workspace.Config.DeepClone() });

    private void ShowHome(string preset, bool notify = true)
    {
        containers.Select(null);
        var next = new Workspace(Workspace.Root, seedDefaultPreset: false);
        if (preset.Length == 0 && seedDefaultPreset) preset = next.InitialPreset().Id;
        if (preset.Length > 0) next.UsePreset(preset);
        foreach (var session in sessions.Values) session.Dispose();
        CloseRoomCharacters();
        sessions.Clear();
        trace.Clear();
        chat = null;
        search = null;
        environment = null;
        presets = null;
        presetConversation = null;
        Workspace = next;
        ensemble = null;
        activeEnsembleChat = "";
        if (notify) EmitHome();
        QueuePresetNotifications(preset);
    }

    private async Task Remove(JsonObject command)
    {
        var preset = WorkspaceIds.Require(command.GetString("preset"));
        var id = WorkspaceIds.Require(command.GetString("id"));
        var index = await catalog;
        if (command.GetString("op") == "delete_chat")
        {
            if (Workspace.PresetId == preset && Workspace.ChatId == id) OpenPresetHome(preset, command);
            titleRequests.Remove(id);
            Workspace.Trash(preset, id);
            index.RemoveChat(preset, id);
        }
        else
        {
            if (preset != id) throw new ArgumentException("The preset has changed.");
            var next = Workspace.PresetEntries().FirstOrDefault(item => item.GetString("id") != preset)?.GetString("id");
            if (next is null && seedDefaultPreset)
            {
                next = Workspace.CreatePreset("Default");
                index.Preset(next, "Default");
            }
            await pluginAgents.StopPreset(preset);
            await presetContainers.ClosePreset(preset, lifetime.Token);
            presetTraces.Remove(preset);
            if (Workspace.PresetId == preset) OpenPresetHome(next ?? "", command);
            Workspace.Trash(preset, null);
            index.RemovePreset(preset);
            Emit(new() { ["kind"] = "preset_removed", ["id"] = preset, ["fallback"] = next ?? "" });
        }
        Emit(new() { ["kind"] = "library_changed", ["preset"] = preset, ["presets_changed"] = preset == id && command.GetString("op") == "delete_preset" });
        Dispatch(new() { ["op"] = "_maintain_storage" });
    }

    private static (string Protocol, string Url, string Model, string Key) PresetConnection(Provider provider) =>
        (provider.Protocol, provider.Url, provider.Model, provider.Key);

    private void PluginAgentCompleted(string preset)
    {
        // The outbox was written by the worker. Interrupt only the matching model stream;
        // the turn reads the durable system message at its next safe request boundary.
        try { Volatile.Read(ref inbox)?.SteerSystemMessages(preset); }
        finally { QueuePresetNotifications(preset); }
    }

    private void QueuePresetNotifications(string preset) =>
        commands.Writer.TryWrite(new() { ["op"] = "_preset_plugin_notifications", ["preset"] = preset, ["agent"] = "preset" });

    private async Task RunPresetNotifications(string requestedPreset, CancellationToken token)
    {
        var preset = Workspace.PresetId;
        if (preset.Length == 0 || requestedPreset.Length > 0 && requestedPreset != preset) return;
        var provider = turnRouting.For("preset");
        if (provider.Model.Length == 0 || provider.Url.Length == 0) return;
        var database = SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(Workspace.Root, preset));
        if (!File.Exists(database)) return;
        using (var db = MemoryDb.ReadOnly(database))
            if (!db.HasEntriesTable() || !AgentSession.HasPendingSystemMessages(db)) return;

        EnsurePresetSession();
        Emit(new() { ["kind"] = "busy", ["value"] = true });
        try
        {
            await RunMessage("preset",
                "Respond to the PluginAgent status updates above. Continue the user's outstanding request when appropriate and tell the user what completed or what needs attention.",
                false, new(), token, inputRole: "system", hiddenInput: true, userInitiated: false);
        }
        finally
        {
            try { SavePresetTrace(); }
            finally { Emit(new() { ["kind"] = "busy", ["value"] = false }); }
        }
    }

    private async Task<JsonNode?> PluginApi(PluginPackage package, JsonObject context, string op, JsonObject args, CancellationToken ct)
    {
        foreach (var name in new[] { "preset", "room", "chat" })
        {
            if (!args.TryGetPropertyValue(name, out var value)) continue;
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var idValue)) WorkspaceIds.Require("");
            else WorkspaceIds.Require(idValue);
        }
        if (op == "pluginDevelopment")
        {
            if (!package.BuiltIn || package.Id != "base") throw new UnauthorizedAccessException();
            var owner = context.GetString("agent") == "preset" ? presetConversation : null;
            Action<string>? observed = owner is null ? null : sessions["preset"].AcknowledgeSystemMessageOnCommit;
            return await pluginAgents.Execute(context, args,
                owner is null ? events.Enqueue : item => Record(item, workerOwner: owner), observed, ct);
        }
        var id = args.GetString("id");
        if (op == "generate")
        {
            var provider = Volatile.Read(ref routing).For("chat") with { MaxOutput = 384, Thinking = false, ReasoningEffort = "" };
            var instructions = args.GetString("system");
            if (instructions.Length > 8192) throw new ArgumentException("Instructions are too long.");
            using var session = agentSessions.Open("plugin-generation", ":memory:", () => provider, () => context, _ => { });
            var reply = await session.Run(instructions, RequireText(args), ct, privateInput: true);
            return new JsonObject { ["text"] = reply.GetString("content") };
        }
        if (op == "listPresets")
            return (await catalog).Page("",
                Math.Max(0, args.GetInt32("offset")), Math.Clamp(args.GetInt32("limit", 30), 1, 100), plugins.Scope(package));
        if (op == "getPreset")
        {
            var config = new Workspace(Workspace.Root, seedDefaultPreset: false);
            config.UsePreset(PluginPreset(package, context, args));
            return config.Config;
        }
        if (op == "getWorldDate")
        {
            var preset = PluginPreset(package, context, args);
            var workspace = new Workspace(Workspace.Root, seedDefaultPreset: false);
            workspace.UsePreset(preset);
            var room = PluginRoom(args, context, preset, workspace.Config.GetString("chat_mode") == "ensemble");
            var document = await ReadRoomDocument(preset, WorkspaceIds.Require(room), ct);
            return WorldCalendar.Snapshot(workspace.Config, document.GetObject("world"), WorldCalendar.Today);
        }
        if (op is "readChat" or "listChats" or "listRooms" or "readRoom")
            return await QueryPluginConversations(op, package, context, args, ct);
        if (op == "readSession")
        {
            var role = args.GetString("agent", context.GetString("agent", "chat"));
            if (!PluginPackage.Agents.Contains(role)) throw new ArgumentException("Unknown session agent.");
            if (role == "temporary")
            {
                var entry = PluginTemporarySession(id, package);
                await entry.Gate.WaitAsync(ct);
                try { return PageMessages(entry.Session.History, args); }
                finally { entry.Gate.Release(); }
            }
            var preset = PluginPreset(package, context, args);
            var file = SessionFile(role, preset, args, context);
            if (!File.Exists(file)) return new JsonArray();
            using var db = MemoryDb.ReadOnly(file);
            return db.HasEntriesTable() ? PageMessages(AgentSession.ReadHistory(db), args) : new JsonArray();
        }
        if (op is "createTemporarySession" or "runTemporary" or "closeTemporary")
        {
            if (op == "createTemporarySession")
            {
                var key = JsonValues.NewId();
                var temporaryContext = new JsonObject { ["session"] = key, ["agent"] = "temporary", ["preset"] = JsonValues.Clone(context["preset"]) };
                var session = agentSessions.Open("temporary", ":memory:", () => Volatile.Read(ref routing).For("chat"),
                    () => temporaryContext, _ => { });
                temporary[key] = new(package.StorageKey, context.GetString("preset"), session, new(1));
                return new JsonObject { ["id"] = key };
            }
            var item = PluginTemporarySession(id, package, requireScope: op != "closeTemporary");
            await item.Gate.WaitAsync(ct);
            try
            {
                if (!temporary.ContainsKey(id)) throw new ArgumentException("Temporary session is closed.");
                if (op == "closeTemporary")
                {
                    temporary.TryRemove(id, out _);
                    item.Session.Dispose();
                    return new JsonObject { ["closed"] = true };
                }
                var result = await item.Session.Run(args.GetString("system", "You are a concise assistant."), RequireText(args),
                    new AgentSkill("temporary-task", "Complete the requested task.", new(), (_, _, _) => Task.FromResult<JsonNode?>(null)),
                    ct);
                return new JsonObject { ["text"] = result.GetString("content") };
            }
            finally { item.Gate.Release(); }
        }
        var agent = op == "setWorldDate" ? "env" : args.GetString("agent", context.GetString("agent", "chat"));
        if (agent is not ("chat" or "env" or "preset") && !(op == "setPrompt" && agent is "search" or "fetch" or "title"))
            throw new ArgumentException("Choose a conversation agent.");
        var targetPreset = op == "createPreset" ? "" : WorkspaceIds.Require(args.GetString("preset",
            context.GetString("preset", op == "setPrompt" ? Workspace.PresetId : "")));
        plugins.RequireScope(package, targetPreset);
        var activePreset = Workspace.PresetId == targetPreset;
        var grouped = targetPreset.Length > 0 && IsEnsemblePreset(targetPreset);
        var target = PluginRoom(args, context, targetPreset, grouped);
        if (target.Length > 0) WorkspaceIds.Require(target);
        else if (op is not ("createChat" or "createRoom" or "createPreset" or "setPrompt"))
            throw new ArgumentException("This operation needs a room or a regular chat.");
        var conversation = grouped ? PluginChat(args, context, targetPreset, target) : "";
        if (grouped && op == "createChat")
            throw new InvalidOperationException("Use createRoom for an ensemble room. EnvAgent creates and assigns the chats inside it.");
        if (op == "createRoom" && !grouped)
            throw new InvalidOperationException("Use createChat for a regular preset.");
        if (grouped && op == "branchChat")
            throw new InvalidOperationException("Branching is available for regular chats.");
        var communicates = agent == "chat" && op is "send" or "insertSystemMessage" or "steer";
        if (communicates)
        {
            if (grouped && conversation.Length == 0 && !(op == "insertSystemMessage" && args.GetString("participant").Length > 0))
                throw new ArgumentException("Choose a chat inside the room.");
            RequirePluginMembership(context, target, conversation, args);
        }
        if (op == "steer")
        {
            if (!activePreset || !plugins.WhileCurrent(package, targetPreset, () => Volatile.Read(ref inbox)?.TryPost(target, agent, new()
                { ["text"] = RequireText(args), ["role"] = args.GetBoolean("system") ? "system" : "user" },
                    agent == "chat" ? conversation : "") == true))
                throw new InvalidOperationException("That conversation is not generating.");
            return new JsonObject { ["accepted"] = true };
        }
        if (op == "insertSystemMessage" && activePreset && (agent != "chat" || ensemble is null) &&
            plugins.WhileCurrent(package, targetPreset, () => Volatile.Read(ref inbox)?.TryPost(target, agent, new()
            { ["text"] = RequireText(args), ["role"] = "system" }) == true))
            return new JsonObject { ["accepted"] = true };
        if (op is not ("createChat" or "createRoom" or "createPreset" or "send" or "insertSystemMessage" or "renameChat" or "renameRoom" or "generateTitle" or "setSearchMode" or "setWorldDate" or "setPrompt" or "branchChat"))
            throw new ArgumentException("Unknown harness operation.");
        if (op == "setWorldDate")
        {
            _ = WorldCalendar.RequireDate(args.GetString("date"));
            if (!activePreset || target != Workspace.ChatId) throw new InvalidOperationException("Choose the active room.");
            WorldCalendar.RequirePluginMode(Workspace.Config);
        }
        var command = (JsonObject)args.DeepClone();
        command.Remove("_character_origin");
        if (communicates && context.GetBoolean("_ensemble") && context.GetString("participant") is { Length: > 0 } member)
            command["_character_origin"] = new JsonObject { ["_ensemble"] = true, ["room"] = context.GetString("room"), ["participant"] = member };
        command["chat"] = op is "createChat" or "createRoom" or "createPreset" ? "" : target;
        command["conversation"] = conversation;
        command["preset"] = targetPreset;
        command["_plugin"] = package.Id;
        command["_plugin_generation"] = package.Generation;
        command["agent"] = agent;
        command["action"] = op;
        command["op"] = "_plugin_workspace";
        command["request"] = JsonValues.NewId();
        if (op == "insertSystemMessage") command["_hidden_notice"] = true;
        if (op == "send")
        {
            foreach (var attachment in command.GetArray("attachments").OfType<JsonObject>())
            {
                if (attachment.GetString("type").Length > 0) attachment["plugin"] = package.Id;
                attachment.Remove("text");
                attachment.Remove("thumbnail");
                attachment.Remove("thumbnail_url");
            }
            if (command.GetArray("attachments").Count == 0 || args.GetString("text").Length > 0) _ = RequireText(args);
        }
        if (op == "insertSystemMessage") _ = RequireText(args);
        // Mutations can be requested from a tool callback while the workspace queue is occupied.
        Dispatch(command);
        return new JsonObject { ["queued"] = true, ["request"] = command.GetString("request") };
    }

    private string SessionFile(string role, string preset, JsonObject args, JsonObject context)
    {
        if (role == "preset") return SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(Workspace.Root, preset));
        var grouped = IsEnsemblePreset(preset);
        var room = WorkspaceIds.Require(PluginRoom(args, context, preset, grouped));
        var root = Path.Combine(Workspace.Root, "conversations", preset, room);
        var participant = args.GetString("participant", room == context.GetString("room") && preset == context.GetString("preset")
            ? context.GetString("participant") : "");
        if (role is "chat" or "search")
        {
            if (grouped)
                return participant.Length > 0 ? SessionFiles.Ensemble(root, participant, role)
                    : throw new ArgumentException("Choose a character in this room.");
            if (participant.Length > 0) throw new ArgumentException("A regular conversation has no character sessions.");
        }
        if (role == "fetch")
        {
            var job = args.GetString("id", context.GetString("session"));
            return SessionFiles.Fetch(root, job.StartsWith("fetch-", StringComparison.Ordinal) ? job[6..] : job);
        }
        return SessionFiles.Conversation(root, role);
    }

    private void PluginWorkspace(JsonObject request)
    {
        RequireChat(request);
        var op = request.GetString("action");
        request["op"] = op switch
        {
            "createChat" or "createRoom" => "select_chat", "createPreset" => "create_preset",
            "send" => "message", "insertSystemMessage" => "system_message",
            "renameChat" when ensemble is not null => "rename_ensemble_chat",
            "renameChat" or "renameRoom" => "rename",
            "generateTitle" => "generate_title", "setSearchMode" => "save_search_mode", "setWorldDate" => "set_world_date", "setPrompt" => "save_prompt",
            "branchChat" => "branch", _ => throw new ArgumentException("Unknown workspace operation.")
        };
        Dispatch(request);
    }

    private string PluginPreset(PluginPackage package, JsonObject context, JsonObject args)
    {
        var preset = WorkspaceIds.Require(args.GetString("preset", context.GetString("preset")));
        plugins.RequireScope(package, preset);
        return preset;
    }

    private TemporarySession PluginTemporarySession(string id, PluginPackage package, bool requireScope = true)
    {
        if (!temporary.TryGetValue(id, out var session) || session.Owner != package.StorageKey)
            throw new ArgumentException("Unknown temporary session.");
        if (requireScope) plugins.RequireScope(package, session.Preset);
        return session;
    }

    private static string RequireText(JsonObject args) => args.GetString("text").Trim() is { Length: > 0 and <= 64000 } text ?
        text : throw new ArgumentException("Provide between 1 and 64,000 characters.");

    private static JsonArray PageMessages(JsonArray messages, JsonObject args) =>
        new(messages.Where(SessionHistory.IsVisible)
            .Skip(Math.Max(0, args.GetInt32("offset"))).Take(Math.Clamp(args.GetInt32("limit", 30), 1, 100))
            .Select(item => (JsonNode?)new JsonObject { ["role"] = item.GetString("role"), ["agent"] = item.GetString("agent"),
                ["participant"] = JsonValues.Clone(item?["participant"]), ["speaker"] = JsonValues.Clone(item?["speaker"]),
                ["text"] = item.GetString("content") is var text ? text[..Math.Min(8192, text.Length)] : "" }).ToArray());

    private async Task CloseTemporarySessions()
    {
        foreach (var entry in temporary.Values)
        {
            await entry.Gate.WaitAsync();
            entry.Session.Dispose();
            entry.Gate.Dispose();
        }
    }
}
