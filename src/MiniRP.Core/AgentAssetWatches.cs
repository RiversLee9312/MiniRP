using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class AgentAssetWatches(MemoryDb db, AgentAssets assets)
{
    private const string Index = "_context/asset-watches";
    private readonly JsonObject subscriptions = JsonValues.ParseObject(db.Get(Index) ?? "{}");
    private readonly Dictionary<string, string> delivered = new(StringComparer.Ordinal);

    internal static bool IsTool(string name) => name is "watch_asset" or "unwatch_asset";

    internal static IEnumerable<JsonObject> Definitions()
    {
        yield return Tools.Define("watch_asset", "Watch an authorized Asset whose metadata declares it listenable. " +
            "Changes appear before the session's next model request. Use view_url_metadata to check the Asset first.",
            new() { ["url"] = Tools.String() }, "url");
        yield return Tools.Define("unwatch_asset", "Stop receiving change notices for an Asset in this session.",
            new() { ["url"] = Tools.String() }, "url");
    }

    internal JsonObject Set(string name, JsonObject args)
    {
        var url = args.GetString("url");
        var watch = name == "watch_asset";
        var state = watch ? assets.Watch(url) : null;
        var (package, asset) = PluginRuntime.ParseAssetUrl(url);
        var key = $"{PluginUrls.Scheme}://assets/{package}/{asset}";
        var result = new JsonObject { ["url"] = url, ["watching"] = watch };
        if (state is not null)
        {
            var previous = subscriptions[key];
            if (previous is null && subscriptions.Count >= 32)
                throw new InvalidOperationException("This session can watch up to 32 Assets.");
            var sameSource = previous.GetString("identity") == state.Identity;
            if (!sameSource) delivered.Remove(key);
            subscriptions[key] = new JsonObject
            {
                ["url"] = url, ["identity"] = state.Identity,
                ["revision"] = sameSource ? previous.GetString("revision") : state.Revision
            };
            result["source"] = state.Source;
        }
        else
        {
            if (!subscriptions.Remove(key)) return result;
            delivered.Remove(key);
        }
        db.WriteInternal([(Index, subscriptions.Count == 0 ? null : JsonValues.Serialize(subscriptions))]);
        return result;
    }

    internal void AppendUpdates(JsonArray history, JsonObject? before)
    {
        foreach (var (key, subscription) in subscriptions)
        {
            var url = subscription.GetString("url");
            PluginRuntime.AssetWatchState state;
            try { state = assets.Watch(url); }
            catch (Exception error) when (error is UnauthorizedAccessException or FileNotFoundException or InvalidOperationException) { continue; }
            if (state.Identity != subscription.GetString("identity") || state.Revision.Length == 0 ||
                state.Revision == delivered.GetValueOrDefault(key, subscription.GetString("revision"))) continue;
            var change = state.Size is { } size ? $"{state.Reason}, {size} bytes" : state.Reason;
            var message = new JsonObject
            {
                ["role"] = "system", ["_harness_hidden"] = true,
                ["content"] = $"Watched Asset {url} changed ({change}). Use its Asset URL to inspect the current content."
            };
            if (before is null) history.AddNode(message);
            else history.Insert(history.IndexOf(before), message);
            delivered[key] = state.Revision;
        }
    }

    internal (string, string?)[] Acknowledgements()
    {
        if (delivered.Count == 0) return [];
        var acknowledged = (JsonObject)subscriptions.DeepClone();
        foreach (var (key, revision) in delivered)
            acknowledged.GetObject(key)["revision"] = revision;
        return [(Index, JsonValues.Serialize(acknowledged))];
    }
}
