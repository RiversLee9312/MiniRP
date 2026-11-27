using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PresetConversation(Workspace workspace)
{
    private readonly string root = SessionFiles.PresetDirectory(workspace.Root, workspace.PresetId);
    internal string PresetId { get; } = workspace.PresetId;

    public string Database => SessionFiles.PresetDatabase(root);
    private string Transcript => Path.Combine(root, "blocks.json");
    private string DraftPath => Path.Combine(root, "draft.json");
    public JsonArray Blocks { get; private set; } = new();
    public Dictionary<string, JsonObject> WorkerTrace { get; } = new(StringComparer.Ordinal);
    public JsonObject? LegacySession { get; private set; }
    public JsonObject? Draft { get; private set; }

    public void Load(Workspace workspace)
    {
        if (File.Exists(Transcript))
        {
            Blocks = JsonNode.Parse(File.ReadAllText(Transcript)) as JsonArray ?? new();
            if (File.Exists(DraftPath)) Draft = JsonValues.ParseObject(File.ReadAllText(DraftPath));
            foreach (var block in Blocks.OfType<JsonObject>().Where(block => block.GetString("plugin_status") == "running"))
                block["plugin_status"] = "interrupted";
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

    public void ConsumeDraft()
    {
        if (Draft is null) return;
        File.Delete(DraftPath);
        Draft = null;
    }

    public static string Capture(Workspace workspace, AgentSession session)
    {
        var id = JsonValues.NewId();
        var root = ConversationVersions.VersionRoot(workspace, id);
        Directory.CreateDirectory(root);
        ConversationStorage.Capture(session.Db, Path.Combine(root, "preset.sqlite"));
        ConversationStorage.WriteWorld(Path.Combine(root, "preset.json"), JsonValues.Serialize(workspace.Config));
        ConversationStorage.MarkCompact(root);
        return id;
    }

    public (string Id, string Name) Fork(Workspace workspace, string blockId, bool reply, object traceGate, CancellationToken ct)
    {
        string version;
        JsonArray prefix;
        JsonObject? draft;
        lock (traceGate)
        {
            var marker = ConversationVersions.Find(Blocks, blockId, reply, "preset");
            version = marker.Version;
            prefix = new JsonArray(Blocks.Take(reply ? marker.Position + 1 : marker.Position).Select(JsonValues.Clone).ToArray());
            draft = reply ? null : ConversationVersions.Draft(Blocks[marker.Position], "preset");
        }
        var source = ConversationVersions.VersionRoot(workspace, version);
        var config = ConversationVersions.Read(Path.Combine(source, "preset.json"));
        var id = WorkspaceIds.New();
        var name = config.GetString("name", workspace.PresetId);
        config["id"] = id;
        config["parent_preset"] = workspace.PresetId;
        config["parent_message"] = blockId;
        config.Remove(PluginRuntime.LegacyOwners);
        // Installed background packages are signed for the original preset.
        if (config["appearance"] is JsonObject appearance) appearance.Remove("background_url");

        var target = SessionFiles.PresetDirectory(workspace.Root, id);
        var database = SessionFiles.PresetDatabase(target);
        ConversationVersions.Restore(Path.Combine(source, "preset.sqlite"), database);
        WorkspaceTransfer.CopyPresetSessionResources(workspace, id, prefix, database, ct);
        AtomicFile.Write(Path.Combine(target, "blocks.json"), JsonValues.Serialize(prefix));
        if (draft is not null)
            AtomicFile.Write(Path.Combine(target, "draft.json"), JsonValues.Serialize(draft));
        ct.ThrowIfCancellationRequested();
        AtomicFile.Write(Path.Combine(workspace.Root, "presets", id + ".json"), JsonValues.Serialize(config));
        return (id, name);
    }

    public static bool IsPreset(JsonNode? node) => node.GetString("agent") is "preset" or "PresetAgent" or "PluginAgent";
}
