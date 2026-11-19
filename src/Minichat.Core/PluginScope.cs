using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace Minichat.Core;

internal sealed record PluginScope(string Mode, FrozenSet<string> Presets)
{
    public static PluginScope Global { get; } = new("global", FrozenSet<string>.Empty);

    public bool Allows(string preset) => Mode switch
    {
        "include" => Presets.Contains(preset),
        "exclude" => !Presets.Contains(preset),
        _ => true
    };

    public JsonObject Describe() => new()
    {
        ["mode"] = Mode,
        ["presets"] = new JsonArray(Presets.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
    };

    public static PluginScope Parse(JsonObject value) => value.GetString("mode") switch
    {
        "global" => Global,
        "include" or "exclude" => new(value.GetString("mode"), value.GetArray("presets")
            .Select(item => Workspace.Safe(JsonValues.GetText(item))).ToFrozenSet(StringComparer.Ordinal)),
        _ => throw new ArgumentException("Choose a plugin scope.")
    };
}

internal sealed partial class PluginRuntime
{
    private readonly ConcurrentDictionary<string, PluginScope> scopes = new(StringComparer.Ordinal);

    public PluginScope Scope(PluginPackage package) => package.BuiltIn ? PluginScope.Global :
        scopes.GetOrAdd(package.StorageKey, _ =>
        {
            if (!File.Exists(SettingsPath(package))) return PluginScope.Global;
            using var db = SettingsDb(package);
            return db.Get("host/scope") is { } value ? PluginScope.Parse(JsonValues.ParseObject(value)) : PluginScope.Global;
        });

    public void SetScope(string id, PluginScope scope)
    {
        var package = Installed(id);
        if (package.BuiltIn) throw new InvalidOperationException("Built-in plugins always run globally.");
        using var db = SettingsDb(package);
        db.Write("host/scope", JsonValues.Serialize(scope.Describe()), internalWrite: true);
        scopes[package.StorageKey] = scope;
    }

    private PluginPackage Installed(string id) => Volatile.Read(ref packages).FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("Unknown plugin.");

    public void RequireScope(string id, string preset) => RequireScope(Installed(id), preset);

    public void RequireScope(PluginPackage package, string preset)
    {
        if (!Scope(package).Allows(preset)) throw new UnauthorizedAccessException("The plugin is unavailable in this preset.");
    }
}
