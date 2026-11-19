using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Minichat.Core;

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
                case "asset_open":
                    var url = command.GetString("url");
                    var agent = command.GetString("agent", "chat");
                    var attachment = command.GetObject("attachment");
                    if (url.Contains('?'))
                    {
                        var id = attachment.GetString("id");
                        if (id.Length == 0 || !url.EndsWith("?attachment=" + Workspace.Safe(id), StringComparison.Ordinal) ||
                            (url != attachment.GetString("thumbnail_url") &&
                                !(url == "minichat://assets/minichat.legacy/attachment-thumbnail?attachment=" + id && attachment["thumbnail"] is JsonObject)))
                            throw new UnauthorizedAccessException("This attachment preview is unavailable.");
                    }
                    response["result"] = plugins.OpenHostAsset(url, PluginEnvironment(agent), attachment.Count == 0 ? null : attachment);
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
        var chat = workspace.ChatId;
        return new()
        {
            ["session"] = agent == "preset" && workspace.PresetId.Length > 0 ? agent + "-" + workspace.PresetId :
                agent.Length == 0 || chat.Length == 0 ? null : agent + "-" + chat,
            ["agent"] = agent.Length == 0 ? null : agent,
            ["chat"] = chat.Length == 0 ? null : chat,
            ["preset"] = workspace.PresetId.Length == 0 ? null : workspace.PresetId
        };
    }

    private void PublishPluginEvent(JsonObject item)
    {
        var name = item.GetString("kind") switch
        {
            "loaded" => "chat.loaded", "library_changed" => "library.changed", "title" => "chat.title",
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
        var payload = item.GetString("kind") == "delta" ?
            new JsonObject { ["block"] = item.GetString("block"), ["text"] = item.GetString("text") } : item;
        var context = PluginEnvironment(role);
        if (item.GetString("preset") is { Length: > 0 } targetPreset) context["preset"] = targetPreset;
        if (name == "chat.title") context["chat"] = item.GetString("id");
        if (item.GetString("session") is { Length: > 0 } session) context["session"] = session;
        plugins.Publish(name, payload, context);
    }

    private async Task<bool> ExecuteExtensionCommand(JsonObject command, CancellationToken token)
    {
        var request = command.GetString("request");
        switch (command.GetString("op"))
        {
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
                var package = plugins.Install(command.GetString("path"), inspect);
                if (!inspect) plugins.Publish("plugin.enabled", new(), new(), package.Id);
                Emit(new() { ["kind"] = "plugin", ["request"] = request, ["item"] = plugins.Descriptor(package) });
                return true;
            case "plugin_uninstall":
                plugins.Uninstall(command.GetString("id"));
                Emit(new() { ["kind"] = "plugins", ["request"] = request, ["items"] = plugins.List() });
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
            case "plugin_settings":
                EmitPluginSettings(request);
                return true;
            case "plugin_page":
                Emit(new() { ["kind"] = "plugin_page", ["request"] = request,
                    ["item"] = plugins.Page(command.GetString("id"), command.GetString("key"), Workspace.PresetId) });
                return true;
            case "plugin_setting":
                plugins.SetSetting(command.GetString("id"), command.GetString("key"), command["value"], PluginEnvironment());
                EmitPluginSettings(request);
                return true;
            case "plugin_action":
            case "plugin_url":
                plugins.OpenSettingLink(command.GetString("id"), command.GetString("key"), PluginEnvironment());
                return true;
            case "home_request":
                plugins.Publish("home.requested", new() { ["locale"] = command.GetString("locale"), ["request"] = request }, PluginEnvironment());
                return true;
            case "_plugin_workspace":
                PluginWorkspace(command);
                return true;
            case "home":
                var preset = command.GetString("preset");
                if (!command.GetBoolean("restore") || Workspace.PresetEntries().Any(item => item.GetString("id") == preset))
                    ShowHome(preset);
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

    private void ShowHome(string preset, bool notify = true)
    {
        var next = new Workspace(Workspace.Root, seedDefaultPreset: false);
        if (preset.Length == 0 && seedDefaultPreset) preset = next.InitialPreset().Id;
        if (preset.Length > 0) next.UsePreset(preset);
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
        trace.Clear();
        chat = null;
        search = null;
        environment = null;
        presets = null;
        presetConversation = null;
        Workspace = next;
        if (notify)
            Emit(new() { ["kind"] = "home", ["preset"] = next.PresetId, ["config"] = next.Config.DeepClone() });
    }

    private async Task Remove(JsonObject command)
    {
        var preset = Workspace.Safe(command.GetString("preset"));
        var id = Workspace.Safe(command.GetString("id"));
        var index = await catalog;
        if (command.GetString("op") == "delete_chat")
        {
            if (Workspace.PresetId == preset && Workspace.ChatId == id) ShowHome(preset);
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
            if (Workspace.PresetId == preset) ShowHome(next ?? "");
            Workspace.Trash(preset, null);
            index.RemovePreset(preset);
            Emit(new() { ["kind"] = "preset_removed", ["id"] = preset, ["fallback"] = next ?? "" });
        }
        Emit(new() { ["kind"] = "library_changed", ["preset"] = preset, ["presets_changed"] = preset == id && command.GetString("op") == "delete_preset" });
    }

    private async Task<JsonNode?> PluginApi(PluginPackage package, JsonObject context, string op, JsonObject args, CancellationToken ct)
    {
        var id = args.GetString("id");
        if (op == "generate")
        {
            var provider = Volatile.Read(ref routing).For("chat") with { MaxOutput = 384, Thinking = false, ReasoningEffort = "" };
            var model = new ModelClient(Port, () => provider);
            var instructions = args.GetString("system");
            if (instructions.Length > 8192) throw new ArgumentException("Instructions are too long.");
            var reply = await model.Complete(new JsonArray(new JsonObject { ["role"] = "system", ["content"] = instructions },
                new JsonObject { ["role"] = "user", ["content"] = RequireText(args) }), new(), (_, _) => { }, ct);
            return new JsonObject { ["text"] = reply.GetString("content") };
        }
        if (op == "listPresets" || op == "listChats")
            return (await catalog).Page(op == "listPresets" ? "" : PluginPreset(package, context, args),
                Math.Max(0, args.GetInt32("offset")), Math.Clamp(args.GetInt32("limit", 30), 1, 100), plugins.Scope(package));
        if (op == "getPreset")
        {
            var config = new Workspace(Workspace.Root, seedDefaultPreset: false);
            config.UsePreset(PluginPreset(package, context, args));
            return config.Config;
        }
        if (op == "readChat")
        {
            var preset = PluginPreset(package, context, args);
            var chatId = Workspace.Safe(args.GetString("chat", context.GetString("chat")));
            var path = Path.Combine(Workspace.Root, "conversations", preset, chatId + ".json");
            var document = JsonValues.ParseObject(await File.ReadAllTextAsync(path, ct));
            if (document["messages"] is not JsonArray) LegacyImporter.ImportChat(document);
            return new JsonObject
            {
                ["id"] = chatId, ["preset"] = preset, ["name"] = document.GetString("name"),
                ["messages"] = PageMessages(document.GetArray("messages"), args)
            };
        }
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
            var file = role == "preset" ? Path.Combine(Workspace.Root, "preset_sessions", preset, "agent.sqlite") :
                Path.Combine(Workspace.Root, "conversations", preset, Workspace.Safe(args.GetString("chat", context.GetString("chat"))),
                    role == "fetch" ? "fetch" : "agents", role == "fetch" ? Workspace.Safe(args.GetString("id", context.GetString("session")).Replace("fetch-", "", StringComparison.Ordinal)) + ".sqlite" : role + ".sqlite");
            if (!File.Exists(file)) return new JsonArray();
            using var db = new MemoryDb(file);
            return PageMessages(JsonNode.Parse(db.Get("_context/history") ?? "[]") as JsonArray ?? new(), args);
        }
        if (op is "createTemporarySession" or "runTemporary" or "closeTemporary")
        {
            if (op == "createTemporarySession")
            {
                if (temporary.Count >= 16) throw new InvalidOperationException("Too many temporary sessions.");
                var key = JsonValues.NewId();
                var provider = Volatile.Read(ref routing).For("chat");
                var session = new AgentSession("TemporaryAgent", ":memory:", new(Port, () => provider), _ => { });
                var temporaryContext = new JsonObject { ["session"] = key, ["agent"] = "temporary", ["preset"] = JsonValues.Clone(context["preset"]) };
                session.AdditionalSkills = () => plugins.Skills("temporary", temporaryContext);
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
                    ct, maxRounds: 10);
                return new JsonObject { ["text"] = result.GetString("content") };
            }
            finally { item.Gate.Release(); }
        }
        var target = args.GetString("chat", context.GetString("chat"));
        if (target.Length > 0) Workspace.Safe(target);
        else if (op is not ("createChat" or "createPreset" or "setPrompt"))
            throw new ArgumentException("This operation needs a chat.");
        var agent = args.GetString("agent", context.GetString("agent", "chat"));
        if (agent is not ("chat" or "env" or "preset") && !(op == "setPrompt" && agent is "search" or "fetch" or "title"))
            throw new ArgumentException("Choose a conversation agent.");
        var targetPreset = op == "createPreset" ? "" : Workspace.Safe(args.GetString("preset",
            context.GetString("preset", op == "setPrompt" ? Workspace.PresetId : "")));
        plugins.RequireScope(package, targetPreset);
        var activePreset = Workspace.PresetId == targetPreset;
        if (op == "steer")
        {
            if (!activePreset || Volatile.Read(ref inbox)?.TryPost(target, agent, new()
                { ["text"] = RequireText(args), ["role"] = args.GetBoolean("system") ? "system" : "user" }) != true)
                throw new InvalidOperationException("That conversation is not generating.");
            return new JsonObject { ["accepted"] = true };
        }
        if (op == "insertSystemMessage" && activePreset && Volatile.Read(ref inbox)?.TryPost(target, agent, new()
            { ["text"] = RequireText(args), ["role"] = "system" }) == true)
            return new JsonObject { ["accepted"] = true };
        if (op is not ("createChat" or "createPreset" or "send" or "insertSystemMessage" or "renameChat" or "generateTitle" or "setSearchMode" or "setPrompt" or "branchChat"))
            throw new ArgumentException("Unknown harness operation.");
        var command = (JsonObject)args.DeepClone();
        command["chat"] = target;
        command["preset"] = targetPreset;
        command["_plugin"] = package.Id;
        command["agent"] = agent;
        command["action"] = op;
        command["op"] = "_plugin_workspace";
        command["request"] = JsonValues.NewId();
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

    private void PluginWorkspace(JsonObject request)
    {
        RequireChat(request);
        var op = request.GetString("action");
        request["op"] = op switch
        {
            "createChat" => "select_chat", "createPreset" => "create_preset",
            "send" => "message", "insertSystemMessage" => "system_message", "renameChat" => "rename",
            "generateTitle" => "generate_title", "setSearchMode" => "save_search_mode", "setPrompt" => "save_prompt",
            "branchChat" => "branch", _ => throw new ArgumentException("Unknown workspace operation.")
        };
        Dispatch(request);
    }

    private string PluginPreset(PluginPackage package, JsonObject context, JsonObject args)
    {
        var preset = Workspace.Safe(args.GetString("preset", context.GetString("preset")));
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
        new(messages.Where(item => item.GetString("role") is "user" or "assistant" or "system")
            .Skip(Math.Max(0, args.GetInt32("offset"))).Take(Math.Clamp(args.GetInt32("limit", 30), 1, 100))
            .Select(item => (JsonNode?)new JsonObject { ["role"] = item.GetString("role"), ["agent"] = item.GetString("agent"),
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
