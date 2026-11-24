using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private readonly ConcurrentDictionary<(string Preset, string Room, string Chat, string Block), JsonObject> displayAssets = new();

    private void IndexDisplayAssets(JsonObject item, string block, string conversation, string? workerPreset = null, Workspace? scope = null)
    {
        if (block.Length == 0 || (item["asset_links"] is not JsonObject && item["plugin_card"] is not JsonObject)) return;
        var workspace = scope ?? Workspace;
        var presetPanel = workerPreset is not null || PresetConversation.IsPreset(item);
        var links = (JsonObject)item.GetObject("asset_links").DeepClone();
        if (item["plugin_card"] is JsonObject card)
            foreach (var url in AgentAssets.PublishedUrls(item.GetString("open_ui") + "\n" + item["open_ui_state"]?.ToJsonString())
                         .Distinct(StringComparer.Ordinal).Take(64))
                links[url] ??= new JsonObject { ["plugin"] = card.DeepClone() };
        var key = (workerPreset ?? workspace.PresetId, presetPanel ? "" : workspace.ChatId, presetPanel ? "" : conversation, block);
        if (links.Count > 0) displayAssets[key] = links;
        else displayAssets.TryRemove(key, out _);
    }

    private JsonObject OpenDisplayAsset(JsonObject command)
    {
        var workspace = Workspace;
        var preset = command.GetString("preset");
        var room = command.GetString("room");
        var chat = command.GetString("conversation");
        var url = command.GetString("url");
        if (preset != workspace.PresetId || (room.Length > 0 && room != workspace.ChatId) ||
            !displayAssets.TryGetValue((preset, room, chat, command.GetString("block")), out var links) ||
            links[url] is not JsonObject reference)
            throw new UnauthorizedAccessException("This Asset is unavailable in the displayed reply.");

        var content = reference["attachment"] is JsonObject file ? plugins.OpenAttachmentAsset(file) :
            reference["research"] is JsonObject research ? ResearchAssets.OpenPublished(workspace.Root, preset, research) :
            reference["plugin"] is JsonObject plugin ? plugins.OpenAgentAsset(url, plugin.GetString("package"), plugin.GetString("signer"),
                DisplayAssetEnvironment(plugin, preset, room, chat)) :
            throw new InvalidDataException("Invalid published Asset reference.");
        return plugins.OpenDisplayAsset(content, url);
    }

    private static JsonObject DisplayAssetEnvironment(JsonObject reference, string preset, string room, string chat)
    {
        var environment = (JsonObject)reference.GetObject("environment").DeepClone();
        environment["preset"] = preset;
        environment["room"] = room;
        environment["chat"] = chat.Length == 0 ? room : chat;
        return environment;
    }
}
