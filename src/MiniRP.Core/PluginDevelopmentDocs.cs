using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class PluginDevelopmentDocs
{
    private static readonly Dictionary<string, string[]> Topics = new(StringComparer.Ordinal)
    {
        ["development"] = ["plugins.getting-started", "plugins.packaging"],
        ["api"] = ["plugins.runtime", "plugins.actions-assets", "plugins.harness", "plugins.state", "plugins.skills-settings"],
        ["assets"] = ["plugins.actions-assets"],
        ["schema"] = ["schema"],
        ["preset"] = ["presets.modes", "presets.search", "presets.workflows"],
        ["automation"] = ["plugins.automation"],
        ["search"] = ["presets.search"],
        ["rooms"] = ["presets.modes"],
        ["state"] = ["plugins.state"],
    };

    public static JsonObject Read(JsonObject args, bool presetOnly = false)
    {
        var topic = args.GetString("topic", presetOnly ? "preset" : "development");
        if (presetOnly && topic is not ("preset" or "search" or "rooms"))
            throw new ArgumentException("Choose preset, search, or rooms.");
        if (!Topics.TryGetValue(topic, out var pages)) throw new ArgumentException("Choose " + string.Join(", ", Topics.Keys) + ".");
        var text = string.Join("\n\n", pages.Select(ReadPage));
        var start = Math.Clamp(args.GetInt32("offset"), 0, text.Length);
        if (args.GetString("query").Trim() is { Length: > 0 } query)
        {
            start = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return new() { ["topic"] = topic, ["found"] = false };
            start = Math.Max(0, start - 80);
        }
        var end = Math.Min(text.Length, start + Math.Clamp(args.GetInt32("length", 4000), 1, 8000));
        if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end++;
        return new() { ["topic"] = topic, ["text"] = text[start..end], ["offset"] = start,
            ["next_offset"] = end == text.Length ? null : JsonValue.Create(end) };
    }

    private static string ReadPage(string page)
    {
        using var stream = typeof(PluginDevelopmentDocs).Assembly.GetManifestResourceStream("MiniRP.PluginDocs." + page)
            ?? throw new InvalidOperationException("Bundled documentation page is missing: " + page);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
