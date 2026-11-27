using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class ConversationVersions
{
    public static string Capture(Workspace workspace, IReadOnlyDictionary<string, AgentSession> sessions)
    {
        var id = JsonValues.NewId();
        var root = VersionRoot(workspace, id);
        Directory.CreateDirectory(root);
        foreach (var (role, session) in sessions)
        {
            if (role != "preset")
            {
                ConversationStorage.Capture(session.Db, Path.Combine(root, role + ".sqlite"));
            }
        }
        lock (workspace.RoomGate)
        {
            ConversationStorage.WriteWorld(Path.Combine(root, "world.json"), JsonValues.Serialize(workspace.State));
            if (workspace.Chat["ensemble"] is JsonObject ensemble)
            {
                ConversationStorage.WriteWorld(Path.Combine(root, "ensemble.json"), JsonValues.Serialize(ensemble));
                foreach (var member in ensemble.GetArray("agents"))
                foreach (var role in ActorRoles)
                {
                    var participant = PresetDesign.Id(member.GetString("id"));
                    var file = SessionFiles.Ensemble(workspace.ChatRoot, participant, role);
                    if (!File.Exists(file)) continue;
                    using var database = MemoryDb.OpenScratch(file);
                    if (database.HasEntriesTable())
                        ConversationStorage.Capture(database, ActorFile(root, participant, role));
                }
            }
        }
        ConversationStorage.MarkCompact(root);
        return id;
    }

    public static (string Id, string Input, JsonArray Attachments, JsonObject? OpenUiAction) Fork(
        Workspace workspace, string blockId, IEnumerable<string> roles, bool afterReply, bool seedDraft, string agent = "chat")
    {
        var blocks = workspace.Chat.GetArray("blocks");
        var (position, version) = Find(blocks, blockId, afterReply, agent);

        var root = VersionRoot(workspace, version);
        var id = WorkspaceIds.New();
        var parent = Path.Combine(workspace.Root, "conversations", workspace.PresetId);
        var target = SessionFiles.ConversationDirectory(Path.Combine(parent, id));
        Directory.CreateDirectory(target);
        foreach (var role in roles) Restore(Path.Combine(root, role + ".sqlite"), Path.Combine(target, role + ".sqlite"));

        var prefix = new JsonArray();
        var sourceMessages = workspace.Chat.GetArray("messages");
        foreach (var block in blocks.Take(afterReply ? position + 1 : position).Where(block => !PresetConversation.IsPreset(block)))
        {
            var copy = (JsonObject)block!.DeepClone();
            if (copy.ContainsKey("message_index"))
            {
                copy["message_index"] = sourceMessages.Take(copy.GetInt32("message_index")).Count(message => !PresetConversation.IsPreset(message));
            }
            prefix.AddNode(copy);
        }
        var messages = new JsonArray();
        foreach (var message in sourceMessages.Take(blocks[position].GetInt32("message_index")).Where(message => !PresetConversation.IsPreset(message)))
        {
            messages.AddNode(JsonValues.Clone(message));
        }
        var world = Read(Path.Combine(root, "world.json"));
        var branch = new JsonObject
        {
            ["id"] = id,
            ["name"] = "",
            ["parent_chat"] = workspace.ChatId,
            ["parent_message"] = blockId,
            ["mode"] = workspace.Mode,
            ["blocks"] = prefix,
            ["messages"] = messages,
            ["world"] = world
        };
        if (workspace.Mode == "ensemble")
        {
            var ensemble = Read(Path.Combine(root, "ensemble.json"));
            branch["ensemble"] = ensemble;
            foreach (var member in ensemble.GetArray("agents"))
            foreach (var role in ActorRoles)
            {
                var participant = PresetDesign.Id(member.GetString("id"));
                var snapshot = ActorFile(root, participant, role);
                if (File.Exists(snapshot) || File.Exists(snapshot + ".gz"))
                    Restore(snapshot, SessionFiles.Ensemble(Path.Combine(parent, id), participant, role));
            }
        }
        var input = blocks[position].GetString("content");
        var attachments = (JsonArray)blocks[position].GetArray("attachments").DeepClone();
        var action = JsonValues.Clone(blocks[position]?["open_ui_action"]) as JsonObject;
        if (seedDraft)
        {
            branch["draft"] = Draft(blocks[position], agent);
        }
        AtomicFile.Write(Path.Combine(parent, id + ".json"), JsonValues.Serialize(branch));
        return (id, input, attachments, action);
    }

    public static (int Position, string Version) Find(JsonArray blocks, string blockId, bool reply, string agent)
    {
        var position = blocks.ToList().FindIndex(block => block.GetString("id") == blockId);
        if (position >= 0 && blocks[position] is JsonObject marker && marker.GetString("agent", "chat") == agent &&
            marker.GetString("kind", marker.GetString("role")) == (reply ? "reply_end" : "user") &&
            marker.GetString("version") is { Length: > 0 } version)
            return (position, version);
        throw new InvalidOperationException("This message has no saved version for this agent.");
    }

    public static JsonObject Draft(JsonNode? block, string agent) => new()
    {
        ["agent"] = agent,
        ["text"] = block.GetString("content"),
        ["attachments"] = block.GetArray("attachments").DeepClone(),
        ["open_ui_action"] = JsonValues.Clone(block?["open_ui_action"])
    };

    public static string VersionRoot(Workspace workspace, string id) => Path.Combine(workspace.Root, "versions", Workspace.Safe(id));

    public static JsonObject Read(string path)
    {
        using var stream = ConversationStorage.Open(path);
        return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException("Invalid conversation snapshot.");
    }

    public static void Restore(string source, string target)
    {
        using var snapshot = ConversationStorage.Open(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var output = File.Create(target);
        snapshot.CopyTo(output);
    }

    private static readonly string[] ActorRoles = ["chat", "search", "direct-chat", "direct-search"];
    private static string ActorFile(string root, string member, string role) => Path.Combine(root, $"actor-{member}-{role}.sqlite");
}
