using System.Text.Json.Nodes;
using MiniRP.Cli;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class AgentTests
{
    private static readonly Provider Model = new("fixture", "Fixture", "openai", "http://fixture.invalid/v1", "test-key", "test-model");

    [TestMethod]
    public async Task SearchCanSubmitManyVirtualResultsWithoutRealSearchAndRecallThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "minirp-test-" + Guid.NewGuid().ToString("N"));
        var workspace = new Workspace(root);
        workspace.Select(workspace.InitialPreset().Id, null);
        var round = 0;
        HostPort? port = null;
        using var scripts = new PluginScripts();
        port = new(request =>
        {
            if (request.GetString("kind") == "cancel_io")
            {
                return;
            }
            if (request.GetString("kind") == "plugin_isolate_close")
            {
                scripts.Close(request.GetString("isolate"));
                return;
            }
            if (request.GetString("kind") == "plugin_script")
            {
                port!.Reply(new() { ["id"] = request.GetString("id"),
                    ["result"] = scripts.Step(request, CancellationToken.None, port.TakeBinary, port.WriteBinary) });
                return;
            }
            Assert.AreEqual("http_request", request.GetString("kind"));
            Assert.EndsWith("/chat/completions", request.GetString("url"));
            var calls = new JsonArray();
            if (round++ == 0)
            {
                for (var i = 0; i < 20; i++)
                {
                    calls.AddNode(Call("add_result", new()
                    {
                        ["title"] = "Result " + i,
                        ["url"] = "https://example.com/" + i,
                        ["snippet"] = "世界"
                    }));
                }
            }
            else if (round == 3)
            {
                calls.AddNode(Call("virtual_search", new()
                {
                    ["query"] = "世界"
                }));
            }
            else if (round == 4)
            {
                var messages = JsonValues.ParseObject(request.GetString("body")).GetArray("messages");
                var recalled = JsonValues.ParseObject(messages.Last()!.GetString("content")).GetArray("results");
                var refs = new JsonArray();
                foreach (var item in recalled)
                {
                    refs.AddNode(item.GetString("ref"));
                }
                calls.AddNode(Call("submit_search_results", new()
                {
                    ["refs"] = refs
                }));
            }
            else
            {
                calls.AddNode(Call("submit_search_results", new()));
            }
            var data = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = calls } }) };
            var stream = "data: " + JsonValues.Serialize(data) + "\n\ndata: [DONE]\n\n";
            // Verify the SSE parser carries lines across arbitrary network chunk boundaries.
            port!.Reply(new()
            {
                ["id"] = request.GetString("id"),
                ["data"] = stream[..17]
            });
            port.Reply(new()
            {
                ["id"] = request.GetString("id"),
                ["data"] = stream[17..],
                ["done"] = true
            });
        });
        var model = new ModelClient(port, () => Model);
        await using var plugins = new PluginRuntime(root, port, _ => { }, (_, _, _, _, _) => throw new NotSupportedException());
        plugins.Load();
        var factory = new AgentSession.Factory(port, plugins, Path.Combine(root, "attachments"), () => "", () => workspace);
        using var session = factory.Open("search", Path.Combine(root, "search.sqlite"), () => Model, () => new()
        {
            ["agent"] = "search", ["preset"] = workspace.PresetId, ["chat"] = workspace.ChatId, ["room"] = workspace.ChatId
        }, _ => { });
        var fetch = new BrowserAgent(port, job => TestAgentSession.Open("fetch",
            Path.Combine(root, "fetch", job + ".sqlite"), model, _ => { }));
        var search = new SearchAgent(workspace, session, new PageSource(port, fetch, () => new()));
        var result = await search.Run("search", "世界", CancellationToken.None);
        Assert.AreEqual(20, result.GetArray("results").Count);
        Assert.AreEqual(20, workspace.State.GetObject("search_cache").GetArray("世界").Count);
        Assert.IsTrue(workspace.State.GetObject("search_cache").GetArray("世界").All(item => ResearchAssets.IsUrl(JsonValues.GetText(item))));
        Assert.AreEqual(2, round);
        var recalledResult = await search.Run("search", "世界", CancellationToken.None);
        Assert.AreEqual(20, recalledResult.GetArray("results").Count);
        Assert.AreEqual(4, round);
        Assert.IsTrue(session.History.Any(message => message.GetString("role") == "tool"));
    }

    [TestMethod]
    public async Task CancelUnresponsiveNetworkDoesNotBlockCommandDispatch()
    {
        await using var engine = new Engine(Path.Combine(Path.GetTempPath(), "minirp-cancel-" + Guid.NewGuid().ToString("N")));
        engine.Dispatch(new()
        {
            ["op"] = "configure",
            ["provider"] = new JsonObject { ["url"] = Model.Url, ["model"] = Model.Model }
        });
        engine.Dispatch(new()
        {
            ["op"] = "select_chat",
            ["preset"] = engine.Workspace.PresetId
        });
        engine.Dispatch(new()
        {
            ["op"] = "message",
            ["text"] = "test"
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        bool requested = false;
        while (!requested)
        {
            requested = engine.Poll().Any(item => item.GetString("kind") == "http_request");
            await Task.Delay(10, timeout.Token);
        }
        engine.Dispatch(new()
        {
            ["op"] = "cancel"
        });
        bool cancelled = false;
        while (!cancelled)
        {
            cancelled = engine.Poll().Any(item => item.GetString("kind") == "cancelled");
            await Task.Delay(10, timeout.Token);
        }
        Assert.IsTrue(cancelled);
    }

    private static JsonObject Call(string name, JsonObject args) => new()
    {
        ["id"] = JsonValues.NewId(),
        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonValues.Serialize(args) }
    };
}
