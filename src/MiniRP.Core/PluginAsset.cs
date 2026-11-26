namespace MiniRP.Core;

internal sealed record PluginAsset(string Id, string Type, string MimeType, string Accessibility, string Path, string Handler,
    bool Writable, IReadOnlyDictionary<string, string>? BackgroundVariants = null, string ChangeEvent = "")
{
    internal bool Listenable => ChangeEvent.Length > 0;

    internal static bool SupportsBackground(string mime) => mime.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/webp" or
        "image/gif" or "image/avif" or "image/heic" or "image/heif" or "video/mp4" or "video/webm" or "video/3gpp";
}
