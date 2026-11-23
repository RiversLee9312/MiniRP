using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    internal sealed record AssetContent(string MimeType, long? Length, Func<CancellationToken, IAsyncEnumerable<byte[]>> Chunks,
        string Generation = "", string Name = "", string Text = "", bool Writable = false);

    private readonly AssetReaders hostAssets;
    private static string AssetUrl(PluginPackage package, string id) => $"{PluginUrls.Scheme}://assets/{package.PackageName}/{id}";

    internal static (string Package, string Id) ParseAssetUrl(string url)
    {
        if (PluginUrls.Route(url, "assets").Split('/') is not [var package, var id] ||
            package.Length is < 1 or > 150 || !package.All(character => char.IsAsciiLetterLower(character) ||
                char.IsAsciiDigit(character) || character is '.' or '_'))
            throw new ArgumentException("Invalid Asset URL.");
        var owner = PluginUrls.CanonicalPackage(package);
        if (owner == "minirp.licenses" && id is "dependencies" or "project-license") owner = "minirp.base";
        return (owner, PluginPackage.Identifier(id));
    }

    private AssetContent OpenAsset(string url, PluginPackage? caller, JsonObject options, JsonObject environment, JsonObject? attachment = null)
    {
        var (name, id) = ParseAssetUrl(url);
        var package = Volatile.Read(ref packages).FirstOrDefault(item => item.PackageName == name)
            ?? throw new FileNotFoundException("Asset package is unavailable.");
        var preset = environment.GetString("preset");
        RequireScope(package, preset);
        var asset = package.Assets?.GetValueOrDefault(id) ?? throw new FileNotFoundException("Asset is unavailable.");
        if (caller is not null && (caller.PackageName != name || caller.Signer != package.Signer) &&
            asset.Accessibility != "public" && !(asset.Accessibility == "protected" && caller.Signed && caller.Signer == package.Signer))
            throw new UnauthorizedAccessException("The asset is private to its package or signer.");
        if (JsonValues.Serialize(options).Length > 8192) throw new InvalidDataException("Asset options exceed 8 KiB.");
        var writable = asset.Writable && WorkspaceIds.IsValid(preset);
        if (writable && WritableAssetPath(package, preset, id) is var path && File.Exists(path))
        {
            return new(asset.MimeType, new FileInfo(path).Length,
                token => ReadWritableAsset(package, preset, path, token), package.Generation, Writable: true);
        }
        if (asset.Type == "file")
        {
            if (package.BuiltIn)
                return new(asset.MimeType, null, token => ReadResource(package.Resources![asset.Path], token), Writable: writable);
            return new(asset.MimeType, null, token => ReadScopedArchive(package, preset, asset.Path, token), package.Generation, Writable: writable);
        }
        var next = (JsonObject)environment.DeepClone();
        var depth = next.GetInt32("_asset_depth");
        if (depth >= 8) throw new InvalidOperationException("Asset reference nesting is too deep.");
        next["_asset_depth"] = depth + 1;
        var request = new JsonObject { ["name"] = "asset.requested", ["url"] = url,
            ["request"] = options.DeepClone(), ["sender"] = caller?.PackageName };
        if (attachment is not null) request["attachment"] = attachment.DeepClone();
        return new(asset.MimeType, null, token => GenerateAsset(new(package, package.Bind(asset.Handler), request, next), token), package.Generation, Writable: writable);
    }

    public JsonObject OpenHostAsset(string url, JsonObject environment, JsonObject? attachment)
    {
        var address = url.Split('?', 2);
        if (address.Length > 1 && attachment is null)
            throw new UnauthorizedAccessException("This attachment is unavailable.");
        if (attachment?["thumbnail"] is JsonObject legacy && legacy.GetString("base64") is { Length: > 0 and <= 180000 } data &&
            legacy.GetString("mime") is "image/png" or "image/jpeg" or "image/webp")
        {
            var bytes = Convert.FromBase64String(data);
            return hostAssets.Open(new(legacy.GetString("mime"), bytes.Length, token => ReadBytes(bytes, token)));
        }
        lock (packageGate) return hostAssets.Open(OpenAsset(address[0], null, new(), environment, attachment));
    }

    private static bool IsBackgroundMime(string mime) => mime.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/webp" or
        "image/gif" or "image/avif" or "image/heic" or "image/heif" or "video/mp4" or "video/webm" or "video/3gpp";

    private static bool OwnsPresetBackgrounds(PluginPackage package, string preset) =>
        preset.Length > 0 && package.Signed && !package.BuiltIn && package.PresetId == preset;

    internal JsonArray PresetBackgroundAssets(string preset)
    {
        var result = new JsonArray();
        foreach (var package in Volatile.Read(ref packages))
        {
            if (!OwnsPresetBackgrounds(package, preset) || !Available(package, preset) || package.Assets is null) continue;
            foreach (var asset in package.Assets.Values.Where(asset => IsBackgroundMime(asset.MimeType)))
                result.AddNode(new JsonObject { ["url"] = AssetUrl(package, asset.Id), ["mime"] = asset.MimeType, ["plugin"] = package.Name });
        }
        return result;
    }

    internal void RequirePresetBackground(string url, string preset)
    {
        var (name, id) = ParseAssetUrl(url);
        var package = Volatile.Read(ref packages).FirstOrDefault(item => item.PackageName == name && OwnsPresetBackgrounds(item, preset))
            ?? throw new UnauthorizedAccessException("The background must be declared by an installed plugin owned by this preset.");
        RequireScope(package, preset);
        if (package.Assets?.GetValueOrDefault(id) is not { } asset || !IsBackgroundMime(asset.MimeType))
            throw new ArgumentException("Choose a declared image, GIF or video Asset.");
    }

    internal JsonObject OpenPresetBackground(string url, string preset)
    {
        lock (packageGate)
        {
            RequirePresetBackground(url, preset);
            return hostAssets.Open(OpenAsset(url, null, new(), new() { ["preset"] = preset, ["agent"] = "chat" }));
        }
    }

    public async Task<JsonObject> PullHostAsset(string id, CancellationToken ct) =>
        await hostAssets.Pull(id, ct) is { } bytes ? new() { ["transfer"] = port.StageBinary(bytes) } : new() { ["done"] = true };

    public ValueTask CloseHostAsset(string id) => hostAssets.Close(id);

    internal JsonObject OpenDisplayAsset(AssetContent content, string url) =>
        hostAssets.Open(content with { Name = content.Name.Length > 0 ? content.Name : url[(url.LastIndexOf('/') + 1)..] });

    internal AssetContent OpenAgentAsset(string url, string publisher, string signer, JsonObject environment, JsonObject? options = null)
    {
        var packageName = PluginUrls.CanonicalPackage(publisher);
        var package = Volatile.Read(ref packages).FirstOrDefault(item => item.PackageName == packageName && item.Signer == signer)
            ?? throw new UnauthorizedAccessException("The skill that supplied this asset is unavailable.");
        RequireScope(package, environment.GetString("preset"));
        return OpenAsset(url, package, options ?? new(), environment);
    }

    internal AssetContent OpenAttachmentAsset(JsonObject attachment)
    {
        var file = new MessageAttachments(files).FileFor(attachment);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("The workspace Asset is unavailable.");
        return new(MessageAttachments.Mime(attachment), file.Length, token => ReadFile(file.FullName, token),
            Name: attachment.GetString("name"), Text: attachment.GetString("text"));
    }

    internal static AssetContent OpenSample(string path, string mime, string name) =>
        new(mime, new FileInfo(path).Length, token => ReadFile(path, token), Name: name);

    internal static JsonObject Describe(string url, AssetContent asset)
    {
        var route = PluginUrls.Route(url, "assets").Split('/');
        return new()
        {
            ["url"] = url, ["package"] = route[0], ["id"] = route[1],
            ["source"] = AgentAssetStorage.IsUrl(url) ? "agent_file" : MessageAttachments.IsAttachmentUrl(url) ? "workspace_file" : "plugin",
            ["length"] = asset.Length, ["mime"] = asset.MimeType.Split(';', 2)[0].Trim().ToLowerInvariant(),
            ["name"] = asset.Name.Length == 0 ? null : JsonValue.Create(asset.Name), ["writable"] = asset.Writable
        };
    }

    private async IAsyncEnumerable<byte[]> GenerateAsset(Invocation request, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var output in RunScript(request, binary: true, ct))
            if (output.Bytes is { } bytes) yield return bytes;
    }

    private async IAsyncEnumerable<byte[]> ReadScopedArchive(PluginPackage package, string preset, string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        RequireScope(package, preset);
        await foreach (var bytes in ReadArchive(package, path, ct))
        {
            RequireScope(package, preset);
            yield return bytes;
        }
    }

    private static async IAsyncEnumerable<byte[]> ReadArchive(PluginPackage package, string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var file = new FileStream(package.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, useAsync: true);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry(path) ?? throw new FileNotFoundException("Asset file is unavailable.");
        if (entry.Length > PluginPackage.MaxPackageBytes || PluginSignature.Hash(entry) != package.Hashes![path])
            throw new IOException("Asset file changed after signature verification.");
        await using var stream = entry.Open();
        await foreach (var bytes in ReadChunks(stream, ct)) yield return bytes;
    }

    private static async IAsyncEnumerable<byte[]> ReadResource(string name, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var source = typeof(PluginRuntime).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("Embedded asset is unavailable.");
        await foreach (var bytes in ReadChunks(source, ct)) yield return bytes;
    }

    private static async IAsyncEnumerable<byte[]> ReadFile(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var source = File.OpenRead(path);
        await foreach (var bytes in ReadChunks(source, ct)) yield return bytes;
    }

    internal static async IAsyncEnumerable<byte[]> ReadBytes(byte[] content, [EnumeratorCancellation] CancellationToken ct)
    {
        using var source = new MemoryStream(content);
        await foreach (var bytes in ReadChunks(source, ct)) yield return bytes;
    }

    private static async IAsyncEnumerable<byte[]> ReadChunks(Stream input, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var buffer = new byte[HostPort.ChunkBytes];
            var count = await input.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
            if (count == 0) yield break;
            yield return count == buffer.Length ? buffer : buffer[..count];
        }
    }

    private sealed class AssetReaders(TimeProvider clock) : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly Dictionary<string, Reader> readers = new(StringComparer.Ordinal);
        private bool disposed;

        public JsonObject Open(AssetContent content)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (readers.Count >= 16) throw new InvalidOperationException("Too many open asset streams.");
                var id = JsonValues.NewId();
                var reader = new Reader(content);
                readers.Add(id, reader);
                _ = Expire(id, reader.Token);
                return new() { ["handle"] = id, ["mimeType"] = content.MimeType, ["length"] = content.Length, ["name"] = content.Name };
            }
        }

        private async Task Expire(string id, CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), clock, token);
                await Close(id);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public async Task<byte[]?> Pull(string id, CancellationToken ct)
        {
            Reader reader;
            lock (gate) reader = readers.GetValueOrDefault(id) ?? throw new IOException("Asset stream is closed.");
            try
            {
                if (await reader.Pull(ct) is { } bytes) return bytes;
                await Close(id);
                return null;
            }
            catch { await Close(id); throw; }
        }

        public AssetContent Content(string id)
        {
            lock (gate) return readers.GetValueOrDefault(id)?.Content ?? throw new IOException("Asset stream is closed.");
        }

        public ValueTask Close(string id)
        {
            Reader? reader;
            lock (gate) readers.Remove(id, out reader);
            return reader?.DisposeAsync() ?? ValueTask.CompletedTask;
        }

        public Task Revoke(string generation)
        {
            KeyValuePair<string, Reader>[] revoked;
            lock (gate)
            {
                revoked = readers.Where(item => item.Value.Generation == generation).ToArray();
                foreach (var item in revoked) readers.Remove(item.Key);
            }
            return Task.WhenAll(revoked.Select(item => item.Value.DisposeAsync().AsTask()));
        }

        public async ValueTask DisposeAsync()
        {
            Reader[] remaining;
            lock (gate)
            {
                disposed = true;
                remaining = readers.Values.ToArray();
                readers.Clear();
            }
            await Task.WhenAll(remaining.Select(reader => reader.DisposeAsync().AsTask()));
        }

        private sealed class Reader(AssetContent content) : IAsyncDisposable
        {
            private readonly SemaphoreSlim gate = new(1);
            private readonly CancellationTokenSource lifetime = new();
            private IAsyncEnumerator<byte[]>? stream;
            private bool disposed;
            public string Generation => content.Generation;
            public AssetContent Content => content;
            public CancellationToken Token => lifetime.Token;

            public async Task<byte[]?> Pull(CancellationToken token)
            {
                await gate.WaitAsync(token);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    lifetime.Token.ThrowIfCancellationRequested();
                    using var cancellation = token.Register(lifetime.Cancel);
                    stream ??= content.Chunks(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
                    return await stream.MoveNextAsync() ? stream.Current : null;
                }
                finally { gate.Release(); }
            }

            public async ValueTask DisposeAsync()
            {
                await lifetime.CancelAsync();
                await gate.WaitAsync();
                try
                {
                    disposed = true;
                    if (stream is not null) await stream.DisposeAsync();
                }
                finally { gate.Release(); lifetime.Dispose(); }
            }
        }
    }
}
