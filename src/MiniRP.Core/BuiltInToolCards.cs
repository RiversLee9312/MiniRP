using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Read-only presentation of host tools. Tool IDs and original payloads stay in the trace.</summary>
internal static class BuiltInToolCards
{
    private static readonly Lazy<JsonObject> Tools = new(() => AgentLanguage.ReadLabels("AgentToolNames"));
    private static readonly Lazy<JsonObject> Labels = new(() => AgentLanguage.ReadLabels("ToolCardLabels"));

    private const string Program = """
        root = ToolFacts($rows, $status, $note, $more, $less)
        $rows = []
        $status = ""
        $note = ""
        $more = ""
        $less = ""
        """;

    private static readonly (string Field, string Label)[] Confirmations =
    [
        ("failed", "_failed"), ("staged", "_stage"), ("completed", "_complete"),
        ("submitted", "_submitted"), ("sent", "_sent"), ("deleted", "_deleted"), ("ready", "_ready"),
        ("initialized", "_ready")
    ];

    internal static string? Title(string tool, string? locale) => Tools.Value[tool] is JsonObject entry
        ? AgentLanguage.Localized(entry.GetObject("names"), locale) : null;

    internal static string? Skill(string tool) => Tools.Value[tool]?.GetString("skill") is { Length: > 0 } id ? id : null;

    internal static JsonObject? Present(string tool, string phase, string text, bool plain, string? locale)
    {
        if (phase is not (ToolPresentation.Arguments or ToolPresentation.Result) ||
            Tools.Value[tool] is not JsonObject entry || text.Length > 128 * 1024) return null;

        JsonNode? data;
        try { data = plain ? JsonValue.Create(text) : JsonNode.Parse(text); }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
        if (phase == ToolPresentation.Arguments && data is not JsonObject) return null;

        var rows = new JsonArray();
        string status = "", note = "";
        if (data is JsonObject fields)
        {
            if (phase == ToolPresentation.Arguments)
            {
                foreach (var key in entry.GetArray("arguments").Select(value => JsonValues.GetText(value)))
                {
                    var value = fields[key];
                    if (tool == "set_value" && key == "value" && fields.GetString("path") == "agentic_search" &&
                        bool.TryParse(JsonValues.GetText(value), out var enabled)) value = JsonValue.Create(enabled);
                    AddField(rows, tool, locale, key, value);
                }
                if (fields.Count == 0) note = Label("_no_arguments", locale);
            }
            else
            {
                if (fields.GetString("error") is { Length: > 0 } error)
                {
                    status = Label("_failed", locale);
                    AddField(rows, tool, locale, "message", JsonValue.Create(error));
                }
                else
                {
                    status = Confirmations.Where(item => fields.GetBoolean(item.Field))
                        .Select(item => Label(item.Label, locale)).FirstOrDefault() ?? "";
                    foreach (var (key, value) in fields)
                        if (rows.Count < 16 && Labels.Value.ContainsKey(key)) AddField(rows, tool, locale, key, value);
                    if (tool == "inspect_preset")
                    {
                        var world = fields.GetObject("world");
                        AddField(rows, tool, locale, "summary", world["summary"]);
                        AddField(rows, tool, locale, "rules", world["rules"]);
                    }
                    if (fields.Count == 0) note = Label("_empty_result", locale);
                }
            }
        }
        else if (data is JsonArray items)
        {
            AddField(rows, tool, locale, "_count", JsonValue.Create(items.Count));
            AddField(rows, tool, locale, "items", data);
        }
        else if (data is JsonValue)
        {
            AddField(rows, tool, locale, "_value", data);
        }
        else if (phase == ToolPresentation.Result) note = Label("_empty_result", locale);
        if (rows.Count == 0 && status.Length == 0 && note.Length == 0) return null;
        return new JsonObject
        {
            ["program"] = Program,
            ["title"] = Title(tool, locale),
            ["state"] = new JsonObject { ["rows"] = rows, ["status"] = status, ["note"] = note,
                ["more"] = Label("_more", locale), ["less"] = Label("_less", locale) }
        };
    }

    private static void AddField(JsonArray rows, string tool, string? locale, string key, JsonNode? value)
    {
        if (value is null) return;
        var rendered = FormatValue(tool, key, value, locale);
        if (rendered.Length == 0) rendered = "—";
        rows.AddNode(new JsonObject
        {
            ["label"] = Label((tool, key) switch
            {
                ("configure_date", "mode") => "date_mode",
                ("configure_experience", "mode") => "chat_mode",
                _ => key
            }, locale),
            ["value"] = Limit(rendered, 6000),
            ["mono"] = key is "command" or "args" or "key" or "db_key" or "content_key" or "stdout" or "stderr" or "snapshot"
        });
    }

    private static string FormatValue(string tool, string key, JsonNode value, string? locale)
    {
        if (value is JsonArray list)
        {
            var names = list.Take(16).Where(item => item is not null).Select(item => Item(item!, locale))
                .Where(text => text.Length > 0);
            return WithOverflow(names, list.Count - 16, locale);
        }
        if (value is JsonObject content)
        {
            if (key == "ensemble")
            {
                var cast = FormatValue(tool, "agents", content.GetArray("agents"), locale);
                var scenes = FormatValue(tool, "chats", content.GetArray("chats"), locale);
                return string.Join("\n\n", new[] { cast, scenes }.Where(section => section.Length > 0));
            }
            return Item(content, locale);
        }
        var scalar = JsonValues.GetText(value);
        if (value is JsonValue primitive && primitive.TryGetValue<bool>(out var flag))
            return Label(flag ? "_true" : "_false", locale);
        if (key == "name" && tool == "activate_skill") return AgentLanguage.SkillName(scalar, locale ?? AgentLanguage.Default);
        if (key == "percent" && scalar.Length > 0) return scalar + "%";
        return key switch
        {
            "chat_mode" or "date_mode" => Label("mode." + scalar, locale, scalar),
            "mode" or "path" or "status" or "direction" => Label(key + "." + scalar, locale, scalar),
            "completeness" => Label("status." + scalar, locale, scalar),
            _ => scalar
        };
    }

    private static string Item(JsonNode value, string? locale)
    {
        if (value is not JsonObject node) return value is JsonValue ? Limit(JsonValues.GetText(value), 400) : "";
        var label = new[] { "title", "name", "key", "path", "id", "label" }.Select(field => node.GetString(field))
            .FirstOrDefault(text => text.Length > 0) ?? "";
        var detail = new[] { "snippet", "excerpt", "url", "asset_url", "content", "text" }.Select(field => node.GetString(field))
            .FirstOrDefault(text => text.Length > 0) ?? "";
        if (label.Length > 0 || detail.Length > 0)
            return string.Join(" · ", new[] { Limit(label, 160), Limit(detail, 240) }.Where(text => text.Length > 0));
        // Keyed state objects have short inline fields, which are easier to scan than an encoded object.
        return string.Join(" · ", node.Where(item => item.Value is JsonValue && Labels.Value.ContainsKey(item.Key)).Take(4)
            .Select(item => Label(item.Key, locale) + " – " + Limit(JsonValues.GetText(item.Value), 100)));
    }

    private static string WithOverflow(IEnumerable<string> items, int overflow, string? locale) => string.Join("\n", overflow <= 0
        ? items : items.Append(Label("_more_items", locale).Replace("{0}", overflow.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)));

    private static string Limit(string value, int max) => value.Length <= max ? value :
        value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)] + "…";

    private static string Label(string key, string? locale, string? fallback = null) =>
        AgentLanguage.Localized(Labels.Value.GetObject(key), locale, fallback ?? key.Replace('_', ' '));
}
