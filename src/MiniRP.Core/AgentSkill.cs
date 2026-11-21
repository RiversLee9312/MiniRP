using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed class AgentSkill(
    string Name,
    string Description,
    JsonArray Tools,
    Func<string, JsonObject, CancellationToken, Task<JsonNode?>> Execute,
    bool Required = false,
    string Instructions = "",
    Action? OnActivate = null,
    Action<string, JsonObject>? OnEvent = null)
{
    public string Name { get; } = Name;
    private readonly string description = Description;
    private readonly string instructions = Instructions;
    private readonly JsonArray tools = (JsonArray)Tools.DeepClone();
    private readonly bool required = Required;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonNode?>> execute = Execute;
    private readonly Action? onActivate = OnActivate;
    private readonly Action<string, JsonObject>? onEvent = OnEvent;

    internal sealed class Turn
    {
        private const string Activate = "activate_skill";
        private const string Management = "skill-management";
        private readonly Dictionary<string, AgentSkill> catalog;
        private readonly Dictionary<string, (AgentSkill Owner, JsonObject Definition)> offered = new(StringComparer.Ordinal);
        private readonly HashSet<string> active = new(StringComparer.Ordinal);

        public Turn(IEnumerable<AgentSkill> skills, IEnumerable<string>? previouslyActive = null)
        {
            catalog = skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
            if (catalog.ContainsKey(Management) || catalog.Keys.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Skills must have a name distinct from the runtime management skill.");

            var retained = previouslyActive?.ToHashSet(StringComparer.Ordinal);
            if (retained?.Contains("assets") == true) retained.Add("view_asset");
            foreach (var skill in catalog.Values)
                if (skill.required || retained?.Contains(skill.Name) == true) active.Add(skill.Name);

            IEnumerable<AgentSkill> all = catalog.Values;
            if (active.Count < catalog.Count)
            {
                all = all.Prepend(new AgentSkill(Management, "Load a session skill.", new JsonArray(
                    MiniRP.Core.Tools.Define(Activate, "Activate a skill before the next model round. Choose a name from Available skills.",
                        new() { ["name"] = MiniRP.Core.Tools.String() }, "name")), ActivateSkill, Required: true));
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var skill in all)
            {
                foreach (var definition in skill.tools)
                {
                    var name = ToolName(definition);
                    if (definition is not JsonObject function || function.GetString("type") != "function" ||
                        string.IsNullOrWhiteSpace(name) || name == Activate && skill.Name != Management || !names.Add(name))
                        throw new ArgumentException("Invalid, duplicate or reserved tool in skill: " + skill.Name);
                    if (skill.required || active.Contains(skill.Name)) offered.Add(name, (skill, function));
                }
            }
        }

        public IEnumerable<string> ActiveNames => catalog.Keys.Where(active.Contains);
        public bool IsActive(string skill) => active.Contains(skill);
        public bool Offers(string tool) => offered.ContainsKey(tool);

        public JsonObject InstructionSections() => new(catalog.Values.Select(skill =>
            new KeyValuePair<string, JsonNode?>(skill.Name, JsonValue.Create(skill.description +
                (active.Contains(skill.Name) ? " [active]\n" + skill.instructions : " [available]")))));

        public string Prompt => catalog.Count == 0 ? "" : "\nAvailable skills (use activate_skill to load one before calling its tools):\n" +
            string.Join("\n", catalog.Values.Select(skill => $"- {skill.Name}: {skill.description}" +
                (active.Contains(skill.Name) && skill.instructions.Length > 0 ? "\n" + skill.instructions : "")));

        public JsonArray Definitions() => new(offered.Values.Select(tool => tool.Definition.DeepClone()).ToArray());

        public async Task<JsonNode?> Invoke(string name, JsonObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!offered.TryGetValue(name, out var tool))
                throw new InvalidOperationException("This tool was not offered for this model request. Activate its skill and wait for the next request.");
            var owner = tool.Owner;
            owner.onEvent?.Invoke("tool.started", new() { ["name"] = "tool.started", ["tool"] = name, ["arguments"] = args.DeepClone() });
            var result = await owner.execute(name, args, ct);
            owner.onEvent?.Invoke("tool.finished", new() { ["name"] = "tool.finished", ["tool"] = name, ["result"] = JsonValues.Clone(result) });
            return result;
        }

        private Task<JsonNode?> ActivateSkill(string _, JsonObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!catalog.TryGetValue(args.GetString("name"), out var skill))
                throw new ArgumentException("This skill is unavailable in this session.");
            // Activation persists into the next request. This request's offered tools are fixed.
            if (active.Add(skill.Name)) skill.onActivate?.Invoke();
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["name"] = skill.Name, ["instructions"] = skill.instructions,
                ["tools"] = new JsonArray(skill.tools.Select(tool => (JsonNode?)JsonValue.Create(ToolName(tool))).ToArray())
            });
        }

        public void Broadcast(string name, JsonObject data)
        {
            foreach (var skill in ActiveNames.Select(key => catalog[key]))
                skill.onEvent?.Invoke(name, (JsonObject)data.DeepClone());
        }

        private static string ToolName(JsonNode? tool) => tool.GetObject("function").GetString("name");
    }
}
