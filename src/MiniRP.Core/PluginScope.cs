using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed record PluginScope(string Mode, FrozenSet<string> Presets)
{
    public static PluginScope Global { get; } = new("global", FrozenSet<string>.Empty);
    internal static PluginScope Include(string preset) =>
        new("include", new[] { WorkspaceIds.Require(preset) }.ToFrozenSet(StringComparer.Ordinal));

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
            .Select(item => WorkspaceIds.Require(JsonValues.GetText(item))).ToFrozenSet(StringComparer.Ordinal)),
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
        lock (packageGate) SaveScope(Installed(id), scope);
    }

    public void SetEnabled(string id, bool enabled, JsonObject environment)
    {
        lock (packageGate)
        {
            var package = Installed(id);
            if (package.BuiltIn) throw new InvalidOperationException("Built-in plugins are always enabled.");
            if (package.Enabled == enabled) return;
            using (var db = SettingsDb(package))
                db.Write("host/enabled", enabled ? "true" : "false", internalWrite: true);
            var updated = package with { Enabled = enabled, Generation = JsonValues.NewId() };
            SetPackages(packages.Select(item => item.Id == id ? updated : item).ToArray());
            if (enabled) PublishEnabled(updated, environment);
        }
    }

    private bool ReadEnabled(PluginPackage package)
    {
        if (!File.Exists(SettingsPath(package))) return true;
        using var db = SettingsDb(package);
        return db.Get("host/enabled") != "false";
    }

    private bool Available(PluginPackage package, string preset) => package.Enabled && Scope(package).Allows(preset);

    private void SaveScope(PluginPackage package, PluginScope scope)
    {
        if (package.BuiltIn) throw new InvalidOperationException("Built-in plugins always run globally.");
        using var db = SettingsDb(package);
        db.Write("host/scope", JsonValues.Serialize(scope.Describe()), internalWrite: true);
        scopes[package.StorageKey] = scope;
    }

    private PluginPackage Installed(string id) => Volatile.Read(ref packages).FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("Unknown plugin.");

    public void RequireScope(string id, string preset) => RequireScope(Installed(id), preset);

    private bool IsCurrent(PluginPackage package) =>
        Volatile.Read(ref packages).Any(item => item.Generation == package.Generation);

    private void RequireCurrent(PluginPackage package)
    {
        if (!IsCurrent(package)) throw new InvalidOperationException("The plugin installation is no longer active.");
    }

    public void RequireGeneration(string id, string generation, string preset)
    {
        var package = Installed(id);
        if (package.Generation != generation) throw new InvalidOperationException("The queued operation belongs to an earlier plugin installation.");
        RequireScope(package, preset);
    }

    public void RequireScope(PluginPackage package, string preset)
    {
        RequireCurrent(package);
        if (!Available(package, preset)) throw new UnauthorizedAccessException("The plugin is unavailable in this preset.");
    }

    public T WhileCurrent<T>(PluginPackage package, string preset, Func<T> operation)
    {
        lock (packageGate)
        {
            RequireScope(package, preset);
            return operation();
        }
    }
}
