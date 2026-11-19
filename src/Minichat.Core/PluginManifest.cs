using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Minichat.Core;

internal sealed record PluginPackage(string Id, string Name, string Source, JsonObject Manifest, bool BuiltIn = false,
    IReadOnlyDictionary<string, string>? Pages = null, string Signer = "", string ArchivePath = "",
    IReadOnlyDictionary<string, string>? Hashes = null, IReadOnlyDictionary<string, PluginAsset>? Assets = null,
    IReadOnlyDictionary<string, string>? Resources = null)
{
    public const int MaxPackageBytes = 128 * 1024 * 1024;
    private const int MaxScriptBytes = 2 * 1024 * 1024;
    public static readonly string[] Agents = ["chat", "env", "preset", "search", "fetch", "temporary"];
    public string PackageName => Manifest.GetString("packageName", Id);
    public bool Signed => Signer.Length > 0;
    internal string Isolate { get; init; } = JsonValues.NewId();
    internal string StorageKey => Signed ? PackageName + "." + Signer : Id;
    public bool UsesActions => PackageName != Id;
    internal string Bind(string value) => UsesActions ? PluginActions.Bind(Manifest, value) : value;

    public static PluginPackage Read(Stream stream, string archivePath = "", bool allowLegacy = false,
        Func<byte[], byte[], string>? platformVerifier = null)
    {
        if (stream.Length > MaxPackageBytes) throw new InvalidDataException("Plugin package is too large.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > 256 || archive.Entries.Sum(item => item.Length) > MaxPackageBytes)
            throw new InvalidDataException("Plugin archive exceeds its limits.");
        if (archive.Entries.Any(entry => !SafePath(entry.FullName.TrimEnd('/')) || (entry.ExternalAttributes & 0xF0000000) == 0xA0000000) ||
            archive.Entries.Select(entry => entry.FullName.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new InvalidDataException("Plugin archive contains duplicate entries.");
        var manifest = JsonValues.ParseObject(ReadEntry(archive, "manifest.json", 64 * 1024));
        var id = Identifier(manifest.GetString("id"));
        if (id is "base" or "licenses") throw new InvalidDataException("This plugin identifier is reserved.");
        var name = manifest.GetString("name").Trim();
        var version = manifest.GetInt32("apiVersion");
        if (name.Length is 0 or > 120 || version != 2 && !(allowLegacy && version == 1))
            throw new InvalidDataException("A plugin name and supported apiVersion are required.");
        var signed = version == 2;
        if (signed) PackageIdentifier(manifest.GetString("packageName"));
        var (certificate, hashes) = signed ? PluginSignature.Verify(archive, platformVerifier) : ("", null);
        var entry = manifest.GetString("entry");
        if (!SafePath(entry) || !entry.EndsWith(".js", StringComparison.Ordinal))
            throw new InvalidDataException("Plugin entry must be a bundle inside the archive.");
        var assets = signed ? ReadAssets(manifest, hashes!.ContainsKey) : new(StringComparer.Ordinal);
        if (signed) PluginActions.Validate(manifest);

        ValidateHooks(manifest, manifest.GetObject("hooks"), signed);
        var skillIds = new HashSet<string>(StringComparer.Ordinal);
        var packageTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in manifest.GetArray("skills"))
        {
            if (!skillIds.Add(Identifier(skill.GetString("name")))) throw new InvalidDataException("Duplicate skill.");
            if (skill.GetString("description").Trim().Length is 0 or > 1024 ||
                skill.GetArray("agents").Count == 0 || skill.GetArray("agents").Any(role => !Agents.Contains(JsonValues.GetText(role))))
                throw new InvalidDataException("Skill needs a description and valid agents.");
            ValidateHooks(manifest, skill.GetObject("hooks"), signed);
            var toolIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in skill.GetArray("tools"))
            {
                if (!toolIds.Add(Identifier(tool.GetString("name"))) || !packageTools.Add(tool.GetString("name")))
                    throw new InvalidDataException("Duplicate tool.");
                if (8 + id.Length + tool.GetString("name").Length > 64) throw new InvalidDataException("Plugin tool name is too long.");
                if (signed) PluginActions.BindRequired(manifest, tool.GetString("handler"));
                else Handler(tool.GetString("handler"));
                if (tool.GetString("description").Trim().Length is 0 or > 1024 ||
                    tool.GetObject("parameters").GetString("type") != "object")
                    throw new InvalidDataException("Tool needs a description and object parameter schema.");
            }
        }
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in manifest.GetArray("attachments"))
        {
            if (!types.Add(Identifier(type.GetString("id"))) || type.GetString("name").Trim().Length == 0 ||
                type.GetArray("mimeTypes").Count == 0 || type.GetArray("mimeTypes").Any(mime => !JsonValues.GetText(mime).Contains('/')))
                throw new InvalidDataException("Attachment needs a unique id, name and MIME types.");
            if (signed) PluginActions.BindRequired(manifest, type.GetString("handler"));
            else Handler(type.GetString("handler"));
            if (type.GetString("thumbnail") is { Length: > 0 } thumbnail)
            {
                if (!signed) Handler(thumbnail);
                else if (!assets.TryGetValue(thumbnail, out var preview) ||
                    preview.MimeType is not ("image/png" or "image/jpeg" or "image/webp") || preview.Accessibility != "private")
                    throw new InvalidDataException("An attachment thumbnail must reference a private image asset.");
            }
        }
        foreach (var permission in manifest.GetArray("permissions").Select(item => JsonValues.GetText(item)))
        {
            if (permission is not ("harness.read" or "harness.write" or "db" or "http" or "picker" or "settings" or "model"))
                throw new InvalidDataException("Unknown plugin permission: " + permission);
        }
        foreach (var origin in manifest.GetArray("network").Select(item => JsonValues.GetText(item)))
        {
            if (!TryHttp(origin, out var uri) || uri.GetLeftPart(UriPartial.Authority) != origin)
                throw new InvalidDataException("Network access must declare exact HTTP(S) origins.");
        }
        var settings = new HashSet<string>(StringComparer.Ordinal);
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in Settings(manifest.GetArray("settings")))
        {
            var key = Identifier(setting.GetString("key"));
            var kind = setting.GetString("type");
            if (!settings.Add(key) || kind is not ("boolean" or "choice" or "text" or "page" or "group" or "link" or "markdown") ||
                (kind != "markdown" && setting.GetString("title").Trim().Length == 0))
                throw new InvalidDataException("A setting needs a unique key, supported type and title.");
            if (setting.GetString("binding").Length > 0)
                throw new InvalidDataException("Private platform bindings are reserved. Use settings and URL Interaction.");
            if (kind == "markdown" && setting.GetString("content").Length == 0 && setting.GetString("markdown").Length == 0)
                throw new InvalidDataException("Markdown needs inline text or a bundled resource.");
            if (setting.GetString("content") is { Length: > 0 } content)
            {
                if (!signed) pages[key] = ReadEntry(archive, content, 64 * 1024);
                else if (!assets.TryGetValue(content, out var asset) || asset.MimeType != "text/markdown")
                    throw new InvalidDataException("A Markdown setting must reference a Markdown asset.");
            }
            if (kind == "choice" && (setting.GetArray("options").Count is 0 or > 20 ||
                setting.GetArray("options").Any(option => option.GetString("value").Length == 0 || option.GetString("title").Length == 0)))
                throw new InvalidDataException("Choices need options with values and titles.");
            if (kind == "link")
            {
                var url = setting.GetString("url");
                if (signed && PluginActions.Parse(url).Kind != "hooks" || !signed && !IsInteractionUrl(url))
                    throw new InvalidDataException("Links must use a registered minichat Action.");
            }
        }
        if (signed && manifest.GetObject("routes").Count > 0) throw new InvalidDataException("Declare plugin endpoints in actions.handlers.");
        foreach (var (route, handler) in manifest.GetObject("routes"))
        {
            Identifier(route);
            Handler(JsonValues.GetText(handler));
        }
        return new(id, name, ReadEntry(archive, entry, MaxScriptBytes), manifest, Pages: pages,
            Signer: certificate, ArchivePath: archivePath, Hashes: hashes, Assets: assets);
    }

    public static IEnumerable<JsonObject> Settings(JsonArray items, int depth = 0)
    {
        if (depth > 5 || items.Count > 50) throw new InvalidDataException("Settings layout exceeds its limits.");
        foreach (var item in items.OfType<JsonObject>())
        {
            yield return item;
            foreach (var child in Settings(item.GetArray("items"), depth + 1)) yield return child;
        }
    }

    internal static Dictionary<string, PluginAsset> ReadAssets(JsonObject manifest, Func<string, bool> exists)
    {
        var assets = new Dictionary<string, PluginAsset>(StringComparer.Ordinal);
        foreach (var asset in manifest.GetArray("assets"))
        {
            var id = Identifier(asset.GetString("id"));
            var kind = asset.GetString("type");
            var mime = asset.GetString("mimeType");
            var access = asset.GetString("accessibility", "private");
            var path = asset.GetString("path");
            var handler = asset.GetString("handler");
            if (access is not ("private" or "protected" or "public") || !MimeType(mime) ||
                !assets.TryAdd(id, new(id, kind, mime, access, path, handler)))
                throw new InvalidDataException("Assets need unique IDs, a MIME type and valid accessibility.");
            if (kind == "dynamic" && path.Length == 0) PluginActions.BindRequired(manifest, handler);
            else if (kind != "file" || handler.Length > 0 || !SafePath(path) || !exists(path))
                throw new InvalidDataException("An asset must reference a signed archive file or a dynamic handler.");
        }
        return assets;
    }

    public static bool IsInteractionUrl(string value) => value.Length <= 4096 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "minichat" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    public bool Has(string permission) => Manifest.GetArray("permissions").Any(value => JsonValues.GetText(value) == permission);

    internal static bool TryHttp(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" && parsed.UserInfo.Length == 0 && parsed.Host.Length > 0)
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    internal static string Identifier(string value)
    {
        if (value.Length is 0 or > 48 || !char.IsAsciiLetterLower(value[0]) ||
            value.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('-' or '_')))
            throw new InvalidDataException("Invalid plugin identifier.");
        return value;
    }

    internal static string PackageIdentifier(string value)
    {
        if (value.Length > 150 || value.StartsWith("minichat.", StringComparison.Ordinal) ||
            value.Split('.') is { Length: < 2 } || value.Split('.').Any(segment =>
                segment.Length is 0 or > 48 || !char.IsAsciiLetterLower(segment[0]) ||
                segment.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_')))
            throw new InvalidDataException("A reverse-DNS packageName is required.");
        return value;
    }

    internal static bool SafePath(string path) => path.Length is > 0 and <= 240 && path.Split('/').All(segment =>
        segment is not ("" or "." or "..") && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));

    private static bool MimeType(string value) => value.Length <= 128 && value.Split('/') is [var major, var minor] &&
        major.Length > 0 && minor.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '+' or '.');

    private static void ValidateHooks(JsonObject manifest, JsonObject hooks, bool urls)
    {
        foreach (var (name, handler) in hooks)
        {
            if (!PluginEvents.Names.Contains(name)) throw new InvalidDataException("Unknown plugin event: " + name);
            if (urls) PluginActions.BindRequired(manifest, JsonValues.GetText(handler));
            else Handler(JsonValues.GetText(handler));
        }
    }

    internal static void Handler(string name)
    {
        if (name.Length is 0 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '$')))
            throw new InvalidDataException("Invalid JavaScript handler name.");
    }

    private static string ReadEntry(ZipArchive archive, string name, int limit)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Missing " + name);
        if (entry.Length > limit) throw new InvalidDataException("Plugin entry is too large.");
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        for (var count = source.Read(chunk); count > 0; count = source.Read(chunk))
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Plugin entry is too large.");
            buffer.Write(chunk, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
    }
}

internal static class PluginEvents
{
    public static readonly HashSet<string> Names =
    [
        "app.ready", "plugin.enabled", "plugin.command", "chat.loaded", "chat.changed", "chat.title",
        "preset.changed", "library.changed", "message.user", "message.system", "message.completed",
        "turn.started", "turn.delta", "turn.finished", "turn.cancelled", "tool.started", "tool.finished",
        "context.compacted", "world.changed", "attachment.process", "attachment.thumbnail", "skill.activated", "home.requested", "settings.changed"
    ];
}
