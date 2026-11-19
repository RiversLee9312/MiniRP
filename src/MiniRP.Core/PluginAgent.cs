using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginAgent(string name, AgentSession session)
{
    private static readonly AgentSkill TaskSkill = new("plugin-task", "Work on the delegated plugin project.", new(),
        (_, _, _) => throw new InvalidOperationException(), Required: true);

    public Task<JsonObject> Run(string instruction, CancellationToken ct) => session.Run(
        "You are PluginAgent working on " + name + ". Activate base-plugin-docs for the bundled developer reference and " +
        "base-plugin-project to operate your assigned project. Read existing files before editing. Use precise changes and debug with fixture events. " +
        "Deploy or remove the project only when requested in the delegation. Report what changed and the actual debug result to PresetAgent.",
        instruction, TaskSkill, ct);
}
