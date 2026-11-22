using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginProjectAssets(string root, string id, string preset, string name,
    MemoryDb db, AgentAssetStorage storage, Func<string, JsonObject, PluginRuntime.AssetContent> open)
{
    private const string KeyPrefix = "_context/plugin-project-assets/";
    private string Key => KeyPrefix + id;
    private string LegacySource => Path.Combine(root, "source");

    internal static bool ContainsAsset(MemoryDb db, string url) =>
        db.Query("SELECT content FROM entries WHERE key GLOB ?", KeyPrefix + "*").Any(row =>
            JsonValues.ParseObject(row["content"]).GetObject("files").Any(file => JsonValues.GetText(file.Value) == url));

    internal static void RequireWritable(MemoryDb db, string url)
    {
        if (ContainsAsset(db, url))
            throw new UnauthorizedAccessException("Plugin draft Assets are read-only for PresetAgent. Send the change to the project's PluginAgent.");
    }

    internal AgentSkill Skill() => new(
        "project_assets", "Associate Asset URLs with files in the assigned plugin draft.",
        new(
            Tools.Define("project_asset_list", "List draft paths and their Asset URLs. Use next_offset for more files.",
                new() { ["offset"] = Tools.Integer(), ["limit"] = Tools.Integer() }),
            Tools.Define("project_asset_add", "Associate an existing Asset URL with a new relative path in the draft.",
                new() { ["path"] = Tools.String(), ["url"] = Tools.String() }, "path", "url"),
            Tools.Define("project_asset_remove", "Remove an Asset URL from the draft. Omit path to unlink all occurrences. The Asset is kept.",
                new() { ["url"] = Tools.String(), ["path"] = Tools.String() }, "url")),
        async (tool, args, ct) =>
        {
            var files = await Files(ct);
            var path = args.GetString("path");
            var url = args.GetString("url");
            switch (tool)
            {
                case "project_asset_list":
                    var offset = Math.Max(0, args.GetInt32("offset"));
                    var limit = Math.Clamp(args.GetInt32("limit", 20), 1, 50);
                    var page = files.OrderBy(item => item.Key, StringComparer.Ordinal).Skip(offset).Take(limit + 1).ToArray();
                    return new JsonObject
                    {
                        ["files"] = new JsonArray(page.Take(limit).Select(item => (JsonNode?)new JsonObject
                            { ["path"] = item.Key, ["url"] = JsonValues.Clone(item.Value) }).ToArray()),
                        ["next_offset"] = page.Length > limit ? JsonValue.Create(offset + limit) : null
                    };
                case "project_asset_add":
                    ValidatePath(path);
                    _ = open(url, new());
                    if (files.GetString(path) == url) return FileReference(path, url);
                    if (files.Any(item => PathsConflict(item.Key, path)))
                        throw new ArgumentException("The draft path conflicts with an existing path. Remove the existing association first.");
                    files[path] = url;
                    Save(files);
                    return FileReference(path, url);
                case "project_asset_remove":
                    var matches = files.Where(item => JsonValues.GetText(item.Value) == url &&
                        (path.Length == 0 || item.Key == path)).Select(item => item.Key).ToArray();
                    foreach (var match in matches) files.Remove(match);
                    if (matches.Length > 0) Save(files);
                    return new JsonObject { ["url"] = url, ["removed"] = matches.Length };
                default:
                    throw new InvalidOperationException("Unknown project Asset tool.");
            }
        },
        Instructions: "Start with project_asset_list. Read and edit existing files, including manifest.json, through their listed Asset URLs. For a new file, use create_asset and link its returned URL to a relative package path. For .mjs imports, link every source at the path the import resolves to. If replacing an association, unlink the old URL first. Unlinking keeps the old Asset.");

    internal async Task Stage(string destination, CancellationToken ct)
    {
        var files = await Files(ct);
        if (files.Count >= 256) throw new InvalidDataException("The draft has too many files.");
        var entries = files.Select(entry =>
        {
            ValidatePath(entry.Key);
            try { return (Path: entry.Key, Asset: open(JsonValues.GetText(entry.Value), new())); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new IOException($"The Asset for '{entry.Key}' is unavailable. Link an accessible Asset before building.", error);
            }
        }).ToArray();
        long total = 0;
        foreach (var (path, asset) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await foreach (var chunk in asset.Chunks(ct))
            {
                total += chunk.Length;
                if (total > PluginPackage.MaxPackageBytes - 65536) throw new InvalidDataException("The draft is too large.");
                await output.WriteAsync(chunk, ct);
            }
        }
    }

    private async Task<JsonObject> Files(CancellationToken ct)
    {
        var state = JsonValues.ParseObject(db.Get(Key) ?? "{}");
        var files = state.GetObject("files");
        if (state.GetBoolean("initialized")) return files;
        var initial = Directory.Exists(LegacySource) ? LegacyFiles() : StarterFiles();
        foreach (var (path, source) in initial)
        {
            ct.ThrowIfCancellationRequested();
            if (files.ContainsKey(path)) continue;
            ValidatePath(path);
            if (files.Any(item => PathsConflict(item.Key, path)))
                throw new InvalidDataException("The legacy draft has conflicting file paths.");
            var asset = await storage.Create(new() { ["name"] = Path.GetFileName(path) }, source, ct);
            files[path] = asset.GetString("url");
            Save(files, initialized: false);
        }
        Save(files);
        return files;
    }

    private void Save(JsonObject files, bool initialized = true) =>
        db.Write(Key, JsonValues.Serialize(new JsonObject { ["initialized"] = initialized, ["files"] = files.DeepClone() }), internalWrite: true);

    private IEnumerable<(string Path, PluginRuntime.AssetContent Asset)> StarterFiles()
    {
        var manifest = JsonValues.Serialize(new JsonObject
        {
            ["id"] = "draft-" + id, ["packageName"] = "local.minirp.p" + id, ["name"] = name,
            ["presetId"] = preset, ["apiVersion"] = 2, ["entry"] = "index.js",
            ["actions"] = new JsonObject { ["handlers"] = new JsonObject() }
        });
        yield return ("manifest.json", Text(manifest, "application/json"));
        yield return ("index.js", Text("globalThis.handlers = {};\n", "text/javascript"));

        static PluginRuntime.AssetContent Text(string value, string mime)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return new(mime, bytes.Length, ct => PluginRuntime.ReadBytes(bytes, ct));
        }
    }

    private IEnumerable<(string Path, PluginRuntime.AssetContent Asset)> LegacyFiles()
    {
        if ((File.GetAttributes(LegacySource) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Links are unavailable in plugin projects.");
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(LegacySource, "*", options))
        {
            var path = Path.GetRelativePath(LegacySource, file).Replace('\\', '/');
            ValidatePath(path);
            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".json" => "application/json",
                ".js" or ".mjs" => "text/javascript",
                ".xml" => "application/xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".pdf" => "application/pdf",
                ".wasm" => "application/wasm",
                ".zip" => "application/zip",
                _ => "text/plain"
            };
            yield return (path, PluginRuntime.OpenSample(file, mime, Path.GetFileName(path)));
        }
    }

    private static JsonObject FileReference(string path, string url) => new() { ["path"] = path, ["url"] = url };

    private static bool PathsConflict(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static void ValidatePath(string path)
    {
        if (!PluginPackage.SafePath(path) || path.Equals(PluginSignature.EntryName, StringComparison.OrdinalIgnoreCase) ||
            path.Split('/').Any(IsWindowsAlias))
            throw new ArgumentException("Use a portable relative path inside the plugin package.");
    }

    private static bool IsWindowsAlias(string component)
    {
        var stem = component.Split('.', 2)[0].ToUpperInvariant();
        return component.EndsWith('.') || stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            char.IsAsciiDigit(stem[3]);
    }
}
