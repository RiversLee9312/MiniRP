using System.Text.Json.Nodes;
namespace MiniRP.Core;

public sealed class PresetAgent(Workspace world, AgentSession session)
{
    private readonly PluginRuntime? plugins;

    internal PresetAgent(Workspace world, AgentSession session, PluginRuntime plugins) : this(world, session)
    {
        this.plugins = plugins;
    }

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
        tools.AddNode(Tools.Define("list_chat_backgrounds", "List image, GIF and video Asset URLs declared by installed plugins belonging to this preset."));
        tools.AddNode(Tools.Define("configure_date", "Choose the calendar source for this preset. Real follows the device's local date. Plugin lets a plugin advance each room's date. Optionally set initial_date for new plugin-managed rooms in YYYY-MM-DD format.", new()
        {
            ["mode"] = WorldCalendar.ModeSchema(), ["initial_date"] = Tools.String(),
        }, "mode"));
        tools.AddNode(Tools.Define("configure_chat_appearance", "Change the preset's chat and home appearance. Use a URL from list_chat_backgrounds. Set background_url to an empty string to remove it. Chat background blur defaults to true. Set theme_color to #RRGGBB or an empty string to use the background color. Omit fields you want to keep.", new()
        {
            ["background_url"] = Tools.String(), ["background_blur"] = Tools.Boolean(), ["theme_color"] = Tools.String(),
        }));
        if (config.GetString("setup_status") == "draft")
            tools.AddNode(Tools.Define("finish_preset_setup", "Complete setup after the user confirms the experience and search choice."));
        Task<JsonNode?> Apply(string name, JsonObject a, CancellationToken token)
        {
            if (name == "configure_experience")
                return Task.FromResult<JsonNode?>(PresetDesign.SetExperience(config, a, () => world.ChatEntries(world.PresetId).Any()));
            if (name == "list_chat_backgrounds")
                return Task.FromResult<JsonNode?>(plugins?.PresetBackgroundAssets(world.PresetId) ?? new JsonArray());
            if (name == "configure_date")
            {
                var mode = a.GetString("mode");
                var initial = a.ContainsKey("initial_date") ? WorldCalendar.RequireDate(a.GetString("initial_date")) : (DateOnly?)null;
                WorldCalendar.Configure(config, mode, WorldCalendar.Today);
                if (initial is { } date) config["sim_date"] = WorldCalendar.Render(date);
                return Task.FromResult<JsonNode?>(new JsonObject { ["staged"] = true, ["mode"] = mode });
            }
            if (name == "configure_chat_appearance")
                return Task.FromResult<JsonNode?>(ConfigureAppearance(config, a));
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
                if (path == "sim_date") _ = WorldCalendar.RequireDate(a.GetString("value"));

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
            "When asked to set a chat background, call list_chat_backgrounds, then configure_chat_appearance with an Asset declared by an installed plugin owned by this preset. Leave chat blur enabled unless the user requests clear playback. The theme normally follows the Asset; use theme_color for an explicit override.\n" +
            "Use configure_date to choose a real or plugin-managed calendar. For a plugin-managed timeline, a scoped plugin can call harness.getWorldDate and harness.setWorldDate. EnvAgent can manually set the date of an existing room with its world-state skill.\n" +
            "Use inspect_preset to read the current staged preset before relying on existing settings. It reflects changes made by tools during this turn.\n" +
            PresetDesign.Practice + "\n" + (config.GetString("setup_status") == "draft" ? PresetDesign.Interview + "\n" : "") +
            world.AgentPrompt("preset");
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

    private JsonObject ConfigureAppearance(JsonObject config, JsonObject args)
    {
        var appearance = (JsonObject)config.GetObject("appearance").DeepClone();
        if (args.ContainsKey("background_url"))
        {
            if (args["background_url"] is not JsonValue value || !value.TryGetValue<string>(out var url))
                throw new ArgumentException("Provide an Asset URL or an empty string.");
            if (url.Length == 0) appearance.Remove("background_url");
            else
            {
                (plugins ?? throw new InvalidOperationException("Preset assets are unavailable.")).RequirePresetBackground(url, world.PresetId);
                appearance["background_url"] = url;
            }
        }
        if (args.ContainsKey("background_blur"))
        {
            if (args["background_blur"] is not JsonValue value || !value.TryGetValue<bool>(out var blur))
                throw new ArgumentException("background_blur must be true or false.");
            appearance["background_blur"] = blur;
        }
        if (args.ContainsKey("theme_color"))
        {
            if (args["theme_color"] is not JsonValue value || !value.TryGetValue<string>(out var color) ||
                color.Length > 0 && (color.Length != 7 || color[0] != '#' || !color[1..].All(char.IsAsciiHexDigit)))
                throw new ArgumentException("Provide a #RRGGBB theme color or an empty string.");
            if (color.Length == 0) appearance.Remove("theme_color");
            else appearance["theme_color"] = color.ToUpperInvariant();
        }
        config["appearance"] = appearance;
        return (JsonObject)appearance.DeepClone();
    }
}
