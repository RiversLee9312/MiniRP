using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class AgentRuntimeTests
{
    [TestMethod]
    public void OnlyTheAgentRuntimeCanConstructSessionsAndSendModelRequests()
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.IsTrue(typeof(AgentSession).GetConstructors(members).All(constructor => constructor.IsPrivate));
        Assert.IsTrue(typeof(AgentSession).GetMethod("Complete", members)!.IsPrivate);
        Assert.IsTrue(typeof(AgentSession).GetMethod("CompleteResponses", members)!.IsPrivate);
        Assert.IsNull(typeof(ModelClient).GetMethod("Complete", members));

        var core = Path.Combine(TestWorkspace.Root, "src", "MiniRP.Core");
        var allowed = new HashSet<string> { "AgentSession.Factory.cs", "AgentSession.Model.cs", "AgentSession.Responses.cs" };
        foreach (var source in Directory.EnumerateFiles(core, "*.cs"))
        {
            var name = Path.GetFileName(source);
            if (allowed.Contains(name)) continue;
            var text = File.ReadAllText(source);
            Assert.IsFalse(Regex.IsMatch(text, @"\b(?:new\s+AgentSession|ModelEvents\s*\.\s*Read)\s*\(", RegexOptions.CultureInvariant),
                $"{name} bypasses the agent session factory or model transport.");
        }
    }

    [TestMethod]
    [DataRow("chat")]
    [DataRow("env")]
    [DataRow("preset")]
    [DataRow("search")]
    [DataRow("fetch")]
    [DataRow("plugin")]
    [DataRow("temporary")]
    [DataRow("title")]
    [DataRow("plugin-generation")]
    public async Task EachRoleRefreshesSkillsAndEmitsTheSameDeltaBeforeTheNextRequest(string role)
    {
        var updated = false;
        var bodies = new List<JsonObject>();
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") != "http_request") return;
            bodies.Add(JsonValues.ParseObject(request.GetString("body")));
            var step = bodies.Count == 1
                ? JsonValues.ParseObject("""{"tool_calls":[{"index":0,"id":"edit","function":{"name":"replace_catalog","arguments":"{}"}}]}""")
                : new JsonObject { ["content"] = "Done." };
            var reply = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = step }) };
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + JsonValues.Serialize(reply) + "\n\n", ["done"] = true });
        });
        IEnumerable<AgentSkill> Skills()
        {
            var name = updated ? "new_catalog" : "old_catalog";
            var tool = updated ? "read_updated_catalog" : "replace_catalog";
            yield return new(name, "Current task skill", new JsonArray(Tools.Define(tool, "Update or read the task catalog.")),
                (_, _, _) =>
                {
                    updated = true;
                    return Task.FromResult<JsonNode?>(JsonValue.Create("Catalog replaced."));
                }, Required: true);
        }
        var model = new ModelClient(port, () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture"));
        using var session = TestAgentSession.Open(role, ":memory:", model, _ => { }, Skills);
        Assert.AreEqual("Done.", (await session.Run("Shared instructions.", "Start", CancellationToken.None)).GetString("content"));
        Assert.AreEqual(2, bodies.Count);
        string[] ToolsIn(JsonObject request) => request.GetArray("tools").Select(tool => tool.GetObject("function").GetString("name")).ToArray();
        CollectionAssert.Contains(ToolsIn(bodies[0]), "replace_catalog");
        CollectionAssert.DoesNotContain(ToolsIn(bodies[1]), "replace_catalog");
        CollectionAssert.Contains(ToolsIn(bodies[1]), "read_updated_catalog");
        var messages = bodies[1].GetArray("messages");
        Assert.AreEqual(bodies[0].GetArray("messages")[0].GetString("content"), messages[0].GetString("content"));
        StringAssert.Contains(messages.Last().GetString("content"), "Skill removed: old_catalog");
        StringAssert.Contains(messages.Last().GetString("content"), "Skill added: new_catalog");
        Assert.IsFalse(SessionHistory.IsVisible(session.History.Last(SessionInstructions.IsFrame)));
    }

    [TestMethod]
    public async Task AToolCanRunOnlyAfterItsSkillWasOfferedToTheModel()
    {
        const string owned = "owned_tool";
        var requests = new List<JsonObject>();
        var events = new List<JsonObject>();
        var executions = 0;
        var activations = 0;
        var skill = new AgentSkill("fixture", "Run the owned tool.", new JsonArray(Tools.Define(owned, "Call the fixture.")),
            (_, _, _) => Task.FromResult<JsonNode?>(JsonValue.Create(++executions)), OnActivate: () => activations++);
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") != "http_request") return;
            requests.Add(JsonValues.ParseObject(request.GetString("body")));
            var delta = requests.Count switch
            {
                1 => JsonValues.ParseObject("""
                    {"tool_calls":[
                        {"index":0,"id":"activate","function":{"name":"activate_skill","arguments":"{\"name\":\"fixture\"}"}},
                        {"index":1,"id":"too_soon","function":{"name":"owned_tool","arguments":"{}"}}
                    ]}
                    """),
                2 => JsonValues.ParseObject("""
                    {"tool_calls":[
                        {"index":0,"id":"allowed","function":{"name":"owned_tool","arguments":"{}"}},
                        {"index":1,"id":"not_offered","function":{"name":"activate_skill","arguments":"{\"name\":\"fixture\"}"}}
                    ]}
                    """),
                _ => new JsonObject { ["content"] = "Done." }
            };
            var reply = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) };
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + JsonValues.Serialize(reply) + "\n\n", ["done"] = true });
        });
        var model = new ModelClient(port, () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture"));
        using var session = TestAgentSession.Open("plugin-generation", ":memory:", model, events.Add, () => [skill]);
        await session.Run("Instructions.", "Start.", CancellationToken.None);

        string[] ToolNames(int request) => requests[request].GetArray("tools").Select(tool => tool.GetObject("function").GetString("name")).ToArray();
        string Response(string id) => requests[2].GetArray("messages").Single(item => item.GetString("tool_call_id") == id).GetString("content");
        CollectionAssert.AreEqual(new[] { "activate_skill" }, ToolNames(0));
        CollectionAssert.AreEqual(new[] { owned }, ToolNames(1));
        StringAssert.Contains(Response("too_soon"), "not offered");
        StringAssert.Contains(Response("not_offered"), "not offered");
        Assert.AreEqual(1, executions);
        Assert.AreEqual(1, activations);
        CollectionAssert.AreEqual(new[] { "activate", "allowed" }, events.Where(item => item.GetString("kind") == "tool_start")
            .Select(item => item.GetString("block")).ToArray());
    }

    [TestMethod]
    public void InactiveSkillsCannotClaimAnAlreadyOwnedTool()
    {
        AgentSkill Skill(string name, bool required) => new(name, "Fixture", new JsonArray(Tools.Define("owned_tool", "Call the fixture.")),
            (_, _, _) => Task.FromResult<JsonNode?>(null), Required: required);
        Assert.ThrowsExactly<ArgumentException>(() => new AgentSkill.Turn([Skill("active", true), Skill("inactive", false)]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LongToolRunsCanFinishOrBeCancelledBeyondTheOldRoundLimit(bool cancel)
    {
        const int steps = 45;
        var executed = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") != "http_request") return;
            var delta = executed == steps ? new JsonObject { ["content"] = "Done." } : new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "step-" + executed,
                    ["function"] = new JsonObject { ["name"] = "advance", ["arguments"] = "{}" } })
            };
            var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) };
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + JsonValues.Serialize(response) + "\n\n",
                ["done"] = true });
        });
        var model = new ModelClient(port, () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture",
            ContextWindow: 128000));
        using var session = TestAgentSession.Open("temporary", ":memory:", model, _ => { });
        var skill = new AgentSkill("fixture", "Advance the task.", new JsonArray(Tools.Define("advance", "Advance the task.")),
            (_, _, _) =>
            {
                executed++;
                if (cancel && executed == steps) cancellation.Cancel();
                return Task.FromResult<JsonNode?>(JsonValue.Create("Advanced."));
            }, Required: true);
        Task<JsonObject> Run() => session.Run("Instructions.", "Run the task.", skill, cancellation.Token);
        if (cancel) await Assert.ThrowsExactlyAsync<OperationCanceledException>(Run);
        else Assert.AreEqual("Done.", (await Run()).GetString("content"));
        Assert.AreEqual(steps, executed);
    }

    [TestMethod]
    public void ClosingASessionInboxRetainsQueuedInputAndRejectsNewInput()
    {
        var model = new ModelClient(new HostPort(_ => { }), () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture"));
        using var session = TestAgentSession.Open("plugin", ":memory:", model, _ => { });
        var inbox = new TurnInbox("", "plugin", _ => null);
        session.Inbox = inbox;
        Assert.IsTrue(inbox.TryPost("", "plugin", new() { ["text"] = "Queued follow-up" }));
        session.CompleteInbox();
        Assert.IsFalse(inbox.TryPost("", "plugin", new() { ["text"] = "Too late" }));
        Assert.IsNull(session.Inbox);
        Assert.AreEqual("Queued follow-up", AgentSession.ReadHistory(session.Db).Single().GetString("content"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ADrainedSteerIsRetainedExactlyOnceEvenWhenTheModelFails(bool fail)
    {
        var inbox = new TurnInbox("", "plugin", _ => "room-fixture");
        var requests = 0;
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") != "http_request") return;
            if (++requests > 1 && fail)
            {
                port!.Reply(new() { ["id"] = request.GetString("id"), ["error"] = "Model unavailable.", ["done"] = true });
                return;
            }
            var response = requests == 1
                ? """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"step","function":{"name":"queue_steer","arguments":"{}"}}]}}]}"""
                : """{"choices":[{"delta":{"content":"Done."}}]}""";
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + response + "\n\n", ["done"] = true });
        });
        var model = new ModelClient(port, () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture"));
        using var session = TestAgentSession.Open("plugin", ":memory:", model, _ => { });
        session.Inbox = inbox;
        var skill = new AgentSkill("fixture", "Queue a new instruction.", new JsonArray(Tools.Define("queue_steer", "Queue the input.")),
            (_, _, _) =>
            {
                Assert.IsTrue(inbox.TryPost("", "plugin", new() { ["text"] = "Keep the exact steer." }));
                return Task.FromResult<JsonNode?>(JsonValue.Create("Queued"));
            }, Required: true);
        Task Run() => session.Run("Instructions.", "Original task.", skill, CancellationToken.None);
        if (fail) await Assert.ThrowsExactlyAsync<IOException>(Run);
        else await Run();
        Assert.AreEqual(2, requests);
        session.CompleteInbox();
        session.Receive("room-fixture", "Duplicate from the Room log.");
        var saved = AgentSession.ReadHistory(session.Db);
        Assert.AreEqual(1, saved.Count(item => item.GetString("content") == "Keep the exact steer."));
        Assert.IsFalse(saved.Any(item => item.GetString("content") == "Duplicate from the Room log."));
        if (fail) Assert.AreEqual(1, saved.Count, "Only admitted input should survive, without a partial tool exchange.");
    }

    [TestMethod]
    public async Task ToolResultBudgetUsesTheRouteBoundAtTheStartOfTheTurn()
    {
        var provider = new Provider("fixture", "Fixture", "openai", "http://fixture.invalid", "", "first",
            ContextWindow: 12000, MaxOutput: 1024, CompactAt: 4000, BackgroundCompaction: false);
        var bodies = new List<JsonObject>();
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") != "http_request") return;
            bodies.Add(JsonValues.ParseObject(request.GetString("body")));
            var response = bodies.Count == 1
                ? """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"route","function":{"name":"update_model","arguments":"{}"}}]}}]}"""
                : """{"choices":[{"delta":{"content":"Done."}}]}""";
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + response + "\n\n", ["done"] = true });
        });
        var model = new ModelClient(port, () => provider);
        using var session = TestAgentSession.Open("temporary", ":memory:", model, _ => { });
        var output = new string('x', 3500);
        var skill = new AgentSkill("fixture", "Change the configured model.", new JsonArray(Tools.Define("update_model", "Update the route.")),
            (_, _, _) =>
            {
                provider = provider with { Model = "next", ContextWindow = 128000, CompactAt = 0 };
                return Task.FromResult<JsonNode?>(JsonValue.Create(output));
            }, Required: true);
        await session.Run("Instructions.", "Start.", skill, CancellationToken.None);
        Assert.AreEqual("first", bodies[1].GetString("model"));
        var projected = bodies[1].GetArray("messages").Single(item => item.GetString("role") == "tool").GetString("content");
        var key = JsonValues.ParseObject(projected).GetString("content_key");
        Assert.AreEqual(output, session.Db.Get(key));
    }
}
