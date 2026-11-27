using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class TranscriptPagingTests
{
    [TestMethod]
    public async Task WebTranscriptPagesKeepScopeOrderAndAssetsAcrossCheckpoints()
    {
        var directory = Directory.CreateTempSubdirectory("minirp-transcript-test-");
        try
        {
            var world = new Workspace(directory.FullName);
            var preset = world.InitialPreset().Id;
            world.Select(preset, null);
            var room = world.ChatId;
            var blocks = world.Chat.GetArray("blocks");
            for (var index = 0; index < 205; index++)
            {
                blocks.AddNode(Block("ChatAgent", index));
                if (index < 95) blocks.AddNode(Block("EnvAgent", index));
            }
            var attachment = WorkspaceIds.New();
            var url = "minirp://assets/minirp.attachments/" + attachment;
            var oldBlock = blocks.OfType<JsonObject>().Single(block => block.GetString("content") == "ChatAgent-80");
            oldBlock["asset_links"] = new JsonObject { [url] = new JsonObject
                { ["attachment"] = new JsonObject { ["id"] = attachment, ["name"] = "history.txt", ["mime"] = "text/plain" } } };
            // An unavailable plugin still used to emit a card update for an unrequested old block.
            oldBlock["plugin_card"] = new JsonObject { ["package"] = "example.history.card" };
            var attachmentPath = Path.Combine(directory.FullName, "attachments", attachment);
            Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
            await File.WriteAllTextAsync(attachmentPath, "Paged attachment");
            world.Chat["name"] = "History fixture";
            world.Save();
            var shared = new PresetConversation(world);
            for (var index = 0; index < 83; index++) shared.Blocks.AddNode(Block("PresetAgent", index));
            var tool = (JsonObject)shared.Blocks[0]!;
            tool["tool"] = "configure_date";
            tool["arguments"] = """{"mode":"plugin"}""";
            tool["result"] = """{"staged":true,"mode":"plugin"}""";
            shared.Save();
            world.Select(preset, null);
            var other = world.ChatId;
            world.Save();

            await using (var engine = new Engine(directory.FullName))
            {
                engine.Dispatch(new() { ["op"] = "configure", ["settings"] = new JsonObject { ["_web_transcripts"] = true } });
                engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = room });
                var loaded = await Event(engine, "loaded");
                var history = loaded.GetObject("history");
                var newest = loaded.GetObject("chat").GetArray("blocks");
                Assert.HasCount(240, newest);
                Assert.AreEqual("ChatAgent-125", newest[0].GetString("content"));
                Assert.AreEqual("EnvAgent-15", newest[80].GetString("content"));
                Assert.AreEqual("PresetAgent-3", newest[160].GetString("content"));
                Assert.AreEqual("PresetAgent-82", newest[239].GetString("content"));
                var before = history.GetString("chat");
                var ruler = history.GetObject("_ruler").GetArray("chat");
                Assert.HasCount(125, ruler);
                Assert.AreEqual("ChatAgent-0", ruler[0].GetString("content"));
                Assert.AreEqual("ChatAgent-124", ruler[124].GetString("content"));
                Assert.IsFalse(ruler[80]!.AsObject().ContainsKey("asset_links"));
                Assert.HasCount(15, history.GetObject("_ruler").GetArray("env"));

                async Task<int> RefreshCards()
                {
                    var count = 0;
                    engine.Dispatch(new() { ["op"] = "plugin_locale", ["locale"] = "en" });
                    engine.Dispatch(new() { ["op"] = "list_presets" });
                    await Next(engine, item =>
                    {
                        if (item.GetString("kind") == "open_ui_card_update") count++;
                        return item.GetString("kind") == "presets";
                    });
                    return count;
                }
                Assert.AreEqual(0, await RefreshCards());

                async Task<JsonObject> Page(string agent, string cursor, string selectedRoom, string locale = "") =>
                    await Request(engine, new JsonObject { ["op"] = "transcript_page", ["preset"] = preset,
                        ["room"] = selectedRoom, ["agent"] = agent, ["before"] = cursor, ["locale"] = locale });

                var middle = await Page("chat", before, room);
                Assert.HasCount(80, middle.GetArray("blocks"));
                Assert.AreEqual("ChatAgent-45", middle.GetArray("blocks")[0].GetString("content"));
                Assert.AreEqual("ChatAgent-124", middle.GetArray("blocks")[79].GetString("content"));
                Assert.AreEqual(1, await RefreshCards());
                var beginning = await Page("chat", middle.GetString("before"), room);
                Assert.HasCount(45, beginning.GetArray("blocks"));
                Assert.IsNull(beginning["before"]);
                var env = await Page("env", history.GetString("env"), room);
                Assert.HasCount(15, env.GetArray("blocks"));
                Assert.IsNull(env["before"]);
                var presetPage = await Page("preset", history.GetString("preset"), "");
                Assert.HasCount(3, presetPage.GetArray("blocks"));
                Assert.AreEqual("PresetAgent-0", presetPage.GetArray("blocks")[0].GetString("content"));
                Assert.AreEqual("Configure calendar", presetPage.GetArray("blocks")[0].GetString("tool_title"));
                var localized = await Page("preset", history.GetString("preset"), "", "zh-Hant");
                var trace = localized.GetArray("blocks")[0];
                Assert.AreEqual("設定日曆", trace.GetString("tool_title"));
                Assert.AreEqual("由外掛管理", trace.GetObject("tool_arguments_ui").GetObject("state")
                    .GetArray("rows")[0].GetString("value"));
                Assert.IsTrue((await Page("env", before, room)).ContainsKey("error"));
                Assert.IsTrue((await Page("chat", before, other)).ContainsKey("error"));
                Assert.IsTrue((await Page("chat", "invalid", room)).ContainsKey("error"));

                var asset = await Request(engine, new JsonObject { ["op"] = "display_asset_open", ["preset"] = preset,
                    ["room"] = room, ["block"] = oldBlock.GetString("id"), ["url"] = url });
                Assert.AreEqual("text/plain", asset.GetObject("result").GetString("mimeType"));
                var handle = asset.GetObject("result").GetString("handle");
                await Request(engine, new JsonObject { ["op"] = "asset_close", ["handle"] = handle });

                lock (engine.Workspace.RoomGate)
                    engine.Workspace.Chat.GetArray("blocks").AddNode(Block("ChatAgent", 205));
                engine.Dispatch(new() { ["op"] = "_transcript_checkpoint" });
                var checkpoint = await Event(engine, "transcript_checkpoint");
                Assert.AreEqual("ChatAgent-205", checkpoint.GetObject("panes").GetArray("chat")[79].GetString("content"));
                Assert.HasCount(126, checkpoint.GetObject("history").GetObject("_ruler").GetArray("chat"));
                var repeated = await Page("chat", before, room);
                CollectionAssert.AreEqual(middle.GetArray("blocks").Select(block => block.GetString("id")).ToArray(),
                    repeated.GetArray("blocks").Select(block => block.GetString("id")).ToArray());

                engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = other });
                await Event(engine, "loaded");
                Assert.IsTrue((await Page("chat", before, room)).ContainsKey("error"));
            }

            await using (var engine = new Engine(directory.FullName))
            {
                engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = room });
                var loaded = await Event(engine, "loaded");
                Assert.IsFalse(loaded.ContainsKey("history"));
                Assert.HasCount(383, loaded.GetObject("chat").GetArray("blocks"));
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task EnsembleHistoryCannotCrossTheSelectedScene()
    {
        var directory = Directory.CreateTempSubdirectory("minirp-ensemble-history-test-");
        try
        {
            var world = new Workspace(directory.FullName);
            var preset = world.InitialPreset().Id;
            world.Select(preset, null);
            var room = world.ChatId;
            var config = (JsonObject)world.Config.DeepClone();
            config["chat_mode"] = "ensemble";
            config["ensemble"] = JsonValues.ParseObject("""
                {"agents":[{"id":"ada","name":"Ada"},{"id":"bryn","name":"Bryn"}],
                 "chats":[{"name":"Bridge","members":["ada"]},{"name":"Lab","members":["bryn"]}]}
                """);
            world.CommitConfig(config);
            var ensemble = new EnsembleConversation(world);
            var bridge = ensemble.List().Single(item => item.GetString("name") == "Bridge").GetString("id");
            var lab = ensemble.List().Single(item => item.GetString("name") == "Lab").GetString("id");
            for (var index = 0; index < 95; index++)
            {
                var entry = Block("Bridge", index);
                entry["role"] = "user";
                ensemble.Blocks(bridge).AddNode(entry);
                ensemble.Blocks(bridge).AddNode(new JsonObject { ["id"] = JsonValues.NewId(), ["participant"] = "ada", ["reasoning"] = "Hidden trace." });
            }
            var labMessage = Block("Lab", 0);
            labMessage["role"] = "assistant";
            ensemble.Blocks(lab).AddNode(labMessage);
            foreach (var name in new[] { "ada", "bryn" })
            {
                using var character = new MemoryDb(ensemble.Database(name, "chat"));
                character.Write("_room/ready", "true", internalWrite: true);
            }
            world.Save();

            await using var engine = new Engine(directory.FullName);
            engine.Dispatch(new() { ["op"] = "configure", ["settings"] = new JsonObject { ["_web_transcripts"] = true } });
            engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = room });
            await Event(engine, "loaded");
            async Task<JsonObject> Select(string scene) => await Request(engine, new JsonObject
                { ["op"] = "select_ensemble_chat", ["chat"] = room, ["conversation"] = scene });
            var bridgeLoaded = await Select(bridge);
            Assert.HasCount(80, bridgeLoaded.GetArray("blocks"));
            Assert.AreEqual("Bridge-15", bridgeLoaded.GetArray("blocks")[0].GetString("content"));
            var before = bridgeLoaded.GetObject("history").GetString("ensemble");
            var ruler = bridgeLoaded.GetObject("history").GetObject("_ruler").GetArray("ensemble");
            Assert.HasCount(15, ruler);
            Assert.AreEqual("Bridge-0", ruler[0].GetString("content"));

            async Task<JsonObject> Read(string scene) => await Request(engine, new JsonObject
                { ["op"] = "transcript_page", ["preset"] = preset, ["room"] = room,
                    ["conversation"] = scene, ["agent"] = "chat", ["before"] = before });
            var old = await Read(bridge);
            Assert.HasCount(15, old.GetArray("blocks"));
            Assert.AreEqual("Bridge-0", old.GetArray("blocks")[0].GetString("content"));
            var traces = (await Request(engine, new JsonObject { ["op"] = "ensemble_member_trace", ["preset"] = preset,
                ["chat"] = room, ["participant"] = "ada" })).GetArray("blocks");
            Assert.HasCount(30, traces);
            Assert.IsTrue(traces.All(block => block.GetString("scene_name") == "Bridge" && block.GetString("conversation") == bridge &&
                block.GetString("reasoning") == "Hidden trace."));
            Assert.IsTrue((await Read(lab)).ContainsKey("error"));
            var labLoaded = await Select(lab);
            Assert.AreEqual("Lab-0", labLoaded.GetArray("blocks").Single().GetString("content"));
            Assert.IsNull(labLoaded.GetObject("history")["ensemble"]);
            Assert.IsTrue((await Read(bridge)).ContainsKey("error"));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static JsonObject Block(string agent, int index) => new()
    {
        ["id"] = JsonValues.NewId(), ["agent"] = agent, ["content"] = agent + "-" + index
    };

    private static async Task<JsonObject> Request(Engine engine, JsonObject command)
    {
        var request = JsonValues.NewId();
        command["request"] = request;
        engine.Dispatch(command);
        return await Next(engine, item => item.GetString("request") == request);
    }

    private static Task<JsonObject> Event(Engine engine, string kind) => Next(engine, item => item.GetString("kind") == kind);

    private static async Task<JsonObject> Next(Engine engine, Func<JsonObject, bool> matches)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>())
                if (matches(item)) return item;
            await Task.Delay(10, deadline.Token);
        }
    }
}
