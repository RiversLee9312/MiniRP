using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Digests;

namespace MiniRP.Core;

/// <summary>Branch state names immutable payloads. Drafts stay in memory until a cache operation retains them.</summary>
internal sealed class ResearchAssets(Workspace workspace, JsonObject state, PresetResearch? shared = null)
{
    internal const string Area = "research-assets";
    private const string Index = "research_assets";
    private const string Prefix = "minirp://assets/minirp.base/research-";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Document(bool Page, JsonObject Metadata, byte[] Bytes);
    private readonly Dictionary<string, Document> drafts = new(StringComparer.Ordinal);
    private readonly string preset = WorkspaceIds.Require(workspace.PresetId);
    public bool Changed { get; private set; }

    public static bool IsUrl(string url) => url.StartsWith(Prefix, StringComparison.Ordinal) &&
        url.Length == Prefix.Length + 32 && url.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    public string Draft(JsonObject document, bool page)
    {
        var url = Prefix + Guid.NewGuid().ToString("N");
        var encoded = Encode(document, page);
        if (shared is null) drafts.Add(url, encoded);
        else Store(url, encoded);
        return url;
    }

    public bool Contains(string url) => IsUrl(url) && (drafts.ContainsKey(url) || Record(url) is not null);

    private JsonObject? Record(string url) => (shared?.Read()[Index]?[url] ?? state[Index]?[url]) as JsonObject;

    public static IEnumerable<string> Hashes(JsonNode? world)
    {
        foreach (var entry in world.GetObject(Index))
        {
            if (!IsUrl(entry.Key) || entry.Value is not JsonObject)
                throw new InvalidDataException("Invalid research Asset index.");
            yield return ValidHash(entry.Value.GetString("hash"));
        }
    }

    public static string PathFor(string root, string preset, string hash) =>
        Path.Combine(root, Area, WorkspaceIds.Require(preset), ValidHash(hash));

    private static string ValidHash(string hash) =>
        hash.Length == 64 && hash.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0
            ? hash : throw new InvalidDataException("Invalid research Asset hash.");

