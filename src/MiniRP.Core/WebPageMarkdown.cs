using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using ReverseMarkdown;

namespace MiniRP.Core;

internal static partial class WebPageMarkdown
{
    private const string Chrome = "script,style,noscript,template,svg,nav,aside,footer,form," +
        "[hidden],[role=navigation],[role=banner],[role=contentinfo],[role=dialog]";

    public static void Normalize(JsonObject page, string requestedUrl)
    {
        var content = page.GetString("content");
        if (!HtmlStart().IsMatch(content)) return;
        var document = new HtmlParser().ParseDocument(content);
        if (string.IsNullOrWhiteSpace(page.GetString("title"))) page["title"] = document.Title?.Trim() ?? "";
        foreach (var element in document.QuerySelectorAll(Chrome)) element.Remove();
        var article = document.QuerySelectorAll("article");
        var body = document.QuerySelector("main,[role=main]") ?? (article.Length == 1 ? article[0] : document.Body);
        if (body is null)
        {
            page["content"] = "";
            return;
        }
        var url = page.GetString("resolved_url", page.GetString("url", requestedUrl));
        if (Uri.TryCreate(url, UriKind.Absolute, out var address))
            foreach (var element in body.QuerySelectorAll("a[href],img[src]"))
            {
                var isLink = element.LocalName == "a";
                var attribute = isLink ? "href" : "src";
                var value = element.GetAttribute(attribute);
                if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(address, value, out var target) &&
                    (target.Scheme is "http" or "https" || isLink && target.Scheme == "mailto"))
                    element.SetAttribute(attribute, target.AbsoluteUri);
                else element.RemoveAttribute(attribute);
            }
        var config = new Config { GithubFlavored = true };
        config.Tags.Unknown = Config.UnknownTagsOption.Bypass;
        config.Images.Base64Handling = Config.Base64ImageHandling.Skip;
        config.Formatting.RemoveComments = true;
        config.Formatting.OutputLineEnding = "\n";
        config.Links.WhitelistedSchemes.UnionWith(["http", "https", "mailto"]);
        page["content"] = new Converter(config).Convert(body.InnerHtml).Trim();
    }

    [GeneratedRegex(@"\A[\s\uFEFF]*(?:(?:<!--[\s\S]*?-->|<\?xml[^>]*\?>)\s*)*(?:<!doctype\s+html\b|<(?:html|head|body|main|article|section|header|nav|aside|footer|div|h[1-6]|p|table|ul|ol|blockquote|pre|span|a|img|script|style|noscript|template|iframe|form)(?=[\s/>]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex HtmlStart();
}
