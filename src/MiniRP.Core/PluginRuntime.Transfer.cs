using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    internal const string LegacyOwners = "_portablePluginOwners";

    internal string ExportablePlugin(string id)
    {
        var package = Installed(id);
        if (package.BuiltIn || package.ArchivePath.Length == 0)
            throw new InvalidOperationException("This plugin has no installed archive.");
        return package.ArchivePath;
    }

    internal PluginPackage[] PresetPlugins(string preset, JsonObject config, string workspace)
    {
        return Volatile.Read(ref packages).Where(package => !package.BuiltIn && package.Signed &&
                (package.PresetId == preset || package.PresetId.Length == 0 && IsLocalDraft(package) && LegacyOwner(package)))
            .ToArray();

        bool LegacyOwner(PluginPackage package)
        {
            if (config.GetObject(LegacyOwners).GetString(package.Id) == package.Signer) return true;
            var deployment = Path.Combine(SessionFiles.PluginDirectory(workspace, preset),
                package.Id[6..], "deployment.json");
            if (!File.Exists(deployment)) return false;
            try
            {
                return JsonValues.ParseObject(File.ReadAllText(deployment)).GetString("signer") == package.Signer;
            }
            catch (JsonException) { return false; }
        }
    }

    internal void ImportPresetPlugins(string preset, JsonObject config, string stage, string[] archives,
        Action<IReadOnlyDictionary<string, string>> commit, CancellationToken ct)
    {
        var candidates = new List<PluginPackage>();
        var legacy = config.GetObject(LegacyOwners);
        foreach (var path in archives)
        {
            using var input = File.OpenRead(path);
            var package = ReadPackage(input, path, ct: ct);
            if (package.Id + ".plugin" != Path.GetFileName(path) ||
                package.PresetId != preset && !(package.PresetId.Length == 0 && IsLocalDraft(package) &&
                    legacy.GetString(package.Id) == package.Signer) ||
                candidates.Any(other => other.Id == package.Id || other.PackageName == package.PackageName))
                throw new InvalidDataException("A bundled plugin does not belong to this preset.");
            candidates.Add(package);
        }

        lock (packageGate)
        {
            var installed = Volatile.Read(ref packages);
            var added = new List<PluginPackage>();
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var package in candidates)
            {
                var target = Path.Combine(root, package.Id + ".plugin");
                var existing = installed.FirstOrDefault(item => item.Id == package.Id || item.PackageName == package.PackageName);
                if (existing is not null || File.Exists(target))
                {
                    if (existing is null || existing.Id != package.Id || existing.PackageName != package.PackageName ||
                        existing.Signer != package.Signer || !WorkspaceTransfer.SameFile(existing.ArchivePath, package.ArchivePath, ct))
                        throw new InvalidOperationException($"The installed plugin {package.Name} conflicts with this archive.");
                    continue;
                }
                files.Add(target, package.ArchivePath);
                var settings = SettingsPath(package);
                if (!File.Exists(settings))
                {
                    var file = Path.Combine(stage, "scopes", package.Id + ".sqlite");
                    using (var db = new MemoryDb(file))
                        db.Write("host/scope", JsonValues.Serialize(PluginScope.Include(preset).Describe()), internalWrite: true);
                    files.Add(settings, file);
                }
                added.Add(package with { ArchivePath = target });
            }
            if (Directory.EnumerateFiles(root, "*.plugin").Take(32).Count() + added.Count > 32)
                throw new InvalidOperationException("At most 32 plugins can be installed.");

            commit(files);
            foreach (var package in added) scopes.TryRemove(package.StorageKey, out _);
            if (added.Count == 0) return;
            SetPackages([.. installed, .. added]);
            foreach (var package in added) PublishEnabled(package, new() { ["preset"] = preset });
        }
    }

    private static bool IsLocalDraft(PluginPackage package) => package.Id.StartsWith("draft-", StringComparison.Ordinal) &&
        (package.PackageName == "local.minirp.p" + package.Id[6..] || package.PackageName == "local.minichat.p" + package.Id[6..]);
}
