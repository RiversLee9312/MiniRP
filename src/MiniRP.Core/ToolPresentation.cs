using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed record ToolPresentation(JsonObject Display, JsonObject Reference)
{
    internal const string Arguments = "arguments";
    internal const string Result = "result";
    internal static string DisplayKey(string phase) => $"tool_{phase}_ui";
    internal static string ReferenceKey(string phase) => $"_tool_{phase}_presentation";

    internal static JsonObject? Render(string program, string title, string tool, string phase, string text, bool plain = false,
        string? locale = null)
    {
        if (text.Length > 32 * 1024) return null;
        JsonNode? data;
        try
        {
            data = plain ? JsonValue.Create(text) : JsonNode.Parse(text);
            if (phase == Arguments && data is not JsonObject || phase == Result && data is JsonObject result &&
                (result.GetString("error").Length > 0 || result.ContainsKey("diagnostic") || result.ContainsKey("content_key"))) return null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
        return new() { ["program"] = program, ["title"] = title,
            ["state"] = new JsonObject { ["data"] = data, ["tool"] = tool, ["phase"] = phase,
                ["title"] = title, ["locale"] = locale ?? "" } };
    }

    internal static bool ContainsAction(string program)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < program.Length; index++)
        {
            var character = program[index];
            if (escaped) escaped = false;
            else if (quoted && character == '\\') escaped = true;
            else if (character == '"') quoted = !quoted;
            else if (!quoted && (character == '@' || char.IsAsciiLetter(character)))
            {
                var start = index;
                while (index + 1 < program.Length && (char.IsAsciiLetterOrDigit(program[index + 1]) || program[index + 1] == '_')) index++;
                var token = program.AsSpan(start, index - start + 1);
                if (token is "@Set" or "@Reset" or "@OpenUrl" or "@ToAssistant") return true;
                var following = index + 1;
                while (following < program.Length && char.IsWhiteSpace(program[following])) following++;
                if (token is "Action" && following < program.Length && program[following] == '(') return true;
            }
        }
        return false;
    }
}
