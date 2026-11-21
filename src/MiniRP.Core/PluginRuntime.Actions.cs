using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    private const string AppActions = "minirp.app";

    private JsonObject DispatchAction(PluginPackage caller, string method, JsonObject args, JsonObject environment)
    {
        if (method is not ("actions.invoke" or "actions.publish")) throw new ArgumentException("Unknown Actions method.");
        RequireScope(caller, environment.GetString("preset"));
        if (!caller.UsesActions) throw new UnauthorizedAccessException("Actions require a signed plugin manifest.");
        var url = args.GetString("url");
        var address = PluginActions.Parse(url);
        if (address.Package == AppActions && method == "actions.invoke")
            return OpenAppAction(address, caller.BuiltIn);

        var next = NextAction(environment);
        var assets = args.GetObject("assets");
        if (assets.Count > 16) throw new ArgumentException("An Action supports up to 16 asset references.");
        foreach (var item in assets)
        {
            PluginPackage.Identifier(item.Key);
            OpenAsset(JsonValues.GetText(item.Value), caller, new(), next);
        }
        var payload = new JsonObject { ["name"] = method, ["url"] = url, ["sender"] = caller.PackageName, ["assets"] = assets.DeepClone() };
        var installed = Volatile.Read(ref packages);
        if (method == "actions.invoke")
        {
            if (address.Kind != "hooks") throw new ArgumentException("Invoke requires a Hook URL.");
            var target = installed.FirstOrDefault(item => item.PackageName == address.Package)
                ?? throw new ArgumentException("Target plugin is unavailable.");
            return InvokeAction(target, target.Bind(url), payload, next);
        }
        if (address.Kind != "events" || address.Package != caller.PackageName ||
            !caller.Manifest.GetObject("actions").GetArray("events").Any(item => JsonValues.GetText(item) == address.Id))
            throw new UnauthorizedAccessException("Only a declared event source can publish this event.");
        var count = 0;
        foreach (var target in installed)
        {
            var (source, listener) = target.Manifest.GetObject("actions").GetObject("subscriptions")
                .FirstOrDefault(subscription => PluginActions.Parse(subscription.Key) == address);
            if (listener is null) continue;
            var delivered = (JsonObject)payload.DeepClone();
            delivered["url"] = source;
            if (EnqueueRoute(target, target.Bind(JsonValues.GetText(listener)), delivered, (JsonObject)next.DeepClone()))
                count++;
        }
        return new() { ["accepted"] = true, ["subscribers"] = count };
    }

    private static JsonObject NextAction(JsonObject environment)
    {
        var depth = environment.GetInt32("_action_depth");
        if (depth >= 8) throw new InvalidOperationException("Too many nested Actions.");
        var next = (JsonObject)environment.DeepClone();
        next["_action_depth"] = depth + 1;
        return next;
    }

    private JsonObject InvokeAction(PluginPackage target, string handler, JsonObject payload, JsonObject environment)
    {
        RequireScope(target, environment.GetString("preset"));
        if (!EnqueueRoute(target, handler, payload, environment)) throw new InvalidOperationException("The plugin event queue is full.");
        return new() { ["accepted"] = true };
    }

    private JsonObject OpenAppAction(PluginActions.Address address, bool allowed)
    {
        if (!allowed || address.Kind != "hooks" || address.Id is not ("language" or "providers" or "models" or "plugins"))
            throw new UnauthorizedAccessException("This app Action is unavailable.");
        emit(new() { ["kind"] = "plugin_action", ["url"] = $"{PluginUrls.Scheme}://actions/{AppActions}/hooks/{address.Id}" });
        return new() { ["accepted"] = true };
    }
}
