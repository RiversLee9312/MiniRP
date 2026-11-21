using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class AgentAssetStorage(PluginRuntime runtime, MemoryDb db, string directory)
{
    internal const string EntryPrefix = "_context/owned-asset/";
    internal const string FilePrefix = "asset-owned-";
    private const string Prefix = "minirp://assets/minirp.agent/";
    internal const string OutputPrefix = "_context/tool-output/";
    private static readonly TimeSpan OutputLifetime = TimeSpan.FromDays(7);

    internal static bool IsUrl(string url) => url.StartsWith(Prefix, StringComparison.Ordinal) &&
        WorkspaceIds.IsValid(url[Prefix.Length..]);

    internal JsonObject? Find(string url)
    {
        if (!IsUrl(url)) return null;
        var id = url[Prefix.Length..];
        if (db.Get(EntryPrefix + id) is { Length: > 0 } owned) return JsonValues.ParseObject(owned);
        if (db.Get(OutputPrefix + id) is not { Length: > 0 } output) return null;
        var file = JsonValues.ParseObject(output);
        return IsCurrentOutput(file) ? file : null;
    }

    internal PluginRuntime.AssetContent Open(string url)
    {
        var file = Find(url) ?? throw new UnauthorizedAccessException("This Asset is unavailable in the session.");
        return runtime.OpenAttachmentAsset(file) with { Writable = !file.ContainsKey("expires_at") };
    }

    internal AgentAssetStorage WithDatabase(MemoryDb connection) => new(runtime, connection, directory);
    internal void RequireWritable(string url) => _ = Require(url);

    internal async Task<JsonObject> StoreOutput(string name, string mime, string? url,
        Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        var file = new JsonObject { ["name"] = Name(new() { ["name"] = name }),
            ["mime"] = MessageAttachments.Mime(new JsonObject { ["mime"] = mime }),
            ["expires_at"] = DateTimeOffset.UtcNow.Add(OutputLifetime).ToUnixTimeSeconds() };
        url ??= Prefix + WorkspaceIds.New();
        var id = FilePrefix + WorkspaceIds.New();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id);
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await write(stream, ct);
                if (stream.Length > MessageAttachments.MaxAssetBytes) throw new IOException("The output Asset is too large.");
                file["size"] = stream.Length;
            }
            ct.ThrowIfCancellationRequested();
            file["id"] = id;
            db.Write(OutputPrefix + url[Prefix.Length..], JsonValues.Serialize(file), internalWrite: true);
            return new() { ["asset_url"] = url, ["size"] = JsonValues.Clone(file["size"]),
                ["instruction"] = "Activate view_asset and use this Asset URL to inspect the output." };
        }
        catch { File.Delete(path); throw; }
    }

    internal AgentSkill Skill(Func<string, JsonObject, PluginRuntime.AssetContent> openSource) => new(
        "asset_storage", "Create and manage files in this session. Share a file with a Markdown link or use its Asset URL in an OpenUI card.",
        new(
            Tools.Define("create_asset", "Create a writable Asset from UTF-8 text, an existing Asset URL, or empty content.",
                new() { ["name"] = Tools.String(), ["mime"] = Tools.String(), ["text"] = Tools.String(), ["source_url"] = Tools.String() }, "name"),
            Tools.Define("list_assets", "List files owned by this session.",
                new() { ["offset"] = Tools.Integer(), ["limit"] = Tools.Integer() }),
            Tools.Define("rename_asset", "Change the download filename of an owned Asset.",
                new() { ["url"] = Tools.String(), ["name"] = Tools.String() }, "url", "name"),
            Tools.Define("delete_asset", "Delete an owned Asset from this session. Previously shared downloads retain their saved revision.",
                new() { ["url"] = Tools.String() }, "url")),
        async (name, args, ct) =>
        {
            var url = args.GetString("url");
            switch (name)
            {
                case "create_asset":
                    if (args.GetString("source_url") is { Length: > 0 } source)
                    {
                        if (args.ContainsKey("text")) throw new ArgumentException("Supply either text or a source Asset URL.");
                        return await Create(args, openSource(source, new()), ct);
                    }
                    var value = Encoding.UTF8.GetBytes(args.GetString("text"));
                    return await Create(args, new(args.ContainsKey("text") ? "text/plain" : "application/octet-stream",
                        value.Length, token => PluginRuntime.ReadBytes(value, token)), ct);
                case "list_assets":
                    var list = new JsonArray();
                    foreach (var row in db.Query("SELECT key, content FROM entries WHERE key GLOB '_context/owned-asset/*' AND content <> '' ORDER BY key LIMIT ? OFFSET ?",
                        Math.Clamp(args.GetInt32("limit", 30), 1, 100), Math.Max(0, args.GetInt32("offset"))))
                        list.AddNode(Describe(Prefix + row["key"][EntryPrefix.Length..], JsonValues.ParseObject(row["content"])));
                    return list;
                case "rename_asset":
                    var filename = Name(args);
                    var renamed = Update(url, file => { file["name"] = filename; return true; });
                    return Describe(url, renamed);
                case "delete_asset":
                    Update(url, _ => false);
                    return new JsonObject { ["url"] = url, ["deleted"] = true };
                default:
                    throw new InvalidOperationException("Unknown asset storage tool.");
            }
        },
        Instructions: "Use the exact URL returned by create_asset. In a reply use [Download filename](minirp://assets/…). Use edit_asset to change the file or container.import_asset to fill an empty file.");

    private async Task<JsonObject> Create(JsonObject args, PluginRuntime.AssetContent source, CancellationToken ct)
    {
        var name = Name(args);
        var mime = MessageAttachments.Mime(new JsonObject { ["mime"] = args.GetString("mime", source.MimeType) });
        var url = Prefix + WorkspaceIds.New();
        var path = await AssetCodecs.Stage(directory, source, FilePrefix, MessageAttachments.MaxAssetBytes, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var file = new JsonObject { ["id"] = Path.GetFileName(path), ["name"] = name,
                ["mime"] = mime, ["size"] = new FileInfo(path).Length };
            Save(url, file);
            return Describe(url, file);
        }
        catch { File.Delete(path); throw; }
    }

    internal Task<JsonObject> Edit(string method, string url, JsonObject args, CancellationToken ct) =>
        Replace(url, async (output, token) =>
        {
            await using var input = new AssetStream(Open(url), token);
            return await PluginRuntime.ApplyAssetEdit(method, input, output, args, token);
        }, ct);

    internal Task<JsonObject> Import(string url, Stream input, CancellationToken ct) =>
        Replace(url, async (output, token) =>
        {
            var buffer = new byte[65536];
            for (int count; (count = await input.ReadAsync(buffer, token)) > 0;)
            {
                if (output.Length + count > MessageAttachments.MaxAssetBytes) throw new IOException("The Asset is too large.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            return 1;
        }, ct);

    private async Task<JsonObject> Replace(string url, Func<Stream, CancellationToken, Task<int>> write, CancellationToken ct)
    {
        var original = Require(url).GetString("id");
        var id = FilePrefix + WorkspaceIds.New();
        var path = Path.Combine(directory, id);
        try
        {
            int changes;
            long size;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                changes = await write(output, ct);
                if (output.Length > MessageAttachments.MaxAssetBytes) throw new IOException("The Asset is too large.");
                size = output.Length;
            }
            ct.ThrowIfCancellationRequested();
            var file = Update(url, current =>
            {
                if (current.GetString("id") != original) throw new InvalidOperationException("The Asset changed while it was being written. Re-read it and retry.");
                current["id"] = id;
                current["size"] = size;
                return true;
            });
            var result = Describe(url, file);
            result["changes"] = changes;
            return result;
        }
        catch { File.Delete(path); throw; }
    }

    private JsonObject Require(string url) => Find(url) is { } file && !file.ContainsKey("expires_at")
        ? file : throw MissingWritableAsset();

    private JsonObject Update(string url, Func<JsonObject, bool> update)
    {
        if (!IsUrl(url)) throw MissingWritableAsset();
        JsonObject? record = null;
        db.UpdateInternal(EntryPrefix + url[Prefix.Length..], stored =>
        {
            if (string.IsNullOrEmpty(stored)) throw MissingWritableAsset();
            record = JsonValues.ParseObject(stored);
            return update(record) ? JsonValues.Serialize(record) : "";
        });
        return record!;
    }

    private static UnauthorizedAccessException MissingWritableAsset() => new("This session has no writable Asset at that URL.");
    private void Save(string url, JsonObject record) => db.Write(EntryPrefix + url[Prefix.Length..], JsonValues.Serialize(record), internalWrite: true);

    private static JsonObject Describe(string url, JsonObject file) => new()
    {
        ["url"] = url, ["name"] = file.GetString("name"), ["mime"] = file.GetString("mime"),
        ["size"] = JsonValues.Clone(file["size"]), ["writable"] = true
    };

    private static string Name(JsonObject args)
    {
        var name = args.GetString("name").Trim();
        if (name.Length is < 1 or > 180 || name is "." or ".." || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("Provide a filename without path separators or reserved characters.");
        return name;
    }

    internal static IEnumerable<string> Files(MemoryDb db)
    {
        if (!db.HasEntriesTable()) yield break;
        foreach (var row in db.Query("SELECT content FROM entries WHERE key GLOB '_context/owned-asset/*' AND content <> ''"))
            yield return JsonValues.ParseObject(row["content"]).GetString("id");
        foreach (var row in db.Query("SELECT content FROM entries WHERE key GLOB '_context/tool-output/*' AND content <> ''"))
        {
            var file = JsonValues.ParseObject(row["content"]);
            if (IsCurrentOutput(file)) yield return file.GetString("id");
        }
    }

    private static bool IsCurrentOutput(JsonObject file) =>
        file["expires_at"]?.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
