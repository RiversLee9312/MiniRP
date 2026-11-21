using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private JsonObject LoadBlock(JsonObject block) => LoadBlock(block, block.GetString("conversation"));

    private JsonObject LoadBlock(JsonObject block, string conversation)
    {
        var cleaned = LoadedBlock(block);
        if (cleaned["plugin_card"] is JsonObject reference) RefreshCard(cleaned, reference);
        IndexDisplayAssets(cleaned, cleaned.GetString("id"), conversation);
        return cleaned;
    }

    private void RefreshCards(JsonObject change)
    {
        var locations = new List<(JsonArray Blocks, string Conversation)>() { (Workspace.Chat.GetArray("blocks"), "") };
        if (presetConversation is not null) locations.Add((presetConversation.Blocks, ""));
        if (ensemble is { } group)
            foreach (var conversation in group.List())
                locations.Add((group.Blocks(conversation.GetString("id")), conversation.GetString("id")));
        lock (presetTraceGate)
        {
            foreach (var (blocks, conversation) in locations)
            {
                foreach (var block in blocks.OfType<JsonObject>())
                {
                    if (block["plugin_card"] is not JsonObject reference ||
                        reference.GetString("package") != change.GetString("package") ||
                        reference.GetString("signer") != change.GetString("signer"))
                        continue;
                    RefreshCard(block, reference);
                    IndexDisplayAssets(block, block.GetString("id"), conversation);
                    var payload = new JsonObject { ["kind"] = "open_ui_card_update", ["chat"] = Workspace.ChatId,
                        ["block"] = block.GetString("id"), ["open_ui"] = JsonValues.Clone(block["open_ui"]),
                        ["open_ui_title"] = JsonValues.Clone(block["open_ui_title"]),
                        ["open_ui_state"] = JsonValues.Clone(block["open_ui_state"]),
                        ["open_ui_revision"] = JsonValues.Clone(block["open_ui_revision"]) };
                    if (conversation.Length > 0) payload["conversation"] = conversation;
                    Emit(payload);
                }
            }
        }
    }

    private void RefreshCard(JsonObject block, JsonObject reference)
    {
        try
        {
            foreach (var (key, value) in plugins.ReadCard(reference)) block[key] = JsonValues.Clone(value);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            block.Remove("open_ui");
            block.Remove("open_ui_state");
        }
    }
}
