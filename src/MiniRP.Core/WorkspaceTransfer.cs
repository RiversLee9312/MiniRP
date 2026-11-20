using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class WorkspaceTransfer(Workspace workspace, PluginRuntime plugins)
{
    private const string Header = "archive.json";
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxEntries = 50000;
    private const long MaxJsonBytes = 256L * 1024 * 1024;
    private static readonly string[] Areas = ["data", "structured"];
    private static readonly EnumerationOptions SafeFiles = new() { AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly EnumerationOptions SafeTree = new()
    {
        RecurseSubdirectories = true, MaxRecursionDepth = 8, AttributesToSkip = FileAttributes.ReparsePoint
    };

    public async Task Export(string kind, string preset, string room, string plugin, string destination, CancellationToken ct)
    {
        destination = Path.GetFullPath(destination);
        var output = destination + "." + JsonValues.NewId() + ".tmp";
        try
        {
            if (kind == "plugin")
                await Copy(plugins.ExportablePlugin(plugin), output, ct);
            else
            {
                if (kind is not ("preset" or "save" or "room")) throw new ArgumentException("Choose an archive type.");
                WorkspaceIds.Require(preset);
                if (kind == "room") WorkspaceIds.Require(room);
                await ExportWorkspace(kind, preset, room, output, ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(output, destination);
        }
        finally { File.Delete(output); }
    }

    private async Task ExportWorkspace(string kind, string preset, string room, string output, CancellationToken ct)
    {
        using var stage = new Staging(workspace.Root);
        var config = ReadObject(At("presets", preset + ".json"));
        config["id"] = preset;
        var bundle = kind == "room" ? [] : plugins.PresetPlugins(preset, config, workspace.Root);
        var legacy = new JsonObject();
        foreach (var package in bundle.Where(plugin => plugin.PresetId.Length == 0))
            legacy[package.Id] = package.Signer;
        config.Remove(PluginRuntime.LegacyOwners);
        if (legacy.Count > 0) config[PluginRuntime.LegacyOwners] = legacy;
        var contents = new Contents(preset);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var configPath = Path.Combine(stage.Path, "preset.json");
        File.WriteAllText(configPath, JsonValues.Serialize(config));
        sources.Add($"presets/{preset}.json", configPath);
        foreach (var package in bundle) sources.Add($"plugins/{package.Id}.plugin", package.ArchivePath);

        if (kind is "save" or "room")
        {
            var parent = At("conversations", preset);
            var rooms = kind == "room" ? new[] { Path.Combine(parent, room + ".json") } : Files(parent, "*.json");
            foreach (var path in rooms)
            {
                ct.ThrowIfCancellationRequested();
                var id = Stem(path);
                if (!WorkspaceIds.IsValid(id)) continue;
                var document = ReadObject(path);
                contents.Room(id, document);
                Add(path);
                foreach (var child in Files(Path.Combine(parent, id), "*", recursive: true))
                    if (RoomFile(Relative(child).Split('/'))) Add(child);
            }
            if (kind == "save")
            {
                foreach (var path in Files(At("preset_sessions", preset), "*"))
                    if (Path.GetFileName(path) is "agent.sqlite" or "blocks.json") Add(path);
            }
            foreach (var version in contents.Versions.ToArray())
                foreach (var path in Files(At("versions", version), "*"))
                    if (VersionFile(Relative(path).Split('/'))) Add(path);
            foreach (var area in Areas)
            {
                var areaPath = At("plugins", area);
                if (!Directory.Exists(areaPath)) continue;
                foreach (var package in Directory.EnumerateDirectories(areaPath, "*", SafeFiles))
                    foreach (var path in Files(package, "*.sqlite", recursive: true))
                        if (ScopedFile(Relative(path).Split('/'), contents, kind == "save")) Add(path);
            }
            foreach (var attachment in contents.Attachments)
            {
                var path = At("attachments", attachment);
                if (File.Exists(path)) Add(path);
            }
        }

        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var header = JsonValues.Serialize(new JsonObject { ["version"] = 1, ["kind"] = kind, ["preset"] = preset,
            ["room"] = kind == "room" ? room : null });
        await using (var entry = zip.CreateEntry(Header).Open())
            await entry.WriteAsync(Encoding.UTF8.GetBytes(header), ct);
        long total = 0;
        if (sources.Count >= MaxEntries) throw new InvalidDataException("The workspace has too many archive items.");
        foreach (var (entry, file) in sources)
        {
            ct.ThrowIfCancellationRequested();
            await using var input = File.OpenRead(file);
            total += input.Length;
            if (total > MaxBytes) throw new InvalidDataException("The workspace archive is too large.");
            await using var target = zip.CreateEntry(entry, entry.StartsWith("attachments/", StringComparison.Ordinal) ||
                entry.EndsWith(".plugin", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
            await input.CopyToAsync(target, ct);
        }

        void Add(string path)
        {
            ct.ThrowIfCancellationRequested();
            EnsureUnlinked(path, workspace.Root);
            var relative = Relative(path);
            if (path.EndsWith(".sqlite", StringComparison.Ordinal))
            {
                var snapshot = Path.Combine(stage.Path, JsonValues.NewId() + ".sqlite");
                using (var db = MemoryDb.ReadOnly(path)) db.Snapshot(snapshot);
                sources.Add(relative, snapshot);
                if (!relative.StartsWith("plugins/", StringComparison.Ordinal))
                    ReadSessionReferences(snapshot, contents, ct);
            }
            else
            {
                sources.Add(relative, path);
                if (path.EndsWith(".json", StringComparison.Ordinal)) contents.Collect(ReadJson(path));
            }
        }
    }

    public async Task<string> Import(string kind, string source, CancellationToken ct)
    {
        if (kind is not ("preset" or "save")) throw new ArgumentException("Choose a preset or save archive.");
        using var stage = new Staging(workspace.Root);
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count is 0 or > MaxEntries || zip.Entries.Sum(entry => entry.Length) > MaxBytes)
            throw new InvalidDataException("The workspace archive exceeds its size limit.");
        var metadata = zip.GetEntry(Header) ?? throw new InvalidDataException("This is not a MiniRP archive.");
        if (metadata.Length > 4096) throw new InvalidDataException("Invalid archive header.");
        JsonObject header;
        await using (var input = metadata.Open())
        using (var buffer = new MemoryStream())
        {
            await CopyEntry(input, buffer, metadata.Length, ct);
            buffer.Position = 0;
            header = JsonNode.Parse(buffer) as JsonObject ?? throw new InvalidDataException("Invalid archive header.");
        }
        if (header.GetInt32("version") != 1 || header.GetString("kind") != kind)
            throw new InvalidDataException("The archive type or version does not match.");
        var preset = WorkspaceIds.Require(header.GetString("preset"));
        var presetPath = $"presets/{preset}.json";
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (ReferenceEquals(entry, metadata)) continue;
            var path = entry.FullName;
            var parts = path.Split('/');
            if (!parts.All(Part) || (entry.ExternalAttributes & 0xf0000000) == 0xa0000000 ||
                !EntryAllowed(parts, preset, kind == "save") ||
                parts[^1].EndsWith(".json", StringComparison.Ordinal) && entry.Length > MaxJsonBytes)
                throw new InvalidDataException("The archive contains an unsupported path or item.");
            var target = Path.Combine(stage.Path, Path.Combine(parts));
            if (!entries.TryAdd(path, target)) throw new InvalidDataException("The archive contains duplicate paths.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await CopyEntry(input, output, entry.Length, ct);
        }

        if (!entries.TryGetValue(presetPath, out var presetSource))
            throw new InvalidDataException("The archive is missing its preset.");
        var config = ReadObject(presetSource);
        if (config.GetString("id") != preset) throw new InvalidDataException("The preset identifier does not match the archive.");
        var contents = new Contents(preset);
        foreach (var (path, file) in entries)
        {
            var parts = path.Split('/');
            if (parts[0] != "conversations" || parts.Length != 3) continue;
            contents.Room(Stem(file), ReadObject(file));
        }
        foreach (var path in entries.Keys)
        {
            var parts = path.Split('/');
            if (parts[0] == "conversations" && parts.Length > 3 && !contents.Rooms.Contains(parts[2]) ||
                parts[0] == "plugins" && parts.Length > 2 && !ScopedFile(parts, contents, includePreset: true))
                throw new InvalidDataException("The archive contains state for another conversation.");
        }

        var conversations = At("conversations");
        foreach (var directory in contents.Rooms.Count > 0 && Directory.Exists(conversations)
                     ? Directory.GetDirectories(conversations, "*", SafeFiles) : [])
        {
            if (Path.GetFileName(directory) == preset) continue;
            if (contents.Rooms.Any(room => File.Exists(Path.Combine(directory, room + ".json"))))
                throw new InvalidOperationException("A Room with this identifier belongs to another preset.");
        }
        var installedPreset = At("presets", preset + ".json");
        EnsureUnlinked(installedPreset, workspace.Root);
        if (File.Exists(installedPreset) && !JsonNode.DeepEquals(ReadObject(installedPreset), config))
            throw new InvalidOperationException("A different preset with this identifier already exists.");
        var bundled = entries.Where(pair => pair.Key.StartsWith("plugins/", StringComparison.Ordinal) &&
                pair.Key.EndsWith(".plugin", StringComparison.Ordinal)).Select(pair => pair.Value).ToArray();
        plugins.ImportPresetPlugins(preset, config, stage.Path, bundled, pluginFiles =>
        {
            var files = entries.Where(pair => !pair.Key.EndsWith(".plugin", StringComparison.Ordinal) &&
                !(pair.Key == presetPath && File.Exists(installedPreset)))
                .ToDictionary(pair => At(pair.Key.Split('/')), pair => pair.Value, StringComparer.Ordinal);
            foreach (var (target, file) in pluginFiles) files.Add(target, file);
            RestoreFiles(files, installedPreset, ct);
        }, ct);
        return preset;
    }

    private void RestoreFiles(Dictionary<string, string> files, string preset, CancellationToken ct)
    {
        var plan = new List<KeyValuePair<string, string>>();
        foreach (var pair in files.OrderBy(item => item.Key == preset))
        {
            ct.ThrowIfCancellationRequested();
            EnsureUnlinked(pair.Key, workspace.Root);
            if (File.Exists(pair.Key))
            {
                var wal = pair.Key + "-wal";
                if ((pair.Key.EndsWith(".sqlite", StringComparison.Ordinal) &&
                    File.Exists(wal) && new FileInfo(wal).Length > 0) ||
                    !SameFile(pair.Key, pair.Value, ct))
                    throw new InvalidOperationException("The archive conflicts with existing saved data.");
            }
            else plan.Add(pair);
        }
        var moved = new Stack<string>();
        try
        {
            foreach (var (target, source) in plan)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                moved.Push(target);
            }
        }
        catch
        {
            foreach (var file in moved) File.Delete(file);
            throw;
        }
    }

    private static bool EntryAllowed(string[] parts, string preset, bool save)
    {
        if (parts.Length == 2 && parts[0] == "presets") return parts[1] == preset + ".json";
        if (parts.Length == 2 && parts[0] == "plugins" && parts[1].EndsWith(".plugin", StringComparison.Ordinal)) return true;
        if (!save) return false;
        return parts[0] switch
        {
            "conversations" => parts.Length >= 3 && parts[1] == preset && RoomFile(parts),
            "preset_sessions" => parts.Length == 3 && parts[1] == preset && parts[2] is "agent.sqlite" or "blocks.json",
            "versions" => VersionFile(parts),
            "attachments" => parts.Length == 2 && Id(parts[1]),
            "plugins" => parts.Length is 5 or 6 && Areas.Contains(parts[1]) && Sqlite(parts[^1]),
            _ => false
        };
    }

    private static bool RoomFile(string[] parts) =>
        parts.Length == 3 && parts[2].EndsWith(".json", StringComparison.Ordinal) && WorkspaceIds.IsValid(Stem(parts[2])) ||
        parts.Length >= 5 && WorkspaceIds.IsValid(parts[2]) &&
        (parts.Length == 5 && parts[3] is "agents" or "fetch" && Sqlite(parts[4]) ||
         parts.Length == 5 && parts[3] == "migration" && parts[4] == "python-v1.json" ||
         parts.Length == 6 && parts[3] == "ensemble" && Id(parts[4]) && Sqlite(parts[5]));

    private static bool VersionFile(string[] parts) =>
        parts.Length == 3 && Id(parts[1]) && (parts[2] == "world.json" || Sqlite(parts[2]));

    private static bool ScopedFile(string[] parts, Contents contents, bool includePreset)
    {
        if (parts.Length is not (5 or 6) || !Sqlite(parts[^1]) ||
            !parts.All(Part) || !Areas.Contains(parts[1])) return false;
        var id = Stem(parts[^1]);
        if (parts.Length == 6)
            return parts[3] == "chat" && contents.Children.TryGetValue(parts[4], out var children) && children.Contains(id);
        return parts[3] switch
        {
            "preset" => includePreset && id == contents.Preset,
            "room" or "chat" => contents.Rooms.Contains(id),
            "session" => id == "preset-" + contents.Preset ? includePreset :
                contents.Rooms.Any(room => SessionForRoom(id, room)),
            _ => false
        };
    }

    private static bool SessionForRoom(string session, string room)
    {
        foreach (var role in new[] { "chat", "env", "search", "title", "fetch" })
        {
            var prefix = role + "-" + room;
            if (session == prefix || session.StartsWith(prefix + "-", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void ReadSessionReferences(string file, Contents contents, CancellationToken ct)
    {
        using var db = MemoryDb.ReadOnly(file);
        if (!db.HasEntriesTable()) return;
        foreach (var row in db.Query("SELECT content FROM entries WHERE key='_context/history' OR key GLOB '_archive/history/*' OR key GLOB '_archive/public/history/*'"))
        {
            ct.ThrowIfCancellationRequested();
            try { contents.Collect(JsonNode.Parse(row["content"])); }
            catch (JsonException) { }
        }
    }

    private static async Task Copy(string source, string destination, CancellationToken ct)
    {
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await input.CopyToAsync(output, ct);
    }

    private static async Task CopyEntry(Stream input, Stream output, long length, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long written = 0;
        for (int count; (count = await input.ReadAsync(buffer, ct)) != 0;)
        {
            written += count;
            if (written > length) throw new InvalidDataException("An archive item exceeds its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (written != length) throw new InvalidDataException("An archive item is truncated.");
    }

    internal static bool SameFile(string first, string second, CancellationToken ct)
    {
        if (first.Length == 0 || !File.Exists(first)) return false;
        using var left = File.OpenRead(first);
        using var right = File.OpenRead(second);
        if (left.Length != right.Length) return false;
        var a = new byte[65536];
        var b = new byte[65536];
        for (long remaining = left.Length; remaining > 0;)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(remaining, a.Length);
            left.ReadExactly(a.AsSpan(0, count));
            right.ReadExactly(b.AsSpan(0, count));
            if (!a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count))) return false;
            remaining -= count;
        }
        return true;
    }

    private static void EnsureUnlinked(string path, string root)
    {
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (!Part(part)) throw new InvalidDataException("Invalid workspace archive path.");
            root = Path.Combine(root, part);
            try
            {
                if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Linked paths are unavailable in workspace archives.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private string At(params string[] parts) => Path.Combine(workspace.Root, Path.Combine(parts));
    private string Relative(string path) => Path.GetRelativePath(workspace.Root, path).Replace('\\', '/');
    private static string[] Files(string path, string match, bool recursive = false) =>
        Directory.Exists(path) ? Directory.GetFiles(path, match, recursive ? SafeTree : SafeFiles) : [];
    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);
    private static bool Part(string value) => value.Length is > 0 and <= 230 && value[0] != '.' &&
        value[^1] != '.' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
        PluginPackage.SafePath(value);
    private static bool Id(string value) => Part(value) && !value.Contains('.');
    private static bool Sqlite(string name) => name.EndsWith(".sqlite", StringComparison.Ordinal) && Id(Stem(name));
    private static JsonNode? ReadJson(string file)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length > MaxJsonBytes) throw new InvalidDataException("A workspace record exceeds the archive limit.");
        return JsonNode.Parse(stream);
    }
    private static JsonObject ReadObject(string file) =>
        ReadJson(file) as JsonObject ?? throw new InvalidDataException("Invalid workspace record.");

    private sealed class Staging : IDisposable
    {
        public Staging(string root)
        {
            Path = System.IO.Path.Combine(root, "trash", "archive-" + JsonValues.NewId());
            EnsureUnlinked(Path, root);
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class Contents(string preset)
    {
        public string Preset { get; } = preset;
        public HashSet<string> Rooms { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Children { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Versions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Attachments { get; } = new(StringComparer.Ordinal);

        public void Room(string id, JsonObject document)
        {
            if (!WorkspaceIds.IsValid(id) || document.GetString("id", id) != id ||
                document["preset"] is JsonValue && document.GetString("preset") != Preset)
                throw new InvalidDataException("A Room identifier does not match the archive.");
            Rooms.Add(id);
            Children[id] = document.GetObject("ensemble").GetArray("chats")
                .Select(chat => WorkspaceIds.Require(chat.GetString("id"))).ToHashSet(StringComparer.Ordinal);
            Collect(document);
        }

        public void Collect(JsonNode? node)
        {
            if (node is JsonArray array)
                foreach (var child in array) Collect(child);
            if (node is not JsonObject record) return;
            var version = record.GetString("version");
            if (Id(version)) Versions.Add(version);
            foreach (var file in record.GetArray("attachments"))
            {
                var id = file.GetString("id");
                if (Id(id)) Attachments.Add(id);
            }
            foreach (var (_, child) in record) Collect(child);
        }
    }
}
