using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class BuiltInToolCardsTests
{
    [TestMethod]
    public void EveryHostToolHasLabelsAndReadOnlyTraceCards()
    {
        var source = Path.Combine(TestWorkspace.Root, "src", "MiniRP.Core");
        var tools = Directory.EnumerateFiles(source, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "\\bDefine\\(\"([a-z_]+)\""))
            .Select(match => match.Groups[1].Value)
            .Concat(["room_ready", "activate_skill", "plugin_state_read", "plugin_card_show"])
            .ToHashSet(StringComparer.Ordinal);
        var catalog = AgentLanguage.ReadLabels("AgentToolNames");
        CollectionAssert.AreEquivalent(tools.ToArray(), catalog.Select(tool => tool.Key).ToArray());
        foreach (var name in tools)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(BuiltInToolCards.Title(name, "en")), name);
            Assert.AreNotEqual(BuiltInToolCards.Title(name, "en"), BuiltInToolCards.Title(name, "zh-CN"), name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(BuiltInToolCards.Title(name, "zh-TW")), name);
            var arguments = new JsonObject();
            foreach (var key in catalog.GetObject(name).GetArray("arguments"))
                arguments[JsonValues.GetText(key)] = "fixture";
            foreach (var (phase, data) in new[] { ("arguments", arguments.ToJsonString()), ("result", "{\"staged\":true}") })
            {
                var card = BuiltInToolCards.Present(name, phase, data, false, "zh-Hans");
                Assert.IsNotNull(card, $"{name} {phase}");
                StringAssert.StartsWith(card.GetString("program"), "root = ToolFacts(");
                Assert.IsFalse(ToolPresentation.ContainsAction(card.GetString("program")));
            }
        }
    }

    [TestMethod]
    public void PresetCardsLocalizeValuesAndKeepLongTextOutOfPrograms()
    {
        var date = BuiltInToolCards.Present("configure_date", "arguments", """
            {"mode":"plugin","initial_date":"2025-09-01"}
            """, false, "zh-Hans");
        Assert.IsNotNull(date);
        Assert.AreEqual("设置日历", date.GetString("title"));
        var dateRows = date.GetObject("state").GetArray("rows");
        Assert.AreEqual("由插件管理", dateRows[0].GetString("value"));
        Assert.AreEqual("起始日期", dateRows[1].GetString("label"));
        Assert.AreEqual("2025-09-01", dateRows[1].GetString("value"));

        var prompt = string.Concat(Enumerable.Repeat("Write a scene with @OpenUrl and \"quotes\".\n", 60));
        var edited = BuiltInToolCards.Present("set_value", "arguments",
            new JsonObject { ["path"] = "chat_agent.system_prompt", ["value"] = prompt }.ToJsonString(), false, "zh-TW");
        Assert.IsNotNull(edited);
        var settings = edited.GetObject("state").GetArray("rows");
        Assert.AreEqual("聊天指令", settings[0].GetString("value"));
        Assert.AreEqual(prompt, settings[1].GetString("value"));
        Assert.IsFalse(edited.GetString("program").Contains("@OpenUrl", StringComparison.Ordinal));
        Assert.IsNull(BuiltInToolCards.Present("set_value", "arguments", "{invalid", false, "en"));
        Assert.IsNull(BuiltInToolCards.Present("third_party", "result", "{}", false, "en"));
    }
}
