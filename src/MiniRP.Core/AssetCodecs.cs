using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class AssetCodecs(string directory, HostPort port) : IDisposable
{
    private readonly List<string> outputs = [];

    public Task<JsonObject> ReadText(PluginRuntime.AssetContent asset, JsonObject args, CancellationToken ct) =>
        WithText(asset, args, Page, ct);

    public Task<JsonObject> Grep(PluginRuntime.AssetContent asset, JsonObject args, CancellationToken ct) =>
        WithText(asset, args, AssetGrep.Text, ct);

    private static async Task<JsonObject> WithText(PluginRuntime.AssetContent asset, JsonObject args,
        Func<TextReader, JsonObject, CancellationToken, Task<JsonObject>> read, CancellationToken ct)
    {
        if (asset.Text.Length > 0)
        {
            using var text = new StringReader(asset.Text);
            return await read(text, args, ct);
        }
        var parts = asset.MimeType.Split(';', StringSplitOptions.TrimEntries);
        var mime = parts[0].ToLowerInvariant();
        if (!(mime.StartsWith("text/", StringComparison.Ordinal) || mime is "application/json" or "application/xml" ||
              mime.EndsWith("+json", StringComparison.Ordinal) || mime.EndsWith("+xml", StringComparison.Ordinal)))
            throw new ArgumentException("The Asset does not contain text.");
        var charset = parts.Skip(1).FirstOrDefault(part => part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase));
        var encoding = args.GetString("encoding", charset is null ? "utf-8" : charset[8..].Trim('"'));
        await using var stream = new AssetStream(asset, ct);
        using var reader = new StreamReader(stream, EncodingFor(encoding), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await read(reader, args, ct);
    }

    public static JsonArray EncodeText(JsonObject args)
    {
        var text = args.GetString("text");
        var encoding = EncodingFor(args.GetString("encoding", "utf-8"));
        if (encoding.GetByteCount(text) > HostPort.ChunkBytes) throw new ArgumentException("Text exceeds 64 KiB after encoding.");
        return new(encoding.GetBytes(text).Select(value => (JsonNode?)JsonValue.Create((int)value)).ToArray());
    }

    public static JsonValue DecodeText(JsonObject args)
    {
        var bytes = args.GetArray("bytes");
        if (bytes.Count > HostPort.ChunkBytes) throw new ArgumentException("Pass at most 64 KiB of bytes.");
        var input = bytes.Select(value => value is JsonValue scalar && scalar.TryGetValue<int>(out var number) && number is >= 0 and <= 255
            ? (byte)number : throw new ArgumentException("Text decoding requires bytes.")).ToArray();
        return JsonValue.Create(EncodingFor(args.GetString("encoding", "utf-8")).GetString(input))!;
    }

    private static Encoding EncodingFor(string encoding) => encoding.ToLowerInvariant() switch
    {
        "utf-8" => new UTF8Encoding(false, true),
        "utf-16le" => new UnicodeEncoding(false, false, true),
        "utf-16be" => new UnicodeEncoding(true, false, true),
        _ => throw new ArgumentException("Choose utf-8, utf-16le, or utf-16be."),
    };

    public async Task<(JsonObject Info, PluginRuntime.AssetContent? Result)> Media(
        string mode, PluginRuntime.AssetContent asset, JsonObject args, CancellationToken ct, int? imageMaxSide = null)
    {
        var mime = asset.MimeType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var valid = mode switch
        {
            "image_metadata" or "image_preview" or "image_decode" or "image_encode" or "pdf_create_page" => mime.StartsWith("image/", StringComparison.Ordinal),
            "pdf_page" or "pdf_metadata" => mime == "application/pdf",
            "video_frame" or "video_metadata" or "video_encode" => mime.StartsWith("video/", StringComparison.Ordinal),
            "audio_clip" => mime.StartsWith("audio/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal),
            _ => false
        };
        if (!valid) throw new ArgumentException("The codec does not accept this Asset type.");
        var format = args.GetString("format", "png");
        if (mode == "image_encode" && format is not ("png" or "jpeg" or "webp"))
            throw new ArgumentException("Choose png, jpeg, or webp.");
        var viewport = mode is "image_preview" or "pdf_page" or "video_frame" ? ImageViewport.Read(args) : null;
        var source = await Stage(directory, asset, "asset-source-", MessageAttachments.MaxAssetBytes, ct);
        var output = Path.Combine(directory, "asset-sample-" + JsonValues.NewId());
        var retain = false;
        try
        {
            var response = await port.CallHost("agent_asset_sample", new()
            {
                ["source"] = Path.GetFileName(source), ["output"] = Path.GetFileName(output), ["mode"] = mode,
                ["format"] = format, ["quality"] = Math.Clamp(args.GetInt32("quality", 90), 1, 100),
                ["max_side"] = imageMaxSide,
                ["page"] = Math.Max(1, args.GetInt32("page", 1)),
                ["start_ms"] = Math.Max(0, args.GetInt32("start_ms")),
                ["duration_ms"] = Math.Clamp(args.GetInt32("duration_ms", 10000), 100, 30000),
                ["viewport"] = viewport?.Describe(),
            }, ct, TimeSpan.FromSeconds(45));
            if (mode.EndsWith("_metadata", StringComparison.Ordinal)) return (response, null);
            var file = new FileInfo(output);
            if (!file.Exists || file.Length is 0 or > MessageAttachments.MaxAssetBytes)
                throw new IOException("The codec did not produce a usable resource.");
            var result = PluginRuntime.OpenSample(output, response.GetString("mime"), asset.Name);
            outputs.Add(output);
            retain = true;
            return (response, result);
        }
        finally
        {
            File.Delete(source);
            if (!retain) File.Delete(output);
        }
    }

    internal static async Task<string> Stage(string directory, PluginRuntime.AssetContent asset, string prefix, long limit, CancellationToken ct)
    {
        if (asset.Length > limit) throw new IOException("The Asset exceeds the supported size for this operation.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, prefix + JsonValues.NewId());
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await foreach (var chunk in asset.Chunks(ct))
            {
                if (file.Length + chunk.Length > limit) throw new IOException("The Asset exceeds the supported size for this operation.");
                await file.WriteAsync(chunk, ct);
            }
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static async Task<JsonObject> Page(TextReader reader, JsonObject args, CancellationToken ct)
    {
        var offset = Math.Max(0, args.GetInt32("offset"));
        var buffer = new char[Math.Clamp(args.GetInt32("length", 6000), 1, 16000) + 1];
        var skipped = 0;
        while (skipped < offset)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, offset - skipped)), ct);
            if (read == 0) break;
            skipped += read;
        }
        var count = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length - 1), ct);
        if (count > 0 && char.IsHighSurrogate(buffer[count - 1]))
            count += await reader.ReadAsync(buffer.AsMemory(count, 1), ct);
        var text = new string(buffer, 0, count);
        var more = await reader.ReadAsync(buffer.AsMemory(0, 1), ct) > 0;
        return new() { ["text"] = text, ["offset"] = skipped, ["next_offset"] = more ? JsonValue.Create(skipped + count) : null };
    }

    public void Dispose()
    {
        foreach (var path in outputs) File.Delete(path);
    }
}
