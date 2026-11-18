using System.Text.Json.Nodes;

namespace Minichat.Core;

internal sealed class MessageAttachments(string root)
{
    public const int MaxBytes = 16_000_000;
    private const int GeminiMaxBytes = 14_000_000;

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

    private static bool Supports(string protocol, string mime) => (protocol, Format(mime)) switch
    {
        ("gemini", not Kind.Unknown) => mime is not ("image/gif" or "audio/webm"),
        ("anthropic" or "responses" or "openai", Kind.Image) => mime is not ("image/heic" or "image/heif"),
        ("ollama", Kind.Image) => mime is "image/png" or "image/jpeg" or "image/webp",
        ("anthropic" or "responses" or "openai", Kind.Pdf) => true,
        ("openai", Kind.Audio) => mime is "audio/wav" or "audio/x-wav" or "audio/mpeg" or "audio/mp3",
        _ => false
    };

    public void Validate(JsonArray attachments, Provider provider)
    {
        long total = 0;
        if (attachments.Count > 20)
        {
            throw new InvalidOperationException("Too many attachments.");
        }
        foreach (var item in attachments)
        {
            if (!Supports(provider.Protocol, item.GetString("mime")))
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

    public Dictionary<JsonNode, List<Media>> Resolve(JsonArray messages, string protocol)
    {
        var remaining = Budget(protocol);
        var result = new Dictionary<JsonNode, List<Media>>(ReferenceEqualityComparer.Instance);
        foreach (var message in messages.Reverse().OfType<JsonObject>())
        {
            if (message.GetString("role") != "user" || message.GetArray("attachments") is not { Count: > 0 } files)
            {
                continue;
            }
            var media = new List<Media>();
            foreach (var file in files)
            {
                var mime = file.GetString("mime");
                if (!Supports(protocol, mime))
                {
                    continue;
                }
                var source = FileFor(file);
                if (!source.Exists || source.Length > remaining)
                {
                    continue;
                }
                remaining -= (int)source.Length;
                media.Add(new(file.GetString("name"), mime, Format(mime), Convert.ToBase64String(File.ReadAllBytes(source.FullName))));
            }
            result[message] = media;
        }
        return result;
    }

    private FileInfo FileFor(JsonNode? file) => new(Path.Combine(root, Workspace.Safe(file.GetString("id"))));
    private static int Budget(string protocol) => protocol == "gemini" ? GeminiMaxBytes : MaxBytes;

    public static string Text(JsonNode? message)
    {
        var content = message.GetString("content");
        var files = message.GetArray("attachments");
        return files.Count == 0 ? content : content + "\n\nAttachments: " + string.Join(", ", files.Select(file => file.GetString("name")));
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
