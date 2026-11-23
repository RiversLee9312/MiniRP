using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MiniRP.Cli;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class BrowserTests
{
    [TestMethod]
    public async Task BrowserPreparationWaitsForTheResultAndRemainsCancellable()
    {
        var requests = new Queue<JsonObject>();
        var port = new HostPort(requests.Enqueue);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var browser = port.Browser("first", "browser_navigate", new(), deadline.Token);
        var id = requests.Dequeue().GetString("id");
        port.Reply(new() { ["id"] = id, ["preparing"] = true });
        port.Reply(new() { ["id"] = id, ["result"] = new JsonObject { ["snapshot"] = "Fixture page" } });
        Assert.AreEqual("Fixture page", (await browser).GetString("snapshot"));
        Assert.AreEqual("cancel_io", requests.Dequeue().GetString("kind"));

        var waiting = port.Browser("second", "browser_navigate", new(), deadline.Token);
        id = requests.Dequeue().GetString("id");
        port.Reply(new() { ["id"] = id, ["preparing"] = true });
        await deadline.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
        Assert.AreEqual(id, requests.Dequeue().GetString("id"));
    }

    [TestMethod]
    public async Task PlaywrightCapturesFullLongPageWithoutReplacingItWithSummary()
    {
        var root = TestWorkspace.Root;
        if (!File.Exists(Path.Combine(root, "node_modules", "@playwright", "mcp", "cli.js")))
        {
            Assert.Inconclusive("Run npm ci to enable the local browser integration test.");
        }
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:18089/");
        listener.Start();
        var html = "<html><head><title>Full page</title></head><body><h1>Full article</h1><p>" + string.Concat(Enumerable.Repeat("完整正文 and preserved text. ", 8000)) + "</p><a href='/end'>END_OF_LONG_PAGE</a></body></html>";
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var server = Task.Run(async () =>
        {
            while (!source.IsCancellationRequested)
            {
                var request = await listener.GetContextAsync().WaitAsync(source.Token);
                var data = Encoding.UTF8.GetBytes(html);
                request.Response.ContentType = "text/html; charset=utf-8";
                request.Response.ContentLength64 = data.Length;
                await request.Response.OutputStream.WriteAsync(data, source.Token);
                request.Response.Close();
            }
        }, source.Token);
        await using var browser = new BrowserHost(root);
        try
        {
            await browser.Execute(Request("browser_navigate", new()
            {
                ["url"] = "http://localhost:18089/"
            }), source.Token);
            var result = await browser.Execute(Request("capture_page", new()), source.Token);
            Assert.IsGreaterThan(150000, result.GetString("content").Length);
            Assert.IsTrue(result.GetString("content").Contains(@"END\_OF\_LONG\_PAGE", StringComparison.Ordinal), "Full page end marker must survive Markdown conversion.");
            Assert.IsTrue(result.GetString("content").Contains("http://localhost:18089/end", StringComparison.Ordinal), "Relative links must be resolved.");
        }
        finally
        {
            await browser.Execute(Request("browser_close", new()), CancellationToken.None);
            source.Cancel();
            listener.Stop();
            try
            {
                await server;
            }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException) { }
        }
    }

    private static JsonObject Request(string tool, JsonObject arguments) => new()
    {
        ["job"] = "integration",
        ["tool"] = tool,
        ["arguments"] = arguments
    };
}
