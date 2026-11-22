using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginAgent(string name, AgentSession session, PageSource web)
{
    private static readonly AgentSkill TaskSkill = new("plugin-task", "Work on the delegated plugin project.", new(),
        (_, _, _) => throw new InvalidOperationException(), Required: true);

    public Task<JsonObject> Run(string instruction, CancellationToken ct)
    {
        Task<JsonObject> Ask() => session.Run(
            $"""
            You are PluginAgent working on {name}. Complete the delegated implementation and tests. Batch activation for the skills you need next. Their tools are available from the next model request.

            Read base-plugin-docs before using an unfamiliar MiniRP API, manifest field, or tool. Use `development` for the workflow or `多文件 JavaScript`, `assets` with `## Assets` for package resources, and `automation` with `声明状态和卡片` for cards. Batch independent lookups. Use research or curl for a third-party API or external download when the task needs it.

            List the assigned draft through project_assets. Read existing files with view_asset, edit their exact URLs with edit_asset, and link new files from asset_storage. Preserve the assigned manifest identity. Test a multi-file plugin through a `.mjs` entry and a handler that uses an imported module. Keep any container files in a project-specific directory under the preset's shared /workspace.

            Declare the smallest necessary permissions and scope. Validate Action inputs and guard repeated side effects. Run a small draft Action with a known input and check the output. For Android cards, run the card preview and inspect diagnostics and rendered text. After a failure, identify the field, path, scope or output that failed. Check the contract or current file, change one relevant thing, and rerun the fixture. Reuse successful evidence while its inputs and source are unchanged.

            Publish or remove only as authorized. For publication, use the verified draft input and serialize the successful Action's inner `result` into `expected_result_json`. Check the installed generation's verification and scope. Then exercise the real entry point. Read your installed public `minirp://assets/` URL with view_asset. When an entry point needs another role or user interaction, hand off the exact action, input and expected result to PresetAgent. Report the observed results and any checks that remain.
            """,
            instruction, TaskSkill, ct);
        return session.Assets?.PresetResearch is { } research ? research.Run(session, web, Ask) : Ask();
    }
}
