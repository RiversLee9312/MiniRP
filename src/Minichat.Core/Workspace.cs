using System.Text.Json.Nodes;

namespace Minichat.Core;

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
    public string AgentPrompt(string role) => Config.GetObject(role + "_agent").GetString("system_prompt");
    public string ChatRoot => Path.Combine(Root, "conversations", PresetId, ChatId);
    public Workspace(string root, bool seedDefaultPreset = true)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(Root, "presets"));
        if (seedDefaultPreset && !Directory.EnumerateFiles(Path.Combine(Root, "presets"), "*.json").Any())
        {
            AtomicFile.Write(Path.Combine(Root, "presets", "default.json"), JsonValues.Serialize(Default()));
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
            JsonObject? config = null;
            try
            {
                config = JsonValues.ParseObject(File.ReadAllText(file));
            }
            catch (System.Text.Json.JsonException) { }
            if (config is not null)
            {
                yield return new JsonObject { ["id"] = Path.GetFileNameWithoutExtension(file), ["name"] = config.GetString("name", Path.GetFileNameWithoutExtension(file)) };
            }
        }
    }
    public JsonArray Chats(string preset) => new(ChatEntries(preset).OrderByDescending(item => item.GetString("updated_at")).ToArray());

    internal IEnumerable<JsonNode> ChatEntries(string preset)
    {
        Safe(preset);
        var path = Path.Combine(Root, "conversations", preset);
        if (!Directory.Exists(path))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
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
                yield return new JsonObject { ["id"] = Path.GetFileNameWithoutExtension(file), ["name"] = display[..Math.Min(display.Length, 80)], ["updated_at"] = chat.GetString("updated_at", File.GetLastWriteTimeUtc(file).ToString("O")) };
            }
        }
    }
    public void Select(string preset, string? chat)
    {
        UsePreset(preset);
        ChatId = string.IsNullOrEmpty(chat) ? JsonValues.NewId() : Safe(chat);
        var file = Path.Combine(Root, "conversations", preset, ChatId + ".json");
        if (!string.IsNullOrEmpty(chat) && !File.Exists(file))
        {
            throw new FileNotFoundException("The requested conversation no longer exists.");
        }
        Chat = File.Exists(file) ? JsonValues.ParseObject(File.ReadAllText(file)) : new JsonObject { ["id"] = ChatId, ["name"] = "", ["messages"] = new JsonArray(), ["blocks"] = new JsonArray() };
        var legacy = Chat["chat"] is JsonObject && Chat["blocks"] is not JsonArray;
        if (legacy)
        {
            AtomicFile.Write(Path.Combine(ChatRoot, "migration", "python-v1.json"), File.ReadAllText(file));
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
        var config = JsonValues.ParseObject(File.ReadAllText(Path.Combine(Root, "presets", Safe(preset) + ".json")));
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
        AtomicFile.Write(Path.Combine(Root, "presets", Safe(PresetId) + ".json"), JsonValues.Serialize(config));
        Config = config;
        Save();
    }
    public string CreatePreset(string name)
    {
        var config = Default();
        config["name"] = name;
        var id = "preset-" + JsonValues.NewId();
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
    public static string Safe(string id)
    {
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new ArgumentException("Invalid identifier");
        }

        return id;
    }
}
