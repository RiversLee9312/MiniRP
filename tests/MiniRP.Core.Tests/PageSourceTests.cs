using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class PageSourceTests
{
    private const string Page = "https://example.test/article";

    [TestMethod]
    [DataRow("tavily", "https://api.tavily.com/search", "Authorization", "Bearer search-key",
        """{"results":[{"url":"https://example.test/article","title":"Fixture","content":"Excerpt"}]}""")]
    [DataRow("exa", "https://api.exa.ai/search", "x-api-key", "search-key",
        """{"results":[{"url":"https://example.test/article","title":"Fixture","highlights":["Excerpt"]}]}""")]
    [DataRow("brave", "https://api.search.brave.com/res/v1/web/search", "X-Subscription-Token", "search-key",
        """{"web":{"results":[{"url":"https://example.test/article","title":"Fixture","description":"Excerpt"}]}}""")]
    public async Task SearchProvidersUseTheirOwnCredentialsAndReturnTheCommonResult(
        string service, string endpoint, string header, string credential, string response)
    {
        var (source, port, requests) = Fixture(service);
        var search = source.Request("search", new() { ["query"] = "test query", ["count"] = 3 }, TestContext.CancellationToken);
        var request = requests.Single();
        Assert.StartsWith(endpoint, request.GetString("url"));
        Assert.AreEqual(credential, request.GetObject("headers").GetString(header));
        if (service == "brave")
        {
            Assert.AreEqual("GET", request.GetString("method"));
            StringAssert.Contains(request.GetString("url"), "q=test%20query&count=3");
        }
        else
        {
            Assert.AreEqual("POST", request.GetString("method"));
            var body = JsonValues.ParseObject(request.GetString("body"));
            Assert.AreEqual("test query", body.GetString("query"));
            Assert.AreEqual(3, body.GetInt32(service == "exa" ? "numResults" : "max_results"));
        }
        Respond(port, request, response);
        var result = (await search).GetArray("results").Single();
        Assert.AreEqual(Page, result.GetString("url"));
        Assert.AreEqual("Fixture", result.GetString("title"));
        Assert.AreEqual("Excerpt", result.GetString("snippet"));
    }

    [TestMethod]
    [DataRow("tavily", "https://api.tavily.com/extract", "Authorization", "Bearer fetch-key", "raw_content")]
    [DataRow("exa", "https://api.exa.ai/contents", "x-api-key", "fetch-key", "text")]
    public async Task FetchUsesItsOwnProviderAndNormalizesTheDocument(
        string service, string endpoint, string header, string credential, string contentField)
    {
        var (source, port, requests) = Fixture("brave", service);
        var fetch = source.Request("fetch", new() { ["url"] = Page }, TestContext.CancellationToken);
        var request = requests.Single();
        Assert.AreEqual(endpoint, request.GetString("url"));
        Assert.AreEqual(credential, request.GetObject("headers").GetString(header));
        var body = JsonValues.ParseObject(request.GetString("body"));
        Assert.AreEqual(Page, body.GetArray("urls").Single()!.GetValue<string>());
        Respond(port, request, JsonValues.Serialize(new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject { ["url"] = Page, ["title"] = "Fixture", [contentField] = "# Article" })
        }));
        var page = await fetch;
        Assert.AreEqual("Fixture", page.GetString("title"));
        Assert.AreEqual("# Article", page.GetString("content"));
    }

    [TestMethod]
    public async Task UnavailablePaidSearchFallsBackWithoutForwardingCredentials()
    {
        var (source, port, requests) = Fixture("tavily");
        var search = source.Request("search", new() { ["query"] = "retry" }, TestContext.CancellationToken);
        Assert.IsTrue(requests.TryDequeue(out var paid));
        Respond(port, paid, "{}", status: 429);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        JsonObject? free;
        while (!requests.TryDequeue(out free)) await Task.Delay(10, deadline.Token);
        Assert.StartsWith("https://api.mwmbl.org/", free.GetString("url"));
        Assert.AreEqual("", free.GetObject("headers").GetString("Authorization"));
        Respond(port, free, """{"results":[{"url":"https://example.test/article","content":"Free excerpt"}]}""");
        Assert.AreEqual("Free excerpt", (await search).GetArray("results").Single().GetString("snippet"));
    }

    public TestContext TestContext { get; set; } = null!;

    private static (PageSource Source, HostPort Port, ConcurrentQueue<JsonObject> Requests) Fixture(string search, string fetch = "deepseek")
    {
        var requests = new ConcurrentQueue<JsonObject>();
        var port = new HostPort(item =>
        {
            if (item.GetString("kind") == "http_request") requests.Enqueue(item);
        });
        var browser = new FetchAgent(port, _ => throw new AssertFailedException("Unexpected browser fallback."));
        var keys = new JsonObject { [search] = "search-key", [fetch] = "fetch-key" };
        return (new(port, browser, () => new() { ["search_provider"] = search, ["fetch_provider"] = fetch, ["research_keys"] = keys }), port, requests);
    }

    private static void Respond(HostPort port, JsonObject request, string response, int status = 200) =>
        port.Reply(new() { ["id"] = request.GetString("id"), ["done"] = true, ["data"] = response, ["status"] = status });
}
