using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Digests;

namespace MiniRP.Core;

internal sealed partial class AgentContainer(AgentContainer.Rooms rooms, AgentAssets assets, MemoryDb db, string preset, string room,
    string? presetDatabase = null)
{
    internal const string StateKey = "_context/container";
    internal const string Storage = "containers";
    internal const string JobPrefix = "_context/shell-job/";
    private const int HeaderSize = 64 * 1024;
    private const int Chunk = 4 * 1024 * 1024;
    private const int DiskSize = 512 * 1024 * 1024;
    private static readonly ConcurrentDictionary<(string Database, string Key), byte> Jobs = new();
    private static readonly ConcurrentDictionary<string, byte> ActiveRuns = new();
    internal static bool ActiveRun(string directory) => ActiveRuns.ContainsKey(directory);

    public AgentSkill Skill() => new("container", "Run commands in a portable x86-64 Alpine Linux workspace.",
        new JsonArray(
            Tools.Define("shell", "Run a Bash command. Long output returns an Asset URL. Use async to get an output Asset URL and a watchable DB key immediately.", new()
            {
                ["command"] = Tools.String(), ["seconds"] = Tools.Integer(),
                ["memory_mib"] = Tools.Integer(), ["async"] = Tools.Boolean()
            }, "command"),
            Tools.Define("export_asset", "Copy a session Asset URL to a guest file.", AssetArguments(), "url", "path"),
            Tools.Define("import_asset", "Copy a guest file into an existing writable session Asset.", AssetArguments(), "url", "path")),
        Execute, Instructions: "Commands start in /workspace, which is saved with this " + (presetDatabase is null ? "room. " : "preset. ") +
        "Background processes keep running while this workspace is open. Redirect background output to a /workspace file to read it later. " +
        "The image includes apk, Bash, curl, Node.js, npm, Python 3, pip, Git, jq, rg, sqlite3, pdftotext, unzip, zstd and GNU diff. " +
        "Use db_watch and db_read with an async shell's db_key to track running, failed or success. Read output Assets with the assets skill. " +
        "Set seconds only when a command needs a deadline. export_asset writes a guest file and import_asset replaces a writable Asset.");

    private static JsonObject AssetArguments() => new()
    {
        ["url"] = Tools.String("An Asset URL supplied in this session."),
        ["path"] = Tools.String("A Linux file path. Relative paths start in /workspace.")
    };

    private async Task<JsonNode?> Execute(string tool, JsonObject arguments, CancellationToken ct)
    {
        var command = arguments.GetString("command");
        var path = arguments.GetString("path");
        var url = arguments.GetString("url");
        if (tool == "shell" ? command.Length is 0 or > 12000 : path.Length is 0 or > 4096 || path.Contains('\0'))
            throw new ArgumentException(tool == "shell" ? "Provide a Bash command up to 12,000 characters." : "Provide a Linux file path.");
        var seconds = arguments.GetInt32("seconds");
        var memory = Math.Clamp(arguments.GetInt32("memory_mib", 2048), 256, 2048);
        if (tool == "shell" && arguments.GetBoolean("async")) return await StartShell(command, seconds, memory, ct);
        return await Run(tool, command, path, seconds, memory, assets.Output, null, ct,
            tool == "export_asset" ? assets.Open(url, new()) : null,
            tool == "import_asset" ? assets.PrepareWrite(url) : null);
    }

    private async Task<JsonObject> StartShell(string command, int seconds, int memory, CancellationToken ct)
    {
        var output = await assets.Output.Store("", "shell-output.txt", "text/plain", null, ct);
        var url = output.GetString("asset_url");
        var key = JobPrefix + JsonValues.NewId();
        var connection = MemoryDb.OpenScratch(db.Path);
        Jobs[(db.Path, key)] = 0;
        var status = new JsonObject { ["db_key"] = key, ["asset_url"] = url, ["status"] = "running" };
        try { db.Write(key, JsonValues.Serialize(status), internalWrite: true); }
        catch
        {
            Jobs.TryRemove((db.Path, key), out _);
            connection.Dispose();
            throw;
        }
        _ = Task.Run(() => CompleteShell(command, seconds, memory, connection, key, url));
        return status;
    }

    private async Task CompleteShell(string command, int seconds, int memory, MemoryDb connection, string key, string url)
    {
        using (connection)
        {
            var status = new JsonObject { ["asset_url"] = url, ["status"] = "failed" };
            try
            {
                using var assetConnection = assets.DatabasePath == connection.Path ? null : MemoryDb.OpenScratch(assets.DatabasePath);
                var result = await Run("shell", command, "", seconds, memory,
                    assets.Output.WithDatabase(assetConnection ?? connection), url, rooms.Lifetime);
                if (result["exit_code"] is { } code) status["exit_code"] = code.DeepClone();
                if (result["error"] is { } error) status["error"] = error.DeepClone();
                else if (result.GetInt32("exit_code", -1) == 0) status["status"] = "success";
            }
            catch (OperationCanceledException) when (rooms.Lifetime.IsCancellationRequested)
            { status["error"] = "The shell job was interrupted before it finished."; }
            catch (Exception error) { status["error"] = error.Message; }
            ToolDiagnostics.AnnotateResult("shell", "container", status);
            try { connection.Write(key, JsonValues.Serialize(status), internalWrite: true); }
            catch (InvalidOperationException error) { System.Diagnostics.Trace.TraceError("Could not update shell job {0}: {1}", key, error.Message); }
            finally { Jobs.TryRemove((db.Path, key), out _); }
        }
    }

    internal static bool IsJobKey(string key) => key.StartsWith(JobPrefix, StringComparison.Ordinal);

    internal static void RecoverJobs(MemoryDb db)
    {
        foreach (var row in db.Query("SELECT key, content FROM entries WHERE key GLOB '_context/shell-job/*'"))
        {
            var key = row["key"];
            if (Jobs.ContainsKey((db.Path, key))) continue;
            var status = JsonValues.ParseObject(db.Get(key) ?? "{}");
            if (status.GetString("status") != "running") continue;
            status["status"] = "failed";
            status["error"] = "The shell job was interrupted before it finished.";
            ToolDiagnostics.AnnotateResult("shell", "container", status);
            db.Write(key, JsonValues.Serialize(status), internalWrite: true);
        }
    }

    private Task<JsonObject> Run(string tool, string command, string path, int seconds, int memory,
        AgentToolOutput output, string? outputUrl, CancellationToken ct,
        PluginRuntime.AssetContent? source = null, Func<Stream, CancellationToken, Task<JsonObject>>? write = null)
        => rooms.Use(preset, room, presetDatabase, async vm =>
        {
            var request = new JsonObject { ["op"] = tool, ["command"] = command, ["path"] = path,
                ["seconds"] = seconds, ["memory_mib"] = memory, ["size"] = 0, ["asset_output"] = outputUrl is not null };
            var response = await vm.Command(request, source, ct);
            var error = response.GetString("error");
            if (error.Length > 0 && (tool != "shell" || !response.ContainsKey("exit_code"))) throw new IOException(error);
            if (write is not null)
                return await WriteAsset(vm, response.GetInt32("size", -1), write, ct);
            response.Remove("id");
            response.Remove("frozen");
            if (source is not null) response["path"] = path;
            if (tool == "shell" && (response.ContainsKey("size") || outputUrl is not null))
            {
                JsonObject stored;
                if (response.ContainsKey("size"))
                {
                    var size = response.GetInt32("size", -1);
                    if (size < 0 || size > MessageAttachments.MaxAssetBytes) throw new IOException("Invalid shell output size.");
                    await using var result = vm.ReadOutput();
                    stored = await output.StoreStream("shell-output.txt", "text/plain", outputUrl,
                        (destination, token) => CopyExactly(result, destination, size, token), ct);
                }
                else stored = await output.Store(response.GetString("output"), "shell-output.txt", "text/plain", outputUrl, ct);
                response.Remove("output");
                foreach (var (field, value) in stored) response[field] = JsonValues.Clone(value);
            }
            return response;
        }, ct);

    private static async Task<JsonObject> WriteAsset(RoomVm vm, int size, Func<Stream, CancellationToken, Task<JsonObject>> write, CancellationToken ct)
    {
        if (size < 0 || size > MessageAttachments.MaxAssetBytes) throw new IOException("Invalid guest file size.");
        await using var result = vm.ReadOutput();
        await using var contents = File.Create(Path.Combine(vm.DirectoryPath, "out"), 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await CopyExactly(result, contents, size, ct);
        contents.Position = 0;
        return await write(contents, ct);
    }

    private static JsonArray QemuArguments(bool microVm, bool initialize)
    {
        string Block(string drive, int address) => microVm
            ? $"virtio-blk-device,drive=minirp-{drive},serial=minirp-{drive}"
            : $"virtio-blk-pci,drive=minirp-{drive},addr=0x{address:x}";
        var boot = "console=ttyS0 rdinit=/init quiet panic=-1";
        if (microVm) boot += " reboot=t minirp.microvm=1";
        if (initialize) boot += " minirp.initialize=1";
        return new(
            "-machine", microVm ? "microvm,acpi=off,x-option-roms=off" : "q35", "-cpu", "qemu64",
            "-m", "2048", "-object", "memory-backend-ram,id=room-ram,size=2G,prealloc=off",
            "-machine", "memory-backend=room-ram",
            "-smp", "2", "-nodefaults", "-no-user-config", "-display", "none", "-monitor", "none", "-no-reboot",
            "-serial", "file:console.log", "-kernel", "{kernel}", "-initrd", "{initrd}",
            "-append", boot,
            "-drive", "file={rootfs},if=none,id=minirp-root,format=raw,readonly=on",
            "-device", Block("root", 5),
            "-drive", "file=workspace.raw,if=none,id=minirp-workspace,format=raw",
            "-device", Block("workspace", 6),
            "-drive", "file=mailbox.raw,if=none,id=minirp-mailbox,format=raw",
            "-device", Block("mailbox", 7),
            "-netdev", "user,id=outbound", "-device",
            microVm ? "virtio-net-device,netdev=outbound" : "virtio-net-pci,netdev=outbound,addr=0x8,romfile=");
    }

    private static async Task Restore(string root, string preset, string path, JsonObject? previous, CancellationToken ct)
    {
        await using var disk = File.Create(path, 65536, FileOptions.Asynchronous);
        disk.SetLength(DiskSize);
        if (previous is null) return;
        var index = 0;
        var bytes = new byte[Chunk];
        foreach (var hash in References(previous))
        {
            if (hash.Length > 0)
            {
                await ReadChunk(Blob(root, preset, hash), hash, bytes, ct);
                disk.Position = (long)index * Chunk;
                await disk.WriteAsync(bytes, ct);
            }
            index++;
        }
    }

    private static async Task<JsonArray> Checkpoint(string root, string preset, string path, JsonObject? previous, CancellationToken ct)
    {
        var previousChunks = previous is null ? null : References(previous);
        var result = new JsonArray();
        var bytes = new byte[Chunk];
        await using var disk = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        if (disk.Length != DiskSize) throw new IOException("The workspace disk size changed.");
        for (var index = 0; index < DiskSize / Chunk; index++)
        {
            await disk.ReadExactlyAsync(bytes, ct);
            var hash = bytes.AsSpan().ContainsAnyExcept((byte)0) ? Hash(bytes) : "";
            result.AddNode(JsonValue.Create(hash));
            if (hash.Length == 0 || previousChunks?[index] == hash) continue;
            var target = Blob(root, preset, hash);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + "." + JsonValues.NewId() + ".tmp";
            try
            {
                await using (var file = File.Create(temporary, 65536, FileOptions.Asynchronous))
                await using (var encoded = new GZipStream(file, CompressionLevel.Fastest))
                    await encoded.WriteAsync(bytes, ct);
                File.Move(temporary, target, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        return result;
    }

    internal static string[] References(JsonObject state)
    {
        var chunks = state.GetArray("chunks").Select(value => value is JsonValue scalar &&
            scalar.TryGetValue<string>(out var hash) && (hash.Length == 0 || IsHash(hash))
            ? hash : throw new InvalidDataException("Invalid container disk extent.")).ToArray();
        if (state.GetInt32("version", 1) != 1 || !IsHash(state.GetString("image")) || chunks.Length != DiskSize / Chunk)
            throw new InvalidDataException("Invalid container checkpoint.");
        return chunks;
    }

    internal static string Blob(string root, string preset, string hash) => Path.Combine(root, Storage, WorkspaceIds.Require(preset), hash);
    internal static bool IsHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal const int MaxChunkFileBytes = Chunk + 65536;
    internal static Task ValidateChunk(string path, string hash, CancellationToken ct) => ReadChunk(path, hash, new byte[Chunk], ct);

    private static async Task ReadChunk(string path, string hash, byte[] bytes, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        if (file.Length > MaxChunkFileBytes) throw new InvalidDataException("A container checkpoint is too large.");
        using var decoded = new GZipStream(file, CompressionMode.Decompress);
        await decoded.ReadExactlyAsync(bytes, ct);
        if (decoded.ReadByte() != -1 || Hash(bytes) != hash)
            throw new InvalidDataException("A container checkpoint failed verification.");
    }
    private static string Hash(byte[] bytes)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(bytes, 0, bytes.Length);
        var result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result, 0);
        return Convert.ToHexStringLower(result);
    }

    private static async Task CopyExactly(Stream input, Stream output, long length, CancellationToken ct)
    {
        var bytes = new byte[65536];
        while (length > 0)
        {
            var count = (int)Math.Min(bytes.Length, length);
            await input.ReadExactlyAsync(bytes.AsMemory(0, count), ct);
            await output.WriteAsync(bytes.AsMemory(0, count), ct);
            length -= count;
        }
    }
}
