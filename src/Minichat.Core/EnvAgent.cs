using System.Text.Json.Nodes;
namespace Minichat.Core;

public sealed class EnvAgent(Workspace world, AgentSession session)
{
    public async Task<string> Run(string input, bool stage, CancellationToken ct)
    {
        var state = (JsonObject)world.State.DeepClone();
        var initial = DateOnly.Parse(state.GetString("sim_date"));
        var tools = new JsonArray(
            Tools.Define("inspect_world", "Inspect staged facts, phase and date."),
            Tools.Define("add_fact", "Add established fact.", new()
            {
                ["fact"] = Tools.String()
            }, "fact"),
            Tools.Define("replace_fact", "Precisely replace a unique established fact.", new()
            {
                ["old"] = Tools.String(),
                ["new"] = Tools.String()
            }, "old", "new"),
            Tools.Define("remove_fact", "Precisely delete a unique fact.", new()
            {
                ["fact"] = Tools.String()
            }, "fact"),
            Tools.Define("set_phase", "Set current world state.", new()
            {
                ["phase"] = Tools.String()
            }, "phase"),
            Tools.Define("advance_date", "Story mode only, cumulative maximum three days.", new()
            {
                ["days"] = Tools.Integer()
            }, "days"));
        Task<JsonNode?> Apply(string name, JsonObject a, CancellationToken _)
        {
            var canon = state.GetArray("canon");
            if (state["canon"] is null)
            {
                state["canon"] = canon;
            }

            string Required(string key) => string.IsNullOrWhiteSpace(a.GetString(key)) ? throw new ArgumentException(key + " required") : a.GetString(key);
            if (name == "inspect_world")
            {
                return Task.FromResult<JsonNode?>(state.DeepClone());
            }

            if (name == "add_fact")
            {
                var fact = Required("fact");
                if (canon.Any(n => JsonValues.GetText(n) == fact))
                {
                    throw new ArgumentException("duplicate fact");
                }

                canon.AddNode(fact);
            }
            else if (name is "replace_fact" or "remove_fact")
            {
                var old = Required(name == "replace_fact" ? "old" : "fact");
                var indices = Enumerable.Range(0, canon.Count).Where(i => JsonValues.GetText(canon[i]) == old).ToArray();
                if (indices.Length != 1)
                {
                    throw new ArgumentException("fact not unique or missing");
                }

                if (name == "replace_fact")
                {
                    canon[indices[0]] = Required("new");
                }
                else
                {
                    canon.RemoveAt(indices[0]);
                }
            }
            else if (name == "set_phase")
            {
                state["phase"] = Required("phase");
            }
            else if (name == "advance_date")
            {
                var days = a.GetInt32("days", -1);
                var date = DateOnly.Parse(state.GetString("sim_date")).AddDays(days);
                if (!stage || days is < 0 or > 3 || date.DayNumber - initial.DayNumber > 3)
                {
                    throw new ArgumentException("date advance not allowed");
                }

                state["sim_date"] = date.ToString("yyyy-MM-dd");
            }
            else
            {
                throw new ArgumentException("unknown tool");
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["staged"] = true });
        }
        var system = "You are EnvAgent, managing story and established facts. Inspect and make minimal precise edits with tools. World rules constrain you. Speculation in chat is not established fact. Changes are staged and committed only on successful completion. Explain actual changes concisely in the user's language. " + (stage ? "Advance the story a small step; default at most one day, at most three if explicitly requested." : "Edit facts; do not advance story or date.") + "\n" + world.WorldText + "\nStage policy:\n" + JsonValues.Serialize(world.Config["stage"]);
        var answer = await session.Run(system, "Conversation reference:\n" + session.ToolText(JsonValue.Create(world.Conversation)) + "\nRequest:\n" + input, tools, Apply, ct);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(answer.GetString("content")))
        {
            throw new IOException("Empty response. Changes not saved.");
        }

        world.Commit(state);
        return answer.GetString("content");
    }
}
