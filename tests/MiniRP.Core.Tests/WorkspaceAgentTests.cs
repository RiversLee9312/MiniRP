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
        using var session = TestAgentSession.Open("env", Path.Combine(world.ChatRoot, "env-test.sqlite"), model, _ => { });
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
        using var session = TestAgentSession.Open("preset", Path.Combine(world.ChatRoot, "preset-test.sqlite"), model, _ => { });
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
        using var session = TestAgentSession.Open("preset", Path.Combine(world.ChatRoot, "mode-test.sqlite"), model, _ => { });
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
        using var session = TestAgentSession.Open("chat", Path.Combine(world.ChatRoot, "compact-test.sqlite"), model, _ => { });
        var original = new string('x', 330000);
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
        using var session = TestAgentSession.Open("search", Path.Combine(world.ChatRoot, "search-test.sqlite"), model, _ => { });
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
        var original = "Keep the lantern green 🟢. " + new string('x', 370000);
        var events = new List<JsonObject>();
        using (var initial = TestAgentSession.Open("chat", path,
            Fixture(round => new JsonObject { ["content"] = round == 0 ? "Compressed summary." : "Done." }), events.Add))
        {
            await initial.Run("Test", original, new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
            Assert.IsFalse(initial.History.Any(item => item.GetString("content") == original));
        }

        string MessageId() => JsonValues.ParseObject(events.Last(e => e.GetString("name") == "history_search" && e.GetString("kind") == "tool_end").GetString("text"))
            .GetArray("matches")[0].GetString("id");

        using var resumed = TestAgentSession.Open("chat", path, Fixture(round => round switch
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

        using var other = TestAgentSession.Open("env", Path.Combine(world.ChatRoot, "separate-history-test.sqlite"), Fixture(round => round == 0
            ? Assistant(Call("history_search", new() { ["query"] = "lantern green" })) : new JsonObject { ["content"] = "No match." }), events.Add);
        await other.Run("Test", "Find a lantern", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
        var otherSearch = events.Last(e => e.GetString("name") == "history_search" && e.GetString("kind") == "tool_end");
        Assert.AreEqual(0, JsonValues.ParseObject(otherSearch.GetString("text")).GetArray("matches").Count);
    }

    [TestMethod]
    public async Task BackgroundCheckpointSurvivesReloadAndKeepsMessagesAddedAfterItsCut()
    {
        var world = NewWorld();
        var path = Path.Combine(world.ChatRoot, "background-compaction.sqlite");
        var provider = new Provider("test", "Test", "openai", "http://fixture.invalid", "", "test",
            ContextWindow: 50000, MaxOutput: 2000);
        var summaryRequest = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new System.Collections.Concurrent.ConcurrentQueue<JsonObject>();
        var summaries = 0;
        HostPort? port = null;
        void Reply(JsonObject request, string text)
        {
            var data = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) };
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + JsonValues.Serialize(data) + "\n\n", ["done"] = true });
        }
        port = new(request =>
        {
            if (request.GetString("kind") == "cancel_io") return;
            var body = JsonValues.ParseObject(request.GetString("body"));
            var first = body.GetArray("messages")[0].GetString("content");
            if (first.StartsWith("Write a continuation checkpoint", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref summaries);
                if (!summaryRequest.TrySetResult(request)) Reply(request, "Unexpected second checkpoint.");
                return;
            }
            requests.Enqueue(body);
            Reply(request, "Reply after the cut.");
        });
        var model = new ModelClient(port, () => provider);
        var original = "Original state " + new string('x', 105000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var originalSession = TestAgentSession.Open("chat", path, model, _ => { }))
        {
            originalSession.Restore(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = original }));
            await originalSession.Run("Test", "Prepare without blocking", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), timeout.Token);
            Assert.AreEqual("", originalSession.Summary);
            Assert.AreEqual(original, originalSession.History[0].GetString("content"));
            Reply(await summaryRequest.Task.WaitAsync(timeout.Token), "Checkpoint retained the original state.");
            while (originalSession.Db.Get("_context/prepared-compaction") is null)
                await Task.Delay(10, timeout.Token);
            Assert.AreEqual("", originalSession.Summary);
        }

        var events = new List<JsonObject>();
        using var resumed = TestAgentSession.Open("chat", path, model, events.Add);
        var nextInput = "New input " + new string('y', 21000);
        await resumed.Run("Test", nextInput, new(), (_, _, _) => Task.FromResult<JsonNode?>(null), timeout.Token);
        Assert.AreEqual(1, summaries);
        Assert.AreEqual(2, requests.Count);
        var messages = requests.Last().GetArray("messages");
        StringAssert.Contains(messages[0].GetString("content"), "Checkpoint retained the original state.");
        Assert.IsTrue(messages.Any(message => message.GetString("content") == "Reply after the cut."));
        Assert.AreEqual(nextInput, messages.Last().GetString("content"));
        Assert.IsTrue(events.Any(e => e.GetString("text").StartsWith("Used the background checkpoint.", StringComparison.Ordinal)));
        var key = events.Single(e => e.GetString("kind") == "compacted").GetString("key");
        Assert.AreEqual(original, JsonValues.ParseObject(resumed.Db.Get(key)!).GetArray("history")[0].GetString("content"));
    }

    [TestMethod]
    public async Task RepeatedCompactionCarriesProgressAndKeepsCompleteRecentToolBatches()
    {
        var world = NewWorld();
        var requests = new List<JsonObject>();
        var progress = 0;
        var completed = 0;
        var model = Fixture(round =>
        {
            var messages = requests[^1].GetArray("messages");
            if (messages[0].GetString("content").StartsWith("Write a continuation checkpoint", StringComparison.Ordinal))
            {
                if (progress++ > 0)
                    StringAssert.Contains(messages[1].GetString("content"), "Checkpoint " + (progress - 1));
                return new() { ["content"] = "Checkpoint " + progress };
            }
            Assert.IsTrue(messages.Any(message => message.GetString("content") == "Finish the current job"));
            var calls = messages.SelectMany(message => message.GetArray("tool_calls")).Select(call => call.GetString("id")).ToArray();
            var results = messages.Where(message => message.GetString("role") == "tool").Select(message => message.GetString("tool_call_id")).ToArray();
            CollectionAssert.AreEqual(calls, results);
            if (completed >= 4)
            {
                Assert.IsTrue(messages.Any(message => message.GetString("role") == "tool" &&
                    message.GetString("content").StartsWith("step-8", StringComparison.Ordinal)));
                return new() { ["content"] = "Done" };
            }
            return Assistant(Call("step", new()), Call("step", new()));
        }, requests.Add, new Provider("test", "Test", "openai", "http://fixture.invalid", "", "test",
            CompactAt: 5400, BackgroundCompaction: false));
        using var session = TestAgentSession.Open("chat", Path.Combine(world.ChatRoot, "repeated-compaction.sqlite"), model, _ => { });
        session.Restore(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new string('x', 22000) }));
        var callsExecuted = 0;
        await session.Run("Test", "Finish the current job", new JsonArray(Tools.Define("step", "Complete one step.")), (_, _, _) =>
        {
            var step = ++callsExecuted;
            completed = step / 2;
            return Task.FromResult<JsonNode?>(JsonValue.Create($"step-{step} " + new string('p', 1880)));
        }, CancellationToken.None);
        Assert.IsGreaterThanOrEqualTo(2, progress);
        Assert.AreEqual(8, callsExecuted);
        StringAssert.Contains(session.Summary, "Checkpoint " + progress);
    }

    [TestMethod]
    public async Task PresetEditsReadTheCurrentConfigWithoutFeedingHistoricalSnapshots()
    {
        var world = NewWorld();
        var marker = new string('设', 25000);
        var config = (JsonObject)world.Config.DeepClone();
        config["canon"] = new JsonArray(marker);
        world.CommitConfig(config);
        var path = Path.Combine(world.ChatRoot, "old-preset.sqlite");
        using (var legacy = new MemoryDb(path))
        {
            legacy.WriteInternal(("_context/history", """[{"role":"user","content":"Keep the lantern green."}]"""),
                ("_context/instructions", JsonValues.Serialize(new JsonObject { ["base"] = marker, ["system"] = marker })));
        }
        var requests = new List<JsonObject>();
        var seen = new List<JsonObject>();
        var model = Fixture(round => round % 2 == 0
            ? Assistant(Call("set_value", new() { ["path"] = "name", ["value"] = "Preset " + round }))
            : new JsonObject { ["content"] = "Saved." },
            inspect: requests.Add, provider: new Provider("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture",
                ContextWindow: 16000, MaxOutput: 1000, BackgroundCompaction: false));
        using var session = TestAgentSession.Open("preset", path, model, seen.Add);
        var preset = new PresetAgent(world, session);
        for (var i = 0; i < 6; i++)
            await preset.Run("Rename the preset " + i, CancellationToken.None);

        Assert.AreEqual(12, requests.Count);
        Assert.AreEqual("Preset 10", world.Config.GetString("name"));
        Assert.IsTrue(requests.All(request => !JsonValues.Serialize(request).Contains(marker, StringComparison.Ordinal)));
        Assert.IsTrue(requests.All(request => request.GetArray("messages").Any(item => item.GetString("content") == "Keep the lantern green.")));
        Assert.IsFalse(session.History.Any(SessionInstructions.IsFrame));
        Assert.IsFalse(seen.Any(item => item.GetString("name") == "compact_context"));
    }

    [TestMethod]
    public async Task EnvAgentKeepsOnlyTheUserRequestsAndUsesTheLatestRoomReference()
    {
        var world = NewWorld();
        var requests = new List<JsonObject>();
        var model = Fixture(_ => new() { ["content"] = "Done." }, requests.Add);
        using var session = TestAgentSession.Open("env", Path.Combine(world.ChatRoot, "env-reference.sqlite"), model, _ => { });
        var agent = new EnvAgent(world, session);
        world.Chat["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Previous room view" });
        await agent.Run("First edit", false, CancellationToken.None);
        world.Chat["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Current room view" });
        await agent.Run("Second edit", false, CancellationToken.None);
        var sent = requests[1].GetArray("messages");
        Assert.AreEqual(1, sent.Count(item => item.GetString("content").Contains("Conversation reference:", StringComparison.Ordinal)));
        Assert.IsTrue(sent.Any(item => item.GetString("content").Contains("Current room view", StringComparison.Ordinal)));
        Assert.IsFalse(sent.Any(item => item.GetString("content").Contains("Previous room view", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "First edit", "Second edit" }, session.History
            .Where(item => item.GetString("role") == "user").Select(item => item.GetString("content")).ToArray());
    }

    [TestMethod]
    public async Task ProviderUsageSurvivesResumeAndIsDiscardedWithThePricedHistory()
    {
        var world = NewWorld();
        var path = Path.Combine(world.ChatRoot, "usage-anchor.sqlite");
        var requests = new List<JsonObject>();
        var summaries = 0;
        var provider = new Provider("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture",
            ContextWindow: 10000, MaxOutput: 1000, CompactAtPercent: 80, BackgroundCompaction: false);
        var model = Fixture(_ =>
        {
            if (requests[^1].GetArray("messages")[0].GetString("content").StartsWith("Write a continuation checkpoint", StringComparison.Ordinal))
            {
                summaries++;
                return new() { ["content"] = "Remember the original status." };
            }
            return new() { ["content"] = "Reply." };
        }, requests.Add, provider, inputUsage: round => round == 0 ? 8400 : 0);
        var original = "Preserve original status " + new string('a', 11250);
        using (var first = TestAgentSession.Open("preset", path, model, _ => { }))
            await first.Run("Test", original, new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);

        using var second = TestAgentSession.Open("preset", path, model, _ => { });
        await second.Run("Test", "Continue", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
        await second.Run("Test", "One more turn", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
        Assert.AreEqual(1, summaries);
        Assert.AreEqual(4, requests.Count);
        Assert.IsFalse(second.History.Any(item => item.GetString("content") == original));
        Assert.IsFalse(requests[2].GetArray("messages").Any(item => item.GetString("content") == original));
        StringAssert.Contains(requests[3].GetArray("messages")[0].GetString("content"), "Remember the original status.");
    }

    [TestMethod]
    public async Task FailedTurnDoesNotRestoreTheCompactedCommittedHistory()
    {
        var world = NewWorld();
        var path = Path.Combine(world.ChatRoot, "durable-compaction.sqlite");
        var original = new string('x', 16000);
        var provider = new Provider("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture",
            ContextWindow: 12000, MaxOutput: 1000, CompactAt: 4000, BackgroundCompaction: false);
        using (var session = TestAgentSession.Open("preset", path, Fixture(round => round == 0
                   ? new() { ["content"] = "The committed checkpoint." }
                   : throw new IOException("Primary generation failed."), provider: provider), _ => { }))
        {
            session.Restore(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = original }));
            await Assert.ThrowsExactlyAsync<IOException>(() => session.Run("Test", "Uncommitted request", new(),
                (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None));
        }
        var requests = new List<JsonObject>();
        using var resumed = TestAgentSession.Open("preset", path, Fixture(_ => new() { ["content"] = "Reply." }, requests.Add, provider), _ => { });
        Assert.AreEqual(0, resumed.History.Count);
        StringAssert.Contains(resumed.Summary, "The committed checkpoint.");
        await resumed.Run("Test", "Retry", new(), (_, _, _) => Task.FromResult<JsonNode?>(null), CancellationToken.None);
        Assert.AreEqual(1, requests.Count);
        var sent = requests[0].GetArray("messages");
        Assert.IsFalse(sent.Any(item => item.GetString("content") == original || item.GetString("content") == "Uncommitted request"));
    }

    [TestMethod]
    public void EstimatesTheProviderProjectionAndCountsUnicodeAndToolSchemas()
    {
        var model = Fixture(_ => new() { ["content"] = "Done." });
        var baseMessage = new JsonObject { ["role"] = "assistant", ["content"] = new string('a', 900) };
        var oldMessage = (JsonObject)baseMessage.DeepClone();
        oldMessage["reasoning_content"] = new string('思', 20000);
        oldMessage["response_items"] = new JsonArray(new JsonObject { ["encrypted_content"] = new string('x', 20000) });
        var baseline = model.EstimateInput(new JsonArray(baseMessage.DeepClone()), new());
        Assert.AreEqual(baseline, model.EstimateInput(new JsonArray(oldMessage), new()));
        Assert.IsGreaterThan(baseline, model.EstimateInput(new JsonArray(new JsonObject
            { ["role"] = "assistant", ["content"] = new string('字', 900) }), new()));
        var tools = new JsonArray(Tools.Define("state", "Report state", new()
            { ["status"] = new JsonObject { ["type"] = "string", ["description"] = new string('a', 3000) } }));
        Assert.IsGreaterThan(baseline + 900, model.EstimateInput(new JsonArray(baseMessage.DeepClone()), tools));
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
    private static ModelClient Fixture(Func<int, JsonObject> respond, Action<JsonObject>? inspect = null,
        Provider? provider = null, Func<int, int>? inputUsage = null)
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
            var index = round++;
            var data = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = respond(index) }) };
            if (inputUsage?.Invoke(index) is > 0 and var tokens)
                data["usage"] = new JsonObject { ["prompt_tokens"] = tokens };
            port!.Reply(new() { ["id"] = request.GetString("id"), ["data"] = "data: " + JsonValues.Serialize(data) + "\n\n", ["done"] = true });
        });
        return new(port, () => provider ?? new("test", "Test", "openai", "http://fixture.invalid", "test", "test"));
    }
}
