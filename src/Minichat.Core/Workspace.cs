using System.Text.Json.Nodes;

namespace Minichat.Core;

public sealed class Workspace
{
    public string Root
    {
        get;
    }
    public string PresetId { get; private set; } = "default";
    public string ChatId { get; private set; } = "";
    public JsonObject Config { get; private set; } = new();
    public JsonObject State { get; private set; } = new();
    public JsonObject Chat { get; private set; } = new();
    public bool AgenticSearch => Config.GetBoolean("agentic_search", true);
    public string ChatRoot => Path.Combine(Root, "conversations", PresetId, ChatId);
    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(Root, "presets"));
        if (!Directory.EnumerateFiles(Path.Combine(Root, "presets"), "*.json").Any())
        {
            AtomicFile.Write(Path.Combine(Root, "presets", "default.json"), JsonValues.Serialize(Default()));
        }
    }
    public static JsonObject Default() => new() { ["name"] = "Default", ["sim_date"] = DateTime.Today.ToString("yyyy-MM-dd"), ["phase"] = "", ["canon"] = new JsonArray(), ["world"] = new JsonObject { ["summary"] = "", ["rules"] = new JsonArray() }, ["chat_agent"] = new JsonObject { ["system_prompt"] = "" }, ["search_editor"] = new JsonObject(), ["page_editor"] = new JsonObject(), ["page_generator"] = new JsonObject(), ["stage"] = new JsonObject() };
    public (string Id, string Name) InitialPreset()
    {
        var preset = PresetEntries().FirstOrDefault() ?? throw new InvalidOperationException("No valid preset found.");
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
                yield return new JsonObject { ["id"] = Path.GetFileNameWithoutExtension(file), ["name"] = chat.GetString("name", chat.GetString("title")), ["updated_at"] = chat.GetString("updated_at", File.GetLastWriteTimeUtc(file).ToString("O")) };
            }
        }
    }
    public void Select(string preset, string? chat)
    {
        Safe(preset);
        Config = JsonValues.ParseObject(File.ReadAllText(Path.Combine(Root, "presets", preset + ".json")));
        PresetId = preset;
        ChatId = string.IsNullOrEmpty(chat) ? JsonValues.NewId() : Safe(chat);
        var file = Path.Combine(Root, "conversations", preset, ChatId + ".json");
        if (!string.IsNullOrEmpty(chat) && !File.Exists(file))
        {
            throw new FileNotFoundException("The requested conversation no longer exists.");
        }
        Chat = File.Exists(file) ? JsonValues.ParseObject(File.ReadAllText(file)) : new JsonObject { ["id"] = ChatId, ["name"] = "", ["messages"] = new JsonArray(), ["blocks"] = new JsonArray() };
        var legacy = Chat.ContainsKey("chat") && !Chat.ContainsKey("blocks");
        if (legacy)
        {
            AtomicFile.Write(Path.Combine(ChatRoot, "migration", "python-v1.json"), File.ReadAllText(file));
            LegacyImporter.ImportChat(Chat);
        }
        State = Chat["world"] as JsonObject is { } world ? (JsonObject)world.DeepClone()
            : (legacy ? LegacyImporter.World(Root, preset) : null)
            ?? new JsonObject { ["sim_date"] = Config.GetString("sim_date", DateTime.Today.ToString("yyyy-MM-dd")), ["phase"] = Config.GetString("phase"), ["canon"] = JsonValues.Clone(Config["canon"]) ?? new JsonArray(), ["search_cache"] = new JsonObject(), ["pages"] = new JsonObject() };
        Directory.CreateDirectory(ChatRoot);
        Save();
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
        Config = config;
        AtomicFile.Write(Path.Combine(Root, "presets", PresetId + ".json"), JsonValues.Serialize(config));
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
