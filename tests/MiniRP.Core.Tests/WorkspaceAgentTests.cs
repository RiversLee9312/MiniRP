using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class WorkspaceAgentTests
{
    [TestMethod]
    public async Task ImportsPythonConversationAndRetainsOriginalBackup()
    {
        var root = NewRoot();
        AtomicFile.Write(Path.Combine(root, "presets", "default.json"), """{"name":"Default"}""");
        var legacy = """{"id":"legacy","preset_id":"default","name":"Legacy","chat":{"history":[{"role":"user","content":"Hello"}],"summary":"Retained summary"},"trace":[{"kind":"thought","agent":"ChatAgent","text":"Prior trace"}]}""";
        AtomicFile.Write(Path.Combine(root, "conversations", "default", "legacy.json"), legacy);
        var state = new JsonObject { ["sim_date"] = "2026-11-17", ["phase"] = "Prior phase", ["canon"] = new JsonArray(), ["pages"] = new JsonObject { ["page"] = new JsonObject { ["content"] = "CACHED_BODY_SENTINEL" } } };
        AtomicFile.Write(Path.Combine(root, "world_state.json"), JsonValues.Serialize(new JsonObject { ["presets"] = new JsonObject { ["default"] = state } }));

        await using (var migration = new Engine(root)) { }
        var world = new Workspace(root);
        var preset = world.InitialPreset().Id;
        var chat = world.Chats(preset).Single().GetString("id");
        Assert.IsTrue(Guid.TryParseExact(preset, "D", out _));
        Assert.IsTrue(Guid.TryParseExact(chat, "D", out _));
        world.Select(preset, chat);
        Assert.AreEqual("Prior phase", world.State.GetString("phase"));
        Assert.AreEqual("Prior trace", world.Chat.GetArray("blocks")[0].GetString("reasoning"));
        Assert.AreEqual(legacy, File.ReadAllText(Path.Combine(world.ChatRoot, "migration", "python-v1.json")));
        Assert.IsFalse(world.WorldText.Contains("CACHED_BODY_SENTINEL", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WorldChangesCommitOnlyAfterACompletedTurn()
    {
        var world = NewWorld();
        var model = Fixture(round => round == 0
            ? Assistant(Call("add_fact", new()
            {
                ["fact"] = "Established fact"
            }), Call("set_phase", new()
            {
                ["phase"] = "Updated phase"
            }))
            : new JsonObject { ["content"] = "Updated." });
        using var session = new AgentSession("EnvAgent", Path.Combine(world.ChatRoot, "env-test.sqlite"), model, _ => { });
        await new EnvAgent(world, session).Run("Update facts", false, CancellationToken.None);
        Assert.AreEqual("Updated phase", world.State.GetString("phase"));
        Assert.AreEqual("Established fact", world.State.GetArray("canon")[0]!.ToString());
    }

    [TestMethod]
    public async Task PresetPromptAndConversationSurviveReload()
    {
        var world = NewWorld();
        var model = Fixture(round => round == 0
            ? Assistant(Call("set_value", new()
            {
                ["path"] = "chat_agent.system_prompt",
                ["value"] = "Date: {date}"
            }))
            : new JsonObject { ["content"] = "Saved." });
        using var session = new AgentSession("PresetAgent", Path.Combine(world.ChatRoot, "preset-test.sqlite"), model, _ => { });
        await new PresetAgent(world, session).Run("Set prompt", CancellationToken.None);
        var loaded = new Workspace(world.Root);
        loaded.Select(world.PresetId, world.ChatId);
        Assert.AreEqual("Date: {date}", loaded.Config.GetObject("chat_agent").GetString("system_prompt"));
        Assert.AreEqual(world.ChatId, loaded.ChatId);
    }

    [TestMethod]
    [DataRow("character", "ensemble")]
    [DataRow("ensemble", "tabletop")]
    public async Task ExistingPresetCannotConvertBetweenRegularAndEnsemble(string current, string requested)
    {
        var world = NewWorld();
        var config = (JsonObject)world.Config.DeepClone();
        config["chat_mode"] = current;
        config["setup_status"] = "complete";
        world.CommitConfig(config);
        var requests = new List<JsonObject>();
        var model = Fixture(round => round == 0 ? Assistant(Call("configure_experience",
            new() { ["mode"] = requested, ["agentic_search"] = true })) : new JsonObject { ["content"] = "Create a new preset." },
            requests.Add);
        using var session = new AgentSession("PresetAgent", Path.Combine(world.ChatRoot, "mode-test.sqlite"), model, _ => { });
        await new PresetAgent(world, session).Run("Change the chat structure.", CancellationToken.None);
        Assert.AreEqual(current, world.Config.GetString("chat_mode"));
        var result = requests[1].GetArray("messages").Single(message => message.GetString("role") == "tool");
        StringAssert.Contains(result.GetString("content"), "Create a new preset to use the other format.");
    }

    [TestMethod]
    public async Task CompactionArchivesFullHistoryAndRetainsAnActiveRequest()
    {
        var world = NewWorld();
        var model = Fixture(round => new JsonObject { ["content"] = round == 0 ? "Preserved key: note/task. Continue the request." : "Done." });
        using var session = new AgentSession("ChatAgent", Path.Combine(world.ChatRoot, "compact-test.sqlite"), model, _ => { });
        var original = new string('x', 170000);
        session.Restore(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = original }));
        await session.Run("Test", "Continue", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
        var archives = session.Db.Call("db_list", new() { ["prefix"] = "_archive/history/" }).GetArray("entries");
        Assert.AreEqual(1, archives.Count);
        Assert.IsTrue(session.Db.Get(archives[0].GetString("key"))!.Contains(original, StringComparison.Ordinal));
        Assert.AreEqual("Continue", session.History[0].GetString("content"));
        Assert.IsTrue(session.Summary.Contains("note/task", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SearchReferenceStaysTransientAndInstructionChangesKeepThePreviousPrefix()
    {
        var world = NewWorld();
        var requests = new List<JsonObject>();
        var model = Fixture(_ => new JsonObject { ["content"] = "Done." }, inspect: requests.Add);
        using var session = new AgentSession("SearchAgent", Path.Combine(world.ChatRoot, "search-test.sqlite"), model, _ => { });
        var reference = "Recent chat: " + new string('x', 2400);
        var oldSkill = new AgentSkill("research", "Find facts.", new(),
            (_, _, _) => Task.FromResult<JsonNode?>(null), Required: true, Instructions: "Policy one.");
        var newSkill = oldSkill with { Instructions = "Policy two.",
            Tools = new JsonArray(Tools.Define("research_new", "Find sources.", new())) };

        await session.Run("Old instructions.", "Search first", oldSkill, CancellationToken.None, reference: reference);
        await session.Run("New instructions.", "Search again", newSkill, CancellationToken.None, reference: reference);

        var first = requests[0].GetArray("messages");
        var second = requests[1].GetArray("messages");
        Assert.AreEqual(first[0].GetString("content"), second[0].GetString("content"));
        Assert.AreEqual("Search first", second[1].GetString("content"));
        var delta = second.First(message => message.GetString("content").StartsWith("Session configuration update.", StringComparison.Ordinal));
        Assert.AreEqual("system", delta.GetString("role"));
        StringAssert.Contains(delta.GetString("content"), "New instructions.");
        StringAssert.Contains(delta.GetString("content"), "Policy two.");
        StringAssert.Contains(delta.GetString("content"), "Tool added: research_new");
        Assert.AreEqual(1, second.Count(message => message.GetString("content") == reference));
        Assert.IsFalse(session.History.Any(message => message.GetString("content") == reference));
    }

    [TestMethod]
    public async Task HistoryToolsRecallOriginalTextAfterCompactionAndStayInSession()
    {
        var world = NewWorld();
        var path = Path.Combine(world.ChatRoot, "history-test.sqlite");
        var original = "Keep the lantern green 🟢. " + new string('x', 170000);
        var events = new List<JsonObject>();
        using (var initial = new AgentSession("ChatAgent", path,
            Fixture(round => new JsonObject { ["content"] = round == 0 ? "Compressed summary." : "Done." }), events.Add))
        {
            await initial.Run("Test", original, new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
            Assert.IsFalse(initial.History.Any(item => item.GetString("content") == original));
        }

        string MessageId() => JsonValues.ParseObject(events.Last(e => e.GetString("name") == "history_search" && e.GetString("kind") == "tool_end").GetString("text"))
            .GetArray("matches")[0].GetString("id");

        using var resumed = new AgentSession("ChatAgent", path, Fixture(round => round switch
        {
            0 => Assistant(Call("history_search", new() { ["query"] = "lantern green" })),
            1 => Assistant(Call("history_recall", new() { ["id"] = MessageId(), ["length"] = 25 })),
            _ => new JsonObject { ["content"] = "Recovered." }
        }), events.Add);
        await resumed.Run("Test", "Recall the color", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
        var recalled = JsonValues.ParseObject(events.Single(e => e.GetString("name") == "history_recall" && e.GetString("kind") == "tool_end").GetString("text"));
        Assert.AreEqual(original[..25], recalled.GetString("content"));
        Assert.AreEqual(original.Length, recalled.GetInt32("total_chars"));
        Assert.AreEqual(25, recalled.GetInt32("next_offset"));

        using var other = new AgentSession("EnvAgent", Path.Combine(world.ChatRoot, "separate-history-test.sqlite"), Fixture(round => round == 0
            ? Assistant(Call("history_search", new() { ["query"] = "lantern green" })) : new JsonObject { ["content"] = "No match." }), events.Add);
        await other.Run("Test", "Find a lantern", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
        var otherSearch = events.Last(e => e.GetString("name") == "history_search" && e.GetString("kind") == "tool_end");
        Assert.AreEqual(0, JsonValues.ParseObject(otherSearch.GetString("text")).GetArray("matches").Count);
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "minirp-workspace-" + Guid.NewGuid().ToString("N"));

    private static Workspace NewWorld()
    {
        var world = new Workspace(NewRoot());
        world.Select(world.InitialPreset().Id, null);
        return world;
    }
    private static JsonObject Call(string name, JsonObject args) => new() { ["id"] = JsonValues.NewId(), ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonValues.Serialize(args) } };
    private static JsonObject Assistant(params JsonObject[] calls)
    {
        var array = new JsonArray(); foreach (var call in calls)
        {
            array.AddNode(call);
        }

        return new() { ["tool_calls"] = array };
    }
    private static ModelClient Fixture(Func<int, JsonObject> respond, Action<JsonObject>? inspect = null)
    {
        var round = 0;
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") == "cancel_io")
            {
                return;
            }

            inspect?.Invoke(JsonValues.ParseObject(request.GetString("body")));
            var data = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = respond(round++) }) };
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + JsonValues.Serialize(data) + "\n\n", ["done"] = true });
        });
        return new(port, () => new("test", "Test", "openai", "http://fixture.invalid", "test", "test"));
    }
}