    public static async Task Verify(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MessageAttachments.MaxAssetBytes) throw new InvalidDataException("Research Asset is too large.");
        var digest = new Sha256Digest();
        var buffer = new byte[65536];
        for (int count; (count = await stream.ReadAsync(buffer, ct)) != 0;)
            digest.BlockUpdate(buffer, 0, count);
        if (Finish(digest) != expected) throw new InvalidDataException("Research Asset checksum mismatch.");
    }

    public void Migrate()
    {
        var pages = state.GetObject("pages");
        foreach (var (url, node) in pages.ToArray())
            if (node is JsonObject page && page.GetString("content").Length > 0)
            {
                var copy = (JsonObject)page.DeepClone();
                WebPageMarkdown.Normalize(copy, url);
                pages[url] = Retain(copy, page: true);
            }

        foreach (var (query, node) in state.GetObject("search_cache"))
            if (node is JsonArray results)
                for (var i = 0; i < results.Count; i++)
                    if (results[i] is JsonObject item && item.GetString("url").Length > 0)
                        results[i] = Retain(item, page: false);
    }

    public JsonObject DocumentAt(string url)
    {
        var item = Read(url);
        var document = item.Page ? (JsonObject)item.Metadata.DeepClone()
            : JsonValues.ParseObject(Utf8.GetString(item.Bytes));
        if (item.Page) document["content"] = Utf8.GetString(item.Bytes);
        document["asset_url"] = url;
        return document;
    }

    public PluginRuntime.AssetContent Open(string url)
    {
        var item = GetDraft(url);
        if (item is not null)
            return new(Mime(item.Page), item.Bytes.Length, ct => PluginRuntime.ReadBytes(item.Bytes, ct),
                Name: Name(item.Page, item.Metadata), Writable: true);
        var (record, path) = Stored(url);
        var page = record.GetBoolean("page");
        return PluginRuntime.OpenSample(path, Mime(page), Name(page, record.GetObject("metadata"))) with { Writable = true };
    }

    internal JsonObject? Published(string url) => IsUrl(url) ? Record(url)?.DeepClone().AsObject() : null;

    internal static PluginRuntime.AssetContent OpenPublished(string root, string preset, JsonObject record)
    {
        var page = record.GetBoolean("page");
        var name = Name(page, record.GetObject("metadata"));
        return PluginRuntime.OpenSample(PathFor(root, preset, record.GetString("hash")), Mime(page), page ? name + ".md" : name);
    }

    private static string Mime(bool page) => page ? "text/markdown" : "application/json";
    private static string Name(bool page, JsonObject metadata) => page ? metadata.GetString("title") : "search-result.json";

    public string Retain(JsonObject document, bool page)
    {
        var url = document.GetString("asset_url");
        if (!Contains(url)) url = Draft(document, page);
        var item = Read(url);
        if (item.Page != page) throw new ArgumentException("The research Asset has a different document type.");
        if (drafts.ContainsKey(url)) Store(url, item);
        return url;
    }

    public async Task<JsonObject> Edit(string method, string url, JsonObject args, CancellationToken ct)
    {
        var item = Read(url);
        using var source = new MemoryStream(item.Bytes, writable: false);
        using var target = new MemoryStream();
        var changes = await PluginRuntime.ApplyAssetEdit(method, source, target, args, ct);
        return ReplaceBytes(url, item, target.ToArray(), changes, ct);
    }

    public async Task<JsonObject> Import(string url, Stream input, CancellationToken ct)
    {
        var item = Read(url);
        using var target = new MemoryStream();
        await input.CopyToAsync(target, ct);
        return ReplaceBytes(url, item, target.ToArray(), 1, ct);
    }

    private JsonObject ReplaceBytes(string url, Document item, byte[] bytes, int changes, CancellationToken ct)
    {
        if (bytes.Length > MessageAttachments.MaxAssetBytes) throw new IOException("Research Asset is too large.");
        var text = Utf8.GetString(bytes);
        if (item.Page && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A cached page needs Markdown.");
        if (!item.Page)
        {
            var record = JsonValues.ParseObject(text);
            SearchAgent.ValidateUrl(record.GetString("url"));
            if (record.GetString("title").Length == 0) throw new ArgumentException("A search result needs a title.");
        }
        ct.ThrowIfCancellationRequested();
        Write(url, item with { Bytes = bytes });
        return new() { ["url"] = url, ["changes"] = changes, ["size"] = bytes.Length };
    }

    private static Document Encode(JsonObject document, bool page)
    {
        var metadata = (JsonObject)document.DeepClone();
        metadata.Remove("ref");
        metadata.Remove("asset_url");
        var text = page ? metadata.GetString("content") : JsonValues.Serialize(metadata);
        metadata.Remove("content");
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length > MessageAttachments.MaxAssetBytes) throw new IOException("Research Asset is too large.");
        return new(page, page ? metadata : new(), bytes);
    }

    private Document Read(string url)
    {
        if (GetDraft(url) is { } draft) return draft;
        var (item, path) = Stored(url);
        return new(item.GetBoolean("page"), item.GetObject("metadata"), File.ReadAllBytes(path));
    }

    private Document? GetDraft(string url)
    {
        if (!IsUrl(url)) throw new ArgumentException("Invalid research Asset URL.");
        return drafts.GetValueOrDefault(url);
    }

    private (JsonObject Record, string Path) Stored(string url)
    {
        if (Record(url) is not { } item) throw new FileNotFoundException("This research Asset is unavailable.");
        var path = PathFor(workspace.Root, preset, item.GetString("hash"));
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new FileNotFoundException("This research Asset is unavailable.");
        if (file.Length > MessageAttachments.MaxAssetBytes) throw new InvalidDataException("Research Asset is too large.");
        return (item, path);
    }

    private void Write(string url, Document document)
    {
        if (Record(url) is not null)
            Store(url, document);
        else
            drafts[url] = document;
    }

    private void Store(string url, Document document)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(document.Bytes, 0, document.Bytes.Length);
        var hash = Finish(digest);
        var file = PathFor(workspace.Root, preset, hash);
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var staged = file + "." + JsonValues.NewId() + ".tmp";
            try
            {
                File.WriteAllBytes(staged, document.Bytes);
                File.Move(staged, file, overwrite: true);
            }
            finally { File.Delete(staged); }
        }
        state[Index] ??= new JsonObject();
        var record = new JsonObject
        {
            ["hash"] = hash, ["page"] = document.Page, ["metadata"] = document.Metadata.DeepClone()
        };
        shared?.Set(Index, url, record);
        state[Index]![url] = record;
        drafts.Remove(url);
        Changed = true;
    }

    private static string Finish(Sha256Digest digest)
    {
        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return Convert.ToHexStringLower(hash);
    }
}
