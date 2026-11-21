using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Keeps the first request prefix stable and appends only changed instructions.</summary>
internal sealed class SessionInstructions(JsonObject saved)
{
    private const string Marker = "_harness_delta";
    public JsonObject State { get; private set; } = (JsonObject)saved.DeepClone();
    public string Base => State.GetString("base");
    public IEnumerable<string> ActiveSkills => State.GetArray("active").Select(item => JsonValues.GetText(item));
    public static bool IsFrame(JsonNode? message) => message.GetBoolean(Marker);

    public void Reset() => State = new();

    public void RememberActiveSkills(IEnumerable<string> names) =>
        State["active"] = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());

    public JsonObject? Update(string system, AgentSkill.Turn skills, JsonArray tools)
    {
        var instructions = skills.InstructionSections();
        var definitions = new JsonObject();
        foreach (var tool in tools)
        {
            var function = tool.GetObject("function");
            definitions[function.GetString("name")] = function.DeepClone();
        }
        var active = new JsonArray(skills.ActiveNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        var previous = State;
        State = new()
        {
            ["base"] = previous.GetString("base", system + skills.Prompt),
            ["system"] = system,
            ["skills"] = instructions,
            ["tools"] = definitions,
            ["active"] = active,
        };
        if (previous.Count == 0) return null;

        var changes = new List<string>();
        if (previous.GetString("system") != system)
            changes.Add("The current task instructions replace the earlier task instructions:\n" + system);
        DescribeChanges(changes, "Skill", previous.GetObject("skills"), instructions);
        DescribeChanges(changes, "Tool", previous.GetObject("tools"), definitions);
        if (changes.Count == 0) return null;
        return new() { ["role"] = "system", [Marker] = true,
            ["content"] = "Session configuration update. Apply these changes from this point onward.\n" + string.Join('\n', changes) };
    }

    private static void DescribeChanges(List<string> output, string label, JsonObject previous, JsonObject current)
    {
        foreach (var (name, value) in current)
        {
            if (JsonNode.DeepEquals(previous[name], value)) continue;
            var detail = label == "Skill" ? ": " + JsonValues.GetText(value) : " (see current tool declaration)";
            output.Add($"{label} {(previous.ContainsKey(name) ? "updated" : "added")}: {name}{detail}");
        }
        foreach (var name in previous.Select(entry => entry.Key).Where(name => !current.ContainsKey(name)))
            output.Add($"{label} removed: {name}");
    }
}
