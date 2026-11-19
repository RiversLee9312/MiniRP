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
                session.Db.Snapshot(Path.Combine(root, role + ".sqlite"));
            }
        }
        AtomicFile.Write(Path.Combine(root, "world.json"), JsonValues.Serialize(workspace.State));
        return id;
    }

    public static (string Id, string Input, JsonArray Attachments, JsonObject? OpenUiAction) Fork(
        Workspace workspace, string blockId, IEnumerable<string> roles, bool afterReply, bool seedDraft)
    {
        var blocks = workspace.Chat.GetArray("blocks");
        var position = blocks.ToList().FindIndex(block => block.GetString("id") == blockId);
        if (position < 0 || blocks[position].GetString("kind", blocks[position].GetString("role")) != (afterReply ? "reply_end" : "user") ||
            blocks[position].GetString("version") is not { Length: > 0 } version)
        {
            throw new InvalidOperationException("This message has no saved world version.");
        }

        var root = VersionRoot(workspace, version);
        var id = WorkspaceIds.New();
        var parent = Path.Combine(workspace.Root, "conversations", workspace.PresetId);
        var target = Path.Combine(parent, id, "agents");
        Directory.CreateDirectory(target);
        foreach (var role in roles)
        {
            File.Copy(Path.Combine(root, role + ".sqlite"), Path.Combine(target, role + ".sqlite"));
        }

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
        var branch = new JsonObject
        {
            ["id"] = id,
            ["name"] = "",
            ["parent_chat"] = workspace.ChatId,
            ["parent_message"] = blockId,
            ["blocks"] = prefix,
            ["messages"] = messages,
            ["world"] = JsonValues.ParseObject(File.ReadAllText(Path.Combine(root, "world.json")))
        };
        var input = blocks[position].GetString("content");
        var attachments = (JsonArray)blocks[position].GetArray("attachments").DeepClone();
        var action = JsonValues.Clone(blocks[position]?["open_ui_action"]) as JsonObject;
        if (seedDraft)
        {
            branch["draft"] = new JsonObject
            {
                ["text"] = input,
                ["attachments"] = attachments.DeepClone(),
                ["open_ui_action"] = action?.DeepClone()
            };
        }
        AtomicFile.Write(Path.Combine(parent, id + ".json"), JsonValues.Serialize(branch));
        return (id, input, attachments, action);
    }

    private static string VersionRoot(Workspace workspace, string id) => Path.Combine(workspace.Root, "versions", Workspace.Safe(id));
}
