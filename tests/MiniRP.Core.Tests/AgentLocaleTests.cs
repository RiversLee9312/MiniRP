using System.Text.Json.Nodes;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class AgentLocaleTests
{
    [TestMethod]
    public void EveryBuiltInSkillHasADisplayNameInEachSupportedLocale()
    {
        var root = TestWorkspace.Root;
        var resources = Path.Combine(root, "apps", "android", "app", "src", "main", "res");
        var locales = Directory.EnumerateDirectories(resources, "values-b+*")
            .Select(path => Path.GetFileName(path)[9..].Replace('+', '-').ToLowerInvariant()).Append("en").ToArray();
        var labels = JsonValues.ParseObject(File.ReadAllText(Path.Combine(root, "src", "MiniRP.Core", "AgentSkillNames.json")));
        string[] builtIn = ["assets", "subagent", "plugin-docs", "plugin-dev", "preset-docs",
            "browser", "browser-fetch-workflow", "browser-search-workflow", "container", "curl", "ffmpeg", "ffprobe", "ensemble-chats", "ensemble-management",
            "interactive-ui", "subagent-task", "preset-editing", "room-loading", "search", "search-full", "search-workflow",
            "session-memory", "skill-management", "world-state"];
        CollectionAssert.AreEquivalent(builtIn, labels.Select(item => item.Key).ToArray());
        foreach (var id in builtIn)
        {
            var translations = labels.GetObject(id);
            CollectionAssert.AreEquivalent(locales, translations.Select(item => item.Key).ToArray(), id);
            foreach (var locale in locales)
            {
                var expected = translations.GetString(locale);
                Assert.IsFalse(string.IsNullOrWhiteSpace(expected), $"{id} / {locale}");
                Assert.AreEqual(expected, AgentLanguage.SkillName(id, locale));
            }
        }
        Assert.AreEqual("編輯預設", AgentLanguage.SkillName("preset-editing", "zh-TW"));
        Assert.AreEqual("编辑预设", AgentLanguage.SkillName("preset-editing", "zh-CN"));
        Assert.AreEqual("Recherche", AgentLanguage.SkillName("research", "fr-CA"));
        Assert.AreEqual("Gerenciamento de habilidades", AgentLanguage.SkillName("skill-management", "pt-PT"));
        Assert.AreEqual("Assets", AgentLanguage.SkillName("assets", "sw-KE"));
        Assert.AreEqual("unknown", AgentLanguage.SkillName("unknown", "ar"));
        Assert.IsNull(AgentLanguage.Normalize("en\nextra text"));
    }

    [TestMethod]
    public async Task OnlyStableIdsActivateLocalizedSkillsAndTools()
    {
        var skill = new AgentSkill("world-state", "Inspect state.", new(Tools.Define("inspect_world", "Inspect world.")),
            (_, _, _) => Task.FromResult<JsonNode?>(JsonValue.Create("state")));
        var plugin = new AgentSkill("example-research", "Review the data.", new(),
            (_, _, _) => Task.FromResult<JsonNode?>(null), DisplayName: "資料檢索外掛");
        var turn = new AgentSkill.Turn([skill, plugin], locale: "zh-Hant");
        StringAssert.Contains(turn.Prompt, "world-state: 世界狀態");
        StringAssert.Contains(turn.InstructionSections().GetString("example-research"), "資料檢索外掛");
        Assert.IsFalse(turn.Offers("inspect_world"));
        await Assert.ThrowsExactlyAsync<ToolFailure>(() => turn.Invoke("activate_skill", new() { ["name"] = "世界狀態" }, default));
        var activated = await turn.Invoke("activate_skill", new() { ["name"] = "world-state" }, default);
        Assert.AreEqual("world-state", activated.GetString("name"));
        Assert.AreEqual("世界狀態", activated.GetString("display_name"));
        CollectionAssert.AreEqual(new[] { "world-state" }, turn.ActiveNames.ToArray());

        var next = new AgentSkill.Turn([skill, plugin], turn.ActiveNames, "ja");
        Assert.AreEqual("world-state", next.OwnerOf("inspect_world"));
        Assert.IsTrue(next.Offers("inspect_world"));
        StringAssert.Contains(next.Prompt, "world-state: 世界の状態");
        Assert.AreEqual("state", JsonValues.GetText(await next.Invoke("inspect_world", new(), default)));
    }

    [TestMethod]
    [DataRow("preset", "zh-TW", null, "zh-TW", "Reply language:", "編輯預設")]
    [DataRow("plugin", "ja-JP", "zh-Hant", "zh-Hant", "Report language:", "編輯預設")]
    public async Task DevelopmentRolesRetainTurnLocaleAndReceiveLocalizedCatalog(string role, string requested,
        string? delegated, string expected, string policy, string name)
    {
        var systems = new List<string>();
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") != "http_request") return;
            var body = JsonValues.ParseObject(request.GetString("body"));
            systems.Add(body.GetArray("messages")[0].GetString("content"));
            port!.Reply(new() { ["id"] = request.GetString("id"), ["done"] = true,
                ["data"] = "data: {\"choices\":[{\"delta\":{\"content\":\"Fixture response\"}}]}\n\n" });
        });
        string? current = requested;
        var factory = new AgentSession.Factory(port, null, null, () => "", requestedLocale: () => current);
        using var session = factory.Open(role, ":memory:",
            () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture"),
            () => new() { ["locale"] = "de" }, _ => { }, delegatedLocale: delegated);
        var skill = new AgentSkill("preset-editing", "Edit a fixture.", new(),
            (_, _, _) => Task.FromResult<JsonNode?>(null), Required: true);

        await session.Run("Fixture agent.", "First task.", skill, default);
        Assert.AreEqual(expected, session.Db.Get("_context/locale"));
        StringAssert.Contains(systems.Single(), policy);
        StringAssert.Contains(systems.Single(), $"application language ({expected})");
        StringAssert.Contains(systems.Single(), $"preset-editing: {name}");

        current = "not a language\nInjected instructions";
        await session.Run("Fixture agent.", "Background update.", skill, default, inputRole: "system");
        Assert.AreEqual(expected, session.Db.Get("_context/locale"));
        Assert.AreEqual(systems[0], systems[1]);
        Assert.IsFalse(session.History.OfType<JsonObject>().Any(SessionInstructions.IsFrame));
    }
}
