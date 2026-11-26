using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    internal sealed record AssetWatchState(string Identity, string Source, string Revision, string Reason, long? Size);

    private static string AssetChangeKey(string preset, PluginAsset asset, PluginActions.Address address) =>
        $"_context/asset-changes/{preset}/{asset.Id}/{address.Id}";

    internal AssetWatchState WatchAgentAsset(string url, string publisher, string signer, string preset)
    {
        lock (packageGate)
        {
            var (owner, asset) = ResolveAssetDeclaration(url, AssetPublisher(publisher, signer, preset), preset);
            if (!asset.Listenable) throw new InvalidOperationException("This Asset does not declare a change event.");
            var address = PluginActions.Parse(asset.ChangeEvent);
            var key = AssetChangeKey(preset, asset, address);
            var path = SettingsPath(owner);
            var update = new JsonObject();
            if (File.Exists(path))
            {
                using var db = MemoryDb.ReadOnly(path);
                update = JsonValues.ParseObject(db.Get(key) ?? "{}");
            }
            return new(owner.StorageKey + "/" + key, asset.ChangeEvent, update.GetString("revision"),
                update.GetString("reason"), update["size"]?.GetValue<long>());
        }
    }

    private JsonObject NotifyAssetChanged(PluginPackage caller, string url, JsonObject environment)
    {
        var (package, id) = ParseAssetUrl(url);
        if (package != caller.PackageName || caller.Assets?.GetValueOrDefault(id) is not { Listenable: true, Type: "dynamic" } asset)
            throw new UnauthorizedAccessException("Only the owner can notify changes to a listenable dynamic Asset.");
        if (environment.GetInt32("_asset_watch_depth") >= 8)
            throw new InvalidOperationException("Asset change notifications are nested too deeply.");
        var subscribers = RouteAssetChange(caller, asset, environment, "invalidate");
        return new() { ["accepted"] = true, ["subscribers"] = subscribers };
    }

    private int RouteAssetChange(PluginPackage owner, PluginAsset asset, JsonObject environment, string reason, long? size = null)
    {
        var preset = environment.GetString("preset");
        var url = AssetUrl(owner, asset.Id);
        var address = PluginActions.Parse(asset.ChangeEvent);
        try
        {
            using var db = SettingsDb(owner);
            db.Write(AssetChangeKey(preset, asset, address), JsonValues.Serialize(new JsonObject
            {
                ["revision"] = WorkspaceIds.New(), ["reason"] = reason, ["size"] = size
            }), internalWrite: true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Fault(owner.Id, "The Asset changed, but its notification for Agent sessions could not be saved.");
        }
        var count = 0;
        foreach (var (target, source, handler) in ActionSubscriptions(address))
        {
            if (!CanReadAsset(owner, asset, target)) continue;
            var payload = new JsonObject
            {
                ["name"] = "asset.changed", ["url"] = url, ["source"] = source, ["sender"] = owner.PackageName,
                ["reason"] = reason, ["assets"] = new JsonObject { ["asset"] = url }
            };
            if (size is { } length) payload["size"] = length;
            var context = new JsonObject { ["preset"] = preset, ["locale"] = environment.GetString("locale"),
                ["_asset_watch_depth"] = environment.GetInt32("_asset_watch_depth") + 1 };
            if (EnqueueRoute(target, handler, payload, context, assetOwner: owner)) count++;
        }
        return count;
    }
}
