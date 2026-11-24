using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private JsonObject LoadBlock(JsonObject block) => LoadBlock(block, block.GetString("conversation"));

    private JsonObject LoadBlock(JsonObject block, string conversation, string? fallbackId = null, Workspace? scope = null, string? locale = null)
    {
        var cleaned = LoadedBlock(block, fallbackId);
        if (cleaned["plugin_card"] is JsonObject reference) RefreshCard(cleaned, reference, locale);
        ProjectToolTrace(block, cleaned, scope?.PresetId ?? Workspace.PresetId, locale);
        IndexDisplayAssets(cleaned, cleaned.GetString("id"), conversation, scope: scope);
        if (scope is not null) TrackWebDisplay(block, cleaned.GetString("id"), conversation, scope);
        return cleaned;
    }

    private void RefreshCards(JsonObject? change = null)
    {
        var workspace = Workspace;
        lock (workspace.RoomGate)
        lock (presetTraceGate)
        {
            if (change is null) RefreshToolTraces(workspace);
            if (WebHistory)
            {
                foreach (var card in TrackedWebCards(workspace))
                    EmitCardUpdate(card.Block, card.Id, card.Conversation, change);
                return;
            }
            var locations = new List<(JsonArray Blocks, string Conversation)>() { (workspace.Chat.GetArray("blocks"), "") };
            if (presetConversation is not null) locations.Add((presetConversation.Blocks, ""));
            if (ensemble is { } group)
                foreach (var conversation in group.List())
                    locations.Add((group.Blocks(conversation.GetString("id")), conversation.GetString("id")));
            foreach (var (blocks, conversation) in locations)
                foreach (var block in blocks.OfType<JsonObject>())
                    EmitCardUpdate(block, block.GetString("id"), conversation, change);
        }
    }

    private void EmitCardUpdate(JsonObject block, string id, string conversation, JsonObject? change)
    {
        if (block["plugin_card"] is not JsonObject reference || change is not null &&
            (reference.GetString("package") != change.GetString("package") ||
             reference.GetString("signer") != change.GetString("signer")))
            return;
        RefreshCard(block, reference);
        IndexDisplayAssets(block, id, conversation);
        var payload = new JsonObject { ["kind"] = "open_ui_card_update", ["chat"] = Workspace.ChatId,
            ["agent"] = block.GetString("agent"), ["block"] = id, ["open_ui"] = JsonValues.Clone(block["open_ui"]),
            ["open_ui_title"] = JsonValues.Clone(block["open_ui_title"]),
            ["open_ui_state"] = JsonValues.Clone(block["open_ui_state"]),
            ["open_ui_revision"] = JsonValues.Clone(block["open_ui_revision"]) };
        if (conversation.Length > 0) payload["conversation"] = conversation;
        Emit(payload);
    }

    private void RefreshCard(JsonObject block, JsonObject reference, string? locale = null)
    {
        try
        {
            foreach (var (key, value) in plugins.ReadCard(reference, AgentLanguage.Normalize(locale) ?? Volatile.Read(ref pluginLocale)))
                block[key] = JsonValues.Clone(value);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            block.Remove("open_ui");
            block.Remove("open_ui_state");
        }
    }
}
