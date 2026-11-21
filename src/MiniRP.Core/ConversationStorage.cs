using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class ConversationStorage
{
    private static readonly EnumerationOptions Safe = new() { AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly EnumerationOptions Recursive = new() { AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true };

    public static void Capture(MemoryDb db, string path)
    {
        var snapshot = path + ".tmp";
        try
        {
            db.SnapshotSession(snapshot);
            Compress(snapshot, path + ".gz");
        }
        finally { File.Delete(snapshot); }
    }

    public static Stream Open(string path)
    {
        if (File.Exists(path)) return File.OpenRead(path);
        return new GZipStream(File.OpenRead(path + ".gz"), CompressionMode.Decompress);
    }

    public static void WriteWorld(string path, string json) =>
        WriteCompressed(path + ".gz", output =>
        {
            using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(json);
        });

    public static void Maintain(string root, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var version in Directories(root, "versions").Where(VersionName))
        {
            ct.ThrowIfCancellationRequested();
            Try(() => CompactVersion(version, ct));
        }
        foreach (var trash in Directories(root, "trash").Where(VersionName))
        {
            ct.ThrowIfCancellationRequested();
            Try(() => PackTrash(trash));
        }
        var trashRoot = Path.Combine(root, "trash");
        var old = now.AddDays(-30);
        if (Directory.Exists(trashRoot))
            foreach (var file in Directory.EnumerateFiles(trashRoot, "*.zip", Safe).Where(file => VersionName(file[..^4])))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetLastWriteTimeUtc(file) < old) Try(() => File.Delete(file));
            }
        foreach (var run in Directories(root, "container-runs").Where(VersionName))
        {
            ct.ThrowIfCancellationRequested();
            if (!AgentContainer.ActiveRun(run) && Directory.GetLastWriteTimeUtc(run) < now.AddDays(-1))
                Try(() => DeleteDirectory(run));
        }
        foreach (var run in Directories(root, "curl-runs").Where(VersionName))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.GetLastWriteTimeUtc(run) < now.AddDays(-1)) Try(() => DeleteDirectory(run));
        }
        Try(() => PruneVersions(root, old, ct));
        Try(() => PruneOwnedAssets(root, old, ct));
    }

    private static void PruneOwnedAssets(string root, DateTime old, CancellationToken ct)
    {
        var attachments = Path.Combine(root, "attachments");
        if (!Directory.Exists(attachments)) return;
        var candidates = Directory.EnumerateFiles(attachments, AgentAssetStorage.FilePrefix + "*", Safe)
            .Where(file => File.GetLastWriteTimeUtc(file) < old)
            .ToDictionary(file => Path.GetFileName(file), StringComparer.Ordinal);
        if (candidates.Count == 0) return;

        void KeepDocument(JsonNode? node)
        {
            if (node is JsonArray array)
                foreach (var item in array) KeepDocument(item);
            if (node is not JsonObject document) return;
            foreach (var (_, link) in document.GetObject("asset_links"))
                candidates.Remove(link.GetObject("attachment").GetString("id"));
            foreach (var (_, child) in document) KeepDocument(child);
        }

        void KeepDatabase(string file)
        {
            using var db = MemoryDb.ReadOnly(file);
            foreach (var id in AgentAssetStorage.Files(db)) candidates.Remove(id);
        }

        void KeepSnapshot(Stream input, bool compressed)
        {
            var temporary = Path.Combine(attachments, "asset-scan-" + JsonValues.NewId());
            try
            {
                using var gzip = compressed ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
                using (var output = File.Create(temporary)) (gzip ?? input).CopyTo(output);
                KeepDatabase(temporary);
            }
            finally { File.Delete(temporary); }
        }

        foreach (var area in new[] { "conversations", "preset_sessions", "versions", "trash" })
        {
            var directory = Path.Combine(root, area);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", Recursive))
            {
                ct.ThrowIfCancellationRequested();
                if (candidates.Count == 0) return;
                if (file.EndsWith(".sqlite", StringComparison.Ordinal)) KeepDatabase(file);
                else if (file.EndsWith(".sqlite.gz", StringComparison.Ordinal))
                {
                    using var input = File.OpenRead(file);
                    KeepSnapshot(input, compressed: true);
                }
                else if (file.EndsWith(".json", StringComparison.Ordinal) && area != "versions")
                {
                    using var input = File.OpenRead(file);
                    KeepDocument(JsonNode.Parse(input));
                }
                else if (area == "trash" && Path.GetDirectoryName(file) == directory &&
                         file.EndsWith(".zip", StringComparison.Ordinal))
                {
                    using var archive = ZipFile.OpenRead(file);
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var compressed = entry.Name.EndsWith(".sqlite.gz", StringComparison.Ordinal);
                        if (compressed || entry.Name.EndsWith(".sqlite", StringComparison.Ordinal))
                        {
                            using var input = entry.Open();
                            KeepSnapshot(input, compressed);
                        }
                        else if (entry.Name.EndsWith(".json", StringComparison.Ordinal))
                        {
                            using var input = entry.Open();
                            KeepDocument(JsonNode.Parse(input));
                        }
                    }
                }
            }
        }
        foreach (var file in candidates.Values)
        {
            ct.ThrowIfCancellationRequested();
            Try(() => File.Delete(file));
        }
    }

    private static void CompactVersion(string version, CancellationToken ct)
    {
        var marker = Path.Combine(version, "compact");
        if (File.Exists(marker)) return;
        var world = Path.Combine(version, "world.json");
        if (File.Exists(world))
        {
            Compress(world, world + ".gz");
            File.Delete(world);
        }
        var databases = Directory.EnumerateFiles(version, "*", Safe).Select(path =>
            path.EndsWith(".sqlite.gz", StringComparison.Ordinal) ? path[..^3] : path)
            .Where(path => path.EndsWith(".sqlite", StringComparison.Ordinal)).Distinct().ToArray();
        foreach (var path in databases)
        {
            ct.ThrowIfCancellationRequested();
            var temporary = path + "." + JsonValues.NewId() + ".tmp";
            try
            {
                using (var input = Open(path))
                using (var output = File.Create(temporary)) input.CopyTo(output);
                using (var db = MemoryDb.OpenScratch(temporary))
                    if (db.HasEntriesTable()) Capture(db, path);
                    else Compress(temporary, path + ".gz");
                File.Delete(path);
            }
            finally { File.Delete(temporary); }
        }
        MarkCompact(version);
    }

    internal static void MarkCompact(string version) => AtomicFile.Write(Path.Combine(version, "compact"), "1");

    private static void PruneVersions(string root, DateTime old, CancellationToken ct)
    {
        var unused = Directories(root, "versions").Where(VersionName)
            .Where(folder => Directory.GetLastWriteTimeUtc(folder) < old)
            .ToDictionary(folder => Path.GetFileName(folder), StringComparer.Ordinal);
        if (unused.Count == 0) return;
        void Keep(Stream stream)
        {
            ct.ThrowIfCancellationRequested();
            if (JsonNode.Parse(stream) is not JsonObject room) throw new InvalidDataException("Invalid room record.");
            foreach (var block in room.GetArray("blocks"))
                unused.Remove(block.GetString("version"));
        }
        void KeepFile(string file)
        {
            using var input = File.OpenRead(file);
            Keep(input);
        }
        foreach (var preset in Directories(root, "conversations"))
            foreach (var file in Directory.EnumerateFiles(preset, "*.json", Safe))
                KeepFile(file);
        var trash = Path.Combine(root, "trash");
        if (Directory.Exists(trash))
        {
            foreach (var folder in Directories(root, "trash").Where(VersionName))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.json", Safe).Where(file => Path.GetFileName(file) != "preset.json"))
                    KeepFile(file);
                var rooms = Path.Combine(folder, "conversations");
                if (Directory.Exists(rooms))
                    foreach (var file in Directory.EnumerateFiles(rooms, "*.json", Safe)) KeepFile(file);
            }
            foreach (var file in Directory.EnumerateFiles(trash, "*.zip", Safe).Where(file => VersionName(file[..^4])))
            {
                using var archive = ZipFile.OpenRead(file);
                foreach (var entry in archive.Entries.Where(entry => entry.Name.EndsWith(".json", StringComparison.Ordinal) &&
                    entry.Name != "preset.json" && (entry.Name == entry.FullName ||
                    entry.FullName == "conversations/" + entry.Name)))
                {
                    using var input = entry.Open();
                    Keep(input);
                }
            }
        }
        foreach (var folder in unused.Values)
        {
            ct.ThrowIfCancellationRequested();
            Try(() => DeleteDirectory(folder));
        }
    }

    private static void PackTrash(string folder)
    {
        if (HasLinks(folder)) return;
        var archive = folder + ".zip";
        if (File.Exists(archive)) return;
        var temporary = archive + ".tmp";
        try
        {
            File.Delete(temporary);
            ZipFile.CreateFromDirectory(folder, temporary, CompressionLevel.Fastest, includeBaseDirectory: false);
            File.Move(temporary, archive);
            DeleteDirectory(folder);
        }
        finally { File.Delete(temporary); }
    }

    private static void DeleteDirectory(string folder)
    {
        if (!HasLinks(folder)) Directory.Delete(folder, recursive: true);
    }

    private static bool HasLinks(string folder)
    {
        foreach (var file in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) && HasLinks(file))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> Directories(string root, string name)
    {
        var directory = Path.Combine(root, name);
        return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory, "*", Safe) : [];
    }

    private static bool VersionName(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.Length == 12 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void Compress(string source, string target)
        => WriteCompressed(target, output =>
        {
            using var input = File.OpenRead(source);
            input.CopyTo(output);
        });

    private static void WriteCompressed(string target, Action<Stream> write)
    {
        var temporary = target + "." + JsonValues.NewId() + ".tmp";
        try
        {
            using (var output = new GZipStream(File.Create(temporary), CompressionLevel.Fastest))
                write(output);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }
}
