using System.Text;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    private readonly SemaphoreSlim[] assetWriters = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1)).ToArray();
    internal const string WritableAssetArea = "writable-assets";

    private string WritableAssetPath(PluginPackage package, string preset, string id) =>
        Path.Combine(root, WritableAssetArea, package.StorageKey, WorkspaceIds.Require(preset), PluginPackage.Identifier(id));

    private async IAsyncEnumerable<byte[]> ReadWritableAsset(PluginPackage package, string preset, string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        RequireScope(package, preset);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, useAsync: true);
        await foreach (var chunk in ReadChunks(source, ct))
        {
            RequireScope(package, preset);
            yield return chunk;
        }
    }

    internal Task<JsonObject> EditAgentAsset(string url, string publisher, string signer, JsonObject environment,
        string method, JsonObject args, CancellationToken ct)
    {
        var (package, preset, id) = ResolveAssetWrite(url, publisher, signer, environment);
        return WriteAsset(url, package, preset, id, async (output, token) =>
        {
            var asset = OpenAsset(url, package, new(), environment);
            await using var input = new AssetStream(asset, token);
            return await ApplyAssetEdit(method, input, output, args, token);
        }, ct);
    }

    internal static Task<int> ApplyAssetEdit(string method, Stream input, Stream output, JsonObject args, CancellationToken ct) =>
        method == "binPatch" ? PatchBytes(input, output, args, ct) : EditText(input, output, args, ct);

    internal Func<Stream, CancellationToken, Task<JsonObject>> PrepareAssetWrite(
        string url, string publisher, string signer, JsonObject environment)
    {
        var (package, preset, id) = ResolveAssetWrite(url, publisher, signer, environment);
        return (input, ct) => WriteAsset(url, package, preset, id, async (output, token) =>
        {
            await input.CopyToAsync(output, token);
            return 1;
        }, ct);
    }

    private (PluginPackage Package, string Preset, string Id) ResolveAssetWrite(
        string url, string publisher, string signer, JsonObject environment)
    {
        var (name, id) = ParseAssetUrl(url);
        if (name != PluginUrls.CanonicalPackage(publisher)) throw new UnauthorizedAccessException("Only the Asset owner can grant edits.");
        var package = Volatile.Read(ref packages).FirstOrDefault(item => item.PackageName == name && item.Signer == signer)
            ?? throw new UnauthorizedAccessException("The Asset owner is unavailable.");
        var preset = WorkspaceIds.Require(environment.GetString("preset"));
        RequireScope(package, preset);
        if (package.Assets?.GetValueOrDefault(id)?.Writable != true)
            throw new UnauthorizedAccessException("This Asset is read-only.");
        return (package, preset, id);
    }

    private async Task<JsonObject> WriteAsset(string url, PluginPackage package, string preset, string id,
        Func<Stream, CancellationToken, Task<int>> write, CancellationToken ct)
    {
        var path = WritableAssetPath(package, preset, id);
        var writer = assetWriters[StringComparer.Ordinal.GetHashCode(path) & (assetWriters.Length - 1)];
        await writer.WaitAsync(ct);
        try
        {
            RequireScope(package, preset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var staging = path + "." + JsonValues.NewId() + ".tmp";
            try
            {
                int changes;
                long length;
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    changes = await write(output, ct);
                    length = output.Length;
                    if (length > MessageAttachments.MaxAssetBytes) throw new IOException("The edited Asset is too large.");
                    await output.FlushAsync(ct);
                }
                ct.ThrowIfCancellationRequested();
                WhileCurrent(package, preset, () => { File.Move(staging, path, overwrite: true); return true; });
                return new() { ["url"] = url, ["changes"] = changes, ["size"] = length };
            }
            finally { File.Delete(staging); }
        }
        finally { writer.Release(); }
    }

    private static async Task<int> PatchBytes(Stream input, Stream output, JsonObject args, CancellationToken ct)
    {
        var offset = args.GetInt32("offset", -1);
        if (offset < 0) throw new ArgumentException("Provide a nonnegative byte offset.");
        byte[] Hex(string key)
        {
            if (args[key] is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || text.Length > 48000)
                throw new ArgumentException("Provide old_hex and new_hex as HEX strings up to 48,000 characters.");
            return text.Length == 0 ? [] : AssetGrep.ParseHex(text);
        }
        var expected = Hex("old_hex");
        var replacement = Hex("new_hex");
        if (expected.AsSpan().SequenceEqual(replacement)) throw new ArgumentException("The patch makes no changes.");
        var buffer = new byte[65536];
        while (offset > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, offset)), ct);
            if (count == 0) throw new ArgumentException("The byte offset is outside this Asset.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            offset -= count;
        }
        var current = new byte[expected.Length];
        await input.ReadExactlyAsync(current, ct);
        if (!current.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("The bytes at offset do not match old_hex.");
        await output.WriteAsync(replacement, ct);
        await input.CopyToAsync(output, ct);
        return 1;
    }

    private static async Task<int> EditText(Stream input, Stream output, JsonObject args, CancellationToken ct)
    {
        if (args["old_string"] is not JsonValue before || !before.TryGetValue<string>(out var old) || old.Length == 0 ||
            args["new_string"] is not JsonValue after || !after.TryGetValue<string>(out var replacement) || old == replacement)
            throw new ArgumentException("Provide different old_string and new_string values.");
        const int max = 8 * 1024 * 1024;
        using var source = new MemoryStream();
        var buffer = new byte[65536];
        while (await input.ReadAsync(buffer.AsMemory(), ct) is > 0 and var count)
        {
            if (source.Length + count > max) throw new IOException("Use bin_patch for Assets larger than 8 MiB.");
            source.Write(buffer, 0, count);
        }
        var bytes = source.GetBuffer().AsSpan(0, (int)source.Length);
        var (encoding, bom) = bytes.StartsWith("\uFEFF"u8) ? (new UTF8Encoding(false, true), 3) :
            bytes.StartsWith(new byte[] { 0xff, 0xfe }) ? ((Encoding)new UnicodeEncoding(false, false, true), 2) :
            bytes.StartsWith(new byte[] { 0xfe, 0xff }) ? (new UnicodeEncoding(true, false, true), 2) :
            (new UTF8Encoding(false, true), 0);
        var text = encoding.GetString(bytes[bom..]);
        var changes = 0;
        for (var next = 0; ; changes++)
        {
            var index = text.IndexOf(old, next, StringComparison.Ordinal);
            if (index < 0) break;
            next = index + old.Length;
            if ((long)text.Length + (changes + 1) * (long)(replacement.Length - old.Length) > max)
                throw new IOException("The text edit would exceed 8 MiB.");
        }
        if (changes == 0 || changes > 1 && !args.GetBoolean("replace_all"))
            throw new InvalidOperationException(changes == 0 ? "old_string was not found." : "old_string is ambiguous. Add context or use replace_all.");
        var edited = encoding.GetBytes(text.Replace(old, replacement, StringComparison.Ordinal));
        if (edited.Length + bom > max) throw new IOException("The text edit would exceed 8 MiB.");
        await output.WriteAsync(source.GetBuffer().AsMemory(0, bom), ct);
        await output.WriteAsync(edited, ct);
        return changes;
    }
}
