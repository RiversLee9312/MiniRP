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
    private JsonObject settings = new();
    private Provider provider = Provider.From(new());
    private ChatAgent? chat;
    private SearchAgent? search;
    private EnvAgent? environment;
    private PresetAgent? presets;

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
        Port = new(Emit);
        Model = new(Port, () => provider);
        worker = Task.Run(ProcessCommands);
        Emit(new()
        {
            ["kind"] = "ready",
            ["presets"] = Workspace.Presets(),
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
                provider = Provider.From(command.GetObject("provider"));
                settings = (JsonObject)command.GetObject("settings").DeepClone();
                break;
            case "models":
                _ = DiscoverModels(Provider.From(command.GetObject("provider")));
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
            if (Workspace.Chat["blocks"] is null)
            {
                Workspace.Chat["blocks"] = new JsonArray();
            }
            Workspace.Chat.GetArray("blocks").AddNode(block);
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
            active = operation;
            Emit(new()
            {
                ["kind"] = "busy",
                ["value"] = true
            });
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
                    ["message"] = error.Message
                });
            }
            finally
            {
                active = null;
                try
                {
                    Workspace.Save();
                }
                catch (Exception error)
                {
                    Emit(new() { ["kind"] = "error", ["message"] = error.Message });
                }
                Emit(new()
                {
                    ["kind"] = "busy",
                    ["value"] = false
                });
            }
        }
    }

    private async Task DiscoverModels(Provider requestedProvider)
    {
        Emit(new() { ["kind"] = "models_loading", ["provider"] = requestedProvider.Id, ["value"] = true });
        try
        {
            var models = await Model.Models(requestedProvider, lifetime.Token);
            Emit(new() { ["kind"] = "models", ["provider"] = requestedProvider.Id, ["items"] = models });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Emit(new() { ["kind"] = "error", ["message"] = error.Message });
        }
        finally
        {
            Emit(new() { ["kind"] = "models_loading", ["provider"] = requestedProvider.Id, ["value"] = false });
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
        var nextSessions = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        try
        {
            foreach (var (role, name) in new[] { ("chat", "ChatAgent"), ("search", "SearchAgent"), ("env", "EnvAgent"), ("preset", "PresetAgent") })
            {
                var session = new AgentSession(name, Path.Combine(nextWorkspace.ChatRoot, "agents", role + ".sqlite"), Model, Record);
                nextSessions[role] = session;
                if (session.History.Count == 0 && nextWorkspace.Chat.GetObject(role)["history"] is JsonArray legacy)
                {
                    session.Restore(legacy, nextWorkspace.Chat.GetObject(role).GetString("summary"));
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
        foreach (var session in nextSessions)
        {
            sessions[session.Key] = session.Value;
        }

        var fetch = new FetchAgent(Port, Model, Record, Workspace.ChatRoot);
        search = new(Workspace, sessions["search"], new PageSource(Port, fetch, () => settings));
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
                ["blocks"] = JsonValues.Clone(Workspace.Chat["blocks"])
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
                Workspace.CreatePreset(command.GetString("name", "Preset"));
                Emit(new()
                {
                    ["kind"] = "presets",
                    ["items"] = Workspace.Presets()
                });
                return;
        }
        if (chat is null || environment is null || presets is null || search is null)
        {
            throw new InvalidOperationException("Choose a preset and a chat first.");
        }
        switch (command.GetString("op"))
        {
            case "message":
                var role = command.GetString("agent", "chat");
                var input = command.GetString("text");
                if (Workspace.Chat["messages"] is null)
                {
                    Workspace.Chat["messages"] = new JsonArray();
                }
                var messages = Workspace.Chat.GetArray("messages");
                messages.AddNode(new JsonObject { ["role"] = "user", ["agent"] = role, ["content"] = input });
                if (Workspace.Chat["blocks"] is null)
                {
                    Workspace.Chat["blocks"] = new JsonArray();
                }
                Workspace.Chat.GetArray("blocks").AddNode(new JsonObject { ["id"] = JsonValues.NewId(), ["kind"] = "user", ["agent"] = role, ["content"] = input });
                if (messages.Count == 1)
                {
                    Workspace.Chat["name"] = input[..Math.Min(input.Length, 40)];
                }
                Emit(new()
                {
                    ["kind"] = "user",
                    ["agent"] = role,
                    ["text"] = input
                });
                Workspace.Save();
                var answer = role switch
                {
                    "env" => await environment.Run(input, command.GetBoolean("stage"), token),
                    "preset" => await presets.Run(input, token),
                    _ => await chat.Run(input, token)
                };
                messages.AddNode(new JsonObject { ["role"] = "assistant", ["agent"] = role, ["content"] = answer });
                Emit(new()
                {
                    ["kind"] = "completed",
                    ["agent"] = role,
                    ["text"] = answer
                });
                break;
            case "memory":
                Emit(new()
                {
                    ["kind"] = "memory",
                    ["result"] = sessions[command.GetString("agent", "chat")].Db.Call(command.GetString("tool", "db_list"), command.GetObject("arguments"))
                });
                break;
            case "rename":
                Workspace.Chat["name"] = command.GetString("name");
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

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        active?.Cancel();
        commands.Writer.TryComplete();
        await worker;
        foreach (var session in sessions.Values)
        {
            session.Dispose();
        }
        lifetime.Dispose();
    }
}
