using System.Text.Json.Nodes;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class BasePluginPresentationTests
{
    [TestMethod]
    public async Task EveryBuiltInPluginToolProvidesLocalizedArgumentsAndResults()
    {
        using var source = typeof(PluginRuntime).Assembly.GetManifestResourceStream("MiniRP.BasePlugin.manifest.json")!;
        var manifest = (JsonObject)JsonNode.Parse(source)!;
        var skills = manifest.GetArray("skills").OfType<JsonObject>().ToArray();
        var cards = manifest.GetArray("cards").OfType<JsonObject>().ToArray();
        var locales = manifest.GetObject("i18n").GetObject("locales");
        var supported = Directory.GetDirectories(Path.Combine(TestWorkspace.Root, "apps", "android", "app", "src", "main", "res"), "values-b+*")
            .Select(path => Path.GetFileName(path)[9..].Replace('+', '-')).ToArray();
        Assert.AreEqual(6, skills.Length);
        Assert.AreEqual(35, skills.Sum(skill => skill.GetArray("tools").Count));
        CollectionAssert.AreEquivalent(supported, locales.Select(pair => pair.Key).ToArray());

        var root = Path.Combine(Path.GetTempPath(), "minirp-base-card-test-" + WorkspaceIds.New());
        try
        {
            await using var runtime = new PluginRuntime(root, new HostPort(_ => { }), _ => { },
                (_, _, _, _, _) => Task.FromResult<JsonNode?>(null));
            runtime.Load();
            var preset = WorkspaceIds.New();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (skill, skillIndex) in skills.Select((skill, index) => (skill, index)))
            {
                var name = skill.GetString("name");
                var agent = JsonValues.GetText(skill.GetArray("agents")[0]);
                var tools = skill.GetArray("tools").OfType<JsonObject>();
                foreach (var (tool, toolIndex) in tools.Select((tool, index) => (tool, index)))
                {
                    var id = tool.GetString("name");
                    var qualified = id;
                    var trace = runtime.LegacyBuiltInToolTrace(preset, qualified, agent, plain: false);
                    Assert.IsTrue(trace.HasValue, qualified);
                    var (origin, arguments, result) = trace.Value;
                    Assert.IsNotNull(runtime.ReadToolTraceLabels(origin, preset, qualified, agent, "en"));
                    foreach (var (phase, reference) in new[] { (ToolPresentation.Arguments, arguments), (ToolPresentation.Result, result) })
                    {
                        Assert.IsNotNull(reference, $"{qualified} / {phase}");
                        var cardId = reference.GetString("card");
                        used.Add(cardId);
                        var cardIndex = Array.FindIndex(cards, card => card.GetString("id") == cardId);
                        Assert.IsTrue(cardIndex >= 0, cardId);
                        var canonical = cards[cardIndex];
                        foreach (var locale in supported.Prepend("en"))
                        {
                            var catalog = locales.GetObject(locale);
                            var expected = locale == "en" ? canonical.GetString("title") : catalog.GetString($"/cards/{cardIndex}/title");
                            Assert.IsFalse(string.IsNullOrWhiteSpace(expected), $"{locale} / {cardId}");
                            var display = runtime.ReadToolPresentation(reference, preset, qualified, phase, "{}", locale, agent);
                            Assert.AreEqual(canonical.GetString("program"), display.GetString("program"), cardId);
                            Assert.AreEqual(expected, display.GetString("title"), cardId);
                            foreach (var label in canonical.GetObject("labels"))
                            {
                                var labelText = locale == "en" ? JsonValues.GetText(label.Value) :
                                    catalog.GetString($"/cards/{cardIndex}/labels/{label.Key}");
                                Assert.IsFalse(string.IsNullOrWhiteSpace(labelText), $"{locale} / {cardId} / {label.Key}");
                                Assert.AreEqual(labelText, display.GetObject("state").GetObject("labels").GetString(label.Key));
                            }
                        }
                    }
                    Assert.IsNull(runtime.ReadToolPresentation(result!, preset, qualified, ToolPresentation.Result,
                        """{"error":"Fixture failure."}""", "en", agent));
                    foreach (var (locale, value) in locales)
                    {
                        var skillTitle = name is AgentAssetStorage.SkillId or "search-full" ? AgentLanguage.SkillName(name, locale) :
                            value.GetString($"/skills/{skillIndex}/title");
                        Assert.IsFalse(string.IsNullOrWhiteSpace(skillTitle), locale);
                        var resolved = runtime.ReadToolTraceLabels(origin, preset, qualified, agent, locale);
                        Assert.AreEqual(skillTitle, resolved?.SkillTitle, locale);
                        var title = value.GetString($"/skills/{skillIndex}/tools/{toolIndex}/title");
                        Assert.IsFalse(string.IsNullOrWhiteSpace(title), qualified);
                        Assert.AreEqual(title, resolved?.Title, qualified);
                    }
                    foreach (var (alias, locale) in new[] { ("zh-TW", "zh-Hant"), ("zh-Hans-TW", "zh-Hans"), ("pt-PT", "pt-BR") })
                    {
                        var expected = runtime.ReadToolTraceLabels(origin, preset, qualified, agent, locale);
                        Assert.AreEqual(expected, runtime.ReadToolTraceLabels(origin, preset, qualified, agent, alias), qualified);
                        var canonical = runtime.ReadToolPresentation(arguments!, preset, qualified, ToolPresentation.Arguments, "{}", locale, agent);
                        var aliased = runtime.ReadToolPresentation(arguments!, preset, qualified, ToolPresentation.Arguments, "{}", alias, agent);
                        Assert.AreEqual(canonical.GetString("title"), aliased.GetString("title"), qualified);
                        Assert.IsTrue(JsonNode.DeepEquals(canonical.GetObject("state").GetObject("labels"),
                            aliased.GetObject("state").GetObject("labels")), qualified);
                    }
                }
            }
            CollectionAssert.AreEquivalent(cards.Select(card => card.GetString("id")).ToArray(), used.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
