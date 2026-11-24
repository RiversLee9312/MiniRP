using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private JsonObject ToolTraceEvent(JsonObject item, string preset)
    {
        if (!ToolTrace.IsEvent(item)) return item;
        var visible = (JsonObject)item.DeepClone();
        ProjectToolTrace(item, visible, preset, null);
        return visible;
    }

    private void ProjectToolTrace(JsonObject source, JsonObject visible, string preset, string? requestedLocale)
    {
        foreach (var key in ToolTrace.DisplayFields) visible.Remove(key);
        ToolTrace.RemoveReferences(visible);
        var tool = ToolTrace.IsEvent(source) ? source.GetString("name") : source.GetString("tool");
        if (tool.Length == 0) return;
        var locale = AgentLanguage.Normalize(requestedLocale) ??
            AgentLanguage.Normalize(Volatile.Read(ref pluginLocale)) ?? AgentLanguage.Default;
        var origin = source[ToolTrace.Source] as JsonObject;
        var legacy = !source.ContainsKey(ToolTrace.Source);
        var agent = ToolTrace.Role(source.GetString("agent"));
        if (agent is "preset" or "plugin" && source.GetString("speaker") == "FetchAgent") agent = "fetch";
        var argumentsReference = source[ToolPresentation.ReferenceKey(ToolPresentation.Arguments)] as JsonObject;
        var resultReference = source[ToolPresentation.ReferenceKey(ToolPresentation.Result)] as JsonObject;
        var plugin = origin?.GetString("kind") == "plugin" ? origin : legacy ? argumentsReference ?? resultReference : null;
        if (legacy && plugin is null && !ToolTrace.IsEvent(source) && BuiltInToolCards.Title(tool, locale) is null &&
            plugins.LegacyBuiltInToolTrace(preset, tool, agent, source.GetBoolean(ToolTrace.ResultPlain)) is { } builtIn)
            (plugin, argumentsReference, resultReference) = builtIn;
        string? native = null;
        string? title = null;
        string? skillTitle = null;
        if (plugin is not null)
        {
            if (agent.Length == 0) return;
            if (plugins.ReadToolTraceLabels(plugin, preset, tool, agent, locale) is not { } labels) return;
            (title, skillTitle, native) = labels;
        }
        else if (legacy || origin?.GetString("kind") == "native")
        {
            native = tool;
            var skill = legacy ? BuiltInToolCards.Skill(tool) : origin!.GetString("skill");
            if (!string.IsNullOrEmpty(skill) && AgentLanguage.SkillName(skill, locale) is { } label && label != skill)
                skillTitle = label;
        }

        title ??= native is null ? null : BuiltInToolCards.Title(native, locale);
        if (!string.IsNullOrWhiteSpace(title)) visible[ToolTrace.ToolTitle] = title;
        if (!string.IsNullOrWhiteSpace(skillTitle)) visible[ToolTrace.SkillTitle] = skillTitle;

        void Present(string phase, string field, JsonObject? reference)
        {
            if (source[field] is not JsonValue data || !data.TryGetValue<string>(out var text)) return;
            JsonObject? display = null;
            if (reference is not null && plugin is not null && SameToolPlugin(plugin, reference))
                display = plugins.ReadToolPresentation(reference, preset, tool, phase, text, locale, agent);
            if (display is null && native is not null)
                display = BuiltInToolCards.Present(native, phase, text,
                    phase == ToolPresentation.Result && source.GetBoolean(ToolTrace.ResultPlain), locale);
            if (display is not null) visible[ToolPresentation.DisplayKey(phase)] = display;
        }
        Present(ToolPresentation.Arguments, "arguments", argumentsReference);
        Present(ToolPresentation.Result, ToolTrace.IsEvent(source) ? "text" : "result", resultReference);
    }

    private static bool SameToolPlugin(JsonObject origin, JsonObject reference) =>
        PluginUrls.CanonicalPackage(origin.GetString("package")) == PluginUrls.CanonicalPackage(reference.GetString("package")) &&
        origin.GetString("signer") == reference.GetString("signer") && origin.GetString("skill") == reference.GetString("skill");

    private void RefreshToolTraces(Workspace workspace)
    {
        if (WebHistory)
        {
            foreach (var trace in TrackedWebToolTraces(workspace))
                EmitToolTraceUpdate(trace.Block, trace.Id, trace.Conversation, workspace);
            return;
        }
        void Update(JsonArray blocks, string conversation = "")
        {
            foreach (var block in blocks.OfType<JsonObject>().Where(item => item.GetString("tool").Length > 0))
                EmitToolTraceUpdate(block, block.GetString("id"), conversation, workspace);
        }
        Update(workspace.Chat.GetArray("blocks"));
        if (presetConversation is { } preset) Update(preset.Blocks);
        if (activeEnsembleChat.Length > 0 && ensemble is { } room) Update(room.Blocks(activeEnsembleChat), activeEnsembleChat);
    }

    private void EmitToolTraceUpdate(JsonObject block, string id, string conversation, Workspace workspace)
    {
        if (id.Length == 0) return;
        var agent = block.GetString("agent");
        var update = new JsonObject { ["kind"] = "tool_trace_update", ["block"] = id, ["agent"] = agent,
            ["chat"] = workspace.ChatId, ["preset"] = workspace.PresetId, ["conversation"] = conversation };
        if (block["plugin_run"] is { } run) update["plugin_run"] = run.DeepClone();
        ProjectToolTrace(block, update, workspace.PresetId, null);
        foreach (var key in ToolTrace.DisplayFields)
            if (!update.ContainsKey(key)) update[key] = null;
        Emit(update);
    }
}
