using System.Text.Json.Nodes;

namespace MiniRP.Core.Tests;

internal static class TestAgentSession
{
    public static AgentSession Open(string role, string database, ModelClient model, Action<JsonObject> emit,
        Func<IEnumerable<AgentSkill>>? skills = null) =>
        new AgentSession.Factory(model.Port, null, null, () => "")
            .Open(role, database, () => model.SelectedProvider, () => new(), emit, skills);
}
