using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed partial class LocalizationTests
{
    [TestMethod]
    public void AtLeastTwentyFiveLocalesHaveCompleteResourcesAndMatchingPlaceholders()
    {
        var root = Path.Combine(TestWorkspace.Root, "apps", "android", "app", "src", "main", "res");
        var canonical = XDocument.Load(Path.Combine(root, "values", "strings.xml")).Root!.Elements()
            .Where(element => element.Attribute("translatable")?.Value != "false")
            .ToDictionary(element => element.Attribute("name")!.Value);
        var locales = Directory.GetDirectories(root, "values-b+*");
        Assert.IsGreaterThanOrEqualTo(24, locales.Length);
        foreach (var locale in locales)
        {
            var elements = XDocument.Load(Path.Combine(locale, "strings.xml")).Root!.Elements().ToDictionary(element => element.Attribute("name")!.Value);
            CollectionAssert.AreEquivalent(canonical.Keys.ToArray(), elements.Keys.ToArray(), locale);
            foreach (var (key, expected) in canonical)
            {
                var actual = elements[key];
                var values = actual.Name == "plurals" ? actual.Elements("item").Select(item => item.Value) : [actual.Value];
                var placeholders = Placeholders().Matches(expected.Value).Select(match => match.Value).Distinct().Order().ToArray();
                foreach (var value in values)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(value), locale + ":" + key);
                    CollectionAssert.AreEqual(placeholders, Placeholders().Matches(value).Select(match => match.Value).Distinct().Order().ToArray(), locale + ":" + key);
                }
                if (actual.Name == "plurals")
                {
                    Assert.IsTrue(actual.Elements("item").Any(item => item.Attribute("quantity")?.Value == "other"));
                }
            }
        }
    }

    [GeneratedRegex(@"%\d+\$[sd]")]
    private static partial Regex Placeholders();
}
