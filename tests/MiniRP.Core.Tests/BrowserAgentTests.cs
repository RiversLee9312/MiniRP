using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class BrowserAgentTests
{
    [TestMethod]
    public async Task BrowserAgentCanPreciselyEditMarkdownAndReturnFullBody()
    {
        var round = 0;
        var content = "Navigation\n" + new string('x', 170000) + "END";
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") == "cancel_io")
            {
                return;
            }

            if (request.GetString("kind") == "browser_request")
            {
                var capture = request.GetString("tool") == "capture_page";
                port!.Reply(new()
                {
                    ["id"] = request.GetString("id"),
                    ["result"] = capture
                    ? new JsonObject { ["title"] = "Page", ["url"] = "https://example.com", ["content"] = content }
                    : new JsonObject { ["snapshot"] = "page" }
                });
                return;
            }
            var (name, args) = round++ switch
            {
                0 => ("browser_navigate", new JsonObject { ["url"] = "https://example.com" }),
                1 => ("capture_page", new JsonObject()),
                2 => ("db_patch", new JsonObject { ["key"] = "page/markdown", ["old_string"] = "Navigation\n", ["new_string"] = "" }),
                _ => ("finish_fetch", new JsonObject { ["content_key"] = "page/markdown", ["summary"] = "Summary", ["complete"] = true })
            };
            var call = new JsonObject { ["index"] = 0, ["id"] = JsonValues.NewId(), ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonValues.Serialize(args) } };
            var message = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call) } }) };
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + JsonValues.Serialize(message) + "\n\n", ["done"] = true });
        });
        var model = new ModelClient(port, () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "test", "model"));
        var root = Path.Combine(Path.GetTempPath(), "minirp-fetch-" + Guid.NewGuid().ToString("N"));
        var fetch = new BrowserAgent(port, job => TestAgentSession.Open("fetch",
            Path.Combine(root, "fetch", job + ".sqlite"), model, _ => { }));
        var page = await fetch.Fetch("https://example.com", CancellationToken.None);
        Assert.AreEqual(170003, page.GetString("content").Length);
        Assert.IsTrue(page.GetString("content").EndsWith("END", StringComparison.Ordinal));
        Assert.AreEqual("Summary", page.GetString("summary"));
        Assert.AreEqual(4, round);
    }
}
