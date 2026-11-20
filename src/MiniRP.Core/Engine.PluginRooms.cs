using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private static string PluginRoom(JsonObject args, JsonObject context, string preset, bool grouped)
    {
        if (!grouped && args.GetString("room") is { Length: > 0 } room && args.GetString("chat", room) != room)
            throw new ArgumentException("In a regular conversation the Chat and Room IDs are the same.");
        var active = preset == context.GetString("preset") && grouped == context.GetBoolean("_ensemble");
        return args.GetString("room", grouped ? (active ? context.GetString("room") : "") :
            args.GetString("chat", active ? context.GetString("room", context.GetString("chat")) : ""));
    }

    private static string PluginChat(JsonObject args, JsonObject context, string preset, string room) =>
        args.GetString("chat", room == context.GetString("room") && preset == context.GetString("preset") ?
            context.GetString("chat") : "");

    private async Task<JsonNode> QueryPluginConversations(string op, PluginPackage package,
        JsonObject context, JsonObject args, CancellationToken ct)
    {
        var preset = PluginPreset(package, context, args);
        var grouped = IsEnsemblePreset(preset);
        var room = op == "listChats" && !grouped ? args.GetString("room") : PluginRoom(args, context, preset, grouped);
        var offset = Math.Max(0, args.GetInt32("offset"));
        var limit = Math.Clamp(args.GetInt32("limit", 30), 1, 100);
        if (op == "listRooms" || op == "listChats" && room.Length == 0 && !grouped)
            return (await catalog).Page(preset, offset, limit, plugins.Scope(package));

        var roomId = WorkspaceIds.Require(room);
        var document = await ReadRoomDocument(preset, roomId, ct);
        var group = document["ensemble"] as JsonObject;
        var chats = group?["chats"] as JsonArray ?? group?["rooms"] as JsonArray;
        JsonObject ChatInfo(JsonNode? chat, string id) => new()
        {
            ["id"] = id, ["name"] = chat.GetString("name"),
            ["members"] = new JsonArray(chat.GetObject("members").Select(member => (JsonNode?)JsonValue.Create(member.Key)).ToArray())
        };
        if (op is "readRoom" or "listChats")
        {
            var items = chats is null ? new JsonArray(ChatInfo(document, roomId)) :
                new JsonArray(chats.Skip(offset).Take(limit).Select(chat => (JsonNode?)ChatInfo(chat, chat.GetString("id"))).ToArray());
            return op == "listChats" ? items : new JsonObject { ["id"] = roomId, ["preset"] = preset,
                ["name"] = document.GetString("name"), ["chats"] = items };
        }
        var chatId = chats is null ? args.GetString("chat", roomId) : PluginChat(args, context, preset, roomId);
        var target = chats is null
            ? chatId == roomId ? document : throw new ArgumentException("In a regular conversation the Chat and Room IDs are the same.")
            : chats.FirstOrDefault(chat => chat.GetString("id") == WorkspaceIds.Require(chatId)) ??
                throw new ArgumentException("Choose a chat in this room.");
        return new JsonObject { ["id"] = chatId, ["room"] = roomId, ["preset"] = preset,
            ["name"] = target.GetString("name"), ["messages"] = PageMessages(target.GetArray("messages"), args) };
    }

    private bool IsEnsemblePreset(string preset)
    {
        var file = Path.Combine(Workspace.Root, "presets", WorkspaceIds.Require(preset) + ".json");
        try { return JsonValues.ParseObject(File.ReadAllText(file)).GetString("chat_mode") == "ensemble"; }
        catch (FileNotFoundException) { return false; }
    }

    private async Task<JsonObject> ReadRoomDocument(string preset, string room, CancellationToken ct)
    {
        var path = Path.Combine(Workspace.Root, "conversations", WorkspaceIds.Require(preset), WorkspaceIds.Require(room) + ".json");
        var document = JsonValues.ParseObject(await File.ReadAllTextAsync(path, ct));
        if (document["messages"] is not JsonArray) LegacyImporter.ImportChat(document);
        return document;
    }

    private void RequirePluginMembership(JsonObject context, string room, string conversation, JsonObject args)
    {
        if (!context.GetBoolean("_ensemble") || context.GetString("participant") is not { Length: > 0 } member) return;
        var recipient = args.GetString("participant");
        if (room != context.GetString("room") || room != Workspace.ChatId || ensemble is not { } hub ||
            (recipient.Length > 0 ? recipient != member : !hub.IsMember(conversation, member)))
            throw new UnauthorizedAccessException("A character can only send to its own chats.");
    }
}
