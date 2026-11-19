using System.Text.Json.Nodes;

namespace Minichat.Core;

public sealed record AgentSkill(
    string Name,
    string Description,
    JsonArray Tools,
    Func<string, JsonObject, CancellationToken, Task<JsonNode?>> Execute,
    bool Required = false,
    string Instructions = "",
    Action? OnActivate = null,
    Action<string, JsonObject>? OnEvent = null);

internal sealed class SkillTurn
{
    private const string Activate = "activate_skill";
    private readonly Dictionary<string, AgentSkill> catalog;
    private readonly Dictionary<string, AgentSkill> enabled = new(StringComparer.Ordinal);
    private readonly HashSet<string> loaded = new(StringComparer.Ordinal);

    public SkillTurn(IEnumerable<AgentSkill> skills)
    {
        catalog = skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        foreach (var skill in catalog.Values.Where(skill => skill.Required))
        {
            Load(skill);
        }
    }

    public string Prompt => catalog.Count == 0 ? "" : "\nAvailable skills (use activate_skill to load one before calling its tools):\n" +
        string.Join("\n", catalog.Values.Select(skill => $"- {skill.Name}: {skill.Description}" +
            (skill.Required && skill.Instructions.Length > 0 ? "\n" + skill.Instructions : "")));

    public JsonArray Definitions()
    {
        var tools = new JsonArray();
        if (catalog.Keys.Any(name => !loaded.Contains(name)))
        {
            tools.AddNode(Tools.Define(Activate, "Load a skill's instructions and tools. Choose the skill by its name in Available skills.", new() { ["name"] = Tools.String() }, "name"));
        }
        foreach (var skill in catalog.Values.Where(skill => loaded.Contains(skill.Name)))
        {
            foreach (var tool in skill.Tools)
            {
                tools.AddNode(JsonValues.Clone(tool));
            }
        }
        return tools;
    }

    public async Task<JsonNode?> Invoke(string name, JsonObject args, CancellationToken ct)
    {
        if (name == Activate)
        {
            var key = args.GetString("name");
            if (!catalog.TryGetValue(key, out var skill))
            {
                throw new ArgumentException("This skill is unavailable in this session.");
            }
            if (!loaded.Contains(key))
            {
                Load(skill);
                skill.OnActivate?.Invoke();
            }
            return new JsonObject { ["name"] = skill.Name, ["instructions"] = skill.Instructions, ["tools"] = new JsonArray(skill.Tools.Select(tool => (JsonNode?)JsonValue.Create(ToolName(tool))).ToArray()) };
        }
        if (!enabled.TryGetValue(name, out var owner))
        {
            throw new InvalidOperationException("This tool is unavailable. Activate its skill first.");
        }
        owner.OnEvent?.Invoke("tool.started", new() { ["name"] = "tool.started", ["tool"] = name, ["arguments"] = args.DeepClone() });
        var result = await owner.Execute(name, args, ct);
        owner.OnEvent?.Invoke("tool.finished", new() { ["name"] = "tool.finished", ["tool"] = name, ["result"] = JsonValues.Clone(result) });
        return result;
    }

    public void Broadcast(string name, JsonObject data)
    {
        foreach (var skill in loaded.Select(key => catalog[key]))
        {
            skill.OnEvent?.Invoke(name, (JsonObject)data.DeepClone());
        }
    }

    private void Load(AgentSkill skill)
    {
        if (loaded.Contains(skill.Name))
        {
            return;
        }
        var names = skill.Tools.Select(ToolName).ToArray();
        if (names.Any(name => name.Length == 0 || name == Activate || enabled.ContainsKey(name)) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new ArgumentException("Skill contains duplicate or reserved tools: " + skill.Name);
        }
        loaded.Add(skill.Name);
        foreach (var name in names)
        {
            enabled.Add(name, skill);
        }
    }

    private static string ToolName(JsonNode? tool) => tool.GetObject("function").GetString("name");
}
