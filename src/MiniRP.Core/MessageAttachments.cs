using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class MessageAttachments(string root)
{
    public const int MaxBytes = 16_000_000;
    public const int MaxAssetBytes = 128 * 1024 * 1024;
    private const int MaxQuoteLength = 32_768;
    private const int GeminiMaxBytes = 14_000_000;
    internal const string AssetPrefix = "minirp://assets/minirp.attachments/";

    internal static bool IsAttachmentUrl(string url) =>
        PluginUrls.Route(url, "assets").StartsWith("minirp.attachments/", StringComparison.Ordinal);

    internal static string AssetUrl(JsonNode? file) => AssetPrefix + Workspace.Safe(file.GetString("id"));

    internal static bool IsQuote(JsonNode? attachment) => attachment.GetString("kind") == "quote";

    private static void ValidateQuote(JsonNode? attachment)
    {
        var text = attachment.GetString("text");
        if (attachment is not JsonObject { Count: 3 } || !WorkspaceIds.IsValid(attachment.GetString("id")) ||
            text.Length > MaxQuoteLength || string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Invalid quote attachment.");
    }

    internal static string Mime(JsonNode? file)
    {
        var mime = file.GetString("mime").Split(';', 2)[0].Trim().ToLowerInvariant();
        var slash = mime.IndexOf('/');
        return mime.Length <= 160 && slash > 0 && slash < mime.Length - 1 &&
            mime.LastIndexOf('/') == slash && mime.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '/' or '+' or '.' or '-' or '_')
            ? mime : "application/octet-stream";
    }

    internal enum Kind { Image, Audio, Pdf, Video, Unknown }

    internal sealed record Media(string Name, string Mime, Kind Format, string Data)
    {
        public string DataUrl => $"data:{Mime};base64,{Data}";
    }

    private static Kind Format(string mime) => mime switch
    {
        "application/pdf" => Kind.Pdf,
        "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/heic" or "image/heif" => Kind.Image,
        "audio/wav" or "audio/x-wav" or "audio/mpeg" or "audio/mp3" or "audio/aac" or "audio/ogg" or "audio/flac" or "audio/mp4" or "audio/webm" => Kind.Audio,
        "video/mp4" or "video/webm" or "video/quicktime" or "video/mpeg" or "video/x-msvideo" or "video/x-flv" or "video/x-ms-wmv" or "video/3gpp" => Kind.Video,
        _ => Kind.Unknown
    };

    internal static bool Supports(string protocol, string mime) => (protocol, Format(mime)) switch
    {
        ("gemini", not Kind.Unknown) => mime is not ("image/gif" or "audio/webm"),
        ("anthropic" or "responses" or "openai", Kind.Image) => mime is not ("image/heic" or "image/heif"),
        ("ollama", Kind.Image) => mime is "image/png" or "image/jpeg" or "image/webp",
        ("anthropic" or "responses" or "openai", Kind.Pdf) => true,
        ("openai", Kind.Audio) => mime is "audio/wav" or "audio/x-wav" or "audio/mpeg" or "audio/mp3",
        _ => false
    };

    internal static bool Supports(Provider provider, string mime) =>
        Supports(provider.Protocol, mime) && (provider.InputModalities is null || provider.InputModalities.Contains(
            Format(mime) == Kind.Pdf ? "document" : Format(mime).ToString().ToLowerInvariant(), StringComparer.Ordinal));

    public void ValidateAssets(JsonArray attachments)
    {
        if (attachments.Count > 20) throw new InvalidOperationException("Too many attachments.");
        long total = 0;
        foreach (var item in attachments)
        {
            if (IsQuote(item))
            {
                ValidateQuote(item);
                continue;
            }
            var info = FileFor(item);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length == 0)
                throw new FileNotFoundException("The original attachment is unavailable.");
            if ((total += info.Length) > MaxAssetBytes)
                throw new InvalidOperationException("Attachments exceed the workspace resource limit.");
        }
    }

    public void Validate(JsonArray attachments, Provider provider)
    {
        long total = 0;
        if (attachments.Count > 20)
        {
            throw new InvalidOperationException("Too many attachments.");
        }
        foreach (var item in attachments)
        {
            if (IsQuote(item))
            {
                ValidateQuote(item);
                continue;
            }
            if (!Supports(provider, Mime(item)))
            {
                throw new InvalidOperationException("This model protocol cannot send this type of attachment.");
            }
            total += FileFor(item).Length;
            if (total > Budget(provider.Protocol))
            {
                throw new InvalidOperationException("Attachments exceed this protocol's inline upload limit.");
            }
        }
    }

    public Dictionary<JsonNode, List<Media>> Resolve(JsonArray messages, Provider provider, bool includeData = true)
    {
        var remaining = Budget(provider.Protocol);
        var result = new Dictionary<JsonNode, List<Media>>(ReferenceEqualityComparer.Instance);
        foreach (var message in messages.Reverse().OfType<JsonObject>())
        {
            if (!message.GetBoolean("_harness_asset") || message.GetArray("attachments") is not { Count: > 0 } files)
            {
                continue;
            }
            var media = new List<Media>();
            foreach (var file in files)
            {
                if (IsQuote(file)) continue;
                var mime = Mime(file);
                if (!Supports(provider, mime))
                {
                    continue;
                }
                var source = FileFor(file);
                if (!source.Exists || source.Length > remaining)
                {
                    continue;
                }
                remaining -= (int)source.Length;
                media.Add(new(file.GetString("name"), mime, Format(mime),
                    includeData ? Convert.ToBase64String(File.ReadAllBytes(source.FullName)) : ""));
            }
            result[message] = media;
        }
        return result;
    }

    internal FileInfo FileFor(JsonNode? file) => new(Path.Combine(root, Workspace.Safe(file.GetString("id"))));
    internal static int Budget(string protocol) => protocol == "gemini" ? GeminiMaxBytes : MaxBytes;

    public static string Text(JsonNode? message, string? original = null)
    {
        var content = original ?? message.GetString("content");
        var files = message.GetArray("attachments");
        if (files.Count == 0) return content;
        foreach (var quote in files.Where(IsQuote))
        {
            var text = quote.GetString("text").ReplaceLineEndings("\n");
            if (content.Length > 0) content += "\n\n";
            content += "Quoted passage (reference):\n> " + text.Replace("\n", "\n> ", StringComparison.Ordinal);
        }
        var assets = files.OfType<JsonObject>().Where(file => !IsQuote(file)).ToArray();
        if (assets.Length == 0) return content;
        if (message.GetBoolean("_harness_asset"))
            return content + "\n\nAttachments: " + string.Join(", ", assets.Select(file => file.GetString("name")));
        return content + "\n\nResources:\n" + string.Join("\n", assets.Where(file => file.GetString("url").Length > 0)
            .Select(file => "- " + file.GetString("url") + " (" + Mime(file) + ", " + JsonValues.Serialize(file["name"]) + ")"));
    }

    public static JsonArray Parts(string protocol, string text, IReadOnlyList<Media> files)
    {
        var result = new JsonArray(protocol switch
        {
            "gemini" => new JsonObject { ["text"] = text },
            "responses" => new JsonObject { ["type"] = "input_text", ["text"] = text },
            _ => new JsonObject { ["type"] = "text", ["text"] = text }
        });
        foreach (var media in files)
        {
            var part = protocol switch
            {
                "gemini" => new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = media.Mime, ["data"] = media.Data } },
                "anthropic" => new JsonObject { ["type"] = media.Format == Kind.Pdf ? "document" : "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = media.Mime, ["data"] = media.Data } },
                "responses" when media.Format == Kind.Pdf => new JsonObject { ["type"] = "input_file", ["filename"] = media.Name, ["file_data"] = media.DataUrl },
                "responses" => new JsonObject { ["type"] = "input_image", ["image_url"] = media.DataUrl, ["detail"] = "auto" },
                _ when media.Format == Kind.Pdf => new JsonObject { ["type"] = "file", ["file"] = new JsonObject { ["filename"] = media.Name, ["file_data"] = media.DataUrl } },
                _ when media.Format == Kind.Audio => new JsonObject { ["type"] = "input_audio", ["input_audio"] = new JsonObject { ["data"] = media.Data, ["format"] = media.Mime is "audio/wav" or "audio/x-wav" ? "wav" : "mp3" } },
                _ => new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = media.DataUrl } }
            };
            result.AddNode(part);
        }
        return result;
    }
}
