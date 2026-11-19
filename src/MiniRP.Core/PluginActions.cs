using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class PluginActions
{
    internal sealed record Address(string Package, string Kind, string Id);

    internal static Address Parse(string value)
    {
        if (PluginUrls.Route(value, "actions").Split('/') is not [var package, var kind, var id] ||
            kind is not ("hooks" or "events"))
            throw new ArgumentException("Invalid plugin Actions URL.");
        PluginPackage.PackageIdentifier(package, allowReserved: true);
        return new(PluginUrls.CanonicalPackage(package), kind, PluginPackage.Identifier(id));
    }

    internal static string Bind(JsonObject manifest, string url)
    {
        if (url.Length == 0) return "";
        var target = Parse(url);
        var handler = manifest.GetObject("actions").GetObject("handlers").GetString(target.Id);
        if (target.Package != PluginUrls.CanonicalPackage(manifest.GetString("packageName")) || target.Kind != "hooks" || handler.Length == 0)
            throw new InvalidDataException("A hook binding must reference a handler declared by the same plugin.");
        return handler;
    }

    internal static string BindRequired(JsonObject manifest, string url)
    {
        if (url.Length == 0) throw new InvalidDataException("A Hook URL is required.");
        return Bind(manifest, url);
    }

    internal static void Validate(JsonObject manifest)
    {
        var actions = manifest.GetObject("actions");
        foreach (var (id, handler) in actions.GetObject("handlers"))
        {
            PluginPackage.Identifier(id);
            PluginPackage.Handler(JsonValues.GetText(handler));
        }
        var events = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in actions.GetArray("events"))
            if (!events.Add(PluginPackage.Identifier(JsonValues.GetText(item))))
                throw new InvalidDataException("Duplicate event source.");
        var subscriptions = new HashSet<Address>();
        foreach (var (source, handler) in actions.GetObject("subscriptions"))
        {
            var address = Parse(source);
            if (address.Kind != "events") throw new InvalidDataException("Subscriptions must reference event sources.");
            if (!subscriptions.Add(address)) throw new InvalidDataException("Duplicate subscription to the same event source.");
            BindRequired(manifest, JsonValues.GetText(handler));
        }
    }
}
