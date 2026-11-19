using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginDevelopmentProject(string root, string id, string preset, PluginRuntime runtime, Action changed)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxTextBytes = 2 * 1024 * 1024;
    private string Source => Path.Combine(root, "source");
    private string PackageId => "draft-" + id;
    private string PackageName => "local.minirp.p" + id;

    public void Create(string name)
    {
        Directory.CreateDirectory(Source);
        AtomicFile.Write(FilePath("manifest.json"), JsonValues.Serialize(new JsonObject
        {
            ["id"] = PackageId, ["packageName"] = PackageName, ["name"] = name,
            ["apiVersion"] = 2, ["entry"] = "index.js", ["actions"] = new JsonObject { ["handlers"] = new JsonObject() }
        }));
        AtomicFile.Write(FilePath("index.js"), "globalThis.handlers = {};\n");
    }

    public async Task<JsonNode?> Execute(string action, JsonObject args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (action)
        {
            case "project_files":
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                return new JsonArray(Directory.EnumerateFiles(Source, "*", options)
                    .Select(file => (JsonNode?)JsonValue.Create(Path.GetRelativePath(Source, file).Replace('\\', '/')))
                    .OrderBy(value => value!.ToString(), StringComparer.Ordinal).ToArray());
            case "project_read":
                var name = args.GetString("path");
                var source = Read(FilePath(name));
                var start = Math.Clamp(args.GetInt32("offset"), 0, source.Length);
                var end = Math.Min(source.Length, start + Math.Clamp(args.GetInt32("length", 8000), 1, 16000));
                if (end < source.Length && char.IsHighSurrogate(source[end - 1])) end++;
                return new JsonObject { ["path"] = name, ["text"] = source[start..end],
                    ["next_offset"] = end == source.Length ? null : JsonValue.Create(end), ["total_chars"] = source.Length };
            case "project_write":
                var target = FilePath(args.GetString("path"));
                if (File.Exists(target) && !args.GetBoolean("overwrite"))
                    throw new InvalidOperationException("File exists. Use project_edit or explicitly request overwrite.");
                Write(target, args.GetString("text"));
                return new JsonObject { ["written"] = true };
            case "project_edit":
                var path = FilePath(args.GetString("path"));
                var previous = Read(path);
                var before = args.GetString("before");
                var first = previous.IndexOf(before, StringComparison.Ordinal);
                if (before.Length == 0 || first < 0 || previous.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
                    throw new ArgumentException("The text to replace must match exactly once.");
                Write(path, previous[..first] + args.GetString("after") + previous[(first + before.Length)..]);
                return new JsonObject { ["edited"] = true };
            case "project_delete":
                File.Delete(FilePath(args.GetString("path")));
                return new JsonObject { ["deleted"] = true };
            case "project_debug":
            case "project_publish":
                return await Build(action == "project_publish", args, ct);
            case "project_remove":
                var deployment = Path.Combine(root, "deployment.json");
                if (!File.Exists(deployment)) throw new InvalidOperationException("This project has no deployment.");
                runtime.RemoveDevelopment(PackageId, JsonValues.ParseObject(File.ReadAllText(deployment)).GetString("signer"));
                changed();
                return new JsonObject { ["removed"] = true, ["id"] = PackageId };
            default:
                throw new ArgumentException("Unknown plugin file or deployment command.");
        }
    }

    private async Task<JsonNode?> Build(bool publish, JsonObject args, CancellationToken ct)
    {
        var manifest = JsonValues.ParseObject(Read(FilePath("manifest.json")));
        var packageName = manifest.GetString("packageName");
        if (manifest.GetString("id") != PackageId || packageName != PackageName && packageName != "local.minichat.p" + id)
            throw new InvalidDataException("Keep the draft's assigned id and packageName.");
        var signer = PluginDevelopmentSigner.Open(Path.Combine(root, "signer.json"));
        var artifact = Path.Combine(root, JsonValues.NewId() + ".plugin");
        try
        {
            PluginArchive.Pack(Source, artifact, signer.Sign);
            var url = publish ? "" : args.GetString("handler");
            var tested = await runtime.Debug(artifact, Path.Combine(root, "preview"), preset, url, args.GetObject("event"), ct);
            if (!publish) return new JsonObject { ["ok"] = true, ["result"] = tested };
            ct.ThrowIfCancellationRequested();
            var (_, fingerprint) = PluginArchive.Verify(artifact);
            AtomicFile.Write(Path.Combine(root, "deployment.json"), JsonValues.Serialize(new JsonObject { ["signer"] = fingerprint }));
            var package = runtime.Deploy(artifact, preset);
            changed();
            return runtime.Descriptor(package);
        }
        finally { File.Delete(artifact); }
    }

    private static string Read(string path)
    {
        if (new FileInfo(path).Length > MaxTextBytes) throw new IOException("This file exceeds the text editor limit.");
        return File.ReadAllText(path, Utf8);
    }

    private static void Write(string path, string text)
    {
        if (Utf8.GetByteCount(text) > MaxTextBytes) throw new ArgumentException("File exceeds the plugin editor limit.");
        AtomicFile.Write(path, text);
    }

    private string FilePath(string name)
    {
        if (!PluginPackage.SafePath(name) || name.Equals(PluginSignature.EntryName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a relative path inside the plugin source directory.");
        var path = Source;
        CheckPath(path);
        foreach (var component in name.Split('/'))
        {
            path = Path.Combine(path, component);
            CheckPath(path);
        }
        return path;

        static void CheckPath(string path)
        {
            var attributes = new FileInfo(path).Attributes;
            if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Links are unavailable in plugin projects.");
        }
    }
}
