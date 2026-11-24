using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    private static IEnumerable<JsonObject> ToolDeclarations(JsonObject manifest) => manifest.GetArray("skills")
        .SelectMany(skill => skill.GetArray("tools").OfType<JsonObject>());

    internal static void ValidateToolPresentations(JsonObject manifest, bool signed)
    {
        foreach (var tool in ToolDeclarations(manifest))
        {
            if (!tool.TryGetPropertyValue("presentation", out var value)) continue;
            if (!signed || value is not JsonObject { Count: > 0 } declaration)
                throw new InvalidDataException("Tool presentations must be declared as an object in a signed plugin.");
            foreach (var (phase, reference) in declaration)
            {
                if (phase is not (ToolPresentation.Arguments or ToolPresentation.Result) ||
                    reference is not JsonValue scalar || !scalar.TryGetValue<string>(out var id))
                    throw new InvalidDataException("Tool presentation must map arguments or result to a declared card ID.");
                var card = manifest.GetArray("cards").OfType<JsonObject>().FirstOrDefault(card => card.GetString("id") == id);
                if (card is null || card.ContainsKey("state") || ToolPresentation.ContainsAction(card.GetString("program")))
                    throw new InvalidDataException("Tool presentation must reference a static card without actions or stored state.");
            }
        }
    }

    private static string QualifiedTool(PluginPackage package, string skill, string tool) =>
        package.BuiltIn && package.Id == "base" && skill == "research" ? tool : "plugin_" + package.Id + "_" + tool;

    private static JsonObject ToolPresentationReference(PluginPackage package, string skill, string card, bool plain) =>
        new() { ["package"] = package.PackageName, ["signer"] = package.Signer, ["skill"] = skill, ["card"] = card, ["plain"] = plain };

    private ToolPresentation? PresentTool(PluginPackage package, string preset, string skill, JsonObject declaration,
        string name, string phase, string text, bool plain, string? locale)
    {
        lock (packageGate)
        {
            if (!IsCurrent(package) || !Available(package, preset)) return null;
            var id = declaration.GetObject("presentation").GetString(phase);
            if (RenderToolPresentation(package, id, name, phase, text, plain, locale) is not { } display) return null;
            return new(display, ToolPresentationReference(package, skill, id, plain));
        }
    }

    internal (JsonObject Origin, JsonObject? Arguments, JsonObject? Result)? LegacyBuiltInToolTrace(string preset, string tool,
        string agent, bool plain)
    {
        lock (packageGate)
        {
            foreach (var package in Volatile.Read(ref packages).Where(package => package.BuiltIn && Available(package, preset)))
            foreach (var skill in package.Manifest.GetArray("skills"))
            {
                if (!skill.GetArray("agents").Any(role => JsonValues.GetText(role) == agent)) continue;
                var id = skill.GetString("name");
                var toolDeclaration = skill.GetArray("tools").FirstOrDefault(item => QualifiedTool(package, id, item.GetString("name")) == tool);
                if (toolDeclaration is null) continue;
                var declaration = toolDeclaration.GetObject("presentation");
                var origin = new JsonObject { ["package"] = package.PackageName, ["signer"] = package.Signer, ["skill"] = id };
                JsonObject? Reference(string phase) => declaration.GetString(phase) is { Length: > 0 } card
                    ? ToolPresentationReference(package, id, card, phase == ToolPresentation.Result && plain) : null;
                return (origin, Reference(ToolPresentation.Arguments), Reference(ToolPresentation.Result));
            }
            return null;
        }
    }

    private (PluginPackage Package, JsonObject Skill, JsonNode? Tool, string? NativeAlias)? ResolveToolTrace(
        JsonObject reference, string preset, string tool, string agent)
    {
        var package = Volatile.Read(ref packages).FirstOrDefault(item => (item.Signed || item.BuiltIn) &&
            item.PackageName == PluginUrls.CanonicalPackage(reference.GetString("package")) && item.Signer == reference.GetString("signer"));
        if (package is null || !Available(package, preset) || agent == "plugin" && !package.BuiltIn) return null;
        var skillId = reference.GetString("skill");
        var skill = package.Manifest.GetArray("skills").OfType<JsonObject>().FirstOrDefault(item => item.GetString("name") == skillId);
        if (skill is null || agent.Length > 0 && !skill.GetArray("agents").Any(role => JsonValues.GetText(role) == agent)) return null;
        var declaration = skill.GetArray("tools").FirstOrDefault(item => QualifiedTool(package, skillId, item.GetString("name")) == tool);
        if (declaration is not null) return (package, skill, declaration, package.BuiltIn ? tool : null);
        var prefix = "plugin_" + package.Id + "_" + skillId;
        if (tool == prefix + "-state" && skill.GetArray("stateEntries").Count > 0)
            return (package, skill, null, "plugin_state_read");
        if (tool == prefix + "-card" && skill.GetArray("cards").Count > 0 && agent is "chat" or "env" or "preset")
            return (package, skill, null, "plugin_card_show");
        return null;
    }

    internal (string? Title, string? SkillTitle, string? NativeAlias)? ReadToolTraceLabels(
        JsonObject origin, string preset, string tool, string agent, string? locale)
    {
        lock (packageGate)
        {
            if (ResolveToolTrace(origin, preset, tool, agent) is not { } resolved) return null;
            var (package, skill, declaration, nativeAlias) = resolved;
            var title = declaration is null ? "" : package.Localization?.Text(declaration, "title", locale) ?? declaration.GetString("title");
            var skillTitle = package.Localization?.Text(skill, "title", locale) ?? skill.GetString("title");
            if (skillTitle.Length == 0 && package.BuiltIn)
            {
                var id = skill.GetString("name");
                var key = package.Id == "base" && id is ("view_asset" or "edit_asset" or "research") ? id : package.Id + "-" + id;
                var candidate = AgentLanguage.SkillName(key, locale ?? AgentLanguage.Default);
                if (candidate != key) skillTitle = candidate;
            }
            return (title.Length > 0 ? title : null, skillTitle.Length > 0 ? skillTitle : null, nativeAlias);
        }
    }

    internal JsonObject? ReadToolPresentation(JsonObject reference, string preset, string tool, string phase, string text,
        string? locale, string agent = "")
    {
        lock (packageGate)
        {
            if (ResolveToolTrace(reference, preset, tool, agent) is not { } resolved) return null;
            var (package, _, declaration, _) = resolved;
            var card = declaration.GetObject("presentation").GetString(phase);
            if (card != reference.GetString("card")) return null;
            return RenderToolPresentation(package, card, tool, phase, text, reference.GetBoolean("plain"), locale);
        }
    }

    private static JsonObject? RenderToolPresentation(PluginPackage package, string id, string tool, string phase,
        string text, bool plain, string? locale)
    {
        if (id.Length == 0) return null;
        var card = FindDeclaration(package, "cards", "id", id);
        var title = package.Localization?.Text(card, "title", locale) ?? card.GetString("title");
        var display = ToolPresentation.Render(card.GetString("program"), title, tool, phase, text, plain, locale);
        if (display is not null)
            foreach (var (key, value) in CardLocaleState(package, card, locale))
                display.GetObject("state")[key] = JsonValues.Clone(value);
        return display;
    }
}
