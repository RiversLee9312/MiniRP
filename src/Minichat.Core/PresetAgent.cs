using System.Text.Json.Nodes;
namespace Minichat.Core;

public sealed class PresetAgent(Workspace world, AgentSession session)
{
    static readonly HashSet<string> Editable = ["name", "subtitle", "sim_date", "phase", "world.summary", "chat_agent.system_prompt", "search_editor.relevance", "search_editor.scope", "search_editor.credibility", "search_editor.add_guidance", "page_editor.guidance", "page_generator.guidance", "stage.guidance", "search_editor.max_results", "search_editor.max_visible", "search_editor.max_added"];
    public async Task<string> Run(string input, CancellationToken ct, JsonArray? attachments = null)
    {
        var config = (JsonObject)world.Config.DeepClone();
        var tools = new JsonArray(Tools.Define("inspect_preset", "Inspect current staged preset."), Tools.Define("set_value", "Set one supported dot path, including chat_agent.system_prompt and agentic_search (true or false). Empty prompt restores default.", new()
        {
            ["path"] = Tools.String(),
            ["value"] = Tools.String()
        }, "path", "value"), Tools.Define("append_item", "Append canon or world.rules item.", new()
        {
            ["path"] = Tools.String(),
            ["value"] = Tools.String()
        }, "path", "value"), Tools.Define("remove_item", "Remove canon or world.rules item by zero-based index.", new()
        {
            ["path"] = Tools.String(),
            ["index"] = Tools.Integer()
        }, "path", "index"));
        Task<JsonNode?> Apply(string name, JsonObject a, CancellationToken token)
        {
            if (name == "inspect_preset")
            {
                return Task.FromResult<JsonNode?>(config.DeepClone());
            }

            var path = a.GetString("path");
            var bits = path.Split('.');
            JsonObject target = config;
            if (name == "set_value" && path != "agentic_search" && !Editable.Contains(path))
            {
                throw new ArgumentException("unsupported path");
            }

            if (name is "append_item" or "remove_item" && path is not ("canon" or "world.rules"))
            {
                throw new ArgumentException("unsupported list");
            }

            foreach (var bit in bits[..^1])
            {
                if (target[bit] is null)
                {
                    target[bit] = new JsonObject();
                }

                target = target[bit]!.AsObject();
            }
            var key = bits[^1];
            if (name == "set_value")
            {
                if (path == "sim_date" && !DateOnly.TryParseExact(a.GetString("value"), "yyyy-MM-dd", out _))
                {
                    throw new ArgumentException("date must be YYYY-MM-DD");
                }

                if (path == "agentic_search")
                {
                    target[key] = bool.Parse(a.GetString("value"));
                }
                else if (key.StartsWith("max_", StringComparison.Ordinal))
                {
                    target[key] = int.Parse(a.GetString("value"));
                }
                else
                {
                    target[key] = a.GetString("value");
                }
            }
            else if (name == "append_item")
            {
                if (target[key] is null)
                {
                    target[key] = new JsonArray();
                }

                var list = target[key]!.AsArray();
                if (a.GetString("value").Length == 0 || list.Any(n => JsonValues.GetText(n) == a.GetString("value")))
                {
                    throw new ArgumentException("empty or duplicate item");
                }

                list.AddNode(a.GetString("value"));
            }
            else if (name == "remove_item")
            {
                target[key]!.AsArray().RemoveAt(a.GetInt32("index", -1));
            }
            else
            {
                throw new ArgumentException("unknown tool");
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["staged"] = true });
        }
        var answer = await session.Run("You are PresetAgent. Edit this world preset with precise minimal tool calls. Do not rewrite unrelated settings. chat_agent.system_prompt configures the main agent and supports {date}; empty uses default. Explain changes briefly in user's language.\n" + JsonValues.Serialize(config), input, tools, Apply, ct, attachments: attachments);
        ct.ThrowIfCancellationRequested();
        if (answer.GetString("content").Length == 0)
        {
            throw new IOException("Empty response. Preset not saved.");
        }

        world.CommitConfig(config);
        return answer.GetString("content");
    }
}
