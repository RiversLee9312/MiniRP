using System.Text.Json.Nodes;
namespace MiniRP.Core;

public sealed class PresetAgent(Workspace world, AgentSession session)
{
    private static readonly HashSet<string> Editable = ["name", "subtitle", "sim_date", "phase", "world.summary", "chat_agent.system_prompt", "chat_agent.greeting_system_message", "search_agent.system_prompt", "env_agent.system_prompt", "preset_agent.system_prompt", "fetch_agent.system_prompt", "title_agent.system_prompt", "search_editor.relevance", "search_editor.scope", "search_editor.credibility", "search_editor.add_guidance", "page_editor.guidance", "page_generator.guidance", "stage.guidance", "search_editor.max_results", "search_editor.max_visible", "search_editor.max_added"];
    public async Task<string> Run(string input, CancellationToken ct, JsonArray? attachments = null, string inputRole = "user",
        bool privateInput = false)
    {
        var config = (JsonObject)world.Config.DeepClone();
        var tools = new JsonArray(Tools.Define("inspect_preset", "Inspect current staged preset."), Tools.Define("set_value", "Set one supported dot path, including name, chat_agent.system_prompt, chat_agent.greeting_system_message and agentic_search (true or false). An empty greeting disables the opening message.", new()
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
        tools.AddNode(Tools.Define("configure_experience", "Set the agreed conversation mode, delegated search choice and initial ensemble. For an ensemble supply agents [{id,name,prompt}] and chats [{name,members:[agent IDs]}]. The app assigns chat UUIDs.", new()
        {
            ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("character", "ensemble", "tabletop") },
            ["agentic_search"] = Tools.Boolean(),
            ["ensemble"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
        }, "mode", "agentic_search"));
        if (config.GetString("setup_status") == "draft")
            tools.AddNode(Tools.Define("finish_preset_setup", "Complete setup after the user confirms the experience and search choice."));
        Task<JsonNode?> Apply(string name, JsonObject a, CancellationToken token)
        {
            if (name == "configure_experience")
                return Task.FromResult<JsonNode?>(PresetDesign.SetExperience(config, a, () => world.ChatEntries(world.PresetId).Any()));
            if (name == "finish_preset_setup")
            {
                if (config.GetString("setup_status") != "draft" || !config.ContainsKey("chat_mode") || !config.ContainsKey("agentic_search"))
                    throw new InvalidOperationException("Confirm the experience and delegated search choice with configure_experience first.");
                config["setup_status"] = "complete";
                return Task.FromResult<JsonNode?>(new JsonObject { ["completed"] = true });
            }
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
                    var value = a.GetString("value");
                    if ((key is "system_prompt" or "greeting_system_message") && value.Length > 16000)
                        throw new ArgumentException("A system prompt or greeting can contain up to 16,000 characters.");
                    if (path == "name")
                    {
                        value = value.Trim();
                        if (value.Length is 0 or > 140 || value.Any(char.IsControl))
                            throw new ArgumentException("Provide a single-line name of up to 140 characters.");
                    }
                    target[key] = value;
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
        var guidance = "You are PresetAgent. Edit the preset with precise tool calls. Keep unrelated settings. Give new presets a short descriptive name. Set a tailored chat_agent.system_prompt and search_agent.system_prompt. Chat instructions support {date}. Use an empty prompt to restore defaults. " +
            "If the user wants an opening scene or a welcome, set chat_agent.greeting_system_message to private instructions for ChatAgent to generate it when a new conversation begins. Put scene and voice details there. Leave it empty for conversations that should wait for the user's first message. Explain changes briefly in the user's language.\n" +
            PresetDesign.Practice + "\n" + (config.GetString("setup_status") == "draft" ? PresetDesign.Interview + "\n" : "") +
            JsonValues.Serialize(config) + "\n" + world.AgentPrompt("preset");
        var answer = await session.Run(guidance, input,
            new AgentSkill("preset-editing", "Inspect and edit the staged preset.", tools, Apply, Required: true), ct,
            attachments: attachments, inputRole: inputRole, privateInput: privateInput);
        ct.ThrowIfCancellationRequested();
        if (answer.GetString("content").Length == 0)
        {
            throw new IOException("Empty response. Preset not saved.");
        }

        world.CommitConfig(config);
        return answer.GetString("content");
    }
}
