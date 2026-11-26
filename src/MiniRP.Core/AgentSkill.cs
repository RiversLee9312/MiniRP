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
    Action<string, JsonObject>? OnEvent = null,
    string DisplayName = "")
{
    public string Name { get; } = Name;
    internal static readonly AgentSkill SubagentTask = new("subagent-task", "Complete the delegated task.", new(),
        (_, _, _) => throw new InvalidOperationException(), Required: true);
    internal Func<string, string, string, bool, ToolPresentation?>? PresentTool { get; init; }
    internal Func<string, JsonObject?>? PluginToolSource { get; init; }
    private readonly string displayName = DisplayName;
    private readonly string description = Description;
    private readonly string instructions = Instructions;
    private readonly JsonArray tools = (JsonArray)Tools.DeepClone();
    private readonly bool required = Required;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonNode?>> execute = Execute;
    private readonly Action? onActivate = OnActivate;
    private readonly Action<string, JsonObject>? onEvent = OnEvent;

    public string GetDisplayName(string locale) => string.IsNullOrWhiteSpace(displayName)
        ? AgentLanguage.SkillName(Name, locale) : displayName;

    internal sealed class Turn
    {
        private const string Activate = "activate_skill";
        private const string Management = "skill-management";
        private readonly Dictionary<string, AgentSkill> catalog;
        private readonly Dictionary<string, AgentSkill> owners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (AgentSkill Owner, JsonObject Definition)> offered = new(StringComparer.Ordinal);
        private readonly HashSet<string> active = new(StringComparer.Ordinal);
        private readonly string locale;

        public Turn(IEnumerable<AgentSkill> skills, IEnumerable<string>? previouslyActive = null, string locale = AgentLanguage.Default)
        {
            this.locale = locale;
            catalog = skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
            if (catalog.ContainsKey(Management) || catalog.Keys.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Skills must have a name distinct from the runtime management skill.");

            var retained = previouslyActive?.Select(AvailableSkillId).ToHashSet(StringComparer.Ordinal);
            foreach (var skill in catalog.Values)
                if (skill.required || retained?.Contains(skill.Name) == true) active.Add(skill.Name);

            IEnumerable<AgentSkill> all = catalog.Values;
            if (active.Count < catalog.Count)
            {
                all = all.Prepend(new AgentSkill(Management, "Load a session skill.", new JsonArray(
                    MiniRP.Core.Tools.Define(Activate, AgentLanguage.SkillName(Management, locale) +
                        ": Activate a skill before the next model round. Pass its exact ID from Available skills as name.",
                        new() { ["name"] = MiniRP.Core.Tools.String() }, "name")), ActivateSkill, Required: true));
            }
            foreach (var skill in all)
            {
                foreach (var definition in skill.tools)
                {
                    var name = ToolName(definition);
                    if (definition is not JsonObject function || function.GetString("type") != "function" ||
                        string.IsNullOrWhiteSpace(name) || name == Activate && skill.Name != Management || !owners.TryAdd(name, skill))
                        throw new ArgumentException("Invalid, duplicate or reserved tool in skill: " + skill.Name);
                    if (skill.required || active.Contains(skill.Name)) offered.Add(name, (skill, function));
                }
            }
        }

        public IEnumerable<string> ActiveNames => catalog.Keys.Where(active.Contains);
        public bool IsActive(string skill) => active.Contains(skill);
        public bool Offers(string tool) => offered.ContainsKey(tool);
        public string? OwnerOf(string tool) => owners.GetValueOrDefault(tool)?.Name;

        public JsonObject InstructionSections() => new(catalog.Values.Select(skill =>
            new KeyValuePair<string, JsonNode?>(skill.Name, JsonValue.Create(SkillDescription(skill) +
                (active.Contains(skill.Name) ? " [active]\n" + skill.instructions : " [available]")))));

        public string Prompt => catalog.Count == 0 ? "" : "\nAvailable skills (pass the exact skill ID before the colon to activate_skill as `name`):\n" +
            string.Join("\n", catalog.Values.Select(skill => $"- {skill.Name}: {SkillDescription(skill)}" +
                (active.Contains(skill.Name) && skill.instructions.Length > 0 ? "\n" + skill.instructions : "")));

        private string SkillDescription(AgentSkill skill)
        {
            var title = skill.GetDisplayName(locale);
            return title == skill.Name ? skill.description : title + " — " + skill.description;
        }

        public JsonArray Definitions() => new(offered.Values.Select(tool => tool.Definition.DeepClone()).ToArray());

        public ToolPresentation? Presentation(string name, string phase, string text, bool plain = false) =>
            offered.TryGetValue(name, out var tool) ? tool.Owner.PresentTool?.Invoke(name, phase, text, plain) : null;

        public JsonObject? TraceSource(string name) => offered.TryGetValue(name, out var tool)
            ? tool.Owner.PluginToolSource?.Invoke(name) is { } plugin ? (JsonObject)plugin.DeepClone()
                : new() { ["kind"] = "native", ["skill"] = tool.Owner.Name }
            : null;

        public async Task<JsonNode?> Invoke(string name, JsonObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!offered.TryGetValue(name, out var tool))
            {
                if (owners.TryGetValue(name, out var known))
                {
                    var activeNow = active.Contains(known.Name);
                    throw new ToolFailure("This tool was not offered for this model request.",
                        activeNow ? "tool_not_offered" : "skill_inactive", "availability",
                        activeNow ? "Use it in the next request if it appears in the tool definitions." :
                            $"Call activate_skill with name '{known.Name}', then use the tool on the next request.");
                }
                if (name == Activate)
                    throw new ToolFailure("activate_skill was not offered because all available skills are already active.",
                        "tool_not_offered", "availability", "Use a tool from the current definitions.");
                throw new ToolFailure("This tool was not offered and does not belong to an available skill.",
                    "tool_unavailable", "availability", "Use the exact tool name from the current definitions or activate a listed skill.");
            }
            var owner = tool.Owner;
            owner.onEvent?.Invoke("tool.started", new() { ["name"] = "tool.started", ["tool"] = name, ["arguments"] = args.DeepClone() });
            var result = await owner.execute(name, args, ct);
            owner.onEvent?.Invoke("tool.finished", new() { ["name"] = "tool.finished", ["tool"] = name, ["result"] = JsonValues.Clone(result) });
            return result;
        }

        private string AvailableSkillId(string id)
        {
            if (catalog.ContainsKey(id)) return id;
            return id switch
            {
                "web-research" or "research" or "direct-research" => catalog.ContainsKey("search-full") ? "search-full" : "search",
                _ => BuiltInSkillIds.Current(id)
            };
        }

        private Task<JsonNode?> ActivateSkill(string _, JsonObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var id = args.GetString("name");
            if (!catalog.TryGetValue(AvailableSkillId(id), out var skill))
                throw new ToolFailure("This skill is unavailable in this session.", "skill_unavailable", "availability",
                    "Use the exact name of an available skill.", new() { ["available_skills"] =
                        new JsonArray(catalog.Keys.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) });
            // Activation persists into the next request. This request's offered tools are fixed.
            if (active.Add(skill.Name)) skill.onActivate?.Invoke();
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["name"] = skill.Name, ["display_name"] = skill.GetDisplayName(locale), ["instructions"] = skill.instructions,
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
