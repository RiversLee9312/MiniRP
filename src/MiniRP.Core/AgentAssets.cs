using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class AgentAssets(PluginRuntime runtime, HostPort port, MemoryDb db, string directory,
    ModelClient model, Func<JsonObject> environment)
{
    private const string Grants = "_context/asset-grants";
    internal sealed record Reading(JsonObject Result, JsonObject? Attachment = null);

    public void Grant(PluginPackage publisher, JsonNode? output)
    {
        var urls = output is JsonArray array ? array.AsEnumerable() : new[] { output };
        var grants = Load();
        foreach (var item in urls)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var url))
                throw new InvalidDataException("An asset-url tool must return an Asset URL or an array of Asset URLs.");
            runtime.OpenAgentAsset(url, publisher.PackageName, publisher.Signer, environment());
            grants[url] = new JsonObject { ["package"] = publisher.PackageName, ["signer"] = publisher.Signer };
        }
        db.Write(Grants, JsonValues.Serialize(grants), internalWrite: true);
    }

    public async Task<Reading> Read(JsonObject args, CancellationToken ct)
    {
        var url = args.GetString("url");
        var grant = Load()[url] as JsonObject ?? throw new UnauthorizedAccessException("Use an Asset URL returned by a skill in this session.");
        var asset = runtime.OpenAgentAsset(url, grant.GetString("package"), grant.GetString("signer"), environment());
        var mime = asset.MimeType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var mode = args.GetString("mode", "inspect");
        if (mode == "inspect") return new(new() { ["url"] = url, ["mime"] = mime, ["length"] = asset.Length });
        var valid = mode switch
        {
            "text" => mime.StartsWith("text/", StringComparison.Ordinal) || mime is "application/json" or "application/xml",
            "image" => mime.StartsWith("image/", StringComparison.Ordinal),
            "pdf_page" => mime == "application/pdf",
            "video_frame" => mime.StartsWith("video/", StringComparison.Ordinal),
            "audio_clip" => mime.StartsWith("audio/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal),
            _ => false,
        };
        if (!valid) throw new ArgumentException("This reading mode does not match the asset's MIME type.");
        Directory.CreateDirectory(directory);
        var sourceId = "asset-source-" + JsonValues.NewId();
        var source = Path.Combine(directory, sourceId);
        var outputId = "asset-sample-" + JsonValues.NewId();
        var output = Path.Combine(directory, outputId);
        var retain = false;
        try
        {
            await using (var file = File.Create(source))
            {
                await foreach (var chunk in asset.Chunks(ct))
                {
                    if (file.Length + chunk.Length > PluginPackage.MaxPackageBytes)
                        throw new IOException("Asset exceeds the supported sampling size.");
                    await file.WriteAsync(chunk, ct);
                }
            }
            if (mode == "text") return new(await ReadText(source, args, ct));
            if (mode == "image") File.Move(source, output);
            else
            {
                var response = await port.CallHost("agent_asset_sample", new()
                {
                    ["source"] = sourceId, ["output"] = outputId, ["mode"] = mode,
                    ["page"] = Math.Max(1, args.GetInt32("page", 1)),
                    ["start_ms"] = Math.Max(0, args.GetInt32("start_ms")),
                    ["duration_ms"] = Math.Clamp(args.GetInt32("duration_ms", 10000), 100, 30000),
                }, ct);
                mime = response.GetString("mime");
            }
            var attachment = new JsonObject { ["id"] = outputId, ["mime"] = mime, ["name"] = url[(url.LastIndexOf('/') + 1)..] };
            model.ValidateAsset(attachment);
            retain = true;
            return new(new JsonObject { ["url"] = url, ["mode"] = mode, ["sample"] = "Attached after the tool response." }, attachment);
        }
        finally
        {
            File.Delete(source);
            if (!retain) File.Delete(output);
        }
    }

    private JsonObject Load() => JsonValues.ParseObject(db.Get(Grants) ?? "{}");

    private static async Task<JsonObject> ReadText(string path, JsonObject args, CancellationToken ct)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
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
        return new() { ["text"] = new string(buffer, 0, count), ["offset"] = skipped,
            ["next_offset"] = reader.Peek() < 0 ? null : JsonValue.Create(skipped + count) };
    }
}
