namespace MiniRP.Core;

internal sealed record PluginAsset(string Id, string Type, string MimeType, string Accessibility, string Path, string Handler, bool Writable);
