using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class ToolTrace
{
    internal const string Source = "_tool_source";
    internal const string ResultPlain = "_tool_result_plain";
    internal const string ToolTitle = "tool_title";
    internal const string SkillTitle = "skill_title";
    internal static readonly string[] DisplayFields =
        [ToolTitle, SkillTitle, ToolPresentation.DisplayKey(ToolPresentation.Arguments), ToolPresentation.DisplayKey(ToolPresentation.Result)];
    private static readonly string[] InternalFields =
        [Source, ResultPlain, ToolPresentation.ReferenceKey(ToolPresentation.Arguments), ToolPresentation.ReferenceKey(ToolPresentation.Result)];

    internal static bool IsEvent(JsonObject item) => item.GetString("kind") is "tool_start" or "tool_end";

    internal static void RemoveReferences(JsonObject item)
    {
        foreach (var key in InternalFields) item.Remove(key);
    }

    internal static JsonObject RawEvent(JsonObject item)
    {
        var raw = (JsonObject)item.DeepClone();
        foreach (var key in DisplayFields) raw.Remove(key);
        RemoveReferences(raw);
        return raw;
    }

    internal static string Role(string agent) => agent switch
    {
        "ChatAgent" => "chat",
        "EnvAgent" => "env",
        "PresetAgent" => "preset",
        "PluginAgent" => "plugin",
        "SearchAgent" => "search",
        "BrowserAgent" or "FetchAgent" => "fetch",
        "TemporaryAgent" => "temporary",
        _ => agent
    };
}
