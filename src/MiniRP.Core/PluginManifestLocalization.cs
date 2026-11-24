using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MiniRP.Core;

internal sealed partial class PluginManifestLocalization
{
    private readonly string defaultLocale;
    private readonly Dictionary<string, Dictionary<string, Translation>> locales;

    private sealed record Translation(string[] Path, string Text);

    private PluginManifestLocalization(string defaultLocale, Dictionary<string, Dictionary<string, Translation>> locales)
    {
        this.defaultLocale = defaultLocale;
        this.locales = locales;
    }

    public static PluginManifestLocalization? Read(JsonObject manifest)
    {
        if (!manifest.TryGetPropertyValue("i18n", out var value)) return null;
        if (value is not JsonObject section || section.Any(field => field.Key is not ("defaultLocale" or "locales")) ||
            section["locales"] is not JsonObject { Count: > 0 } dictionaries)
            throw Invalid("i18n must contain a nonempty locales object.");
        var defaultLocale = "";
        if (section.TryGetPropertyValue("defaultLocale", out var fallback))
        {
            if (!TryText(fallback, out defaultLocale) || !ValidLocale(defaultLocale))
                throw Invalid("defaultLocale must be a BCP 47 language tag.");
        }

        var targets = new Dictionary<string, (string[] Path, int Limit)>(StringComparer.Ordinal);
        foreach (var (owner, field, limit) in Fields(manifest))
        {
            if (!TryText(owner[field], out _)) continue;
            var path = Segments(owner).Append(field).ToArray();
            targets.Add(Pointer(path), (path, limit));
        }

        var locales = new Dictionary<string, Dictionary<string, Translation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (locale, map) in dictionaries)
        {
            if (!ValidLocale(locale) || map is not JsonObject { Count: > 0 } entries || locales.ContainsKey(locale))
                throw Invalid("Each locale must be a unique BCP 47 tag with a nonempty translation object.");
            var translations = new Dictionary<string, Translation>(StringComparer.Ordinal);
            foreach (var (pointer, replacement) in entries)
            {
                if (!targets.TryGetValue(pointer, out var target))
                    throw Invalid("Translation pointers must address declared display text.");
                if (!TryText(replacement, out var text) || string.IsNullOrWhiteSpace(text) || text.Length > target.Limit)
                    throw Invalid("Translation text must be nonblank and fit the source field limit.");
                translations.Add(pointer, new(target.Path, text));
            }
            locales.Add(locale, translations);
        }
        return new(defaultLocale, locales);
    }

    public JsonObject Project(JsonObject manifest, string? locale)
    {
        JsonObject? projected = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in Candidates(locale))
        {
            foreach (var (pointer, translation) in map)
            {
                if (!seen.Add(pointer)) continue;
                // The localized display projection never needs its source translation catalog.
                projected ??= new JsonObject(manifest.Where(field => field.Key != "i18n").Select(field =>
                    new KeyValuePair<string, JsonNode?>(field.Key, JsonValues.Clone(field.Value))));
                JsonNode node = projected;
                foreach (var part in translation.Path.AsSpan(0, translation.Path.Length - 1))
                    node = node is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : node[part]!;
                node[translation.Path[^1]] = translation.Text;
            }
        }
        return projected ?? manifest;
    }

    public string? Text(JsonNode owner, string field, string? locale)
    {
        var pointer = Pointer(Segments(owner).Append(field));
        foreach (var map in Candidates(locale))
            if (map.TryGetValue(pointer, out var translation)) return translation.Text;
        return null;
    }

    private IEnumerable<Dictionary<string, Translation>> Candidates(string? requested)
    {
        IEnumerable<string> requestedTags = [];
        if (requested is { } language && ValidLocale(language))
        {
            requestedTags = Parents(language);
            if (AgentLanguage.PlatformLocaleAlias(language) is { } alias) requestedTags = requestedTags.Append(alias);
        }
        var candidates = requestedTags.Concat(Parents(defaultLocale));
        foreach (var locale in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (locales.TryGetValue(locale, out var entries)) yield return entries;
    }

    internal static IEnumerable<string> Parents(string locale)
    {
        while (locale.Length > 0)
        {
            yield return locale;
            var separator = locale.LastIndexOf('-');
            if (separator < 0) yield break;
            locale = locale[..separator];
            separator = locale.LastIndexOf('-');
            // A lookup truncation also removes an extension/private-use singleton left at the end.
            if (locale.Length - separator == 2) locale = separator < 0 ? "" : locale[..separator];
        }
    }

    private static IEnumerable<(JsonObject Owner, string Field, int Limit)> Fields(JsonObject manifest)
    {
        yield return (manifest, "name", 120);
        yield return (manifest, "description", 1024);
        foreach (var attachment in manifest.GetArray("attachments").OfType<JsonObject>())
        {
            yield return (attachment, "name", 1024);
            yield return (attachment, "description", 1024);
        }
        foreach (var card in manifest.GetArray("cards").OfType<JsonObject>())
        {
            yield return (card, "title", 120);
            if (card["labels"] is JsonObject labels)
                foreach (var label in labels) yield return (labels, label.Key, 240);
        }
        foreach (var state in manifest.GetArray("stateEntries").OfType<JsonObject>()) yield return (state, "description", 512);
        foreach (var setting in PluginPackage.Settings(manifest.GetArray("settings")))
        {
            yield return (setting, "title", 1024);
            yield return (setting, "description", 1024);
            yield return (setting, "markdown", 64 * 1024);
            foreach (var option in setting.GetArray("options").OfType<JsonObject>()) yield return (option, "title", 1024);
        }
        foreach (var skill in manifest.GetArray("skills").OfType<JsonObject>())
        {
            yield return (skill, "title", 120);
            yield return (skill, "description", 1024);
            yield return (skill, "instructions", 64 * 1024);
            foreach (var tool in skill.GetArray("tools").OfType<JsonObject>())
            {
                yield return (tool, "title", 120);
                yield return (tool, "description", 1024);
                foreach (var schema in Schemas(tool["parameters"]))
                {
                    yield return (schema, "title", 1024);
                    yield return (schema, "description", 64 * 1024);
                    yield return (schema, "markdownDescription", 64 * 1024);
                }
            }
        }
    }

    private static IEnumerable<JsonObject> Schemas(JsonNode? node)
    {
        if (node is not JsonObject schema) yield break;
        yield return schema;
        foreach (var (keyword, value) in schema)
        {
            var children = keyword switch
            {
                "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas" or "dependencies"
                    when value is JsonObject properties => properties.Select(property => property.Value),
                "allOf" or "anyOf" or "oneOf" or "prefixItems" or "items" when value is JsonArray list => list,
                "items" or "not" or "if" or "then" or "else" or "additionalProperties" or "unevaluatedProperties" or
                    "additionalItems" or "unevaluatedItems" or "contains" or "propertyNames" or "contentSchema" => [value],
                _ => (IEnumerable<JsonNode?>)[]
            };
            foreach (var child in children)
                foreach (var nested in Schemas(child)) yield return nested;
        }
    }

    private static IEnumerable<string> Segments(JsonNode node)
    {
        var path = new Stack<string>();
        for (var current = node; current.Parent is JsonNode parent; current = parent)
            path.Push(parent is JsonArray ? current.GetElementIndex().ToString(CultureInfo.InvariantCulture) : current.GetPropertyName());
        return path;
    }

    private static string Pointer(IEnumerable<string> path) => "/" + string.Join('/', path.Select(segment =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));

    private static bool TryText(JsonNode? value, out string text)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var result) && result is not null)
        {
            text = result;
            return true;
        }
        text = "";
        return false;
    }

    internal static bool ValidLocale(string value)
    {
        if (value.Length is 0 or > 128 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ||
            !Locale().IsMatch(value)) return false;
        if (value.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) return true;
        HashSet<string>? variants = null;
        HashSet<string>? extensions = null;
        foreach (var subtag in value.Split('-').Skip(1))
        {
            if (subtag.Equals("x", StringComparison.OrdinalIgnoreCase)) break;
            if (subtag.Length == 1)
            {
                if (!(extensions ??= new(StringComparer.OrdinalIgnoreCase)).Add(subtag)) return false;
            }
            else if (extensions is null && (subtag.Length >= 5 || subtag.Length == 4 && char.IsAsciiDigit(subtag[0])) &&
                !(variants ??= new(StringComparer.OrdinalIgnoreCase)).Add(subtag)) return false;
        }
        return true;
    }

    // RFC 5646 langtag, private-use, and irregular grandfathered forms. Regular grandfathered tags match langtag.
    [GeneratedRegex("""
        \A(?:
            (?:[a-z]{2,3}(?:-[a-z]{3}){0,3}|[a-z]{4}|[a-z]{5,8}) # language and extlang
            (?:-[a-z]{4})?                                        # script
            (?:-(?:[a-z]{2}|[0-9]{3}))?                            # region
            (?:-(?:[a-z0-9]{5,8}|[0-9][a-z0-9]{3}))*               # variants
            (?:-[0-9a-wy-z](?:-[a-z0-9]{2,8})+)*                    # extensions
            (?:-x(?:-[a-z0-9]{1,8})+)?                             # private use
          | x(?:-[a-z0-9]{1,8})+
          | en-gb-oed
          | i-(?:ami|bnn|default|enochian|hak|klingon|lux|mingo|navajo|pwn|tao|tay|tsu)
          | sgn-(?:be-fr|be-nl|ch-de)
        )\z
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Locale();

    private static InvalidDataException Invalid(string message) => new("Plugin manifest i18n: " + message);
}
