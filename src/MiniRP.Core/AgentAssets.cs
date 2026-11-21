using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MiniRP.Core;

internal sealed class AgentAssets(PluginRuntime runtime, MemoryDb db, string directory,
    Func<JsonObject> environment, Func<Workspace>? workspace = null, PresetResearch? presetResearch = null)
{
    private const string Grants = "_context/asset-grants";
    private const string FileGrants = "_context/asset-file/";
    private const string ResearchGrants = "_context/research-asset/";
    private readonly MessageAttachments files = new(directory);
    private readonly AgentAssetStorage storage = new(runtime, db, directory);
    private static readonly Regex AssetLinks = new(@"minirp://assets/[a-z0-9._]+/[A-Za-z0-9_.-]+", RegexOptions.NonBacktracking);
    internal AgentSkill StorageSkill() => storage.Skill(Open);
    internal AgentToolOutput Output => new(storage);
    internal string DatabasePath => db.Path;
    internal PresetResearch? PresetResearch => presetResearch;
    internal sealed record Reading(JsonObject Result, JsonObject? Attachment = null);
    internal sealed record ResearchScope(SearchTurn Turn, PageSource Source);
    internal ResearchScope? Research { get; set; }

    private ResearchAssets? CurrentResearch()
    {
        if (Research is { } active) return active.Turn.Assets;
        if (presetResearch is not null) return presetResearch.Assets();
        var owner = workspace?.Invoke();
        var context = environment();
        return owner is not null && owner.ChatId.Length > 0 && owner.PresetId == context.GetString("preset") &&
            owner.ChatId == context.GetString("room", context.GetString("chat"))
            ? new ResearchAssets(owner, owner.State) : null;
    }

    private ResearchAssets ResearchFor(string url) =>
        db.Get(ResearchGrants + url) is not null && CurrentResearch() is { } store && store.Contains(url)
            ? store : throw MissingGrant();

    internal void GrantResearch(string url)
    {
        if (ResearchAssets.IsUrl(url) && CurrentResearch()?.Contains(url) == true && db.Get(ResearchGrants + url) is null)
            db.Write(ResearchGrants + url, "1", internalWrite: true);
    }

    internal Task<JsonNode?> CacheResearch(JsonObject args, CancellationToken ct) =>
        runtime.CacheResearch(environment(), Research ?? throw MissingGrant(), args, ct);

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
        var id = url[MessageAttachments.AssetPrefix.Length..];
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return null;
        if (db.Get(FileGrants + id) is not { } stored) return null;
        var file = JsonValues.ParseObject(stored);
        return file.GetString("preset") == environment().GetString("preset") ? file : null;
    }

    public void Grant(PluginPackage publisher, JsonNode? output, IReadOnlySet<string>? supplied = null)
    {
        var urls = output is JsonArray array ? array.AsEnumerable() : new[] { output };
        var pending = new JsonObject();
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
            pending[url] = new JsonObject { ["package"] = publisher.PackageName, ["signer"] = publisher.Signer };
        }
        if (pending.Count > 0) MergeGrants(db, pending);
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
                    if (AgentAssetStorage.IsUrl(url) ? storage.Find(url) is not null :
                        ResearchAssets.IsUrl(url) ? db.Get(ResearchGrants + url) is not null && CurrentResearch()?.Contains(url) == true :
                        MessageAttachments.IsAttachmentUrl(url) ? FileGrant(url) is not null : grants.ContainsKey(url))
                        allowed.Add(url);
                    break;
            }
        }
    }

    internal PluginRuntime.AssetContent Open(string url, JsonObject options)
    {
        if (AgentAssetStorage.IsUrl(url)) return storage.Open(url);
        if (ResearchAssets.IsUrl(url)) return ResearchFor(url).Open(url);
        if (MessageAttachments.IsAttachmentUrl(url))
            return runtime.OpenAttachmentAsset(FileGrant(url) ?? throw MissingGrant());
        var grant = Load()[url] as JsonObject ?? throw MissingGrant();
        return runtime.OpenAgentAsset(url, grant.GetString("package"), grant.GetString("signer"), environment(), options);
    }

    internal Task<JsonObject> Edit(string method, string url, JsonObject args, CancellationToken ct)
    {
        if (AgentAssetStorage.IsUrl(url)) return storage.Edit(method, url, args, ct);
        if (ResearchAssets.IsUrl(url)) return ResearchFor(url).Edit(method, url, args, ct);
        var grant = Load()[url] as JsonObject ?? throw MissingGrant();
        return runtime.EditAgentAsset(url, grant.GetString("package"), grant.GetString("signer"), environment(), method, args, ct);
    }

    internal Func<Stream, CancellationToken, Task<JsonObject>> PrepareWrite(string url)
    {
        if (AgentAssetStorage.IsUrl(url))
        {
            storage.RequireWritable(url);
            return (input, ct) => storage.Import(url, input, ct);
        }
        if (ResearchAssets.IsUrl(url))
        {
            var research = ResearchFor(url);
            return (input, ct) => research.Import(url, input, ct);
        }
        var grant = Load()[url] as JsonObject ?? throw MissingGrant();
        return runtime.PrepareAssetWrite(url, grant.GetString("package"), grant.GetString("signer"), environment());
    }

    private static UnauthorizedAccessException MissingGrant() => new("Use an Asset URL supplied in this session.");

    internal JsonObject Published(string text)
    {
        var result = new JsonObject();
        var grants = Load();
        foreach (var url in PublishedUrls(text))
        {
            if (result.Count >= 64) break;
            if (result.ContainsKey(url)) continue;
            if ((storage.Find(url) ?? FileGrant(url)) is { } file)
                result[url] = new JsonObject { ["attachment"] = file };
            else if (ResearchAssets.IsUrl(url) && db.Get(ResearchGrants + url) is not null &&
                     CurrentResearch()?.Published(url) is { } research)
                result[url] = new JsonObject { ["research"] = research };
            else if (grants[url] is JsonObject grant)
                result[url] = new JsonObject { ["plugin"] = grant.DeepClone() };
        }
        return result;
    }

    internal static IEnumerable<string> PublishedUrls(string text)
    {
        foreach (Match match in AssetLinks.Matches(text))
        {
            if (match.Index + match.Length < text.Length && text[match.Index + match.Length] is '?' or '/' or '#') continue;
            if (match.Length <= 512) yield return match.Value;
        }
    }

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

    private static void MergeGrants(MemoryDb target, JsonObject pending, bool replace = true) =>
        target.UpdateInternal(Grants, stored =>
        {
            var grants = JsonValues.ParseObject(stored ?? "{}");
            foreach (var (url, grant) in pending)
                if (replace || grants[url] is null) grants[url] = grant?.DeepClone();
            return JsonValues.Serialize(grants);
        });

    internal static void ImportPluginGrants(MemoryDb target, string workers)
    {
        if (!Directory.Exists(workers)) return;
        foreach (var folder in Directory.EnumerateDirectories(workers))
        {
            var file = Path.Combine(folder, "session.sqlite");
            var key = "_context/plugin-asset-import/" + Path.GetFileName(folder);
            if (!File.Exists(file) || target.Get(key) is not null) continue;
            using var source = MemoryDb.ReadOnly(file);
            if (!source.HasEntriesTable()) continue;
            foreach (var row in source.Query(
                "SELECT key,content FROM entries WHERE key GLOB ? OR key GLOB ? OR key GLOB ? OR key GLOB ?",
                AgentAssetStorage.EntryPrefix + "*", AgentAssetStorage.OutputPrefix + "*", FileGrants + "*", ResearchGrants + "*"))
                target.Execute("INSERT OR IGNORE INTO entries VALUES(?,?,1,?)",
                    row["key"], row["content"], DateTimeOffset.UtcNow.ToString("O"));
            if (source.Get(Grants) is { } grants) MergeGrants(target, JsonValues.ParseObject(grants), replace: false);
            target.Write(key, "1", internalWrite: true);
        }
    }
}
