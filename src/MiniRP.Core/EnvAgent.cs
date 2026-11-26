using System.Text.Json.Nodes;
namespace MiniRP.Core;

public sealed class EnvAgent(Workspace world, AgentSession session, PageSource? web = null)
{
    public async Task<string> Run(string input, bool advanceRequested, CancellationToken ct, JsonArray? attachments = null, string inputRole = "user")
    {
        var initial = world.State;
        var state = (JsonObject)initial.DeepClone();
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
            Tools.Define("set_date", "Manually set the room date when explicitly requested. Use YYYY-MM-DD. In real date mode this override lasts until the next local day.", new()
            {
                ["date"] = Tools.String()
            }, "date"));
        if (!world.AgenticSearch)
        {
            tools.AddNode(Tools.Define("set_chat_search_guidance", "Replace session-local ChatAgent search guidance for this world. Use an empty string to clear it.", new()
            {
                ["guidance"] = Tools.String()
            }, "guidance"));
        }
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
                var visible = (JsonObject)state.DeepClone();
                visible.Remove("manual_date");
                visible["sim_date"] = WorldCalendar.Render(WorldCalendar.Date(world.Config, state, WorldCalendar.Today));
                visible["date_mode"] = WorldCalendar.Mode(world.Config);
                return Task.FromResult<JsonNode?>(visible);
            }

            if (name == "set_chat_search_guidance" && !world.AgenticSearch)
            {
                state["chat_search_guidance"] = a.GetString("guidance");
                return Task.FromResult<JsonNode?>(new JsonObject { ["staged"] = true });
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
            else if (name == "set_date")
            {
                var date = WorldCalendar.RequireDate(a.GetString("date"));
                WorldCalendar.Set(world.Config, state, date, WorldCalendar.Today);
                return Task.FromResult<JsonNode?>(new JsonObject { ["staged"] = true, ["date"] = WorldCalendar.Render(date) });
            }
            else
            {
                throw new ArgumentException("unknown tool");
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["staged"] = true });
        }
        var system = "You are EnvAgent, managing story and established facts. Inspect and make minimal precise edits with tools. World rules constrain you. Speculation in chat is not established fact. World-state edits are staged and committed on successful completion. Explain actual changes concisely in the user's language. " +
            "Decide whether to advance the story using the current request, conversation, established facts, and stage policy. Advance only when they support a concrete next step, and only as far as they support. Leave the phase unchanged for questions and corrections unless they establish that an advancement condition has been met. " +
            (advanceRequested ? "The caller has asked you to consider advancing the story this turn. " : "") +
            "The room calendar follows the real date or advances through a plugin. Use set_date only for an explicit date change, including during story advancement.\n" +
            world.WorldText + "\nStage policy:\n" + JsonValues.Serialize(world.Config["stage"]);
        if (world.Mode == "ensemble")
            system += "\nManage public group chats with ensemble-management. Inspect existing chats and members first. " +
                "Create a chat when the story needs a new group, change its members when who can participate changes, " +
                "and archive a chat when it should leave the active story. Membership and chat changes take effect when the tool succeeds. " +
                "Characters choose where to post within their memberships.";
        if (!world.AgenticSearch)
        {
            system += "\nChatAgent handles search directly. When changes affect search, use set_chat_search_guidance to explain how ChatAgent should apply current canon, interpret real sources, use simulated pages, and keep caches consistent. Preserve unrelated guidance and the preset's base prompt.";
        }
        var reference = "Conversation reference:\n" + session.ToolText(JsonValue.Create(world.Conversation));
        var research = web is not null && session.Assets is not null ? new SearchTurn("direct", "", world, session) : null;
        Task<JsonObject> Ask() => session.Run(system + "\n" + world.AgentPrompt("env"), input,
            new AgentSkill("world-state", "Inspect and edit the staged world state.", tools, Apply, Required: true), ct,
            attachments: attachments, inputRole: inputRole, reference: reference);
        var answer = research is null ? await Ask() : await SearchAgent.InResearch(session, research, web!, Ask);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(answer.GetString("content")))
        {
            throw new IOException("Empty response. Changes not saved.");
        }

        if (!JsonNode.DeepEquals(initial, state) || research?.Changed == true)
        {
            lock (world.RoomGate)
            {
                // A room character may have committed research while the world turn was running.
                var current = (JsonObject)world.State.DeepClone();
                foreach (var key in initial.Select(item => item.Key).Union(state.Select(item => item.Key)))
                {
                    if (JsonNode.DeepEquals(initial[key], state[key])) continue;
                    if (!state.TryGetPropertyValue(key, out var value)) current.Remove(key);
                    else current[key] = JsonValues.Clone(value);
                }
                research?.MergeInto(current);
                world.Commit(current);
            }
        }
        return answer.GetString("content");
    }
}
