using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Minichat.Core;

internal sealed partial class PluginRuntime
{
    private sealed record AssetContent(string MimeType, long? Length, Func<CancellationToken, IAsyncEnumerable<byte[]>> Chunks);

    private readonly AssetReaders hostAssets = new();
    private static string AssetUrl(PluginPackage package, string id) => "minichat://assets/" + package.PackageName + "/" + id;

    private static (string Package, string Id) ParseAssetUrl(string url)
    {
        const string prefix = "minichat://assets/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal) || url[prefix.Length..].Split('/') is not [var package, var id] ||
            package.Length is < 1 or > 150 || !package.All(character => char.IsAsciiLetterLower(character) ||
                char.IsAsciiDigit(character) || character is '.' or '_'))
            throw new ArgumentException("Invalid Asset URL.");
        return (package, PluginPackage.Identifier(id));
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
        if (asset.Type == "file")
        {
            if (package.BuiltIn)
                return new(asset.MimeType, null, token => ReadResource(package.Resources![asset.Path], token));
            return new(asset.MimeType, null, token => ReadScopedArchive(package, preset, asset.Path, token));
        }
        var next = (JsonObject)environment.DeepClone();
        var depth = next.GetInt32("_asset_depth");
        if (depth >= 8) throw new InvalidOperationException("Asset reference nesting is too deep.");
        next["_asset_depth"] = depth + 1;
        var request = new JsonObject { ["name"] = "asset.requested", ["url"] = url,
            ["request"] = options.DeepClone(), ["sender"] = caller?.PackageName };
        if (attachment is not null) request["attachment"] = attachment.DeepClone();
        return new(asset.MimeType, null, token => GenerateAsset(new(package, package.Bind(asset.Handler), request, next), token));
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
        return hostAssets.Open(OpenAsset(address[0], null, new(), environment, attachment));
    }

    public async Task<JsonObject> PullHostAsset(string id, CancellationToken ct) =>
        await hostAssets.Pull(id, ct) is { } bytes ? new() { ["transfer"] = port.StageBinary(bytes) } : new() { ["done"] = true };

    public ValueTask CloseHostAsset(string id) => hostAssets.Close(id);

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

    private static async IAsyncEnumerable<byte[]> ReadBytes(byte[] content, [EnumeratorCancellation] CancellationToken ct)
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

    private sealed class AssetReaders : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, Reader> readers = new();

        public JsonObject Open(AssetContent content)
        {
            if (readers.Count >= 16) throw new InvalidOperationException("Too many open asset streams.");
            var id = JsonValues.NewId();
            if (!readers.TryAdd(id, new(content))) throw new InvalidOperationException("Asset stream already exists.");
            return new() { ["handle"] = id, ["mimeType"] = content.MimeType, ["length"] = content.Length };
        }

        public async Task<byte[]?> Pull(string id, CancellationToken ct)
        {
            if (!readers.TryGetValue(id, out var reader)) throw new IOException("Asset stream is closed.");
            try
            {
                if (await reader.Pull(ct) is { } bytes) return bytes;
                await Close(id);
                return null;
            }
            catch { await Close(id); throw; }
        }

        public async ValueTask Close(string id)
        {
            if (readers.TryRemove(id, out var reader)) await reader.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var id in readers.Keys) await Close(id);
        }

        private sealed class Reader(AssetContent content) : IAsyncDisposable
        {
            private readonly SemaphoreSlim gate = new(1);
            private readonly CancellationTokenSource lifetime = new(TimeSpan.FromMinutes(5));
            private IAsyncEnumerator<byte[]>? stream;
            private bool disposed;

            public async Task<byte[]?> Pull(CancellationToken token)
            {
                await gate.WaitAsync(token);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
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
