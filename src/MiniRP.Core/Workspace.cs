using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed class Workspace
{
    public string Root
    {
        get;
    }
    public string PresetId { get; private set; } = "";
    public string ChatId { get; private set; } = "";
    public JsonObject Config { get; private set; } = new();
    public JsonObject State { get; private set; } = new();
    public JsonObject Chat { get; private set; } = new();
    public bool AgenticSearch => Config.GetBoolean("agentic_search", true);
    public string Mode => Chat.GetString("mode", Config.GetString("chat_mode", "character"));
    public string AgentPrompt(string role) => Config.GetObject(role + "_agent").GetString("system_prompt");
    public string ChatRoot => Path.Combine(Root, "conversations", PresetId, ChatId);
    public Workspace(string root, bool seedDefaultPreset = true)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(Root, "presets"));
        if (seedDefaultPreset && !Directory.EnumerateFiles(Path.Combine(Root, "presets"), "*.json").Any())
        {
            var id = WorkspaceIds.New();
            var preset = Default();
            preset["id"] = id;
            AtomicFile.Write(Path.Combine(Root, "presets", id + ".json"), JsonValues.Serialize(preset));
        }
    }
    public static JsonObject Default() => new() { ["name"] = "Default", ["sim_date"] = DateTime.Today.ToString("yyyy-MM-dd"), ["phase"] = "", ["canon"] = new JsonArray(), ["world"] = new JsonObject { ["summary"] = "", ["rules"] = new JsonArray() }, ["chat_agent"] = new JsonObject { ["system_prompt"] = "" }, ["search_editor"] = new JsonObject(), ["page_editor"] = new JsonObject(), ["page_generator"] = new JsonObject(), ["stage"] = new JsonObject() };
    public (string Id, string Name) InitialPreset()
    {
        var preset = PresetEntries().FirstOrDefault();
        return (preset.GetString("id"), preset.GetString("name"));
    }

    public JsonArray Presets() => new(PresetEntries().ToArray());

    internal IEnumerable<JsonNode> PresetEntries()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "presets"), "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!WorkspaceIds.IsValid(id)) continue;
            JsonObject? config = null;
            try
            {
                config = JsonValues.ParseObject(File.ReadAllText(file));
            }
            catch (System.Text.Json.JsonException) { }
            if (config is not null)
            {
                yield return new JsonObject { ["id"] = id, ["name"] = config.GetString("name", id) };
            }
        }
    }
    public JsonArray Chats(string preset) => new(ChatEntries(preset).OrderByDescending(item => item.GetString("updated_at")).ToArray());

    internal IEnumerable<JsonNode> ChatEntries(string preset)
    {
        WorkspaceIds.Require(preset);
        var path = Path.Combine(Root, "conversations", preset);
        if (!Directory.Exists(path))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!WorkspaceIds.IsValid(id)) continue;
            JsonObject? chat = null;
            try
            {
                chat = JsonValues.ParseObject(File.ReadAllText(file));
            }
            catch (System.Text.Json.JsonException) { }
            if (chat is not null)
            {
                ConversationTitle.ClearLegacyTitle(chat);
                var display = chat.GetString("name", chat.GetString("title"));
                if (display.Length == 0) display = chat.GetArray("messages").FirstOrDefault(item => item.GetString("role") == "user").GetString("content");
                yield return new JsonObject { ["id"] = id, ["name"] = display[..Math.Min(display.Length, 80)], ["updated_at"] = chat.GetString("updated_at", File.GetLastWriteTimeUtc(file).ToString("O")) };
            }
        }
    }
    public void Select(string preset, string? chat)
    {
        UsePreset(preset);
        ChatId = string.IsNullOrEmpty(chat) ? WorkspaceIds.New() : WorkspaceIds.Require(chat);
        var file = Path.Combine(Root, "conversations", preset, ChatId + ".json");
        if (!string.IsNullOrEmpty(chat) && !File.Exists(file))
        {
            throw new FileNotFoundException("The requested conversation no longer exists.");
        }
        Chat = File.Exists(file) ? JsonValues.ParseObject(File.ReadAllText(file)) : new JsonObject { ["id"] = ChatId, ["name"] = "", ["messages"] = new JsonArray(), ["blocks"] = new JsonArray() };
        var legacy = Chat["chat"] is JsonObject && Chat["blocks"] is not JsonArray;
        if (legacy)
        {
            var backup = Path.Combine(ChatRoot, "migration", "python-v1.json");
            if (!File.Exists(backup)) AtomicFile.Write(backup, File.ReadAllText(file));
            LegacyImporter.ImportChat(Chat);
        }
        if (Chat["messages"] is not JsonArray) Chat["messages"] = new JsonArray();
        if (Chat["blocks"] is not JsonArray) Chat["blocks"] = new JsonArray();
        State = Chat["world"] as JsonObject is { } world ? (JsonObject)world.DeepClone()
            : (legacy ? LegacyImporter.World(Root, preset) : null)
            ?? new JsonObject { ["sim_date"] = Config.GetString("sim_date", DateTime.Today.ToString("yyyy-MM-dd")), ["phase"] = Config.GetString("phase"), ["canon"] = JsonValues.Clone(Config["canon"]) ?? new JsonArray(), ["search_cache"] = new JsonObject(), ["pages"] = new JsonObject() };
        Directory.CreateDirectory(ChatRoot);
        if (legacy) Save();
    }

    public void UsePreset(string preset)
    {
        var config = JsonValues.ParseObject(File.ReadAllText(Path.Combine(Root, "presets", WorkspaceIds.Require(preset) + ".json")));
        PresetId = preset;
        Config = config;
    }

    internal void Trash(string preset, string? chat)
    {
        var target = Path.Combine(Root, "trash", JsonValues.NewId());
        Directory.CreateDirectory(target);
        var parent = Path.Combine(Root, "conversations", preset);
        void MoveFile(string path, string name)
        {
            if (File.Exists(path)) File.Move(path, Path.Combine(target, name));
        }
        void MoveDirectory(string path, string name)
        {
            if (Directory.Exists(path)) Directory.Move(path, Path.Combine(target, name));
        }
        if (chat is null)
        {
            MoveFile(Path.Combine(Root, "presets", preset + ".json"), "preset.json");
            MoveDirectory(parent, "conversations");
            MoveDirectory(Path.Combine(Root, "preset_sessions", preset), "preset-session");
        }
        else
        {
            MoveFile(Path.Combine(parent, chat + ".json"), chat + ".json");
            MoveDirectory(Path.Combine(parent, chat), "session");
        }
    }
    public void Save()
    {
        if (ChatId.Length == 0)
        {
            return;
        }

        Chat["world"] = State.DeepClone();
        Chat["preset"] = PresetId;
        Chat["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        AtomicFile.Write(Path.Combine(Root, "conversations", PresetId, ChatId + ".json"), JsonValues.Serialize(Chat));
    }
    public void Commit(JsonObject state)
    {
        State = state;
        Save();
    }
    public void CommitConfig(JsonObject config)
    {
        AtomicFile.Write(Path.Combine(Root, "presets", WorkspaceIds.Require(PresetId) + ".json"), JsonValues.Serialize(config));
        Config = config;
        Save();
    }
    public string CreatePreset(string name, bool interview = false)
    {
        var config = Default();
        config["name"] = name;
        if (interview) config["setup_status"] = "draft";
        var id = WorkspaceIds.New();
        config["id"] = id;
        AtomicFile.Write(Path.Combine(Root, "presets", id + ".json"), JsonValues.Serialize(config));
        return id;
    }
    public string WorldText => JsonValues.Serialize(new JsonObject
    {
        ["preset"] = Config.GetString("name"),
        ["world"] = JsonValues.Clone(Config["world"]),
        ["instruction"] = "Current state overrides initial preset phase and canon. Cached documents are historical material.",
        ["current_state"] = new JsonObject
        {
            ["sim_date"] = State.GetString("sim_date"),
            ["phase"] = State.GetString("phase"),
            ["canon"] = JsonValues.Clone(State["canon"])
        }
    });
    public string Conversation => JsonValues.Serialize(Chat.GetArray("messages"));
    public string RecentConversation(int maxCharacters)
    {
        var excerpts = new Stack<string>();
        foreach (var message in Chat.GetArray("messages").Reverse())
        {
            var content = message.GetString("content");
            if (content.Length == 0) continue;
            var role = message.GetString("role");
            var length = Math.Min(content.Length, maxCharacters - role.Length - 4);
            if (length <= 0) break;
            excerpts.Push(role + ": " + content[..length]);
            maxCharacters -= length + role.Length + 3;
        }
        return string.Join('\n', excerpts);
    }
    public static string Safe(string id)
    {
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new ArgumentException("Invalid identifier");
        }

        return id;
    }
}
