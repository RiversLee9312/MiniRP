using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Minichat.Core;

/// <summary>Coordinates serialized workspace changes; platform I/O replies never wait in that queue.</summary>
public sealed partial class Engine : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonObject> events = new();
    private readonly Channel<JsonObject> commands = Channel.CreateUnbounded<JsonObject>(new() { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, AgentSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> trace = new(StringComparer.Ordinal);
    private readonly Task worker;
    private CancellationTokenSource? active;
    private TurnInbox? inbox;
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

    public Engine(string root, bool seedDefaultPreset = true)
    {
        this.seedDefaultPreset = seedDefaultPreset;
        Workspace = new(root, seedDefaultPreset);
        var initialPreset = Workspace.InitialPreset();
        if (initialPreset.Id.Length > 0) Workspace.UsePreset(initialPreset.Id);
        attachmentRoot = Path.Combine(Workspace.Root, "attachments");
        var initialWorkspace = Workspace;
        catalog = Task.Run(() => new WorkspaceCatalog(initialWorkspace));
        Port = new(Emit);
        plugins = new(Workspace.Root, Port, Emit, PluginApi);
        turnRouting = routing;
        Model = new(Port, () => Volatile.Read(ref routing).Default);
        worker = Task.Run(ProcessCommands);
        Dispatch(new() { ["op"] = "plugins_start" });
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
        switch (command.GetString("op"))
        {
            case "io_reply":
                Port.Reply(command);
                break;
            case "cancel":
                try { active?.Cancel(); } catch (ObjectDisposedException) { }
                break;
            case "steer":
                var role = command.GetString("agent", "chat");
                if (command.GetString("text").Trim().Length == 0 || command.GetString("role", "user") is not ("user" or "system"))
                    throw new ArgumentException("A user or system text message is required.");
                if (Volatile.Read(ref inbox)?.TryPost(command.GetString("chat"), role, (JsonObject)command.DeepClone()) != true)
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

    private void Emit(JsonObject item)
    {
        events.Enqueue(item);
        PublishPluginEvent(item);
    }

    private void Record(JsonObject item)
    {
        Emit(item);
        var kind = item.GetString("kind");
        if (kind is not ("delta" or "tool_start" or "tool_end" or "open_ui_delta"))
        {
            return;
        }
        var id = item.GetString("block");
        if (!trace.TryGetValue(id, out var block))
        {
            block = new()
            {
                ["id"] = id,
                ["agent"] = item.GetString("agent"),
                ["content"] = "",
                ["reasoning"] = ""
            };
            trace[id] = block;
            if (PresetConversation.IsPreset(item))
            {
                presetConversation!.Blocks.AddNode(block);
            }
            else
            {
                Workspace.Chat["blocks"] ??= new JsonArray();
                Workspace.Chat.GetArray("blocks").AddNode(block);
            }
        }
        if (kind == "open_ui_delta")
        {
            block["open_ui"] = block.GetString("open_ui") + item.GetString("text");
        }
        else if (kind == "delta")
        {
            var type = item.GetString("type");
            block[type] = block.GetString(type) + item.GetString("text");
        }
        else
        {
            block["tool"] = item.GetString("name");
            block["running"] = kind == "tool_start";
            block[kind == "tool_start" ? "arguments" : "result"] = kind == "tool_start" ? item.GetString("arguments") : item.GetString("text");
        }
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
            var modifiesChat = op is "message" or "system_message" or "branch" or "rename" or "save_prompt" or "save_search_mode" or "reset_world" or "save"
                || op == "select_chat" && command.GetString("id").Length == 0;
            var showsBusy = modifiesChat || op is "select_chat" or "create_preset" or "delete_chat" or "delete_preset";
            turnRouting = Volatile.Read(ref routing);
            active = operation;
            if (showsBusy)
            {
                Emit(new() { ["kind"] = "busy", ["value"] = true });
            }
            try
            {
                await Execute(command, operation.Token);
            }
            catch (OperationCanceledException)
            {
                FinishPendingTools("Cancelled.");
                Emit(new()
                {
                    ["kind"] = "cancelled",
                    ["request"] = command.GetString("request")
                });
            }
            catch (Exception error)
            {
                FinishPendingTools(error.Message);
                Emit(new()
                {
                    ["kind"] = "error",
                    ["request"] = command.GetString("request"),
                    ["model_turn"] = op == "message",
                    ["agent"] = command.GetString("agent", "chat"),
                    ["message"] = error.Message
                });
            }
            finally
            {
                active = null;
                try
                {
                    if (op == "message" && command.GetString("agent") == "preset")
                    {
                        presetConversation?.Save();
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
            reply["items"] = index.Page(command.GetString("preset"), Math.Max(0, command.GetInt32("offset")), Math.Clamp(command.GetInt32("limit"), 1, 100));
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

    private void FinishPendingTools(string message)
    {
        foreach (var block in trace.Values.Where(block => block.GetBoolean("running")).ToArray())
        {
            Record(new()
            {
                ["kind"] = "tool_end",
                ["agent"] = block.GetString("agent"),
                ["block"] = block.GetString("id"),
                ["name"] = block.GetString("tool"),
                ["text"] = message
            });
        }
    }

    private void SelectConversation(string preset, string? id, bool fromHomeSend = false)
    {
        var nextWorkspace = new Workspace(Workspace.Root, seedDefaultPreset: false);
        nextWorkspace.Select(preset, id);
        ConversationTitle.ClearLegacyTitle(nextWorkspace.Chat);
        var nextPreset = new PresetConversation(nextWorkspace);
        nextPreset.Load(nextWorkspace);
        var nextSessions = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        try
        {
            foreach (var (role, name) in new[] { ("chat", "ChatAgent"), ("search", "SearchAgent"), ("env", "EnvAgent"), ("preset", "PresetAgent") })
            {
                var database = role == "preset" ? nextPreset.Database : Path.Combine(nextWorkspace.ChatRoot, "agents", role + ".sqlite");
                var previous = role == "preset" ? nextPreset.LegacySession : nextWorkspace.Chat.GetObject(role);
                nextSessions[role] = CreateSession(role, name, database, previous, nextWorkspace);
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
        Workspace = nextWorkspace;
        presetConversation = nextPreset;
        foreach (var session in nextSessions)
        {
            sessions[session.Key] = session.Value;
        }

        var fetch = new FetchAgent(Port, new(Port, () => turnRouting.For("fetch")), Record, Workspace.ChatRoot)
        {
            Prompt = () => Workspace.AgentPrompt("fetch"),
            Skills = job =>
            {
                var context = PluginEnvironment("fetch");
                context["session"] = "fetch-" + job;
                return plugins.Skills("fetch", context);
            }
        };
        search = new(Workspace, sessions["search"], new PageSource(Port, fetch, () => turnRouting.Settings));
        chat = new(Workspace, sessions["chat"], search);
        environment = new(Workspace, sessions["env"]);
        presets = new(Workspace, sessions["preset"]);
        Emit(new()
        {
            ["kind"] = "loaded",
            ["from_home_send"] = fromHomeSend,
            ["draft"] = JsonValues.Clone(Workspace.Chat["draft"]),
            ["chat"] = new JsonObject
            {
                ["id"] = Workspace.ChatId,
                ["name"] = Workspace.Chat.GetString("name"),
                ["blocks"] = new JsonArray(Workspace.Chat.GetArray("blocks").OfType<JsonObject>()
                    .Where(block => !PresetConversation.IsPreset(block))
                    .Concat(nextPreset.Blocks.OfType<JsonObject>()).Select(LoadedBlock).ToArray())
            },
            ["preset"] = Workspace.PresetId,
            ["config"] = Workspace.Config.DeepClone()
        });
    }

    private AgentSession CreateSession(string role, string name, string database, JsonObject? previous, Workspace workspace)
    {
        var session = new AgentSession(name, database, new(Port, () => turnRouting.For(role), attachmentRoot), Record);
        try
        {
            session.AdditionalSkills = () => plugins.Skills(role, PluginEnvironment(role));
            if (role == "chat") session.OpenUiInstructions = () => turnRouting.Settings.GetString("android_open_ui");
            if (session.History.Count == 0 && previous?["history"] is JsonArray legacy)
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
        var conversation = new PresetConversation(Workspace);
        conversation.Load(Workspace);
        var session = CreateSession("preset", "PresetAgent", conversation.Database, conversation.LegacySession, Workspace);
        sessions.Add("preset", session);
        presetConversation = conversation;
        presets = new(Workspace, session);
    }

    private void OpenPresetEditor(string request, bool created = false)
    {
        EnsurePresetSession();
        Emit(new()
        {
            ["kind"] = "preset_opened",
            ["request"] = request,
            ["created"] = created,
            ["preset"] = Workspace.PresetId,
            ["config"] = Workspace.Config.DeepClone(),
            ["blocks"] = new JsonArray(presetConversation!.Blocks.OfType<JsonObject>().Select(LoadedBlock).ToArray())
        });
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
            plugins.RequireScope(pluginId, command.GetString("preset"));
            if (command.GetString("action") is not ("createChat" or "createPreset")) RequirePreset(command);
        }
        if (await ExecuteExtensionCommand(command, token)) return;
        switch (command.GetString("op"))
        {
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
                return;
            case "create_preset":
                var name = command.GetString("name", "Preset");
                var preset = Workspace.CreatePreset(name);
                (await catalog).Preset(preset, name);
                Emit(new() { ["kind"] = "library_changed" });
                if (command.GetBoolean("open"))
                {
                    ShowHome(preset, notify: false);
                    OpenPresetEditor(command.GetString("request"), created: true);
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
        switch (command.GetString("op"))
        {
            case "message":
                var messageAgent = command.GetString("agent", "chat");
                RequireAgent(command, messageAgent);
                await RunMessage(messageAgent, command.GetString("text"), command.GetBoolean("stage"),
                    command.GetArray("attachments"), token, command.GetString("role", "user"), command.GetString("request"),
                    command["open_ui_action"] as JsonObject);
                break;
            case "system_message":
                var systemAgent = command.GetString("agent", "chat");
                var systemText = command.GetString("text").Trim();
                if (systemAgent is not ("chat" or "env" or "preset") || systemText.Length == 0)
                    throw new ArgumentException("A system message requires a conversation agent and text.");
                RequireAgent(command, systemAgent);
                sessions[systemAgent].Insert(systemText, "system");
                RecordInput(systemAgent, systemText, "system", new(), false);
                presetConversation?.Save();
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
                Emit(new()
                {
                    ["kind"] = "memory",
                    ["request"] = command.GetString("request"),
                    ["result"] = sessions[memoryAgent].Db.Call(command.GetString("tool", "db_list"), command.GetObject("arguments"))
                });
                break;
            case "rename":
                RequireActiveChat(command);
                titleRequests.Remove(Workspace.ChatId);
                Workspace.Chat["name"] = command.GetString("name");
                Workspace.Chat["title_source"] = "manual";
                Emit(new() { ["kind"] = "title", ["id"] = Workspace.ChatId, ["name"] = Workspace.Chat.GetString("name") });
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
                Workspace.Commit(new JsonObject { ["sim_date"] = Workspace.Config.GetString("sim_date"), ["phase"] = Workspace.Config.GetString("phase"), ["canon"] = JsonValues.Clone(Workspace.Config["canon"]), ["pages"] = new JsonObject(), ["search_cache"] = new JsonObject() });
                break;
            case "save":
                break;
            default:
                throw new ArgumentException("Unknown operation: " + command.GetString("op"));
        }
    }

    private async Task RunMessage(string role, string input, bool stage, JsonArray attachments, CancellationToken token,
        string inputRole = "user", string request = "", JsonObject? openUiAction = null)
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
        if (attachments.OfType<JsonObject>().Any(item => item.GetString("type").Length > 0))
            attachments = await plugins.ProcessAttachments(attachments, PluginEnvironment(role), token);
        new MessageAttachments(attachmentRoot).Validate(attachments, turnRouting.For(role));
        var session = sessions[role];
        if (role == "chat") session.ConfigureWorldMode(Workspace.AgenticSearch);
        var turn = new TurnInbox(Workspace.ChatId, role, message => RecordInput(role, message.GetString("text"), message.GetString("role", "user"), new(), false));
        session.Inbox = turn;
        Volatile.Write(ref inbox, turn);
        Emit(new() { ["kind"] = "generating", ["agent"] = role, ["value"] = true });
        var turnStart = Workspace.Chat.GetArray("blocks").Count;
        string answer;
        try
        {
            RecordInput(role, input, inputRole, attachments, inputRole == "user", openUiAction, consumeDraft: role == "chat");
            if (request.Length > 0)
                Emit(new() { ["kind"] = "message_accepted", ["request"] = request, ["chat"] = Workspace.ChatId });
            var worldBefore = Workspace.State;
            var presetNameBefore = Workspace.Config.GetString("name", Workspace.PresetId);
            answer = role switch
            {
                "env" => await environment!.Run(input, stage, token, attachments, inputRole),
                "preset" => await presets!.Run(input, token, attachments, inputRole),
                _ => await chat!.Run(modelInput, token, attachments, inputRole)
            };
            if (string.IsNullOrWhiteSpace(answer))
                throw new IOException("The model finished without a visible reply.");
            if (!ReferenceEquals(worldBefore, Workspace.State))
                Emit(new() { ["kind"] = "world_changed", ["agent"] = role });
            if (role != "preset")
            {
                Workspace.Chat.GetArray("messages").AddNode(new JsonObject { ["role"] = "assistant", ["agent"] = role, ["content"] = answer });
            }
            Emit(new() { ["kind"] = "completed", ["agent"] = role, ["text"] = answer });
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
            if (role == "chat" && !string.IsNullOrWhiteSpace(answer)) StartTitle(force: false);
        }
        finally
        {
            turn.Close();
            turn.Drain(message => session.Insert(message.GetString("content"), message.GetString("role")));
            Volatile.Write(ref inbox, null);
            session.Inbox = null;
            Emit(new() { ["kind"] = "generating", ["agent"] = role, ["value"] = false });
        }
        if (role == "chat") RecordReplyEnd(turnStart, answer);
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
            presetConversation!.Blocks.AddNode(block);
            presetConversation.Save();
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
        _ = GenerateTitle(Workspace.ChatId, Workspace.PresetId, request, transcript, selected, force, Workspace.AgentPrompt("title"));
    }

    private async Task GenerateTitle(string id, string preset, string request, string transcript, Provider selected, bool force, string instructions)
    {
        var result = new JsonObject { ["op"] = "title_result", ["id"] = id, ["preset"] = preset, ["request"] = request };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            result["title"] = await ConversationTitle.Generate(new(Port, () => selected), transcript, timeout.Token, instructions);
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
        Emit(new() { ["kind"] = "title", ["id"] = id, ["preset"] = result.GetString("preset"), ["name"] = title });
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
        commands.Writer.TryComplete();
        await worker;
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
