using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class AgentLanguage
{
    public const string Default = "en";
    private static readonly Lazy<JsonObject> SkillNames = new(() => ReadLabels("AgentSkillNames"));

    public static string? Normalize(string? locale) => locale is not null && PluginManifestLocalization.ValidLocale(locale)
        ? locale : null;

    public static string SkillName(string id, string locale) =>
        Localized(SkillNames.Value.GetObject(id switch
        {
            "web-research" => "search",
            "research" or "direct-research" => "search-full",
            "read-page" => "browser-fetch-workflow",
            "read-search" => "browser-search-workflow",
            _ => BuiltInSkillIds.Current(id)
        }), locale, id);

    internal static string Localized(JsonObject names, string? locale, string fallback = "")
    {
        if (Normalize(locale)?.ToLowerInvariant() is { } tag)
        {
            foreach (var candidate in PluginManifestLocalization.Parents(tag))
                if (names.GetString(candidate) is { Length: > 0 } title) return title;
            if (PlatformLocaleAlias(tag) is { } alias && names.GetString(alias) is { Length: > 0 } platformTitle)
                return platformTitle;
        }
        return names.GetString(Default, fallback);
    }

    internal static string? PlatformLocaleAlias(string locale)
    {
        var parts = locale.ToLowerInvariant().Split('-');
        if (parts[0] == "pt") return "pt-br";
        if (parts[0] != "zh") return null;
        // Skip any extended language identifiers before the script or region.
        var index = 1;
        while (index < parts.Length && parts[index].Length == 3 && char.IsAsciiLetter(parts[index][0])) index++;
        return index < parts.Length && parts[index] is "hant" or "tw" or "hk" or "mo" ? "zh-hant" : "zh-hans";
    }

    internal static JsonObject ReadLabels(string file)
    {
        using var stream = typeof(AgentLanguage).Assembly.GetManifestResourceStream($"MiniRP.{file}.json")
            ?? throw new InvalidOperationException("Bundled display labels are missing.");
        return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException("Invalid bundled display labels.");
    }
}
