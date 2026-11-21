using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginAgent(string name, AgentSession session, PageSource web)
{
    private static readonly AgentSkill TaskSkill = new("plugin-task", "Work on the delegated plugin project.", new(),
        (_, _, _) => throw new InvalidOperationException(), Required: true);

    public Task<JsonObject> Run(string instruction, CancellationToken ct)
    {
        Task<JsonObject> Ask() => session.Run(
            "You are PluginAgent working on " + name + ". Activate base-plugin-docs for the bundled developer reference and " +
            "base-plugin-project to operate your assigned project. Read existing files before editing. Use precise changes and debug with fixture events. " +
            "Activate research for real_search and real_fetch, or curl for raw public downloads. Use view_asset and asset_storage to work with the Assets " +
            "shared with PresetAgent. The container's /workspace is shared across the preset. Use project-specific paths to avoid overwriting other work. " +
            "Deploy or remove the project only when requested in the delegation. Report what changed, the actual debug result, and useful Asset URLs to PresetAgent.",
            instruction, TaskSkill, ct);
        return session.Assets?.PresetResearch is { } research ? research.Run(session, web, Ask) : Ask();
    }
}
