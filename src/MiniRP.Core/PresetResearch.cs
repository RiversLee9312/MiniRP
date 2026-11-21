using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PresetResearch(Workspace workspace, MemoryDb db)
{
    internal const string StateKey = "_context/preset-research";

    internal JsonObject Read() => JsonValues.ParseObject(db.Get(StateKey) ?? "{}");

    internal void Set(string collection, string key, JsonNode value) =>
        db.UpdateInternal(StateKey, stored =>
        {
            var state = JsonValues.ParseObject(stored ?? "{}");
            if (state[collection] is not JsonObject entries)
                state[collection] = entries = new();
            entries[key] = value.DeepClone();
            return JsonValues.Serialize(state);
        });

    internal ResearchAssets Assets() => new(workspace, new(), this);

    internal Task<JsonObject> Run(AgentSession session, PageSource source, Func<Task<JsonObject>> run) =>
        SearchAgent.InResearch(session, new SearchTurn("direct", "", workspace, session, Read(), this), source, run);
}
