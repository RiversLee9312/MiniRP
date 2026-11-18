using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Minichat.Core;

/// <summary>Coordinates serialized workspace changes; platform I/O replies never wait in that queue.</summary>
public sealed class Engine : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonObject> events = new();
    private readonly Channel<JsonObject> commands = Channel.CreateUnbounded<JsonObject>(new() { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, AgentSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> trace = new(StringComparer.Ordinal);
    private readonly Task worker;
    private CancellationTokenSource? active;
    private sealed record Routing(Provider Default, JsonObject Agents, JsonObject Settings)
    {
        public Provider For(string role) => Agents[role] is JsonObject value ? Provider.From(value) : Default;
    }

    private readonly Task<WorkspaceCatalog> catalog;
    private readonly string attachmentRoot;
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

    public Engine(string root)
    {
        Workspace = new(root);
        attachmentRoot = Path.Combine(Workspace.Root, "attachments");
        var initialWorkspace = Workspace;
        catalog = Task.Run(() => new WorkspaceCatalog(initialWorkspace));
        Port = new(Emit);
        turnRouting = routing;
        Model = new(Port, () => Volatile.Read(ref routing).Default);
        worker = Task.Run(ProcessCommands);
        var initialPreset = Workspace.InitialPreset();
        Emit(new()
        {
            ["kind"] = "ready",
            ["preset"] = initialPreset.Id,
            ["preset_name"] = initialPreset.Name,
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
                active?.Cancel();
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

    private void Emit(JsonObject item) => events.Enqueue(item);

    private void Record(JsonObject item)
    {
        Emit(item);
        var kind = item.GetString("kind");
        if (kind is not ("delta" or "tool_start" or "tool_end"))
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
        if (kind == "delta")
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
            var modifiesChat = op is "message" or "branch" or "select_chat" or "rename" or "save_prompt" or "save_search_mode" or "reset_world" or "save";
            var showsBusy = modifiesChat || op == "create_preset";
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
                    ["kind"] = "cancelled"
                });
            }
            catch (Exception error)
            {
                FinishPendingTools(error.Message);
                Emit(new()
                {
                    ["kind"] = "error",
                    ["request"] = command.GetString("request"),
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

    private void SelectConversation(string preset, string? id)
    {
        var nextWorkspace = new Workspace(Workspace.Root);
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
                var session = new AgentSession(name, database, new(Port, () => turnRouting.For(role), attachmentRoot), Record);
                nextSessions[role] = session;
                var previous = role == "preset" ? nextPreset.LegacySession : nextWorkspace.Chat.GetObject(role);
                if (session.History.Count == 0 && previous?["history"] is JsonArray legacy)
                {
                    session.Restore(legacy, previous.GetString("summary"));
                }
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

        var fetch = new FetchAgent(Port, new(Port, () => turnRouting.For("fetch")), Record, Workspace.ChatRoot);
        search = new(Workspace, sessions["search"], new PageSource(Port, fetch, () => turnRouting.Settings));
        chat = new(Workspace, sessions["chat"], search);
        environment = new(Workspace, sessions["env"]);
        presets = new(Workspace, sessions["preset"]);
        Emit(new()
        {
            ["kind"] = "loaded",
            ["chat"] = new JsonObject
            {
                ["id"] = Workspace.ChatId,
                ["name"] = Workspace.Chat.GetString("name"),
                ["messages"] = JsonValues.Clone(Workspace.Chat["messages"]),
                ["blocks"] = new JsonArray(Workspace.Chat.GetArray("blocks").Where(block => !PresetConversation.IsPreset(block))
                    .Concat(nextPreset.Blocks).Select(JsonValues.Clone).ToArray())
            },
            ["preset"] = Workspace.PresetId,
            ["config"] = Workspace.Config.DeepClone()
        });
    }

    private async Task Execute(JsonObject command, CancellationToken token)
    {
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
                (await catalog).Preset(Workspace.CreatePreset(name), name);
                Emit(new() { ["kind"] = "library_changed" });
                Emit(new()
                {
                    ["kind"] = "presets",
                    ["items"] = Workspace.Presets()
                });
                return;
            case "title_result":
                await ApplyTitle(command);
                return;
        }
        if (chat is null || environment is null || presets is null || search is null)
        {
            throw new InvalidOperationException("Choose a preset and a chat first.");
        }
        switch (command.GetString("op"))
        {
            case "message":
                await RunMessage(command.GetString("agent", "chat"), command.GetString("text"), command.GetBoolean("stage"), command.GetArray("attachments"), token);
                break;
            case "branch":
                if (command.GetString("chat") != Workspace.ChatId)
                {
                    throw new InvalidOperationException("The conversation has changed.");
                }
                var branch = ConversationVersions.Fork(Workspace, command.GetString("block"), sessions.Keys.Where(role => role != "preset"));
                SelectConversation(Workspace.PresetId, branch.Id);
                await RunMessage("chat", branch.Input, false, branch.Attachments, token);
                break;
            case "generate_title":
                StartTitle(force: true);
                break;
            case "memory":
                Emit(new()
                {
                    ["kind"] = "memory",
                    ["request"] = command.GetString("request"),
                    ["result"] = sessions[command.GetString("agent", "chat")].Db.Call(command.GetString("tool", "db_list"), command.GetObject("arguments"))
                });
                break;
            case "rename":
                titleRequests.Remove(Workspace.ChatId);
                Workspace.Chat["name"] = command.GetString("name");
                Workspace.Chat["title_source"] = "manual";
                Emit(new() { ["kind"] = "title", ["id"] = Workspace.ChatId, ["name"] = Workspace.Chat.GetString("name") });
                break;
            case "save_search_mode":
                var updated = (JsonObject)Workspace.Config.DeepClone();
                updated["agentic_search"] = command.GetBoolean("enabled", true);
                Workspace.CommitConfig(updated);
                EmitConfig();
                break;
            case "save_prompt":
                var config = (JsonObject)Workspace.Config.DeepClone();
                if (config["chat_agent"] is null)
                {
                    config["chat_agent"] = new JsonObject();
                }
                config.GetObject("chat_agent")["system_prompt"] = command.GetString("text");
                Workspace.CommitConfig(config);
                break;
            case "reset_world":
                Workspace.Commit(new JsonObject { ["sim_date"] = Workspace.Config.GetString("sim_date"), ["phase"] = Workspace.Config.GetString("phase"), ["canon"] = JsonValues.Clone(Workspace.Config["canon"]), ["pages"] = new JsonObject(), ["search_cache"] = new JsonObject() });
                break;
            case "save":
                break;
            default:
                throw new ArgumentException("Unknown operation: " + command.GetString("op"));
        }
    }

    private async Task RunMessage(string role, string input, bool stage, JsonArray attachments, CancellationToken token)
    {
        if (role is not ("chat" or "env" or "preset"))
        {
            throw new ArgumentException("Unknown conversation agent.");
        }
        new MessageAttachments(attachmentRoot).Validate(attachments, turnRouting.For(role));
        Workspace.Chat["messages"] ??= new JsonArray();
        Workspace.Chat["blocks"] ??= new JsonArray();
        var messages = Workspace.Chat.GetArray("messages");
        var block = new JsonObject { ["id"] = JsonValues.NewId(), ["kind"] = "user", ["agent"] = role, ["content"] = input };
        if (role == "chat")
        {
            block["version"] = ConversationVersions.Capture(Workspace, sessions);
            block["message_index"] = messages.Count;
        }
        var message = new JsonObject { ["role"] = "user", ["agent"] = role, ["content"] = input };
        if (attachments.Count > 0)
        {
            block["attachments"] = attachments.DeepClone();
            message["attachments"] = attachments.DeepClone();
        }
        if (role == "preset")
        {
            presetConversation!.Blocks.AddNode(block);
            presetConversation.Save();
        }
        else
        {
            messages.AddNode(message);
            Workspace.Chat.GetArray("blocks").AddNode(block);
            Workspace.Save();
        }
        Emit(new() { ["kind"] = "user", ["agent"] = role, ["text"] = input, ["block"] = block.GetString("id"), ["version"] = block.GetString("version"), ["attachments"] = attachments.DeepClone() });
        var answer = role switch
        {
            "env" => await environment!.Run(input, stage, token, attachments),
            "preset" => await presets!.Run(input, token, attachments),
            _ => await chat!.Run(input, token, attachments)
        };
        if (role != "preset")
        {
            messages.AddNode(new JsonObject { ["role"] = "assistant", ["agent"] = role, ["content"] = answer });
        }
        Emit(new() { ["kind"] = "completed", ["agent"] = role, ["text"] = answer });
        if (role == "preset")
        {
            EmitConfig();
        }
        if (role == "chat" && !string.IsNullOrWhiteSpace(answer))
        {
            StartTitle(force: false);
        }
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
        _ = GenerateTitle(Workspace.ChatId, Workspace.PresetId, request, transcript, selected, force);
    }

    private async Task GenerateTitle(string id, string preset, string request, string transcript, Provider selected, bool force)
    {
        var result = new JsonObject { ["op"] = "title_result", ["id"] = id, ["preset"] = preset, ["request"] = request };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            result["title"] = await ConversationTitle.Generate(new(Port, () => selected), transcript, timeout.Token);
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
        Emit(new() { ["kind"] = "title", ["id"] = id, ["name"] = title });
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        active?.Cancel();
        commands.Writer.TryComplete();
        await worker;
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
