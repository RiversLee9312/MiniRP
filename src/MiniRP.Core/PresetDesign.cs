using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class PresetDesign
{
    internal const string Interview = """
        This preset is in setup. Have a short interview. Ask one question per reply and wait. Skip questions the user has already answered. Do not ask the user to write prompts or know the implementation.
        Start with what they want to experience. Then offer three ways to play in plain language: talk to one character while the world changes; follow several characters in separate chats while the world changes; or talk to a game master who runs scenes and rules like a tabletop game. Ask about the user's role and the starting situation where useful. For an ensemble, agree on an initial cast and chats.
        Before asking about search, activate base-plugin-docs and read the search topic. Briefly explain what delegated search changes in their scenario, in their language. Ask whether to use it. Accept a choice of either setting and honor it. Set up the world, a short name, and tailored ChatAgent and SearchAgent instructions when enough is known. Keep any hidden world instructions in the world or SearchAgent configuration. Review the result briefly with the user before calling finish_preset_setup.
        """;

    internal const string Practice = """
        Design the experience around what the user wants to do. A preset can be an investigation, shared project, living simulation, tabletop game, playable interface or something new.
        Proactively suggest plugins, OpenUI cards, and worthwhile interactions during setup and when improving an existing preset. Tie each suggestion to the user's scenario. For example, a station map can reveal clues when a location is tapped, a quest board can update after an encounter, and a roll button can show an outcome and update a character sheet. Explain what the user would see, what they would do, and what would change. Offer a clear first feature and take care of the prompts, code, and wiring for them. Use the user's language and introduce technical terms only as needed.
        Prefer a generated OpenUI card for a quick interaction. Recommend a scoped plugin with a reusable card when the experience needs persistent state, actions, or future updates. Reuse suitable installed capabilities. When the user wants to proceed, build a small working version first, delegate plugin work when needed, and verify the actual interactions in a preview or test. Tell the user where to try it and what they should see. Respect their choices about scope and deployment.
        Use the built-in preset and automation documentation for mode choices and plugin architecture. Prefer small reusable Skills and explicit events over prompts that guess whether something happened. A conversation agent can invoke a plugin tool. A plugin Action can then ask EnvAgent to perform a focused update. EnvAgent should enforce world rules and notify affected participants with system messages. Keep durable structured state in declared DB entries, render it with reusable OpenUI cards and subscribe to specific changes. Prevent duplicate effects when events repeat. Give PluginAgents clear boundaries, test realistic fixtures, and follow the user's wishes on deployment.
        """;

    public static JsonObject SetExperience(JsonObject config, JsonObject arguments, Func<bool> hasConversations)
    {
        var mode = arguments.GetString("mode");
        if (mode is not ("character" or "ensemble" or "tabletop"))
            throw new ArgumentException("Choose character, ensemble, or tabletop.");
        var mayChoose = config.GetString("setup_status") == "draft" && !config.ContainsKey("chat_mode") && !hasConversations();
        if (!mayChoose && (mode == "ensemble") != (config.GetString("chat_mode") == "ensemble"))
            throw new InvalidOperationException("An ensemble preset and a regular preset are separate. Create a new preset to use the other format.");
        if (arguments["agentic_search"] is not JsonValue search || !search.TryGetValue<bool>(out var delegated))
            throw new ArgumentException("Set agentic_search to the user's choice.");
        if (mode == "ensemble") config["ensemble"] = EnsembleDefinition(arguments.GetObject("ensemble"));
        config["chat_mode"] = mode;
        config["agentic_search"] = delegated;
        return new() { ["staged"] = true, ["mode"] = mode, ["agentic_search"] = delegated };
    }

    public static JsonObject EnsembleDefinition(JsonObject input)
    {
        var agents = input.GetArray("agents");
        var chats = input["chats"] is JsonArray configured ? configured : input.GetArray("rooms");
        if (agents.Count is < 2 or > 24 || chats.Count is < 1 or > 32)
            throw new ArgumentException("An ensemble needs 2–24 agents and 1–32 chats.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            var id = Id(agent.GetString("id"));
            Name(agent.GetString("name"));
            if (!ids.Add(id)) throw new ArgumentException("Agent IDs must be unique.");
            if (agent.GetString("prompt").Length > 8000) throw new ArgumentException("Agent instructions exceed 8,000 characters.");
        }
        var channels = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new JsonArray();
        foreach (var chat in chats)
        {
            Name(chat.GetString("name"));
            var members = chat.GetArray("members").Select(item => JsonValues.GetText(item)).ToArray();
            if (members.Length == 0 || members.Distinct(StringComparer.Ordinal).Count() != members.Length || members.Any(member => !ids.Contains(member)))
                throw new ArgumentException("Each chat needs one or more unique known agent IDs.");
            var id = chat.GetString("id") is { Length: > 0 } existing ? WorkspaceIds.Require(existing) : WorkspaceIds.New();
            if (!channels.Add(id)) throw new ArgumentException("Chat IDs must be unique.");
            definitions.AddNode(new JsonObject { ["id"] = id, ["name"] = chat.GetString("name"),
                ["members"] = chat.GetArray("members").DeepClone() });
        }
        return new() { ["agents"] = agents.DeepClone(), ["chats"] = definitions };
    }

    public static string Id(string value) => value.Length <= 48 ? Workspace.Safe(value) : throw new ArgumentException("ID exceeds 48 characters.");
    public static string Name(string value) => value.Trim() is { Length: > 0 and <= 120 } name && !name.Any(char.IsControl) ?
        name : throw new ArgumentException("Provide a single-line name of up to 120 characters.");

    public static string ChatGuidance(string mode) => mode switch
    {
        "character" => "If the preset defines a character, speak from their perspective and keep the wider world consistent.",
        "ensemble" => "Stay in the selected character's perspective. Read the chat notices and use the chat skill to send only what others should see.",
        "tabletop" => "Act as the tabletop game master. Set scenes, interpret the rules, explain consequences, and leave player decisions to the user. Use the tabletop skill to resolve uncertain actions when the game calls for it.",
        _ => "",
    };

    public static string SearchGuidance(string mode) => mode switch
    {
        "ensemble" => "Serve the requesting character's perspective. Filter evidence by what the character can discover, and respect private information in other chats.",
        "tabletop" => "Help the game master find relevant rules and setting material. Distinguish actual rules, optional variants and claims made inside the game.",
        _ => "Respect the lead character's perspective, established facts, and what they could plausibly discover.",
    };

    public static AgentSkill Tabletop(HostPort port) => new("tabletop", "Resolve uncertain actions using fair dice.", new JsonArray(
        Tools.Define("roll_dice", "Roll dice and return each result and the total.", new()
            { ["count"] = Tools.Integer(), ["sides"] = Tools.Integer(), ["modifier"] = Tools.Integer() }, "count", "sides")),
        async (_, args, ct) =>
        {
            var count = args.GetInt32("count");
            var sides = args.GetInt32("sides");
            if (count is < 1 or > 30 || sides is < 2 or > 1000) throw new ArgumentException("Roll 1–30 dice with 2–1,000 sides.");
            var rolls = await RollDice(port, count, sides, ct);
            return new JsonObject { ["rolls"] = new JsonArray(rolls.Select(roll => (JsonNode?)JsonValue.Create(roll)).ToArray()),
                ["total"] = (long)rolls.Sum() + args.GetInt32("modifier") };
        });

    private static
#if MINIRP_ANDROID_BIONIC
        async Task<int[]> RollDice(HostPort port, int count, int sides, CancellationToken ct)
    {
        var reply = await port.CallHost("roll_dice", new() { ["count"] = count, ["sides"] = sides }, ct, TimeSpan.FromSeconds(15));
        var rolls = reply.GetArray("rolls").Select(value => value is JsonValue n && n.TryGetValue<int>(out var roll) ? roll : 0).ToArray();
        if (rolls.Length != count || rolls.Any(roll => roll < 1 || roll > sides))
            throw new InvalidDataException("Invalid dice results from the platform.");
        return rolls;
    }
#else
        Task<int[]> RollDice(HostPort port, int count, int sides, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Enumerable.Range(0, count)
            .Select(_ => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, sides + 1)).ToArray());
    }
#endif
}
