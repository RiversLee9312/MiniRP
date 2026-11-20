using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class AgentAssets(PluginRuntime runtime, MemoryDb db, string directory,
    Func<JsonObject> environment)
{
    private const string Grants = "_context/asset-grants";
    private const string FileGrants = "_context/asset-file/";
    private readonly MessageAttachments files = new(directory);
    internal sealed record Reading(JsonObject Result, JsonObject? Attachment = null);

    internal string StoreFile(JsonObject file)
    {
        var url = MessageAttachments.AssetUrl(file);
        var key = FileGrants + Workspace.Safe(file.GetString("id"));
        if (db.Get(key) is null)
        {
            var grant = new JsonObject
            {
                ["id"] = file.GetString("id"), ["mime"] = MessageAttachments.Mime(file),
                ["name"] = file.GetString("name"), ["text"] = JsonValues.Clone(file["text"]),
                ["preset"] = environment().GetString("preset")
            };
            db.Write(key, JsonValues.Serialize(grant), internalWrite: true);
        }
        return url;
    }

    internal void RegisterMessage(JsonObject message)
    {
        if (message.GetBoolean("_harness_asset") || message.GetString("role") != "user") return;
        foreach (var file in message.GetArray("attachments").OfType<JsonObject>())
            file["url"] = StoreFile(file);
    }

    private JsonObject? FileGrant(string url)
    {
        if (!url.StartsWith(MessageAttachments.AssetPrefix, StringComparison.Ordinal)) return null;
        var id = Workspace.Safe(url[MessageAttachments.AssetPrefix.Length..]);
        if (db.Get(FileGrants + id) is not { } stored) return null;
        var file = JsonValues.ParseObject(stored);
        return file.GetString("preset") == environment().GetString("preset") ? file : null;
    }

    public void Grant(PluginPackage publisher, JsonNode? output, IReadOnlySet<string>? supplied = null)
    {
        var urls = output is JsonArray array ? array.AsEnumerable() : new[] { output };
        var grants = Load();
        foreach (var item in urls)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var url))
                throw new InvalidDataException("An asset-url tool must return an Asset URL or an array of Asset URLs.");
            if (MessageAttachments.IsAttachmentUrl(url))
            {
                if (supplied?.Contains(url) != true || FileGrant(url) is null)
                    throw new UnauthorizedAccessException("The file was not supplied to this tool.");
                continue;
            }
            if (supplied?.Contains(url) == true)
            {
                Open(url, new());
                continue;
            }
            runtime.OpenAgentAsset(url, publisher.PackageName, publisher.Signer, environment());
            grants[url] = new JsonObject { ["package"] = publisher.PackageName, ["signer"] = publisher.Signer };
        }
        db.Write(Grants, JsonValues.Serialize(grants), internalWrite: true);
    }

    internal IReadOnlySet<string> SuppliedUrls(JsonNode? arguments)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var grants = Load();
        Collect(arguments);
        return allowed;

        void Collect(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var entry in obj) Collect(entry.Value);
                    break;
                case JsonArray array:
                    foreach (var child in array) Collect(child);
                    break;
                case JsonValue value when value.TryGetValue<string>(out var url) &&
                    PluginUrls.Route(url, "assets").Length > 0:
                    if (MessageAttachments.IsAttachmentUrl(url) ? FileGrant(url) is not null : grants.ContainsKey(url))
                        allowed.Add(url);
                    break;
            }
        }
    }

    internal PluginRuntime.AssetContent Open(string url, JsonObject options)
    {
        if (MessageAttachments.IsAttachmentUrl(url))
            return runtime.OpenAttachmentAsset(FileGrant(url) ?? throw MissingGrant());
        var grant = Load()[url] as JsonObject ?? throw MissingGrant();
        return runtime.OpenAgentAsset(url, grant.GetString("package"), grant.GetString("signer"), environment(), options);
    }

    private static UnauthorizedAccessException MissingGrant() => new("Use an Asset URL supplied in this session.");

    internal async Task<Reading> Present(PluginRuntime.AssetContent asset, Provider provider, string url, CancellationToken ct)
    {
        var mime = asset.MimeType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!MessageAttachments.Supports(provider, mime))
            throw new InvalidOperationException("The selected model or protocol does not accept this media.");
        var path = await AssetCodecs.Stage(directory, asset, "asset-sample-", MessageAttachments.Budget(provider.Protocol), ct);
        var retained = false;
        try
        {
            if (new FileInfo(path).Length == 0) throw new IOException("The Asset contains no media.");
            var attachment = new JsonObject { ["id"] = Path.GetFileName(path), ["mime"] = mime,
                ["name"] = asset.Name.Length > 0 ? asset.Name : url[(url.LastIndexOf('/') + 1)..] };
            files.Validate(new JsonArray(attachment.DeepClone()), provider);
            var result = new JsonObject
            {
                ["sample_url"] = StoreFile(attachment), ["sample_mime"] = mime,
                ["sample"] = "Attached after the tool response."
            };
            retained = true;
            return new(result, attachment);
        }
        finally { if (!retained) File.Delete(path); }
    }

    private JsonObject Load() => JsonValues.ParseObject(db.Get(Grants) ?? "{}");
}
