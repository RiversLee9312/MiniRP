using System.Text.Json.Nodes;

namespace Minichat.Core;

internal static class PluginActions
{
    internal sealed record Address(string Package, string Kind, string Id);

    internal static Address Parse(string value)
    {
        const string prefix = "minichat://actions/";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value[prefix.Length..].Split('/') is not [var package, var kind, var id] ||
            kind is not ("hooks" or "events"))
            throw new ArgumentException("Invalid plugin Actions URL.");
        if (!package.StartsWith("minichat.", StringComparison.Ordinal)) PluginPackage.PackageIdentifier(package);
        return new(package, kind, PluginPackage.Identifier(id));
    }

    internal static string Bind(JsonObject manifest, string url)
    {
        if (url.Length == 0) return "";
        var target = Parse(url);
        var handler = manifest.GetObject("actions").GetObject("handlers").GetString(target.Id);
        if (target.Package != manifest.GetString("packageName") || target.Kind != "hooks" || handler.Length == 0)
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
        foreach (var (source, handler) in actions.GetObject("subscriptions"))
        {
            if (Parse(source).Kind != "events") throw new InvalidDataException("Subscriptions must reference event sources.");
            BindRequired(manifest, JsonValues.GetText(handler));
        }
    }
}
