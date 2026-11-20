using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PresetConversation(Workspace workspace)
{
    private readonly string root = SessionFiles.PresetDirectory(workspace.Root, workspace.PresetId);

    public string Database => SessionFiles.PresetDatabase(root);
    private string Transcript => Path.Combine(root, "blocks.json");
    public JsonArray Blocks { get; private set; } = new();
    public JsonObject? LegacySession { get; private set; }

    public void Load(Workspace workspace)
    {
        if (File.Exists(Transcript))
        {
            Blocks = JsonNode.Parse(File.ReadAllText(Transcript)) as JsonArray ?? new();
            return;
        }
        if (!File.Exists(Database) && FindLegacy(workspace) is { } seed)
        {
            Blocks = new JsonArray(seed.Chat.GetArray("blocks").Where(IsPreset).Select(JsonValues.Clone).ToArray());
            LegacySession = seed.Chat["preset"] as JsonObject;
            if (File.Exists(seed.Database))
            {
                Directory.CreateDirectory(root);
                using var legacy = MemoryDb.ReadOnly(seed.Database);
                legacy.Snapshot(Database);
            }
        }
        Save();
    }

    private static (JsonObject Chat, string Database)? FindLegacy(Workspace workspace)
    {
        var parent = Path.Combine(workspace.Root, "conversations", workspace.PresetId);
        if (!Directory.Exists(parent))
        {
            return null;
        }

        // Older versions had a separate preset session in every chat. Seed the shared one from
        // the most recent chat with preset activity, leaving all source records intact.
        foreach (var file in new DirectoryInfo(parent).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc))
        {
            try
            {
                var chat = JsonValues.ParseObject(File.ReadAllText(file.FullName));
                var database = SessionFiles.Conversation(Path.Combine(parent, Path.GetFileNameWithoutExtension(file.Name)), "preset");
                if (chat.ContainsKey("chat") && !chat.ContainsKey("blocks"))
                {
                    LegacyImporter.ImportChat(chat);
                }
                if (chat.GetObject("preset").GetArray("history").Count > 0 || chat.GetArray("blocks").Any(IsPreset))
                {
                    return (chat, database);
                }
                if (File.Exists(database))
                {
                    using var probe = MemoryDb.ReadOnly(database);
                    if (probe.HasEntriesTable() &&
                        probe.Call("db_list", new() { ["notes_only"] = true, ["limit"] = 1 }).GetArray("entries").Count != 0)
                    {
                        return (chat, database);
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed older conversations remain on disk and do not prevent opening a new one.
            }
        }
        return null;
    }

    public void Save() => AtomicFile.Write(Transcript, JsonValues.Serialize(Blocks));

    public static bool IsPreset(JsonNode? node) => node.GetString("agent") is "preset" or "PresetAgent";
}
