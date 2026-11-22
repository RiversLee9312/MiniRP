using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using MiniRP.Cli;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class ExtensionTests
{
    [TestMethod]
    [DataRow("./missing.js")]
    [DataRow("../../helper.js")]
    [DataRow("helper.js")]
    [DataRow("file:///helper.js")]
    public void ModuleImportsRequireRelativePathsInsideThePackage(string specifier)
    {
        using var scripts = new PluginScripts();
        var failure = Assert.Throws<ScriptEngineException>(() => scripts.Step(new()
        {
            ["action"] = "load", ["isolate"] = "module", ["entry"] = "src/index.mjs", ["bootstrap"] = "",
            ["modules"] = new JsonObject
            {
                ["src/index.mjs"] = $"import '{specifier}';",
                ["helper.js"] = "export const value = 42;"
            }
        }, CancellationToken.None));
        StringAssert.Contains(failure.Message, specifier);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PresetOpeningRunsAsPrivateSystemInputOnceForNewChats(bool agenticSearch)
    {
        var root = NewRoot();
        var workspace = new Workspace(root);
        var preset = workspace.InitialPreset().Id;
        workspace.UsePreset(preset);
        const string privateSetup = "Greet the user as the keeper of the lunar greenhouse.";
        var config = (JsonObject)workspace.Config.DeepClone();
        config["agentic_search"] = agenticSearch;
        config.GetObject("chat_agent")["greeting_system_message"] = privateSetup;
        workspace.CommitConfig(config);
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        Configure(engine);
        await Until(engine, "ready", scripts);
        Assert.AreEqual(0, engine.Workspace.Chats(preset).Count, "Opening the app does not create a conversation.");

        var chat = "";
        engine.Dispatch(new() { ["op"] = "start_chat", ["preset"] = preset });
        var openingRequest = await NextModel(engine, scripts, item =>
        {
            if (item.GetString("kind") == "loaded") chat = item.GetObject("chat").GetString("id");
            Assert.IsFalse(item.GetString("kind") is "system" or "user", "The setup must stay hidden.");
        });
        var opening = JsonValues.ParseObject(openingRequest.GetString("body")).GetArray("messages");
        Assert.IsTrue(opening.Any(message => message.GetString("role") == "system" &&
            message.GetString("content").Contains(privateSetup, StringComparison.Ordinal)));
        Reply(engine, openingRequest, "Welcome to the greenhouse.");
        await Until(engine, "completed", scripts);
        AssertCanonicalUuid(chat);

        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = chat });
        var loaded = (await Until(engine, "loaded", scripts)).GetObject("chat").GetArray("blocks");
        Assert.IsTrue(loaded.Any(block => block.GetString("content") == "Welcome to the greenhouse."));
        Assert.IsFalse(loaded.Any(block => block.GetString("content").Contains(privateSetup, StringComparison.Ordinal)));
        engine.Dispatch(new() { ["op"] = "message", ["preset"] = preset, ["chat"] = chat, ["text"] = "Show me the plants." });
        var followUp = await NextModel(engine, scripts);
        var messages = JsonValues.ParseObject(followUp.GetString("body")).GetArray("messages");
        Assert.AreEqual("Show me the plants.", messages.Last().GetString("content"), "Reopening did not start another greeting.");
        Assert.AreEqual(1, messages.Count(message => message.GetString("content").Contains(privateSetup, StringComparison.Ordinal)));
        Reply(engine, followUp, "Follow me.");
        await Until(engine, "completed", scripts);
    }

    [TestMethod]
    public async Task EnsembleOpeningAppearsOnlyOnceForEachCharacterAcrossChats()
    {
        var root = NewRoot();
        var preset = WriteEnsemblePreset(root);
        var workspace = new Workspace(root, seedDefaultPreset: false);
        workspace.UsePreset(preset);
        var config = (JsonObject)workspace.Config.DeepClone();
        config["chat_agent"] = new JsonObject { ["greeting_system_message"] = "Give a private station welcome." };
        workspace.CommitConfig(config);
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
        var room = await Until(engine, "loaded", scripts);
        var roomId = room.GetObject("chat").GetString("id");
        var children = room.GetArray("ensemble_chats");

        async Task OpenChild(string name, string participant)
        {
            var child = children.Single(entry => entry.GetString("name") == name).GetString("id");
            engine.Dispatch(new() { ["op"] = "select_ensemble_chat", ["chat"] = roomId, ["conversation"] = child });
            var request = await NextModel(engine, scripts);
            var messages = JsonValues.ParseObject(request.GetString("body")).GetArray("messages");
            Assert.IsTrue(messages.Any(message => message.GetString("role") == "system" &&
                message.GetString("content").Contains("private station welcome", StringComparison.Ordinal)));
            Assert.IsTrue(messages.Any(message => message.GetString("content").Contains("Your character is " + participant, StringComparison.Ordinal)));
            Reply(engine, request, "Welcome from " + participant + ".");
            var posted = await Until(engine, "ensemble_message", scripts);
            Assert.AreEqual(child, posted.GetString("conversation"));
            Assert.AreEqual("Welcome from " + participant + ".", posted.GetObject("item").GetString("content"));
        }

        await OpenChild("Lab", "Bryn");
        await OpenChild("Bridge", "Ada");
        var saved = JsonValues.ParseObject(File.ReadAllText(Path.Combine(root, "conversations", preset, roomId + ".json")));
        CollectionAssert.AreEquivalent(new[] { "ada", "bryn" }, saved.GetArray("greeted_participants").Select(item => JsonValues.GetText(item)).ToArray());

        var lab = children.Single(entry => entry.GetString("name") == "Lab").GetString("id");
        engine.Dispatch(new() { ["op"] = "select_ensemble_chat", ["chat"] = roomId, ["conversation"] = lab });
        await Until(engine, "ensemble_chat_loaded", scripts);
        var path = Path.Combine(root, "lab.jsonl");
        engine.Dispatch(new()
        {
            ["op"] = "archive_export", ["type"] = "transcript", ["agent"] = "chat", ["preset"] = preset,
            ["chat"] = roomId, ["conversation"] = lab, ["path"] = path,
        });
        await Until(engine, "archive_exported", scripts);
        var transcript = File.ReadAllLines(path).Select(JsonValues.ParseObject).ToArray();
        Assert.AreEqual(lab, transcript[0].GetString("chat"));
        Assert.IsTrue(transcript.Any(row => row.GetString("content") == "Welcome from Bryn." && row.GetString("speaker") == "Bryn"));
        Assert.IsFalse(transcript.Any(row => row.GetString("content") is "Welcome from Ada." or "Give a private station welcome."));
    }

    [TestMethod]
    public async Task HomeStartsWithoutConversationAndDeletionRemovesChatAndPresetFromCatalog()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        var preset = engine.Workspace.PresetId;
        AssertCanonicalUuid(preset);
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = preset });
        await Until(engine, "home");
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "conversations")));

        engine.Dispatch(new() { ["op"] = "system_message", ["text"] = "System note" });
        await Until(engine, "system");
        var chat = engine.Workspace.ChatId;
        AssertCanonicalUuid(chat);
        engine.Dispatch(new() { ["op"] = "delete_chat", ["preset"] = preset, ["id"] = chat });
        await Until(engine, "deleted");
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.IsFalse(File.Exists(Path.Combine(root, "conversations", preset, chat + ".json")));
        engine.Dispatch(new() { ["op"] = "delete_preset", ["preset"] = preset, ["id"] = preset });
        await Until(engine, "deleted");
        Assert.AreNotEqual(preset, engine.Workspace.PresetId);
        AssertCanonicalUuid(engine.Workspace.PresetId);
        engine.Dispatch(new() { ["op"] = "library_page" });
        Assert.AreEqual(1, (await Until(engine, "library_page")).GetArray("items").Count);
    }

    [TestMethod]
    public async Task CreatingAPresetStartsAnInvisibleBaseHookTurn()
    {
        await using var engine = new Engine(NewRoot(), seedDefaultPreset: false);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "create_preset", ["open"] = true, ["name"] = "Draft", ["locale"] = "zh-Hans" });
        var opened = await Until(engine, "preset_opened", scripts);
        Assert.AreEqual(0, opened.GetArray("blocks").Count);
        var request = await NextModel(engine, scripts, item =>
        {
            Assert.AreNotEqual("system", item.GetString("kind"), "Setup is private.");
            Assert.AreNotEqual("user", item.GetString("kind"), "No synthetic user bubble.");
        });
        var body = JsonValues.ParseObject(request.GetString("body"));
        var instruction = body.GetArray("messages").Skip(1).First();
        Assert.AreEqual("system", instruction.GetString("role"));
        StringAssert.Contains(instruction.GetString("content"), "zh-Hans");
        StringAssert.Contains(instruction.GetString("content"), "Ask only the first question");
        StringAssert.Contains(instruction.GetString("content"), "setting documents");
        StringAssert.Contains(instruction.GetString("content"), "SillyTavern");
        Reply(engine, request, "你想创造一个什么样的世界？");
        await Until(engine, "completed", scripts);
        engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = opened.GetString("preset") });
        var reopened = await Until(engine, "preset_opened", scripts);
        Assert.AreEqual(1, reopened.GetArray("blocks").Count);
        Assert.AreEqual("你想创造一个什么样的世界？", reopened.GetArray("blocks")[0].GetString("content"));
        Assert.AreEqual("", engine.Workspace.ChatId);
    }

    [TestMethod]
    public async Task ReopeningADraftRequiresPresetAgentToCompleteSetup()
    {
        await using var engine = new Engine(NewRoot(), seedDefaultPreset: false);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "create_preset", ["open"] = true, ["name"] = "Draft" });
        var preset = (await Until(engine, "preset_opened", scripts)).GetString("preset");
        Reply(engine, await NextModel(engine, scripts), "What would you like to create?");
        await Until(engine, "completed", scripts);

        async Task AssertOpensDraft(string operation)
        {
            engine.Dispatch(new() { ["op"] = operation, ["preset"] = preset });
            var opened = await Until(engine, "preset_opened", scripts);
            Assert.IsTrue(opened.GetBoolean("selected"));
            Assert.AreEqual("draft", opened.GetObject("config").GetString("setup_status"));
            Assert.IsTrue(opened.GetArray("blocks").Any(item => item.GetString("content") == "What would you like to create?"));
            Assert.AreEqual("", engine.Workspace.ChatId);
        }

        foreach (var operation in new[] { "home", "select_chat", "start_chat" }) await AssertOpensDraft(operation);
        engine.Dispatch(new() { ["op"] = "message", ["preset"] = preset, ["text"] = "Try to start a chat." });
        StringAssert.Contains((await Until(engine, "error", scripts)).GetString("message"), "Finish setting up the preset");
        Assert.AreEqual("", engine.Workspace.ChatId);

        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["preset"] = preset, ["text"] = "Use a single character and delegated search." });
        ReplyTool(engine, await NextModel(engine, scripts), "configure_experience",
            new() { ["mode"] = "character", ["agentic_search"] = true });
        Reply(engine, await NextModel(engine, scripts), "The experience is ready to review.");
        await Until(engine, "completed", scripts);
        await AssertOpensDraft("home");

        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["preset"] = preset, ["text"] = "Complete the setup." });
        ReplyTool(engine, await NextModel(engine, scripts), "finish_preset_setup", new());
        var finished = await NextModel(engine, scripts);
        StringAssert.Contains(finished.GetString("body"), "completed");
        Reply(engine, finished, "The preset is ready.");
        await Until(engine, "completed", scripts);
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = preset });
        Assert.AreEqual("complete", (await Until(engine, "home", scripts)).GetObject("config").GetString("setup_status"));
        engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = preset });
        var card = (await Until(engine, "preset_opened", scripts)).GetArray("blocks")
            .Single(block => block.GetString("open_ui_host_action") == "preset-home");
        StringAssert.Contains(card.GetString("open_ui"), "@ToAssistant(\"preset-home\")");
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
        Assert.AreNotEqual("", (await Until(engine, "loaded", scripts)).GetObject("chat").GetString("id"));
    }

    [TestMethod]
    public async Task PresetReadsAnAttachedCardAndResearchesWithoutCreatingARoom()
    {
        var root = NewRoot();
        var id = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(Path.Combine(root, "attachments"));
        File.WriteAllText(Path.Combine(root, "attachments", id), """{"name":"Mira","description":"Keeps a blue lantern."}""");
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        var preset = engine.Workspace.PresetId;
        engine.Dispatch(new() { ["op"] = "configure", ["provider"] = new JsonObject
            { ["url"] = "http://fixture.invalid/v1", ["model"] = "fixture" },
            ["settings"] = new JsonObject { ["search_key"] = "test", ["search_url"] = "https://research.fixture.invalid" } });
        engine.Dispatch(new() { ["op"] = "_curl_available" });
        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["preset"] = preset, ["text"] = "Import my card.",
            ["attachments"] = new JsonArray(new JsonObject { ["id"] = id, ["name"] = "character.json", ["mime"] = "application/json" }) });

        var model = await NextModel(engine, scripts);
        var request = JsonValues.ParseObject(model.GetString("body"));
        var tools = request.GetArray("tools");
        Assert.IsTrue(tools.Any(tool => tool.GetObject("function").GetString("name") == "real_search"));
        Assert.IsTrue(tools.Any(tool => tool.GetObject("function").GetString("name") == "real_fetch"));
        StringAssert.Contains(model.GetString("body"), MessageAttachments.AssetPrefix + id);

        ReplyTool(engine, model, "activate_skill", new() { ["name"] = "container" });
        var withContainer = await NextModel(engine, scripts);
        Assert.IsTrue(JsonValues.ParseObject(withContainer.GetString("body")).GetArray("tools")
            .Any(tool => tool.GetObject("function").GetString("name") == "export_asset"));
        ReplyTool(engine, withContainer, "activate_skill", new() { ["name"] = "curl" });
        var withCurl = await NextModel(engine, scripts);
        Assert.IsTrue(JsonValues.ParseObject(withCurl.GetString("body")).GetArray("tools")
            .Any(tool => tool.GetObject("function").GetString("name") == "curl"));
        ReplyTool(engine, withCurl, "activate_skill", new() { ["name"] = "view_asset" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_grep",
            new() { ["url"] = MessageAttachments.AssetPrefix + id, ["pattern"] = "blue lantern" });
        var card = await NextModel(engine, scripts);
        StringAssert.Contains(card.GetString("body"), "Keeps a blue lantern");

        const string url = "https://lore.fixture.invalid/lantern";
        ReplyTool(engine, card, "real_search", new() { ["query"] = "Lantern lore" });
        var apiSearch = await NextModel(engine, scripts);
        Assert.AreEqual("https://research.fixture.invalid/v1/tools/search", apiSearch.GetString("url"));
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = apiSearch.GetString("id"), ["done"] = true,
            ["data"] = """{"results":[{"url":"https://lore.fixture.invalid/lantern","title":"Lantern lore","snippet":"Harbor signal"}]}""" });
        var hits = await NextModel(engine, scripts);
        StringAssert.Contains(hits.GetString("body"), "Harbor signal");
        ReplyTool(engine, hits, "real_fetch", new() { ["url"] = url });
        var apiFetch = await NextModel(engine, scripts);
        Assert.AreEqual("https://research.fixture.invalid/v1/tools/fetch", apiFetch.GetString("url"));
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = apiFetch.GetString("id"), ["done"] = true,
            ["data"] = """{"title":"Lantern lore","content":"Blue lanterns signal safe harbor."}""" });
        var page = await NextModel(engine, scripts);
        var lastTool = JsonValues.ParseObject(page.GetString("body")).GetArray("messages").Last(item => item.GetString("role") == "tool");
        var document = JsonValues.ParseObject(lastTool.GetString("content"));
        StringAssert.Contains(document.GetString("content"), "Blue lanterns signal safe harbor");
        ReplyTool(engine, page, "cache_page", new() { ["ref"] = document.GetString("ref") });
        ReplyTool(engine, await NextModel(engine, scripts), "set_value",
            new() { ["path"] = "chat_agent.system_prompt", ["value"] = "You are Mira and carry a blue lantern." });
        Reply(engine, await NextModel(engine, scripts), "I imported Mira.");
        await Until(engine, "completed", scripts);

        Assert.AreEqual("You are Mira and carry a blue lantern.", engine.Workspace.AgentPrompt("chat"));
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "conversations")));
        using var database = MemoryDb.ReadOnly(SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, preset)));
        StringAssert.Contains(database.Get(PresetResearch.StateKey) ?? "", url);
    }

    [TestMethod]
    public async Task EnsembleTurnsUseChatMembershipAndSeparateCharacterSessions()
    {
        var root = NewRoot();
        var preset = WriteEnsemblePreset(root);
        await using var engine = new Engine(root, seedDefaultPreset: false);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
        var loaded = await Until(engine, "loaded", scripts);
        var chats = loaded.GetArray("ensemble_chats");
        Assert.AreEqual(2, chats.Count);
        var room = loaded.GetObject("chat").GetString("id");
        var bridgeId = chats.Single(item => item.GetString("name") == "Bridge").GetString("id");
        var labId = chats.Single(item => item.GetString("name") == "Lab").GetString("id");
        foreach (var id in new[] { room, bridgeId, labId }) AssertCanonicalUuid(id);
        Assert.HasCount(3, new[] { room, bridgeId, labId }.Distinct());
        engine.Dispatch(new() { ["op"] = "message", ["chat"] = room, ["conversation"] = bridgeId, ["text"] = "Status?" });

        var ada = await NextModel(engine, scripts);
        StringAssert.Contains(ada.GetString("body"), "Your character is Ada");
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.Dispatch(new() { ["op"] = "steer",
            ["chat"] = room, ["conversation"] = labId, ["text"] = "Wrong chat." }));
        ReplyTool(engine, ada, "chat_send", new() { ["chat"] = labId, ["text"] = "Unauthorized" });
        var denied = await NextModel(engine, scripts);
        StringAssert.Contains(denied.GetString("body"), "not a member");
        ReplyTool(engine, denied, "chat_send", new() { ["chat"] = bridgeId, ["text"] = "Ada reported in." });
        Reply(engine, await NextModel(engine, scripts), "Private continuation.");
        var bryn = await NextModel(engine, scripts);
        var messages = JsonValues.ParseObject(bryn.GetString("body")).GetArray("messages");
        StringAssert.Contains(messages[0].GetString("content"), "Your character is Bryn");
        Assert.IsTrue(messages.Any(message => message.GetString("role") == "system" &&
            message.GetString("content").Contains("Ada reported in.", StringComparison.Ordinal)));
        Assert.IsFalse(messages.Any(message => message.GetString("content").Contains("Private continuation.", StringComparison.Ordinal)));
        ReplyTool(engine, bryn, "chat_send", new() { ["chat"] = labId, ["text"] = "Lab note." });
        Reply(engine, await NextModel(engine, scripts), "Done.");

        // Selection is serialized after the turn, so both posts have been committed.
        engine.Dispatch(new() { ["op"] = "select_ensemble_chat", ["conversation"] = labId, ["chat"] = room });
        var lab = await Until(engine, "ensemble_chat_loaded", scripts);
        Assert.AreEqual("Lab note.", lab.GetArray("blocks").Single().GetString("content"));
        Assert.AreEqual("Bryn", lab.GetArray("blocks").Single().GetString("speaker"));
        var adaSession = engine.LoadedCharacterSession("ada", "chat");
        var brynSession = engine.LoadedCharacterSession("bryn", "chat");
        var brynSearch = engine.LoadedCharacterSession("bryn", "search");
        Assert.IsNotNull(adaSession);
        Assert.IsNotNull(brynSession);
        Assert.IsNotNull(brynSearch);
        Assert.AreNotSame(adaSession, brynSession);
        engine.Dispatch(new() { ["op"] = "message", ["chat"] = room, ["conversation"] = labId, ["text"] = "Anything from the bridge?" });
        var followUp = await NextModel(engine, scripts);
        var labPrompt = followUp.GetString("body");
        StringAssert.Contains(labPrompt, "Your character is Bryn");
        StringAssert.Contains(labPrompt, "The active chat is Lab (" + labId + ")");
        StringAssert.Contains(labPrompt, "Ada reported in.");
        Assert.AreEqual(1, labPrompt.Split("Ada reported in.", StringSplitOptions.None).Length - 1);
        Assert.AreSame(adaSession, engine.LoadedCharacterSession("ada", "chat"));
        Assert.AreSame(brynSession, engine.LoadedCharacterSession("bryn", "chat"));
        Assert.AreSame(brynSearch, engine.LoadedCharacterSession("bryn", "search"));
        Reply(engine, followUp, "Private lab follow-up.");
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = room });
        await Until(engine, "loaded", scripts);
        Assert.AreSame(brynSession, engine.LoadedCharacterSession("bryn", "chat"));
        foreach (var character in new[] { "ada", "bryn" })
            foreach (var role in new[] { "chat", "search" })
                Assert.IsTrue(File.Exists(Path.Combine(engine.Workspace.ChatRoot, "ensemble", character, role + ".sqlite")));

        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
        var newRoom = await Until(engine, "loaded", scripts);
        Assert.AreNotEqual(room, newRoom.GetObject("chat").GetString("id"));
        var freshSession = engine.LoadedCharacterSession("bryn", "chat");
        Assert.IsNotNull(freshSession);
        Assert.AreNotSame(brynSession, freshSession);
        Assert.IsFalse(freshSession.HasHistory);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = room });
        await Until(engine, "loaded", scripts);
        engine.Dispatch(new() { ["op"] = "message", ["chat"] = room, ["conversation"] = labId, ["text"] = "Reopened lab." });
        var reopened = await NextModel(engine, scripts);
        StringAssert.Contains(reopened.GetString("body"), "Private lab follow-up.");
        Assert.AreNotSame(brynSession, engine.LoadedCharacterSession("bryn", "chat"));
        Reply(engine, reopened, "Restored the room.");
    }

    [TestMethod]
    public async Task EnsembleRoomRequiresEveryCharacterToConfirmAndResumesPartialPreparation()
    {
        var root = NewRoot();
        var preset = WriteEnsemblePreset(root);
        await using var engine = new Engine(root, seedDefaultPreset: false);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
        var events = new List<JsonObject>();
        var ada = await NextModel(engine, scripts, events.Add);
        var loading = events.Last(item => item.GetString("kind") == "room_loading");
        var room = loading.GetString("room");
        Assert.AreEqual(0, loading.GetInt32("ready"));
        Assert.AreEqual(2, loading.GetInt32("total"));
        CollectionAssert.AreEqual(new[] { "Watch the navigation lights.", "Read the watch roster." },
            loading.GetArray("messages").Select(item => JsonValues.GetText(item)).ToArray());
        var adaPrompt = ada.GetString("body");
        StringAssert.Contains(adaPrompt, "Your character is Ada");
        StringAssert.Contains(adaPrompt, "Your chats: Bridge");
        Assert.IsFalse(adaPrompt.Contains("Lab", StringComparison.Ordinal));
        Assert.IsTrue(Offers(ada, "room_ready") && Offers(ada, "room_progress"));
        Assert.IsFalse(Offers(ada, "chat_send"));

        ReplyTool(engine, ada, "room_progress", new() { ["percent"] = 60 });
        var followUp = await NextModel(engine, scripts, events.Add);
        var progress = events.Last(item => item.GetString("kind") == "room_loading");
        Assert.AreEqual(0.3, progress["progress"]!.GetValue<double>());
        Reply(engine, followUp, "I am ready.");
        var retry = await NextModel(engine, scripts, events.Add);
        Assert.IsFalse(events.Any(item => item.GetString("kind") == "loaded"));
        ReplyTool(engine, retry, "room_ready", new());

        var bryn = await NextModel(engine, scripts, events.Add);
        StringAssert.Contains(bryn.GetString("body"), "Your character is Bryn");
        StringAssert.Contains(bryn.GetString("body"), "Your chats: Bridge, Lab");
        Assert.AreEqual(1, events.Last(item => item.GetString("kind") == "room_loading").GetInt32("ready"));
        Assert.IsFalse(events.Any(item => item.GetString("kind") == "loaded"));
        engine.Dispatch(new() { ["op"] = "cancel" });
        await Until(engine, "cancelled", scripts);
        Assert.AreEqual("", engine.Workspace.ChatId);

        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = room });
        events.Clear();
        var resumed = await NextModel(engine, scripts, events.Add);
        StringAssert.Contains(resumed.GetString("body"), "Your character is Bryn");
        Assert.AreEqual(1, events.Last(item => item.GetString("kind") == "room_loading").GetInt32("ready"));
        ReplyTool(engine, resumed, "room_ready", new());
        var loaded = await Until(engine, "loaded", scripts);
        Assert.AreEqual(room, loaded.GetObject("chat").GetString("id"));
        var chat = loaded.GetArray("ensemble_chats").Single(item => item.GetString("name") == "Lab").GetString("id");
        engine.Dispatch(new() { ["op"] = "message", ["chat"] = room, ["conversation"] = chat, ["text"] = "Status?" });
        var regular = await NextModel(engine, scripts);
        Assert.IsFalse(Offers(regular, "room_ready") || Offers(regular, "room_progress"));
        Assert.IsTrue(Offers(regular, "chat_send"));
        Assert.IsFalse(regular.GetString("body").Contains("I am ready.", StringComparison.Ordinal));
        Reply(engine, regular, "Ready for work.");
    }

    [TestMethod]
    public async Task PluginWorkflowOwnsStateNotifiesWatchersAndRefreshesItsCard()
    {
        var root = NewRoot();
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "MiniRP.slnx"))) source = source.Parent;
        Assert.IsNotNull(source);
        var example = Path.Combine(source.FullName, "examples", "room-status", "plugin");
        Directory.CreateDirectory(root);
        using var signer = Certificate();
        var archive = Path.Combine(root, "room-status.plugin");
        PluginArchive.Pack(example, archive, signer);
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        engine.Dispatch(new() { ["op"] = "configure",
            ["provider"] = new JsonObject { ["url"] = "http://fixture.invalid/v1", ["model"] = "fixture" },
            ["agents"] = new JsonObject { ["title"] = new JsonObject() } });
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
        await Until(engine, "plugin", scripts);
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Verify the station." });
        async Task Tool(string name, JsonObject args) =>
            ReplyTool(engine, await NextModel(engine, scripts), name, args);
        const string key = "_state/room-status/station";
        await Tool("activate_skill", new() { ["name"] = "room-status-status" });
        await Tool("plugin_room-status_status-card", new() { ["id"] = "station" });
        await Tool("db_watch", new() { ["key"] = key });
        await Tool("db_write", new() { ["key"] = key, ["content"] = """{"line":"Forged"}""" });
        var denied = await NextModel(engine, scripts);
        StringAssert.Contains(denied.GetString("body"), "Plugin state is read-only");
        ReplyTool(engine, denied, "plugin_room-status_report", new() { ["line"] = "Gate closed." });
        var notified = await NextModel(engine, scripts);
        var history = JsonValues.ParseObject(notified.GetString("body")).GetArray("messages");
        Assert.IsTrue(history.Any(message => message.GetString("role") == "system" &&
            message.GetString("content").Contains($"Watched DB entry {key} changed to revision 1", StringComparison.Ordinal)));
        ReplyTool(engine, notified, "db_read", new() { ["key"] = key });
        var read = await NextModel(engine, scripts);
        StringAssert.Contains(read.GetString("body"), "Gate closed.");
        Reply(engine, read, "Report saved.");
        var cardUpdated = false;
        var env = await NextModel(engine, scripts, item =>
        {
            if (item.GetString("kind") == "open_ui_card_update")
                cardUpdated |= item.GetObject("open_ui_state").GetString("line") == "Gate closed.";
        });
        Assert.IsTrue(cardUpdated);
        StringAssert.Contains(env.GetString("body"), "You are EnvAgent");
        StringAssert.Contains(env.GetString("body"), "A station report changed");
        ReplyTool(engine, env, "notify_chat", new() { ["text"] = "Gate closure has been confirmed." });
        Reply(engine, await NextModel(engine, scripts), "I notified the character.");
        var envCompleted = await Until(engine, "completed", scripts);
        Assert.AreEqual("env", envCompleted.GetString("agent"));
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Any update?" });
        var next = await NextModel(engine, scripts);
        Assert.IsTrue(JsonValues.ParseObject(next.GetString("body")).GetArray("messages").Any(message =>
            message.GetString("role") == "system" && message.GetString("content") == "Gate closure has been confirmed."));
        Reply(engine, next, "The gate is closed.");
        await Until(engine, "completed", scripts);
    }

    [TestMethod]
    public async Task PluginRoomStorageIsSharedWithinItsChatsAndOrdinaryScopesAlias()
    {
        var root = NewRoot();
        var castPreset = WriteEnsemblePreset(root);
        var regularPreset = Guid.NewGuid().ToString("D");
        AtomicFile.Write(Path.Combine(root, "presets", regularPreset + ".json"), """{"name":"Regular"}""");
        using var certificate = Certificate();
        var plugin = Pack(root, "scope-probe", certificate, """
            {"id":"scope-probe","packageName":"test.scopes","name":"Scopes","apiVersion":2,"entry":"index.js",
             "permissions":["db","harness.read"],"actions":{"handlers":{"run":"run"}},
             "hooks":{"plugin.command":"minirp://actions/test.scopes/hooks/run"}}
            """, """
            globalThis.handlers = {
              async run(event, api) {
                if (event.write) await api.db.put({scope:"room",key:"value",content:"room-value"});
                const room = await api.db.get({scope:"room",key:"value"});
                const chat = await api.db.get({scope:"chat",key:"value"});
                if (event.writeChat) await api.db.put({scope:"chat",key:"value",content:"chat-value"});
                const parent = await api.harness.readRoom();
                const child = await api.harness.readChat();
                await api.db.put({scope:"preset",key:event.report,content:JSON.stringify({
                  room:api.environment.room,chat:api.environment.chat, r:room.content,c:chat.content,
                  children:parent.chats.map(item=>item.id), child:child.id
                })});
              }
            };
            """);
        await using var engine = new Engine(root, seedDefaultPreset: false);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = plugin });
        await Until(engine, "plugin", scripts);
        async Task<(string Room, Dictionary<string, string> Chats)> Create(string preset)
        {
            engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
            var loaded = await Until(engine, "loaded", scripts);
            var room = loaded.GetObject("chat").GetString("id");
            AssertCanonicalUuid(room);
            var chats = loaded.GetArray("ensemble_chats").ToDictionary(item => item.GetString("name"), item => item.GetString("id"));
            foreach (var id in chats.Values) AssertCanonicalUuid(id);
            return (room, chats);
        }
        async Task Select(string room, string chat)
        {
            engine.Dispatch(new() { ["op"] = "select_ensemble_chat", ["chat"] = room, ["conversation"] = chat });
            await Until(engine, "ensemble_chat_loaded", scripts);
        }
        async Task<JsonObject> Report(string name, bool write = false, bool writeChat = false)
        {
            var workingPreset = engine.Workspace.PresetId;
            var file = Path.Combine(root, "plugins", "data", "test.scopes." + certificate.GetCertHashString(HashAlgorithmName.SHA256),
                "preset", workingPreset + ".sqlite");
            engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "scope-probe",
                ["arguments"] = new JsonObject { ["report"] = name, ["write"] = write, ["writeChat"] = writeChat } });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                foreach (var item in engine.Poll().OfType<JsonObject>()) Process(engine, item, scripts, timeout.Token);
                if (File.Exists(file))
                {
                    using var db = new MemoryDb(file);
                    if (db.Get(name) is { } value) return JsonValues.ParseObject(value);
                }
                await Task.Delay(8, timeout.Token);
            }
        }

        var first = await Create(castPreset);
        var firstBridge = first.Chats["Bridge"];
        var firstLab = first.Chats["Lab"];
        await Select(first.Room, firstBridge);
        var bridge = await Report("bridge-one", write: true, writeChat: true);
        Assert.AreEqual(first.Room, bridge.GetString("room"));
        Assert.AreEqual(firstBridge, bridge.GetString("child"));
        Assert.AreEqual("", bridge.GetString("c"));
        await Select(first.Room, firstLab);
        var lab = await Report("lab");
        Assert.AreEqual(first.Room, lab.GetString("room"));
        Assert.AreEqual(firstLab, lab.GetString("chat"));
        Assert.AreEqual("room-value", lab.GetString("r"));
        Assert.AreEqual("", lab.GetString("c"));
        CollectionAssert.AreEquivalent(new[] { firstBridge, firstLab }, lab.GetArray("children").Select(item => JsonValues.GetText(item)).ToArray());
        var second = await Create(castPreset);
        var secondBridge = second.Chats["Bridge"];
        Assert.AreNotEqual(firstBridge, secondBridge);
        Assert.AreNotEqual(firstLab, second.Chats["Lab"]);
        await Select(second.Room, secondBridge);
        var isolated = await Report("bridge-two");
        Assert.AreNotEqual(first.Room, isolated.GetString("room"));
        Assert.AreEqual(secondBridge, isolated.GetString("chat"));
        Assert.AreEqual("", isolated.GetString("r"));
        Assert.AreEqual("", isolated.GetString("c"));
        var normal = await Create(regularPreset);
        var regular = await Report("regular", write: true);
        Assert.AreEqual(normal.Room, regular.GetString("room"));
        Assert.AreEqual(normal.Room, regular.GetString("chat"));
        Assert.AreEqual("room-value", regular.GetString("c"));
    }

    [TestMethod]
    public async Task NewPresetCanBeNamedAndReopenedWithoutCreatingAChat()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        var initialPreset = engine.Workspace.PresetId;
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "create_preset", ["name"] = "New preset", ["open"] = true, ["interview"] = false });
        var created = await Until(engine, "preset_opened");
        var preset = created.GetString("preset");
        Assert.IsTrue(created.GetBoolean("created"));
        Assert.AreNotEqual(initialPreset, preset);
        AssertCanonicalUuid(preset);
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.AreEqual(0, created.GetArray("blocks").Count);

        engine.Dispatch(new() { ["op"] = "message", ["preset"] = preset, ["agent"] = "preset",
            ["text"] = "Create an undersea research station preset called Pelagic." });
        var first = await Until(engine, "http_request");
        var frame = new JsonObject { ["choices"] = new JsonArray(new JsonObject
        {
            ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "name", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "set_value", ["arguments"] = """{"path":"name","value":"Pelagic"}""" }
            }) }
        }) };
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = first.GetString("id"), ["done"] = true,
            ["data"] = "data: " + JsonValues.Serialize(frame) + "\n\n" });
        var second = await Until(engine, "http_request");
        StringAssert.Contains(second.GetString("body"), "staged");
        Reply(engine, second, "Pelagic is ready.");
        var changed = await Until(engine, "preset_config");
        Assert.AreEqual("Pelagic", changed.GetObject("config").GetString("name"));
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "conversations")));

        engine.Dispatch(new() { ["op"] = "library_page", ["limit"] = 30 });
        var presetRows = (await Until(engine, "library_page")).GetArray("items");
        Assert.AreEqual("Pelagic", presetRows.Single(row => row.GetString("id") == preset).GetString("name"));
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = preset });
        await Until(engine, "home");
        engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = preset });
        var reopened = await Until(engine, "preset_opened");
        Assert.IsTrue(reopened.GetArray("blocks").Any(block => block.GetString("content") == "Pelagic is ready."));
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "conversations")));

        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
        var loaded = await Until(engine, "loaded");
        Assert.IsTrue(loaded.GetObject("chat").GetArray("blocks").Any(block =>
            block.GetString("content") == "Pelagic is ready."));
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = initialPreset });
        await Until(engine, "home");
        engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = initialPreset });
        Assert.AreEqual(0, (await Until(engine, "preset_opened")).GetArray("blocks").Count);
    }

    [TestMethod]
    public async Task PresetDelegatesIndependentPluginProjectsThroughBaseSkills()
    {
        await using var engine = new Engine(NewRoot());
        using var scripts = new PluginScripts();
        engine.Dispatch(new()
        {
            ["op"] = "configure",
            ["provider"] = new JsonObject { ["url"] = "http://fixture.invalid/v1", ["model"] = "chat-model" },
            ["agents"] = new JsonObject
            {
                ["preset"] = new JsonObject { ["url"] = "http://fixture.invalid/v1", ["model"] = "preset-model" }
            }
        });
        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["text"] = "Delegate two projects." });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var rounds = new Dictionary<string, int>();
        string[] children = [];
        string deployed = "";
        string draftAsset = "";
        var completed = false;
        while (!completed)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                if (item.GetString("kind") == "completed")
                {
                    Assert.AreEqual("Both projects finished.", item.GetString("text"));
                    completed = true;
                }
                if (item.GetString("kind") != "http_request")
                {
                    Process(engine, item, scripts, timeout.Token);
                    continue;
                }
                var body = JsonValues.ParseObject(item.GetString("body"));
                var messages = body.GetArray("messages");
                var system = messages[0].GetString("content");
                var role = system.StartsWith("You are PluginAgent working on Writer", StringComparison.Ordinal) ? "writer" :
                    system.StartsWith("You are PluginAgent working on Reader", StringComparison.Ordinal) ? "reader" : "preset";
                Assert.AreEqual("preset-model", body.GetString("model"), role);
                var round = rounds.GetValueOrDefault(role);
                rounds[role] = round + 1;
                var result = messages.LastOrDefault(message => message.GetString("role") == "tool").GetString("content");
                if (role == "preset")
                {
                    Assert.IsFalse(body.GetArray("tools").Any(tool => tool.GetObject("function").GetString("name").StartsWith("project_asset_", StringComparison.Ordinal)));
                    Assert.IsFalse(system.Contains("base-plugin-docs", StringComparison.Ordinal));
                    switch (round)
                    {
                        case 0: Tool(item, "activate_skill", new() { ["name"] = "base-plugin-development" }); break;
                        case 1: Tool(item, "plugin_base_plugin_spawn", new() { ["name"] = "Writer", ["message"] = "Write and publish the project." }); break;
                        case 2:
                            children = [JsonValues.ParseObject(result).GetString("id")];
                            Tool(item, "plugin_base_plugin_spawn", new() { ["name"] = "Reader", ["message"] = "Read the manifest schema." });
                            break;
                        case 3:
                            children = [..children, JsonValues.ParseObject(result).GetString("id")];
                            Tool(item, "plugin_base_plugin_wait", new() { ["ids"] = new JsonArray(children[0], children[1]), ["seconds"] = 10 });
                            break;
                        case 4:
                            Assert.IsInstanceOfType<JsonArray>(JsonNode.Parse(result), result);
                            var statuses = (JsonArray)JsonNode.Parse(result)!;
                            CollectionAssert.AreEquivalent(new[] { "Published: " + draftAsset, "Schema read." },
                                statuses.Select(child => child.GetString("result")).ToArray(), result);
                            Tool(item, "activate_skill", new() { ["name"] = "view_asset" });
                            break;
                        case 5:
                            Tool(item, "plugin_base_grep", new() { ["url"] = draftAsset, ["pattern"] = "Updated documentation", ["literal"] = true });
                            break;
                        case 6:
                            Assert.AreEqual(1, JsonValues.ParseObject(result).GetInt32("match_count"), result);
                            Tool(item, "plugin_base_plugin_archive", new() { ["id"] = children[0] });
                            break;
                        case 7:
                            Assert.IsTrue(JsonValues.ParseObject(result).GetBoolean("archived"));
                            Reply(engine, item, "Both projects finished.");
                            break;
                        default: Assert.Fail("Unexpected PresetAgent request."); break;
                    }
                }
                else if (role == "reader")
                {
                    switch (round)
                    {
                        case 0: Tool(item, "activate_skill", new() { ["name"] = "base-plugin-docs" }); break;
                        case 1: Tool(item, "plugin_base_plugin_docs", new() { ["topic"] = "schema", ["query"] = "returnType" }); break;
                        case 2:
                            StringAssert.Contains(result, "asset-url");
                            Tool(item, "plugin_base_plugin_docs", new() { ["topic"] = "assets", ["query"] = "## Assets", ["length"] = 2400 });
                            break;
                        case 3:
                            StringAssert.Contains(JsonValues.ParseObject(result).GetString("text"), "api.text.encode");
                            Reply(engine, item, "Schema read.");
                            break;
                        default: Assert.Fail("Unexpected reader request."); break;
                    }
                }
                else
                {
                    switch (round)
                    {
                        case 0: Tool(item, "activate_skill", new() { ["name"] = "project_assets" }); break;
                        case 1:
                            var workers = SessionFiles.PluginDirectory(engine.Workspace.Root, engine.Workspace.PresetId);
                            var folder = Directory.EnumerateDirectories(workers).Single(path =>
                                JsonValues.ParseObject(File.ReadAllText(Path.Combine(path, "worker.json"))).GetString("name") == "Writer");
                            var workerId = Path.GetFileName(folder);
                            var source = Path.Combine(folder, "source");
                            AtomicFile.Write(Path.Combine(source, "manifest.json"), JsonValues.Serialize(new JsonObject
                            {
                                ["id"] = "draft-" + workerId, ["packageName"] = "local.minichat.p" + workerId,
                                ["name"] = "Writer", ["entry"] = "index.mjs", ["apiVersion"] = 2,
                                ["actions"] = new JsonObject { ["handlers"] = new JsonObject { ["echo"] = "echo" } },
                                ["assets"] = new JsonArray(
                                    new JsonObject { ["id"] = "live-notes", ["type"] = "file", ["mimeType"] = "text/plain",
                                        ["path"] = "legacy.txt", ["accessibility"] = "public" },
                                    new JsonObject { ["id"] = "private-notes", ["type"] = "file", ["mimeType"] = "text/plain", ["path"] = "legacy.txt" })
                            }));
                            AtomicFile.Write(Path.Combine(source, "index.mjs"),
                                "import { echo } from './lib/echo.js'; globalThis.handlers = { echo };");
                            AtomicFile.Write(Path.Combine(source, "lib", "echo.js"),
                                "import { format } from '../format.mjs'; export function echo(event) { return format(event.arguments); }");
                            AtomicFile.Write(Path.Combine(source, "format.mjs"), "export const format = value => value;");
                            AtomicFile.Write(Path.Combine(source, "legacy.txt"), "Retain this draft file.");
                            Tool(item, "project_asset_list", new());
                            break;
                        case 2:
                            var files = JsonValues.ParseObject(result).GetArray("files");
                            CollectionAssert.AreEqual(new[] { "format.mjs", "index.mjs", "legacy.txt", "lib/echo.js", "manifest.json" },
                                files.Select(file => file.GetString("path")).ToArray());
                            Assert.IsTrue(files.All(file => file.GetString("url").StartsWith("minirp://assets/minirp.agent/", StringComparison.Ordinal)));
                            Tool(item, "activate_skill", new() { ["name"] = "asset_storage" });
                            break;
                        case 3:
                            Tool(item, "create_asset", new() { ["name"] = "README.md", ["mime"] = "text/markdown", ["text"] = "Plugin documentation" });
                            break;
                        case 4:
                            draftAsset = JsonValues.ParseObject(result).GetString("url");
                            Assert.StartsWith("minirp://assets/minirp.agent/", draftAsset);
                            Tool(item, "project_asset_add", new() { ["path"] = "README.md", ["url"] = draftAsset });
                            break;
                        case 5:
                            Assert.AreEqual(draftAsset, JsonValues.ParseObject(result).GetString("url"));
                            Tool(item, "project_asset_add", new() { ["path"] = "linked.md", ["url"] = draftAsset });
                            break;
                        case 6:
                            Tool(item, "project_asset_remove", new() { ["path"] = "linked.md", ["url"] = draftAsset });
                            break;
                        case 7:
                            Assert.AreEqual(1, JsonValues.ParseObject(result).GetInt32("removed"), result);
                            Tool(item, "activate_skill", new() { ["name"] = "edit_asset" });
                            break;
                        case 8:
                            Tool(item, "plugin_base_text_edit", new() { ["url"] = draftAsset,
                                ["old_string"] = "Plugin", ["new_string"] = "Updated" });
                            break;
                        case 9:
                            Assert.AreEqual(1, JsonValues.ParseObject(result).GetInt32("changes"), result);
                            Tool(item, "activate_skill", new() { ["name"] = "base-plugin-project" });
                            break;
                        case 10:
                            Tool(item, "plugin_base_project_debug", new());
                            break;
                        case 11:
                            Assert.IsTrue(JsonValues.ParseObject(result).GetBoolean("ok"), result);
                            Tool(item, "plugin_base_project_publish", Fixture(expected: "different"));
                            break;
                        case 12:
                            StringAssert.Contains(result, "Draft check before installation");
                            StringAssert.Contains(result, "expected_result");
                            StringAssert.Contains(result, "Expected (Object)");
                            StringAssert.Contains(result, "Actual handler result");
                            StringAssert.Contains(result, "different");
                            StringAssert.Contains(result, "observed");
                            var installedDirectory = Path.Combine(engine.Workspace.Root, "plugins");
                            Assert.IsFalse(Directory.Exists(installedDirectory) &&
                                Directory.EnumerateFiles(installedDirectory, "draft-*.plugin").Any());
                            var badJson = Fixture(expected: "observed", serialized: true);
                            badJson["expected_result_json"] = "{";
                            Tool(item, "plugin_base_project_publish", badJson);
                            break;
                        case 13:
                            StringAssert.Contains(result, "expected_result_json must contain valid JSON");
                            StringAssert.Contains(result, "line 1, byte");
                            Tool(item, "plugin_base_project_publish", Fixture(expected: "observed", serialized: true));
                            break;
                        case 14:
                            var deployment = JsonValues.ParseObject(result);
                            deployed = deployment.GetString("id");
                            Assert.AreEqual("include", deployment.GetObject("scope").GetString("mode"), result);
                            Assert.AreEqual(engine.Workspace.PresetId, JsonValues.GetText(deployment.GetObject("scope").GetArray("presets")[0]));
                            var verification = deployment.GetObject("verification");
                            Assert.IsTrue(verification.GetBoolean("ok"), result);
                            Assert.AreEqual(deployment.GetString("generation"), verification.GetString("generation"));
                            Assert.AreEqual("observed", verification.GetObject("result").GetString("status"));
                            Tool(item, "plugin_base_project_verify", Fixture(expected: "observed"));
                            break;
                        case 15:
                            Assert.IsTrue(JsonValues.ParseObject(result).GetBoolean("ok"), result);
                            Tool(item, "activate_skill", new() { ["name"] = "view_asset" });
                            break;
                        case 16:
                            Tool(item, "plugin_base_grep", new() { ["url"] = "minirp://assets/local.minichat.p" + children[0] + "/live-notes",
                                ["pattern"] = "Retain this draft file", ["literal"] = true });
                            break;
                        case 17:
                            Assert.AreEqual(1, JsonValues.ParseObject(result).GetInt32("match_count"), result);
                            Tool(item, "plugin_base_grep", new() { ["url"] = "minirp://assets/local.minichat.p" + children[0] + "/private-notes",
                                ["pattern"] = "Retain this draft file", ["literal"] = true });
                            break;
                        case 18:
                            StringAssert.Contains(result, "public Asset declared by this project");
                            Tool(item, "plugin_base_grep", new() { ["url"] = "minirp://assets/minirp.base/project-license",
                                ["pattern"] = "MIT License", ["literal"] = true });
                            break;
                        case 19:
                            StringAssert.Contains(result, "Use an Asset URL supplied in this session");
                            var diagnostic = JsonValues.ParseObject(result).GetObject("diagnostic");
                            Assert.AreEqual("access_denied", diagnostic.GetString("code"));
                            Assert.AreEqual("host_api", diagnostic.GetString("phase"));
                            Assert.AreEqual("assetView.grep", diagnostic.GetObject("details").GetString("api"));
                            Reply(engine, item, "Published: " + draftAsset);
                            break;
                        default: Assert.Fail("Unexpected writer request."); break;
                    }
                }
            }
            if (!completed) await Task.Delay(8, timeout.Token);
        }
        Assert.StartsWith("draft-", deployed);
        using (var archive = ZipFile.OpenRead(Path.Combine(engine.Workspace.Root, "plugins", deployed + ".plugin")))
        {
            Assert.IsNull(archive.GetEntry("linked.md"));
            using var legacy = new StreamReader(archive.GetEntry("legacy.txt")!.Open());
            Assert.AreEqual("Retain this draft file.", legacy.ReadToEnd());
            using var reader = new StreamReader(archive.GetEntry("README.md")!.Open());
            Assert.AreEqual("Updated documentation", reader.ReadToEnd());
        }
        Assert.AreNotEqual(children[0], children[1]);
        var preset = engine.Workspace.PresetId;
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = preset });
        await Until(engine, "home", scripts);
        engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = preset });
        var reopened = (await Until(engine, "preset_opened", scripts)).GetArray("blocks");
        using var presetDb = MemoryDb.ReadOnly(SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(engine.Workspace.Root, preset)));
        Assert.IsFalse(AgentSession.ReadHistory(presetDb).Any(message =>
            message.GetString("content").StartsWith("PluginAgent task status update.", StringComparison.Ordinal)),
            "A committed wait already included these results.");
        var panels = reopened.Where(block => block.GetString("agent") == "PluginAgent")
            .GroupBy(block => block.GetString("plugin_run")).ToArray();
        Assert.HasCount(2, panels);
        foreach (var (name, reply, tool) in new[]
        {
            ("Writer", "Published: " + draftAsset, "plugin_base_project_publish"),
            ("Reader", "Schema read.", "plugin_base_plugin_docs")
        })
        {
            var panel = panels.Single(group => group.First().GetString("plugin_agent_name") == name);
            Assert.IsFalse(string.IsNullOrEmpty(panel.Key));
            Assert.AreEqual("finished", panel.First().GetString("plugin_status"));
            Assert.IsTrue(panel.Any(block => block.GetString("content") == reply));
            Assert.IsTrue(panel.Any(block => block.GetString("tool") == tool));
        }

        JsonObject Fixture(string expected, bool serialized = false) => new()
        {
            ["handler"] = "minirp://actions/local.minichat.p" + children[0] + "/hooks/echo",
            ["event"] = new JsonObject { ["arguments"] = new JsonObject { ["status"] = "observed" } },
            [serialized ? "expected_result_json" : "expected_result"] = serialized
                ? JsonValue.Create(JsonValues.Serialize(new JsonObject { ["status"] = expected }))
                : new JsonObject { ["status"] = expected },
        };

        void Tool(JsonObject request, string name, JsonObject args) => ReplyTool(engine, request, name, args);
    }

    [TestMethod]
    [DataRow(false, "idle")]
    [DataRow(true, "idle")]
    [DataRow(false, "steer")]
    [DataRow(true, "steer")]
    [DataRow(false, "inactive")]
    [DataRow(true, "inactive")]
    [DataRow(true, "retry")]
    public async Task PluginWorkerReportsAfterTheLastWait(bool fail, string timing)
    {
        var root = NewRoot();
        var workspace = new Workspace(root);
        var preset = workspace.InitialPreset().Id;
        var other = workspace.CreatePreset("Other preset");
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = preset });
        await Until(engine, "home", scripts);
        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["preset"] = preset, ["text"] = "Delegate." });
        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "base-plugin-development" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_plugin_spawn",
            new() { ["name"] = "Late worker", ["message"] = "Run the delegated task." });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        JsonObject? parent = null;
        JsonObject? worker = null;
        while (parent is null || worker is null)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                if (item.GetString("kind") != "http_request") Process(engine, item, scripts, timeout.Token);
                else if (Messages(item)[0].GetString("content").StartsWith("You are PluginAgent", StringComparison.Ordinal)) worker = item;
                else parent = item;
            }
            if (parent is null || worker is null) await Task.Delay(8, timeout.Token);
        }
        var id = JsonValues.ParseObject(Messages(parent).Last(message => message.GetString("role") == "tool").GetString("content"))
            .GetString("id");
        ReplyTool(engine, parent, "plugin_base_plugin_wait", new() { ["ids"] = new JsonArray(id), ["seconds"] = 1 });
        parent = await NextModel(engine, scripts);
        var wait = (JsonArray)JsonNode.Parse(Messages(parent).Last(message => message.GetString("role") == "tool").GetString("content"))!;
        Assert.AreEqual("running", wait.Single().GetString("status"));
        if (timing != "steer")
        {
            Reply(engine, parent, "The worker is still running.");
            await Until(engine, "completed", scripts);
        }

        if (timing == "inactive")
        {
            engine.Dispatch(new() { ["op"] = "home", ["preset"] = other });
            await Until(engine, "home", scripts);
            engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = other });
            await Until(engine, "preset_opened", scripts);
        }
        var expectedStatus = fail ? "failed" : "finished";
        var expectedResult = fail ? "The worker's model failed." : "The worker finished the project.";
        if (fail) engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = worker.GetString("id"), ["error"] = expectedResult });
        else Reply(engine, worker, expectedResult);

        var terminal = "";
        if (timing == "inactive")
        {
            terminal = (await Until(engine, "plugin_agent_status", scripts)).GetString("status");
            using var ownerDb = MemoryDb.ReadOnly(SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, preset)));
            Assert.IsTrue(AgentSession.HasPendingSystemMessages(ownerDb));
            using var otherDb = MemoryDb.ReadOnly(SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, other)));
            Assert.IsFalse(AgentSession.ReadHistory(otherDb).Any(message => message.GetString("content").Contains(expectedResult,
                StringComparison.Ordinal)));
            engine.Dispatch(new() { ["op"] = "home", ["preset"] = preset });
        }
        // The worker result must trigger a model request without another user message.
        var interrupted = false;
        var continuation = await NextModel(engine, scripts, item =>
        {
            if (item.GetString("kind") == "plugin_agent_status") terminal = item.GetString("status");
            if (timing != "steer") return;
            Assert.IsFalse(item.GetString("kind") == "generating" && !item.GetBoolean("value"),
                "The status update should reach the current turn.");
            if (item.GetString("kind") == "cancel_io" && item.GetString("id") == parent.GetString("id")) interrupted = true;
        });
        Assert.AreEqual(expectedStatus, terminal);
        if (timing == "steer") Assert.IsTrue(interrupted, "The outstanding request was never answered and must be steered.");
        AssertNotice(Messages(continuation));
        if (timing == "retry")
        {
            engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = continuation.GetString("id"), ["error"] = "Preset model unavailable." });
            Assert.AreEqual("preset", (await Until(engine, "error", scripts)).GetString("agent"));
            using (var pending = MemoryDb.ReadOnly(SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, preset))))
                Assert.IsTrue(AgentSession.HasPendingSystemMessages(pending));
            engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = preset });
            continuation = await NextModel(engine, scripts);
            AssertNotice(Messages(continuation));
        }
        Reply(engine, continuation, "I received the update.");
        await Until(engine, "completed", scripts);
        using (var db = MemoryDb.ReadOnly(SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, preset))))
        {
            AssertNotice(AgentSession.ReadHistory(db));
            Assert.IsFalse(AgentSession.HasPendingSystemMessages(db));
        }

        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["preset"] = preset, ["text"] = "Any update?" });
        var next = await NextModel(engine, scripts);
        AssertNotice(Messages(next));
        Reply(engine, next, "I received the update.");
        await Until(engine, "completed", scripts);

        static JsonArray Messages(JsonObject request) => JsonValues.ParseObject(request.GetString("body")).GetArray("messages");
        void AssertNotice(JsonArray messages)
        {
            var notice = messages.Single(message => message.GetString("content").StartsWith("PluginAgent task status update.",
                StringComparison.Ordinal));
            Assert.AreEqual("system", notice.GetString("role"));
            var report = JsonValues.ParseObject(notice.GetString("content").Split('\n', 2)[1]);
            Assert.AreEqual(id, report.GetString("id"));
            Assert.AreEqual(expectedStatus, report.GetString("status"));
            Assert.AreEqual(expectedResult, report.GetString("result"));
        }
    }

    [TestMethod]
    public async Task PresetProcessesPersistedWorkerReportsAfterRestart()
    {
        var root = NewRoot();
        var preset = new Workspace(root).InitialPreset().Id;
        var database = SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, preset));
        using (var db = new MemoryDb(database))
        {
            AgentSession.QueueSystemMessage(db, "plugin-agent-finished", "PluginAgent task status update. Worker A finished.");
            AgentSession.QueueSystemMessage(db, "plugin-agent-failed", "PluginAgent task status update. Worker B failed.");
        }
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        Configure(engine);
        var request = await NextModel(engine, scripts);
        var messages = JsonValues.ParseObject(request.GetString("body")).GetArray("messages");
        var reports = messages.Where(message => message.GetString("role") == "system" &&
            message.GetString("content").StartsWith("PluginAgent task status update.", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, reports);
        Assert.IsTrue(reports.Any(message => message.GetString("content").EndsWith("Worker A finished.", StringComparison.Ordinal)));
        Assert.IsTrue(reports.Any(message => message.GetString("content").EndsWith("Worker B failed.", StringComparison.Ordinal)));
        Reply(engine, request, "I received both reports.");
        await Until(engine, "completed", scripts);
        using var saved = MemoryDb.ReadOnly(database);
        Assert.IsFalse(AgentSession.HasPendingSystemMessages(saved));
    }

    [TestMethod]
    public async Task ScopedPluginReadsAndAdvancesTheActiveRoomCalendarThroughHarness()
    {
        var root = NewRoot();
        using var signer = Certificate();
        var archive = Pack(root, "calendar", signer, """
            {"id":"calendar","packageName":"test.calendar","name":"Calendar","apiVersion":2,"entry":"index.js",
             "permissions":["harness.read","harness.write"],"actions":{"handlers":{"advance":"advance"}},
             "hooks":{"plugin.command":"minirp://actions/test.calendar/hooks/advance"}}
            """, """
            globalThis.handlers = {
              async advance(_, api) {
                const { mode, date } = await api.harness.getWorldDate();
                if (mode !== "plugin" || date !== "2032-02-29") throw new Error("Unexpected date " + date);
                return api.harness.setWorldDate({ date: "2032-03-01" });
              }
            };
            """);
        var world = new Workspace(root);
        world.UsePreset(world.InitialPreset().Id);
        var config = (JsonObject)world.Config.DeepClone();
        config["date_mode"] = "plugin";
        config["sim_date"] = "2032-02-29";
        world.CommitConfig(config);

        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
        await Until(engine, "plugin", scripts);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = world.PresetId });
        var loaded = await Until(engine, "loaded", scripts);
        var room = loaded.GetObject("chat").GetString("id");
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "calendar" });
        var changed = await Until(engine, "world_changed", scripts);
        Assert.AreEqual("plugin", changed.GetObject("calendar").GetString("mode"));
        Assert.AreEqual("2032-03-01", changed.GetObject("calendar").GetString("date"));
        world.Select(world.PresetId, room);
        Assert.AreEqual("2032-03-01", world.WorldDate);
    }

    [TestMethod]
    public async Task HarnessSessionInfoIsBoundToTheInvocationAndUsesProviderUsage()
    {
        var root = NewRoot();
        using var signer = Certificate();
        var archive = Pack(root, "session-info", signer, """
            {"id":"session-info","packageName":"test.sessioninfo","name":"Session info","apiVersion":2,"entry":"index.js",
             "permissions":["harness.read","db"],"actions":{"handlers":{"read":"read","check":"check"}},
             "hooks":{"plugin.command":"minirp://actions/test.sessioninfo/hooks/check"},
             "skills":[{"name":"measure","description":"Read the session budget.","agents":["preset"],"tools":[
               {"name":"read","description":"Read the measured session budget.","parameters":{"type":"object"},
                "handler":"minirp://actions/test.sessioninfo/hooks/read"}]}]}
            """, """
            globalThis.handlers = {
                async read(_, api) {
                    let rejectsTarget = false;
                    try { await api.harness.getSessionInfo({agent: "chat"}); } catch { rejectsTarget = true; }
                    return {info: await api.harness.getSessionInfo(), rejectsTarget};
                },
                async check(_, api) {
                    const info = await api.harness.getSessionInfo();
                    await api.db.put({scope: "preset", key: "unbound", content: JSON.stringify(info)});
                }
            };
            """);
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        engine.Dispatch(new() { ["op"] = "configure", ["provider"] = new JsonObject
        {
            ["id"] = "configured-provider", ["url"] = "http://fixture.invalid/v1",
            ["model"] = "fixture", ["context_window"] = 64000, ["max_output"] = 2048
        } });
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
        await Until(engine, "plugin", scripts);
        var preset = engine.Workspace.PresetId;
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "session-info" });
        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["text"] = "Read the session budget." });
        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "session-info-measure" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_session-info_read", new(), promptTokens: 18800);
        var last = await NextModel(engine, scripts);
        var result = JsonValues.ParseObject(JsonValues.ParseObject(last.GetString("body")).GetArray("messages")
            .Last(message => message.GetString("role") == "tool").GetString("content"));
        Assert.IsTrue(result.GetBoolean("rejectsTarget"));
        var info = result.GetObject("info");
        Assert.HasCount(5, info);
        Assert.AreEqual("fixture", info.GetString("model"));
        Assert.AreEqual("configured-provider", info.GetString("provider"));
        Assert.AreEqual(64000, info.GetInt32("contextWindow"));
        var used = info.GetInt32("usedTokens");
        Assert.IsTrue(used >= 18800 && used < 19000);
        Assert.AreEqual(64000 - used, info.GetInt32("remainingTokens"));
        Reply(engine, last, "Budget read.");
        await Until(engine, "completed", scripts);
        var path = Path.Combine(root, "plugins", "data", "test.sessioninfo." + signer.GetCertHashString(HashAlgorithmName.SHA256),
            "preset", preset + ".sqlite");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>()) Process(engine, item, scripts, deadline.Token);
            if (File.Exists(path))
            {
                using var db = new MemoryDb(path);
                if (db.Get("unbound") is { } unbound)
                {
                    Assert.AreEqual("null", unbound);
                    break;
                }
            }
            await Task.Delay(8, deadline.Token);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChatOpenUrlReturnsMarkdownAfterFetchAndAfterSearchAgentEdits(bool longPage)
    {
        const string url = "https://fixture.invalid/docs/page";
        const string raw = """
            <!doctype html><html><head><title>Page title</title></head><body>
            <nav>Navigation junk</nav><main><h1>Article</h1><p><strong>Important</strong> <a href="next">Next</a>.</p>
            <script>script junk</script></main><footer>Footer junk</footer></body></html>
            """;
        await using var engine = new Engine(NewRoot());
        using var scripts = new PluginScripts();
        engine.Dispatch(new() { ["op"] = "configure", ["provider"] = new JsonObject
        {
            ["url"] = "http://fixture.invalid/v1", ["model"] = "fixture"
        }, ["settings"] = new JsonObject { ["search_key"] = "test", ["fetch_backend"] = "api" } });
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Open the source." });
        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "web-research" });
        ReplyTool(engine, await NextModel(engine, scripts), "open_url", new() { ["url"] = url });
        ReplyTool(engine, await NextModel(engine, scripts), "real_fetch", new() { ["url"] = url });
        var fetch = await NextModel(engine, scripts);
        StringAssert.EndsWith(fetch.GetString("url"), "/v1/tools/fetch");
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = fetch.GetString("id"), ["done"] = true,
            ["data"] = JsonValues.Serialize(new JsonObject { ["content"] = raw }) });

        static JsonObject ToolResult(JsonObject request) => JsonValues.ParseObject(JsonValues.ParseObject(request.GetString("body"))
            .GetArray("messages").Last(message => message.GetString("role") == "tool").GetString("content"));
        var search = await NextModel(engine, scripts);
        var preview = ToolResult(search);
        Assert.IsFalse(preview.ContainsKey("error"), JsonValues.Serialize(preview));
        var content = preview.GetString("content");
        var reference = preview.GetString("ref");
        var asset = preview.GetString("asset_url");
        Assert.IsTrue(ResearchAssets.IsUrl(asset));
        StringAssert.Contains(content, "# Article");
        StringAssert.Contains(content, "[Next](https://fixture.invalid/docs/next)");
        ReplyTool(engine, search, "activate_skill", new() { ["name"] = "edit_asset" });
        var updated = longPage ? raw.Replace("</main>", string.Concat(Enumerable.Repeat("<p>" + new string('A', 90) + "</p>", 95)) +
            "<p>Final paragraph marker</p></main>", StringComparison.Ordinal) : raw;
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_text_edit", new()
        {
            ["url"] = asset, ["old_string"] = content, ["new_string"] = updated
        });
        var submit = await NextModel(engine, scripts);
        Assert.AreEqual(1, ToolResult(submit).GetInt32("changes"), JsonValues.Serialize(ToolResult(submit)));
        ReplyTool(engine, submit, "submit_page", new() { ["ref"] = reference });
        var chat = await NextModel(engine, scripts);
        var result = ToolResult(chat);
        Assert.AreEqual("Page title", result.GetString("title"));
        Assert.AreEqual(url, result.GetString("url"));
        Assert.IsFalse(content.Contains("junk", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("<html>", StringComparison.Ordinal));
        if (longPage)
        {
            Assert.IsFalse(result.ContainsKey("content"));
            Assert.AreEqual(asset, result.GetString("source_asset_url"));
            Assert.IsTrue(AgentAssetStorage.IsUrl(result.GetString("asset_url")));
        }
        else
        {
            Assert.AreEqual(content, result.GetString("content"));
            Assert.AreEqual(asset, result.GetString("asset_url"));
        }
        ReplyTool(engine, chat, "activate_skill", new() { ["name"] = "view_asset" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_grep", new()
        {
            ["url"] = result.GetString("asset_url"), ["pattern"] = longPage ? "Final paragraph marker" : "Important"
        });
        var read = await NextModel(engine, scripts);
        Assert.AreNotEqual(0, ToolResult(read).GetArray("lines").Count);
        Reply(engine, read, "Article read.");
        await Until(engine, "completed", scripts);
    }

    [TestMethod]
    public async Task SkillActionPublishesLongUnicodeOutputAsAReadableAsset()
    {
        var root = NewRoot();
        using var signer = Certificate();
        var archive = Pack(root, "report", signer, """
            {"id":"report","packageName":"test.longreport","name":"Report","apiVersion":2,"entry":"index.js",
             "actions":{"handlers":{"write":"write"}},
             "skills":[{"name":"reports","description":"Generate reports.","agents":["chat"],"tools":[
                {"name":"write","description":"Generate a report.","parameters":{"type":"object"},
                 "handler":"minirp://actions/test.longreport/hooks/write"}]}]}
            """, """
            globalThis.handlers = { async write(_, api) {
              const boundary = "😀".repeat(8192);
              const inline = await api.actions.publishOutput({text: boundary});
              return { inlineCorrect: inline.output === boundary,
                ...await api.actions.publishOutput({text: boundary + "\nfinal report line", name: "report.txt"}) };
            } };
            """);
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
        await Until(engine, "plugin", scripts);
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Generate a report." });
        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "report-reports" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_report_write", new());
        var next = await NextModel(engine, scripts);
        static JsonObject Result(JsonObject request) => JsonValues.ParseObject(JsonValues.ParseObject(request.GetString("body"))
            .GetArray("messages").Last(message => message.GetString("role") == "tool").GetString("content"));
        var published = Result(next);
        Assert.IsTrue(published.GetBoolean("inlineCorrect"), published.ToJsonString());
        Assert.IsFalse(published.ContainsKey("output"));
        var url = published.GetString("asset_url");
        Assert.IsTrue(AgentAssetStorage.IsUrl(url));
        ReplyTool(engine, next, "activate_skill", new() { ["name"] = "view_asset" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_grep", new() { ["url"] = url, ["pattern"] = "final report line" });
        next = await NextModel(engine, scripts);
        Assert.AreEqual(1, Result(next).GetInt32("match_count"), Result(next).ToJsonString());
        Reply(engine, next, "Report read.");
        await Until(engine, "completed", scripts);
    }

    [TestMethod]
    public async Task PublishedAssetLinksKeepTheirRevisionAndAreScopedToTheReply()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Make a downloadable file." });
        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "asset_storage" });
        ReplyTool(engine, await NextModel(engine, scripts), "create_asset", new() { ["name"] = "report.txt", ["text"] = "original" });
        var next = await NextModel(engine, scripts);
        var created = JsonValues.ParseObject(JsonValues.ParseObject(next.GetString("body")).GetArray("messages")
            .Last(message => message.GetString("role") == "tool").GetString("content"));
        var url = created.GetString("url");
        Assert.IsTrue(AgentAssetStorage.IsUrl(url), created.ToJsonString());
        Reply(engine, next, "[Download report](" + url + ")");
        await Until(engine, "completed", scripts);

        var preset = engine.Workspace.PresetId;
        var room = engine.Workspace.ChatId;
        var block = engine.Workspace.Chat.GetArray("blocks").Single(item => item.GetString("content") == "[Download report](" + url + ")");
        var blockId = block.GetString("id");
        async Task CheckDownload()
        {
            engine.Dispatch(new() { ["op"] = "display_asset_open", ["preset"] = preset, ["room"] = room,
                ["block"] = blockId, ["url"] = url });
            var asset = (await Until(engine, "asset_result", scripts)).GetObject("result");
            Assert.AreEqual("report.txt", asset.GetString("name"));
            Assert.AreEqual("text/plain", asset.GetString("mimeType"));
            var handle = asset.GetString("handle");
            engine.Dispatch(new() { ["op"] = "asset_pull", ["handle"] = handle });
            var chunk = (await Until(engine, "asset_result", scripts)).GetObject("result");
            CollectionAssert.AreEqual("original"u8.ToArray(), engine.Port.TakeBinary(chunk.GetString("transfer")));
            engine.Dispatch(new() { ["op"] = "asset_close", ["handle"] = handle });
            await Until(engine, "asset_result", scripts);
        }
        await CheckDownload();

        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Update that file and remove it from the working set." });
        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "edit_asset" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_text_edit", new()
            { ["url"] = url, ["old_string"] = "original", ["new_string"] = "revised" });
        var edited = await NextModel(engine, scripts);
        StringAssert.Contains(edited.GetString("body"), "changes");
        ReplyTool(engine, edited, "delete_asset", new() { ["url"] = url });
        Reply(engine, await NextModel(engine, scripts), "Removed from my files.");
        await Until(engine, "completed", scripts);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = room });
        await Until(engine, "loaded", scripts);
        await CheckDownload();

        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset });
        await Until(engine, "loaded", scripts);
        engine.Dispatch(new() { ["op"] = "display_asset_open", ["preset"] = preset, ["room"] = engine.Workspace.ChatId,
            ["block"] = blockId, ["url"] = url });
        var denied = await Until(engine, "error", scripts);
        StringAssert.Contains(denied.GetString("message"), "unavailable in the displayed reply");
    }

    [TestMethod]
    [DataRow("deepseek")]
    [DataRow("mwmbl")]
    [DataRow("browser-cn")]
    [DataRow("browser-us")]
    public async Task BaseSearchCachesTheEditedResultAsset(string source)
    {
        const string url = "https://fixture.invalid/result";
        var configuredSearch = source is "deepseek" or "browser-us";
        var browserFallback = source.StartsWith("browser-", StringComparison.Ordinal);
        await using var engine = new Engine(NewRoot());
        using var scripts = new PluginScripts();
        engine.Dispatch(new() { ["op"] = "configure", ["provider"] = new JsonObject
        {
            ["url"] = "http://fixture.invalid/v1", ["model"] = "fixture"
        }, ["settings"] = new JsonObject { ["search_key"] = configuredSearch ? "test" : "",
            ["search_url"] = "http://web.fixture.invalid", ["search_region"] = source == "browser-cn" ? "CN" : "US" } });
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Find the source." });

        static JsonObject LastResult(JsonObject request) => JsonValues.ParseObject(JsonValues.ParseObject(request.GetString("body"))
            .GetArray("messages").Last(message => message.GetString("role") == "tool").GetString("content"));

        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "web-research" });
        ReplyTool(engine, await NextModel(engine, scripts), "search", new() { ["query"] = "fixture terms" });
        ReplyTool(engine, await NextModel(engine, scripts), "real_search", new() { ["query"] = "fixture terms", ["limit"] = 55 });
        var request = await NextModel(engine, scripts);
        if (configuredSearch)
        {
            Assert.AreEqual("http://web.fixture.invalid/v1/tools/search", request.GetString("url"));
            Assert.AreEqual(50, JsonValues.ParseObject(request.GetString("body")).GetInt32("count"));
            if (browserFallback)
            {
                engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = request.GetString("id"), ["status"] = 429, ["done"] = true });
                request = await NextModel(engine, scripts);
            }
        }
        if (source != "deepseek")
        {
            Assert.AreEqual("https://api.mwmbl.org/api/v2/search/?q=fixture%20terms", request.GetString("url"));
            Assert.AreEqual("GET", request.GetString("method"));
            Assert.IsFalse(request.GetObject("headers").ContainsKey("Authorization"));
        }
        JsonObject Result(string snippetField) => new() { ["results"] = new JsonArray(new JsonObject
            { ["url"] = url, ["title"] = "Fixture result", [snippetField] = "Original snippet" }) };

        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = request.GetString("id"), ["done"] = true,
            ["data"] = browserFallback ? """{"results":[]}""" : JsonValues.Serialize(Result(configuredSearch ? "snippet" : "content")) });
        var browserUrls = new List<string>();
        var closedJobs = new HashSet<string>();
        void ObserveBrowser(JsonObject item)
        {
            if (item.GetString("kind") != "browser_request") return;
            var tool = item.GetString("tool");
            if (tool == "browser_navigate") browserUrls.Add(item.GetObject("arguments").GetString("url"));
            if (tool == "browser_close") Assert.IsTrue(closedJobs.Add(item.GetString("job")));
            engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = item.GetString("id"),
                ["result"] = new JsonObject { ["snapshot"] = "Fixture search results" } });
        }
        string[] engines = source == "browser-cn"
            ? ["https://www.baidu.com/s?wd=", "https://cn.bing.com/search?q="]
            : ["https://www.google.com/search?q=", "https://duckduckgo.com/?q=", "https://www.bing.com/search?q="];
        for (var i = 0; browserFallback && i < engines.Length; i++)
        {
            var fetch = await NextModel(engine, scripts, ObserveBrowser);
            StringAssert.Contains(fetch.GetString("body"), engines[i] + "fixture%20terms");
            ReplyTool(engine, fetch, "browser_navigate", new() { ["url"] = engines[i] + "fixture%20terms" });
            var snapshot = await NextModel(engine, scripts, ObserveBrowser);
            ReplyTool(engine, snapshot, i == engines.Length - 1 ? "finish_search" : "fail_request",
                i == engines.Length - 1 ? Result("snippet") : new() { ["message"] = "Search page unavailable." });
        }
        var search = await NextModel(engine, scripts, ObserveBrowser);
        if (browserFallback)
        {
            CollectionAssert.AreEqual(engines.Select(engineUrl => engineUrl + "fixture%20terms").ToArray(), browserUrls);
            Assert.AreEqual(engines.Length, closedJobs.Count);
        }
        var item = LastResult(search).GetArray("results").Single();
        var asset = item.GetString("asset_url");
        Assert.IsTrue(ResearchAssets.IsUrl(asset));
        ReplyTool(engine, search, "activate_skill", new() { ["name"] = "edit_asset" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_text_edit", new()
        {
            ["url"] = asset, ["old_string"] = "Original snippet", ["new_string"] = "Edited snippet"
        });
        var cache = await NextModel(engine, scripts);
        Assert.AreEqual(1, LastResult(cache).GetInt32("changes"), JsonValues.Serialize(LastResult(cache)));
        ReplyTool(engine, cache, "cache_search_results", new() { ["refs"] = new JsonArray(item.GetString("ref")) });
        ReplyTool(engine, await NextModel(engine, scripts), "virtual_search", new() { ["query"] = "Edited snippet" });
        var submit = await NextModel(engine, scripts);
        var recalled = LastResult(submit).GetArray("results").Single();
        Assert.AreEqual(asset, recalled.GetString("asset_url"));
        ReplyTool(engine, submit, "submit_search_results", new() { ["refs"] = new JsonArray(recalled.GetString("ref")) });
        var chat = await NextModel(engine, scripts);
        Assert.AreEqual("Edited snippet", LastResult(chat).GetArray("results").Single().GetString("snippet"));
        Reply(engine, chat, "Found it.");
        await Until(engine, "completed", scripts);
        Assert.AreEqual(asset, JsonValues.GetText(engine.Workspace.State.GetObject("search_cache").GetArray("fixture terms").Single()));
    }

    [TestMethod]
    public async Task AgentCanReadPrivateAssetReturnedByASkillInItsSession()
    {
        var root = NewRoot();
        using var signer = Certificate();
        var archive = Pack(root, "report", signer, """
            {"id":"report","packageName":"example.report","name":"Report","apiVersion":2,"entry":"index.js",
             "actions":{"handlers":{"open":"open"}},
             "assets":[{"id":"notes","type":"file","path":"notes.txt","mimeType":"text/plain","accessibility":"private"}],
             "skills":[{"name":"notes","description":"Read notes.","agents":["chat"],"tools":[
                {"name":"notes_url","description":"Open the notes.","parameters":{"type":"object"},
                 "handler":"minirp://actions/example.report/hooks/open","returnType":"asset-url"}]}]}
            """, "globalThis.handlers = { open: (_, api) => [api.assets.url('notes')] };",
            source => File.WriteAllText(Path.Combine(source, "notes.txt"), "alpha beta"));
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
        await Until(engine, "plugin", scripts);
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Read notes." });

        const string url = "minirp://assets/example.report/notes";
        var query = new JsonObject { ["url"] = url, ["pattern"] = "b[a-z]+" };
        var round = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                if (item.GetString("kind") == "completed") return;
                if (item.GetString("kind") != "http_request")
                {
                    Process(engine, item, scripts, timeout.Token);
                    continue;
                }
                var messages = JsonValues.ParseObject(item.GetString("body")).GetArray("messages");
                var result = messages.LastOrDefault(message => message.GetString("role") == "tool").GetString("content");
                switch (round++)
                {
                    case 0: ReplyTool(engine, item, "activate_skill", new() { ["name"] = "view_asset" }); break;
                    case 1: ReplyTool(engine, item, "plugin_base_grep", query); break;
                    case 2:
                        StringAssert.Contains(result, "supplied in this session");
                        ReplyTool(engine, item, "activate_skill", new() { ["name"] = "report-notes" });
                        break;
                    case 3: ReplyTool(engine, item, "plugin_report_notes_url", new()); break;
                    case 4:
                        Assert.AreEqual(url, JsonValues.GetText(((JsonArray)JsonNode.Parse(result)!)[0]));
                        ReplyTool(engine, item, "plugin_base_grep", query);
                        break;
                    case 5:
                        var row = JsonValues.ParseObject(result).GetArray("lines").Single();
                        Assert.AreEqual("alpha beta", row.GetString("text"));
                        Assert.AreEqual(7, row.GetInt32("column"));
                        Reply(engine, item, "Notes read.");
                        break;
                    default: Assert.Fail("Unexpected model request."); break;
                }
            }
            await Task.Delay(8, timeout.Token);
        }
    }

    [TestMethod]
    public async Task AssetArgumentsGrantOnlyTheInvocationAndToolsCanReturnTheSameUrl()
    {
        var root = NewRoot();
        using var signer = Certificate();
        const string url = "minirp://assets/minirp.attachments/shared";
        var archive = Pack(root, "asset-probe", signer, """
            {"id":"asset-probe","packageName":"test.assetprobe","name":"Asset fixture","apiVersion":2,"entry":"index.js",
             "actions":{"handlers":{"echo":"echo"}},
             "skills":[{"name":"forward","description":"Forward a resource.","agents":["chat"],"tools":[
                {"name":"echo","description":"Echo the URL of a checked file.","parameters":{"type":"object"},
                 "handler":"minirp://actions/test.assetprobe/hooks/echo","returnType":"asset-url"}]}]}
            """, """
            let previous;
            globalThis.handlers = { async echo(event, api) {
                const url = event.arguments.input?.url ?? previous;
                const asset = await api.assets.read(url);
                const decoded = await api.text.decode(asset.bytes);
                const encoding = { encoding: "utf-16le" };
                const unicode = await api.text.decode(await api.text.encode(decoded + " 🟢", encoding), encoding);
                if (asset.mimeType !== "text/plain" || unicode !== "fixture 🟢")
                    throw new Error("Incorrect supplied file");
                previous = url;
                return url;
            } };
            """);
        Directory.CreateDirectory(Path.Combine(root, "attachments"));
        File.WriteAllText(Path.Combine(root, "attachments", "shared"), "fixture");
        File.WriteAllText(Path.Combine(root, "attachments", "outside"), "fixture");
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
        await Until(engine, "plugin", scripts);
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Forward the file.",
            ["attachments"] = new JsonArray(new JsonObject { ["id"] = "shared", ["name"] = "fixture.txt", ["mime"] = "text/plain" }) });

        async Task<JsonObject> Next() => await NextModel(engine, scripts);
        static string Result(JsonObject request) => JsonValues.ParseObject(request.GetString("body")).GetArray("messages")
            .Last(message => message.GetString("role") == "tool").GetString("content");
        var request = await Next();
        StringAssert.Contains(request.GetString("body"), url);
        ReplyTool(engine, request, "activate_skill", new() { ["name"] = "asset-probe-forward" });
        ReplyTool(engine, await Next(), "plugin_asset-probe_echo", new() { ["input"] = new JsonObject { ["url"] = url } });
        request = await Next();
        Assert.AreEqual(url, Result(request));
        ReplyTool(engine, request, "plugin_asset-probe_echo", new());
        request = await Next();
        StringAssert.Contains(Result(request), "not supplied to the handler");
        ReplyTool(engine, request, "plugin_asset-probe_echo",
            new() { ["input"] = new JsonObject { ["url"] = "minirp://assets/minirp.attachments/outside" } });
        request = await Next();
        StringAssert.Contains(Result(request), "not supplied to the handler");
        Reply(engine, request, "Asset URL round trip verified.");
        await Until(engine, "completed", scripts);
    }

    [TestMethod]
    public async Task PluginsExchangeAnAssetThroughTheSameActionUsedByAnAgentTool()
    {
        var root = NewRoot();
        using var signer = Certificate();
        const string action = "minirp://actions/test.routecheck/hooks/execute";
        var archive = Pack(root, "route-check", signer, """
            {"id":"route-check","packageName":"test.routecheck","name":"Route check","apiVersion":2,"entry":"index.js",
             "permissions":["db"],"actions":{"handlers":{"execute":"execute"}},
             "skills":[{"name":"routing","description":"Verify handler permissions.","agents":["chat"],"tools":[
               {"name":"execute","description":"Return the invocation kind.","parameters":{"type":"object"},"handler":"minirp://actions/test.routecheck/hooks/execute"}]}]}
            """, """
            globalThis.handlers = {
                async execute(event, api) {
                    if (event.name === "actions.invoke" && event.assets?.message) {
                        const data = await api.assets.read(event.assets.message);
                        const content = await api.text.decode(data.bytes);
                        await api.db.put({scope: "preset", key: "action-message", content: `${event.sender}/${content}`});
                    }
                    return event.name;
                }
            };
            """);
        var caller = Pack(root, "route-caller", signer, """
            {"id":"route-caller","packageName":"test.routecaller","name":"Route caller","apiVersion":2,"entry":"index.js",
             "actions":{"handlers":{"send":"send"}},"hooks":{"plugin.command":"minirp://actions/test.routecaller/hooks/send"},
             "assets":[{"id":"message","type":"file","path":"message.txt","mimeType":"text/plain","accessibility":"public"}]}
            """, """
            globalThis.handlers = { async send(_, api) {
                return await api.actions.invoke({url: "minirp://actions/test.routecheck/hooks/execute", assets: {message: api.assets.url("message")}});
            } };
            """, folder => File.WriteAllText(Path.Combine(folder, "message.txt"), "shared asset"));
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        Configure(engine);
        foreach (var package in new[] { archive, caller })
        {
            engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = package });
            await Until(engine, "plugin", scripts);
        }
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "route-caller" });
        var preset = engine.Workspace.PresetId;
        var dbPath = Path.Combine(root, "plugins", "data", "test.routecheck." + signer.GetCertHashString(HashAlgorithmName.SHA256),
            "preset", preset + ".sqlite");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (true)
            {
                foreach (var item in engine.Poll().OfType<JsonObject>())
                {
                    Assert.IsFalse(item.GetString("kind") is "tool_start" or "tool_end");
                    Process(engine, item, scripts, timeout.Token);
                }
                if (File.Exists(dbPath))
                {
                    using var stored = new MemoryDb(dbPath);
                    if (stored.Get("action-message") is { } received)
                    {
                        Assert.AreEqual("test.routecaller/shared asset", received);
                        break;
                    }
                }
                await Task.Delay(8, timeout.Token);
            }
        }

        await using (var draft = new PluginRuntime(Path.Combine(root, "debug-runtime"), engine.Port, _ => { },
            (_, _, _, _, _) => throw new InvalidOperationException()))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => draft.Debug(archive, Path.Combine(root, "preview"), preset,
                action, new() { ["name"] = "skill.tool" }, timeout.Token));
            var debugging = draft.Debug(archive, Path.Combine(root, "preview"), preset, action, new(), timeout.Token);
            while (!debugging.IsCompleted)
            {
                foreach (var item in engine.Poll().OfType<JsonObject>()) Process(engine, item, scripts, timeout.Token);
                await Task.Delay(8, timeout.Token);
            }
            Assert.AreEqual("actions.invoke", JsonValues.GetText(await debugging));
        }

        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Check the registered routes." });
        JsonNode? Result(JsonObject request) => JsonValues.ParseObject(request.GetString("body")).GetArray("messages")
            .Last(message => message.GetString("role") == "tool");
        ReplyTool(engine, await NextModel(engine, scripts), "activate_skill", new() { ["name"] = "route-check-routing" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_route-check_execute", new());
        var allowed = await NextModel(engine, scripts);
        Assert.AreEqual("skill.tool", Result(allowed).GetString("content"));
        Reply(engine, allowed, "Routing verified.");
        await Until(engine, "completed", scripts);
    }

    [TestMethod]
    public async Task PresetAppearanceOnlyAcceptsOwnedMediaAndPersistsTheStagedConfiguration()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        using var signer = Certificate();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "create_preset", ["name"] = "Backdrop", ["open"] = true, ["interview"] = false });
        var preset = (await Until(engine, "preset_opened")).GetString("preset");

        string PackBackground(string id, string owner) => Pack(root, id, signer, $$"""
            {"id":"{{id}}","packageName":"test.{{id}}","name":"Backdrop","apiVersion":2,"presetId":"{{owner}}","entry":"index.js",
             "assets":[{"id":"scene","type":"file","path":"scene.bin","mimeType":"image/png"},
                       {"id":"text","type":"file","path":"scene.bin","mimeType":"text/plain"}]}
            """, "globalThis.handlers = {};", folder => File.WriteAllText(Path.Combine(folder, "scene.bin"), "backdrop"));
        const string background = "minirp://assets/test.owned/scene";
        foreach (var archive in new[] { PackBackground("owned", preset), PackBackground("foreign", Guid.NewGuid().ToString()) })
        {
            engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
            await Until(engine, "plugin");
        }

        engine.Dispatch(new() { ["op"] = "message", ["agent"] = "preset", ["text"] = "Set my chat background." });
        var request = await Until(engine, "http_request");
        string Result(JsonObject response) => JsonValues.ParseObject(response.GetString("body")).GetArray("messages")
            .Last(message => message.GetString("role") == "tool").GetString("content");
        async Task<JsonObject> Call(string tool, JsonObject args)
        {
            ReplyTool(engine, request, tool, args);
            return request = await Until(engine, "http_request");
        }
        var listed = Result(await Call("list_chat_backgrounds", new()));
        StringAssert.Contains(listed, background);
        Assert.IsFalse(listed.Contains("test.foreign", StringComparison.Ordinal));
        Assert.IsFalse(listed.Contains("/text", StringComparison.Ordinal));
        StringAssert.Contains(Result(await Call("configure_chat_appearance", new() { ["background_url"] = "minirp://assets/test.foreign/scene" })),
            "owned by this preset");
        StringAssert.Contains(Result(await Call("configure_chat_appearance", new() { ["background_url"] = "minirp://assets/test.owned/text" })),
            "Choose a declared image");
        var staged = JsonValues.ParseObject(Result(await Call("configure_chat_appearance", new() { ["background_url"] = background })));
        Assert.IsFalse(staged.ContainsKey("background_blur"));
        var edited = JsonValues.ParseObject(Result(await Call("configure_chat_appearance", new() { ["background_blur"] = false, ["theme_color"] = "#2277bc" })));
        Assert.AreEqual(background, edited.GetString("background_url"));
        Assert.IsFalse(edited.GetBoolean("background_blur", true));
        Assert.AreEqual("#2277BC", edited.GetString("theme_color"));
        Reply(engine, request, "Backdrop saved.");
        await Until(engine, "preset_config");

        engine.Dispatch(new() { ["op"] = "home", ["preset"] = preset });
        var restored = (await Until(engine, "home")).GetObject("config").GetObject("appearance");
        Assert.AreEqual(background, restored.GetString("background_url"));
        engine.Dispatch(new() { ["op"] = "preset_background_open", ["preset"] = preset, ["url"] = background });
        var source = (await Until(engine, "asset_result")).GetObject("result");
        Assert.AreEqual("image/png", source.GetString("mimeType"));
        engine.Dispatch(new() { ["op"] = "asset_pull", ["handle"] = source.GetString("handle") });
        var part = (await Until(engine, "asset_result")).GetObject("result");
        CollectionAssert.AreEqual("backdrop"u8.ToArray(), engine.Port.TakeBinary(part.GetString("transfer")));
    }

    [TestMethod]
    public async Task SendingAsSystemFromHomeAcknowledgesTheInputAndRequestsAReply()
    {
        await using var engine = new Engine(NewRoot());
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "message", ["role"] = "system",
            ["text"] = "Answer in French.", ["request"] = "send-from-home" });
        JsonObject? receipt = null;
        JsonObject? request = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (request is null)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                var kind = item.GetString("kind");
                Assert.AreNotEqual("error", kind, item.GetString("message"));
                if (kind == "message_accepted") receipt = item;
                if (kind == "http_request") request = item;
            }
            if (request is null) await Task.Delay(8, timeout.Token);
        }
        Assert.IsNotNull(receipt);
        Assert.AreEqual("send-from-home", receipt.GetString("request"));
        Assert.AreEqual(engine.Workspace.ChatId, receipt.GetString("chat"));
        var messages = JsonValues.ParseObject(request.GetString("body")).GetArray("messages");
        Assert.AreEqual("system", messages[^2].GetString("role"));
        Assert.AreEqual("Answer in French.", messages[^2].GetString("content"));
        Assert.AreEqual("user", messages[^1].GetString("role"));
        Reply(engine, request, "Bonjour");
        Assert.AreEqual("Bonjour", (await Until(engine, "completed")).GetString("text"));
        Assert.AreEqual("system", engine.Workspace.Chat.GetArray("messages")[0].GetString("role"));
    }

    [TestMethod]
    public async Task LegacyMalformedTraceLoadsAndLeavesOriginalBackup()
    {
        var root = NewRoot();
        AtomicFile.Write(Path.Combine(root, "presets", "default.json"), """{"name":"Default"}""");
        var parent = Path.Combine(root, "conversations", "default");
        const string legacy = """
            {"name":"Old","chat":{"history":[null,{"role":"user","content":"An old message"}]},"trace":[false,null],"blocks":{}}
            """;
        AtomicFile.Write(Path.Combine(parent, "legacy.json"), legacy);
        await using var engine = new Engine(root);
        var preset = engine.Workspace.PresetId;
        var chat = engine.Workspace.Chats(preset).Single().GetString("id");
        AssertCanonicalUuid(preset);
        AssertCanonicalUuid(chat);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = preset, ["id"] = chat });
        var loaded = await Until(engine, "loaded");
        Assert.AreEqual(chat, loaded.GetObject("chat").GetString("id"));
        Assert.AreEqual("An old message", loaded.GetObject("chat").GetArray("blocks")[0].GetString("content"));
        Assert.AreEqual(legacy, File.ReadAllText(Path.Combine(engine.Workspace.ChatRoot, "migration", "python-v1.json")));
    }

    [TestMethod]
    public async Task DelegatedPromptHidesWorldAndSteerReachesTheNextRequestAsSystem()
    {
        var root = NewRoot();
        var workspace = new Workspace(root);
        workspace.UsePreset(workspace.InitialPreset().Id);
        var config = (JsonObject)workspace.Config.DeepClone();
        config["world"] = new JsonObject { ["summary"] = "HIDDEN_WORLD", ["rules"] = new JsonArray("HIDDEN_RULE") };
        config["sim_date"] = "2001-01-01";
        workspace.CommitConfig(config);
        await using var engine = new Engine(root);
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Start" });
        var first = await Until(engine, "http_request");
        var body = first.GetString("body");
        Assert.IsFalse(body.Contains("HIDDEN_WORLD") || body.Contains("HIDDEN_RULE") || body.Contains("current_state") || body.Contains("2001-01-01"));
        Assert.IsFalse(body.Contains("open_url"), "Web tools only become available after activation.");
        StringAssert.Contains(body, "web-research");
        engine.Dispatch(new() { ["op"] = "steer", ["chat"] = engine.Workspace.ChatId, ["agent"] = "chat", ["role"] = "system", ["text"] = "New instruction" });
        var second = await Until(engine, "http_request");
        var messages = JsonValues.ParseObject(second.GetString("body")).GetArray("messages");
        Assert.AreEqual("system", messages.Last().GetString("role"));
        Assert.AreEqual("New instruction", messages.Last().GetString("content"));
        Reply(engine, second, "Done");
        Assert.AreEqual("Done", (await Until(engine, "completed")).GetString("text"));
    }

    [TestMethod]
    [DataRow("user")]
    [DataRow("system")]
    public async Task SteerIsVisibleWhileAHostToolIsStillRunning(string role)
    {
        const string text = "Keep going with this direction.";
        await using var engine = new Engine(NewRoot());
        using var scripts = new PluginScripts();
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "_curl_available" });
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Start." });
        ReplyTool(engine, await Until(engine, "http_request"), "activate_skill", new() { ["name"] = "curl" });
        ReplyTool(engine, await Until(engine, "http_request"), "curl", new() { ["args"] = new JsonArray("--version") });
        var blocked = await Until(engine, "curl_download");

        engine.Dispatch(new() { ["op"] = "steer", ["chat"] = engine.Workspace.ChatId, ["role"] = role, ["text"] = text });
        var queued = (await Until(engine, "steer_queued")).GetObject("item");
        Assert.AreEqual(role, queued.GetString("kind"));
        Assert.AreEqual(text, queued.GetString("content"));
        Assert.IsFalse(engine.Workspace.Chat.GetArray("blocks").Any(block => block.GetString("content") == text));

        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = blocked.GetString("id"), ["error"] = "Fixture tool finished." });
        JsonObject? delivered = null;
        var continued = await NextModel(engine, scripts, item =>
        {
            if (item.GetString("kind") == role && item.GetString("text") == text) delivered = item;
        });
        Assert.IsNotNull(delivered);
        Assert.AreEqual(queued.GetString("id"), delivered.GetString("block"));
        var messages = JsonValues.ParseObject(continued.GetString("body")).GetArray("messages");
        var input = messages.Single(message => message.GetString("content") == text);
        Assert.AreEqual(role, input.GetString("role"));
        Assert.AreEqual(queued.GetString("id"), engine.Workspace.Chat.GetArray("blocks")
            .Single(block => block.GetString("content") == text).GetString("id"));
        Reply(engine, continued, "Done.");
        await Until(engine, "completed");
    }

    [TestMethod]
    public async Task V8RunsBaseRecommendationsAndPluginUrlWithScopedStorage()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        using var certificate = Certificate();
        Configure(engine);
        var plugin = Pack(root, "fixture", certificate, """
                {"id":"fixture","packageName":"test.fixture","name":"Fixture","apiVersion":2,"entry":"index.js","permissions":["db"],
                 "actions":{"handlers":{"store":"store","relay":"relay"}},
                 "hooks":{"message.system":"minirp://actions/test.fixture/hooks/relay","plugin.command":"minirp://actions/test.fixture/hooks/store"},
                 "settings":[{"key":"options","type":"page","title":"Options","items":[{"key":"flag","type":"boolean","title":"Flag","default":true}]}]}
                """, """
                let calls = 0;
                globalThis.handlers = {
                  async relay(_, api) { await api.actions.invoke({url: "minirp://actions/test.fixture/hooks/store"}); },
                  async store(event, api) {
                    const entry = await api.db.get({scope:"chat",key:"test"});
                    await api.db.put({scope:"chat",key:"test",expected_revision:entry.missing ? 0 : entry.revision,
                      content: (event.sender ?? "command") + ":" + api.environment.agent + ":" + ++calls});
                  }
                };
                """);
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = plugin });
        Assert.AreEqual("fixture", (await Until(engine, "plugin", scripts)).GetObject("item").GetString("id"));
        engine.Dispatch(new() { ["op"] = "system_message", ["text"] = "Trigger" });
        await Until(engine, "system", scripts);
        var database = ChatDb(root, "test.fixture", certificate, engine.Workspace.ChatId);
        using var db = new MemoryDb(database);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitForWrite(previous: null);
        Assert.AreEqual("test.fixture:chat:1", db.Get("test"));
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "fixture" });
        await WaitForWrite("test.fixture:chat:1");
        Assert.AreEqual("command:null:2", db.Get("test"));
        Assert.IsTrue(File.ReadAllBytes(plugin).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "plugins", "fixture.plugin"))));

        async Task WaitForWrite(string? previous)
        {
            while (db.Get("test") == previous)
            {
                foreach (var item in engine.Poll().OfType<JsonObject>()) Process(engine, item, scripts, deadline.Token);
                await Task.Delay(8, deadline.Token);
            }
        }
    }

    [TestMethod]
    public async Task PluginAttachmentUsesItsHandlerAndStoresThumbnailWithoutFeedingItToModel()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        using var certificate = Certificate();
        Configure(engine);
        var plugin = Pack(root, "attachment", certificate, """
                {"id":"attachment","packageName":"test.attachment","name":"Attachment fixture","apiVersion":2,"entry":"index.js",
                 "actions":{"handlers":{"convert":"convert","preview":"preview"}},
                 "assets":[{"id":"preview","type":"dynamic","mimeType":"image/png","accessibility":"private","handler":"minirp://actions/test.attachment/hooks/preview"}],
                 "attachments":[{"id":"text","name":"Converted PDF","mimeTypes":["application/pdf"],"handler":"minirp://actions/test.attachment/hooks/convert","thumbnail":"preview"}]}
                """, """
                globalThis.handlers = {
                  async convert(event, api) {
                    const source = await api.attachments.open({id:event.attachment.id});
                    const { value } = await source.body.getReader().read();
                    return {text:"Parsed " + String.fromCharCode(...value)};
                  },
                  preview() { return new Uint8Array([137,80,78,71,13,10,26,10]); }
                };
                """);
        var files = Path.Combine(root, "attachments");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "abc"), "ABC");
        engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = plugin });
        await Until(engine, "plugin", scripts);
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Read this", ["attachments"] = new JsonArray(new JsonObject
            { ["id"] = "abc", ["name"] = "file.pdf", ["mime"] = "application/pdf", ["plugin"] = "attachment", ["type"] = "text" }) });
        var request = await NextModel(engine, scripts);
        StringAssert.Contains(request.GetString("body"), "minirp://assets/minirp.attachments/abc");
        Assert.IsFalse(request.GetString("body").Contains("Parsed ABC", StringComparison.Ordinal));
        Assert.IsFalse(request.GetString("body").Contains("data:application/pdf;base64", StringComparison.Ordinal));
        Assert.IsFalse(request.GetString("body").Contains("thumbnail_url", StringComparison.Ordinal));
        ReplyTool(engine, request, "activate_skill", new() { ["name"] = "view_asset" });
        ReplyTool(engine, await NextModel(engine, scripts), "plugin_base_grep",
            new() { ["url"] = "minirp://assets/minirp.attachments/abc", ["pattern"] = "^Parsed" });
        var converted = await NextModel(engine, scripts);
        StringAssert.Contains(converted.GetString("body"), "Parsed ABC");
        var attachment = engine.Workspace.Chat.GetArray("blocks").First().GetArray("attachments").First();
        Assert.AreEqual("minirp://assets/test.attachment/preview?attachment=abc", attachment.GetString("thumbnail_url"));
        Assert.IsFalse(attachment!.AsObject().ContainsKey("thumbnail"));
        engine.Dispatch(new() { ["op"] = "asset_open", ["url"] = attachment.GetString("thumbnail_url"), ["attachment"] = attachment.DeepClone() });
        var stream = (await Until(engine, "asset_result", scripts)).GetObject("result").GetString("handle");
        engine.Dispatch(new() { ["op"] = "asset_pull", ["handle"] = stream });
        var chunk = (await Until(engine, "asset_result", scripts)).GetObject("result").GetString("transfer");
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, engine.Port.TakeBinary(chunk));
        engine.Dispatch(new() { ["op"] = "asset_close", ["handle"] = stream });
        Reply(engine, converted, "Converted text read.");
    }

    [TestMethod]
    public async Task SignedPluginsExchangeAssetUrlsAndStreamBytesWithSignerAccess()
    {
        var root = NewRoot();
        using var signer = Certificate();
        using var stranger = Certificate();
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        var source = Pack(root, "publisher", signer, """
            {"id":"publisher","packageName":"test.publisher","name":"Publisher","apiVersion":2,"entry":"index.js",
             "actions":{"handlers":{"announce":"announce","generate":"generate"},"events":["ready"]},
             "hooks":{"plugin.command":"minirp://actions/test.publisher/hooks/announce"},
             "assets":[
                {"id":"secret","type":"file","path":"large.bin","mimeType":"application/octet-stream","accessibility":"private"},
                {"id":"shared","type":"file","path":"large.bin","mimeType":"application/octet-stream","accessibility":"protected"},
                {"id":"generated","type":"dynamic","handler":"minirp://actions/test.publisher/hooks/generate","mimeType":"application/octet-stream","accessibility":"public"}]}
            """, """
            globalThis.handlers = {
              async announce(_, api) {
                await api.actions.publish({url:"minirp://actions/test.publisher/events/ready",
                  assets:{secret:api.assets.url("secret"),shared:api.assets.url("shared"),generated:api.assets.url("generated")}});
              },
              generate() {
                let count = 0;
                return new ReadableStream({pull(controller) {
                  if (count === 3) { controller.close(); return; }
                  const source = new Uint8Array(80002);
                  source.fill(++count);
                  controller.enqueue(source.subarray(1, 80001));
                }});
              }
            };
            """, path => File.WriteAllBytes(Path.Combine(path, "large.bin"), Enumerable.Repeat((byte)42, 700_007).ToArray()));
        await Install(source);
        foreach (var (id, certificate) in new[] { ("peer", signer), ("stranger", stranger) })
        {
            var receiver = Pack(root, id, certificate, $$$$"""
                {"id":"{{{{id}}}}","packageName":"test.{{{{id}}}}","name":"Receiver","apiVersion":2,"entry":"index.js","permissions":["db"],
                 "actions":{"handlers":{"consume":"consume"},"subscriptions":{"minirp://actions/test.publisher/events/ready":"minirp://actions/test.{{{{id}}}}/hooks/consume"}}}
                """, """
                globalThis.handlers = {
                  async consume(event, api) {
                    const results = [];
                    for (const url of Object.values(event.assets)) {
                      try {
                        const source = await api.assets.open(url);
                        const reader = source.body.getReader({mode:"byob"});
                        let length = 0, sum = 0;
                        for (;;) {
                          const {done,value} = await reader.read(new Uint8Array(27001));
                          if (done) break;
                          length += value.byteLength;
                          for (const byte of value) sum += byte;
                        }
                        results.push(length + ":" + sum);
                      } catch (error) {
                        results.push(error.message === "The asset is private to its package or signer."
                          ? "denied" : "error:" + error.message);
                      }
                    }
                    await api.db.put({scope:"chat",key:"result",content:results.join("|")});
                  }
                };
                """);
            await Install(receiver);
        }
        engine.Dispatch(new() { ["op"] = "system_message", ["text"] = "Create fixture context" });
        await Until(engine, "system", scripts);
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "publisher" });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var chat = engine.Workspace.ChatId;
        var peerPath = ChatDb(root, "test.peer", signer, chat);
        var strangerPath = ChatDb(root, "test.stranger", stranger, chat);
        Directory.CreateDirectory(Path.GetDirectoryName(peerPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(strangerPath)!);
        using var peer = new MemoryDb(peerPath);
        using var denied = new MemoryDb(strangerPath);
        while (peer.Get("result") is null || denied.Get("result") is null)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>()) Process(engine, item, scripts, deadline.Token);
            await Task.Delay(8, deadline.Token);
        }
        Assert.AreEqual("denied|700007:29400294|240000:480000", peer.Get("result"));
        Assert.AreEqual("denied|denied|240000:480000", denied.Get("result"));
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            archive.GetEntry("index.js")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("index.js").Open());
            writer.Write("globalThis.handlers = {};");
        }
        Assert.ThrowsExactly<CryptographicException>(() => PluginArchive.Verify(source));

        async Task Install(string path)
        {
            engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = path });
            await Until(engine, "plugin", scripts);
        }
    }

    [TestMethod]
    public async Task APluginWaitingOnHostIoDoesNotBlockAnotherPlugin()
    {
        var root = NewRoot();
        using var signer = Certificate();
        await using var engine = new Engine(root);
        using var scripts = new PluginScripts();
        var slow = Pack(root, "slow", signer, """
            {"id":"slow","packageName":"test.slow","name":"Slow","apiVersion":2,"entry":"index.js",
             "permissions":["http"],"network":["https://fixture.invalid"],"actions":{"handlers":{"run":"run"}},
             "hooks":{"plugin.command":"minirp://actions/test.slow/hooks/run"}}
            """, """
            globalThis.handlers = { async run(_, api) { await api.http.request({url:"https://fixture.invalid/wait"}); } };
            """);
        var fast = Pack(root, "fast", signer, """
            {"id":"fast","packageName":"test.fast","name":"Fast","apiVersion":2,"entry":"index.js",
             "permissions":["db"],"actions":{"handlers":{"run":"run"}},
             "hooks":{"plugin.command":"minirp://actions/test.fast/hooks/run"}}
            """, """
            globalThis.handlers = { async run(_, api) { await api.db.put({scope:"chat",key:"ready",content:"yes"}); } };
            """);
        foreach (var path in new[] { slow, fast })
        {
            engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = path });
            await Until(engine, "plugin", scripts);
        }
        engine.Dispatch(new() { ["op"] = "system_message", ["text"] = "Fixture context" });
        await Until(engine, "system", scripts);
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "slow" });
        var waiting = await Until(engine, "plugin_http", scripts);
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "fast" });
        var database = ChatDb(root, "test.fast", signer, engine.Workspace.ChatId);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var db = new MemoryDb(database);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (db.Get("ready") != "yes")
        {
            foreach (var item in engine.Poll().OfType<JsonObject>()) Process(engine, item, scripts, deadline.Token);
            await Task.Delay(8, deadline.Token);
        }
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = waiting.GetString("id"), ["result"] = new JsonObject { ["status"] = 200 } });
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "minirp-extensions-" + Guid.NewGuid().ToString("N"));

    private static string WriteEnsemblePreset(string root)
    {
        var preset = Guid.NewGuid().ToString("D");
        AtomicFile.Write(Path.Combine(root, "presets", preset + ".json"), $$$"""
            {"name":"Cast","chat_mode":"ensemble","agentic_search":true,
             "ensemble":{"agents":[{"id":"ada","name":"Ada"},{"id":"bryn","name":"Bryn"}],
             "loading_messages":["Watch the navigation lights.","Read the watch roster."],
             "chats":[{"id":"{{{Guid.NewGuid():D}}}","name":"Bridge","members":["ada","bryn"]},
                      {"id":"{{{Guid.NewGuid():D}}}","name":"Lab","members":["bryn"]}]}}
            """);
        return preset;
    }

    private static void AssertCanonicalUuid(string id)
    {
        Assert.IsTrue(Guid.TryParseExact(id, "D", out var parsed), $"Expected UUID, got '{id}'.");
        Assert.AreNotEqual(Guid.Empty, parsed);
        Assert.AreEqual(parsed.ToString("D"), id);
    }

    [TestMethod]
    public async Task WritableAssetEditsAreAtomicAndScopedToThePreset()
    {
        var root = NewRoot();
        using var signer = Certificate();
        var archive = Pack(root, "editable", signer, """
            {"id":"editable","packageName":"test.editable","name":"Editable","apiVersion":2,"entry":"index.js",
             "assets":[{"id":"notes","type":"file","path":"notes.txt","mimeType":"text/plain","writable":true},
                       {"id":"fixed","type":"file","path":"notes.txt","mimeType":"text/plain"}]}
            """, "globalThis.handlers = {};", folder => File.WriteAllText(Path.Combine(folder, "notes.txt"), "abc abc"));
        await using var runtime = new PluginRuntime(root, new HostPort(_ => { }), _ => { }, (_, _, _, _, _) => throw new InvalidOperationException());
        runtime.Load();
        var installed = runtime.Install(archive, inspect: false);
        const string url = "minirp://assets/test.editable/notes";
        var preset = new JsonObject { ["preset"] = WorkspaceIds.New() };
        var other = new JsonObject { ["preset"] = WorkspaceIds.New() };
        async Task<string> Read(JsonObject scope)
        {
            var asset = runtime.OpenAgentAsset(url, installed.PackageName, installed.Signer, scope);
            await using var input = new AssetStream(asset, default);
            using var reader = new StreamReader(input, leaveOpen: true);
            return await reader.ReadToEndAsync();
        }
        Task<JsonObject> Edit(string method, JsonObject args, string target = url, string publisher = "test.editable") =>
            runtime.EditAgentAsset(target, publisher, installed.Signer, preset, method, args, default);
        var textEdit = new JsonObject { ["old_string"] = "abc", ["new_string"] = "xyz" };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Edit("textEdit", textEdit));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Edit("textEdit", textEdit, "minirp://assets/test.editable/fixed"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Edit("textEdit", textEdit, publisher: "other.plugin"));
        Assert.AreEqual("abc abc", await Read(preset));
        textEdit["replace_all"] = true;
        Assert.AreEqual(2, (await Edit("textEdit", textEdit)).GetInt32("changes"));
        await Edit("binPatch", new() { ["offset"] = 0, ["old_hex"] = "78 79 | 7a", ["new_hex"] = "77" });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Edit("binPatch", new() { ["offset"] = 0, ["old_hex"] = "ff", ["new_hex"] = "00" }));
        Assert.AreEqual("w xyz", await Read(preset));
        Assert.AreEqual("abc abc", await Read(other));
        CollectionAssert.AreEqual(File.ReadAllBytes(archive), File.ReadAllBytes(runtime.ExportablePlugin(installed.Id)));
    }

    [TestMethod]
    public async Task DisabledPluginRetainsItsPackageAndScopeButRevokesRuntimeAccess()
    {
        var root = NewRoot();
        using var signer = Certificate();
        var archive = Pack(root, "switchable", signer, """
            {"id":"switchable","packageName":"test.switchable","name":"Switchable","apiVersion":2,"entry":"index.js",
             "actions":{"handlers":{"ping":"ping"}},
             "settings":[{"key":"preview","type":"boolean","title":"Preview","default":true}],
             "assets":[{"id":"fixture","type":"file","path":"fixture.txt","mimeType":"text/plain"}],
             "skills":[{"name":"check","description":"Check the test fixture.","agents":["chat"],"tools":[
                 {"name":"ping","description":"Ping the fixture.","parameters":{"type":"object"},
                  "handler":"minirp://actions/test.switchable/hooks/ping"}]}]}
            """, "globalThis.handlers = { ping() { return 'ok'; } };",
            folder => File.WriteAllText(Path.Combine(folder, "fixture.txt"), "fixture"));
        var preset = WorkspaceIds.New();
        var environment = new JsonObject { ["preset"] = preset };
        const string url = "minirp://assets/test.switchable/fixture";
        PluginRuntime OpenRuntime() => new(root, new HostPort(_ => { }), _ => { },
            (_, _, _, _, _) => Task.FromResult<JsonNode?>(null));

        await using (var runtime = OpenRuntime())
        {
            runtime.Load();
            var installed = runtime.Install(archive, inspect: false, initialScope: PluginScope.Include(preset));
            Assert.IsTrue(runtime.Settings(preset).Any(item => item.GetString("plugin") == installed.Id));
            var turn = new AgentSkill.Turn(runtime.Skills("chat", environment), ["switchable-check"]);
            Assert.IsTrue(turn.Offers("plugin_switchable_ping"));
            var handle = runtime.OpenHostAsset(url, environment, null).GetString("handle");

            runtime.SetEnabled(installed.Id, false, environment);
            CollectionAssert.AreEqual(File.ReadAllBytes(archive), File.ReadAllBytes(runtime.ExportablePlugin(installed.Id)));
            Assert.IsFalse(runtime.Settings(preset).Any(item => item.GetString("plugin") == installed.Id));
            Assert.IsFalse(runtime.Skills("chat", environment).Any(skill => skill.Name == "switchable-check"));
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => runtime.OpenHostAsset(url, environment, null));
            await Assert.ThrowsExactlyAsync<IOException>(() => runtime.PullHostAsset(handle, default));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => turn.Invoke("plugin_switchable_ping", new(), default));
            Assert.ThrowsExactly<InvalidOperationException>(() => runtime.SetEnabled("base", false, environment));
        }

        await using var reloaded = OpenRuntime();
        reloaded.Load();
        JsonNode? Descriptor() => reloaded.List().Single(item => item.GetString("id") == "switchable");
        Assert.IsFalse(Descriptor().GetBoolean("enabled"));
        reloaded.SetEnabled("switchable", true, environment);
        Assert.IsTrue(Descriptor().GetBoolean("enabled"));
        Assert.AreEqual(preset, JsonValues.GetText(Descriptor().GetObject("scope").GetArray("presets").Single()));
        reloaded.RequireScope("switchable", preset);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => reloaded.RequireScope("switchable", WorkspaceIds.New()));
        Assert.HasCount(1, reloaded.Skills("chat", environment).Where(skill => skill.Name == "switchable-check"));
    }

    [TestMethod]
    public async Task PluginPresetScopeFiltersSettingsAndHarnessAndPersistsAcrossRestarts()
    {
        var root = NewRoot();
        var workspace = new Workspace(root);
        var initialPreset = workspace.InitialPreset().Id;
        var outpost = workspace.CreatePreset("Outpost");
        workspace.Select(outpost, null);
        workspace.Save();
        var room = workspace.ChatId;
        using (var sessionDb = new MemoryDb(SessionFiles.Conversation(workspace.ChatRoot, "chat")))
            sessionDb.Write("_context/history", """
                [{"role":"user","content":"Visible message"},
                 {"role":"system","_harness_delta":true,"content":"Internal change"},
                 {"role":"user","content":"No result submitted. Complete with a submission tool or fail_request."}]
                """, internalWrite: true);
        using var signer = Certificate();
        using var scripts = new PluginScripts();
        var path = Pack(root, "scoped", signer, """
            {"id":"scoped","packageName":"test.scoped","name":"Scoped","apiVersion":2,"entry":"index.js",
             "permissions":["db","harness.read"], "actions":{"handlers":{"run":"run"}},
             "hooks":{"plugin.command":"minirp://actions/test.scoped/hooks/run"},
             "settings":[{"key":"preview","type":"boolean","title":"Preview","default":true}]}
            """, """
            globalThis.handlers = { async run(event, api) {
              const presets = await api.harness.listPresets({limit: 10});
              let denied = false;
              try { await api.harness.getPreset({preset: event.excludedPreset}); } catch { denied = true; }
              const badIds = [
                ["getPreset", {preset: "default"}],
                ["readRoom", {room: "old-room"}],
                ["readChat", {chat: "bridge"}],
                ["listRooms", {preset: "00000000-0000-0000-0000-000000000000"}]
              ];
              const malformed = [];
              for (const [method, args] of badIds) {
                try { await api.harness[method](args); }
                catch (error) { if (String(error.message).includes("UUID")) malformed.push(method); }
              }
              const session = await api.harness.readSession({room:event.room});
              let roomConflict = false, participantDenied = false;
              try { await api.harness.readSession({room:event.room, chat:event.otherRoom}); }
              catch (error) { roomConflict = String(error.message).includes("same."); }
              try { await api.harness.readSession({room:event.room, participant:"ada"}); }
              catch (error) { participantDenied = String(error.message).includes("no character sessions"); }
              await api.db.put({scope: "preset", key: "result", content: JSON.stringify({presets, denied, malformed, session, roomConflict, participantDenied})});
            } };
            """);

        await using (var engine = new Engine(root))
        {
            engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = path });
            await Until(engine, "plugin", scripts);
            engine.Dispatch(new() { ["op"] = "plugin_scope", ["id"] = "scoped",
                ["scope"] = new JsonObject { ["mode"] = "include", ["presets"] = new JsonArray(outpost) } });
            var plugins = await Until(engine, "plugins", scripts);
            Assert.IsFalse(plugins.GetArray("items").Where(item => item.GetBoolean("builtIn"))
                .Any(item => item.GetObject("scope").GetString("mode") != "global"));
            engine.Dispatch(new() { ["op"] = "home", ["preset"] = initialPreset });
            await Until(engine, "home", scripts);
            engine.Dispatch(new() { ["op"] = "plugin_settings" });
            Assert.IsFalse((await Until(engine, "plugin_settings", scripts)).GetArray("items")
                .Any(item => item.GetString("plugin") == "scoped"));
        }

        await using var reloaded = new Engine(root);
        reloaded.Dispatch(new() { ["op"] = "plugins_list" });
        var installed = (await Until(reloaded, "plugins", scripts)).GetArray("items")
            .Single(item => item.GetString("id") == "scoped");
        Assert.AreEqual("include", installed.GetObject("scope").GetString("mode"));
        reloaded.Dispatch(new() { ["op"] = "home", ["preset"] = outpost });
        await Until(reloaded, "home", scripts);
        reloaded.Dispatch(new() { ["op"] = "plugin_settings" });
        Assert.IsTrue((await Until(reloaded, "plugin_settings", scripts)).GetArray("items")
            .Any(item => item.GetString("plugin") == "scoped"));

        reloaded.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "scoped",
            ["arguments"] = new JsonObject { ["excludedPreset"] = initialPreset, ["room"] = room, ["otherRoom"] = WorkspaceIds.New() } });
        var database = Path.Combine(root, "plugins", "data", "test.scoped." + signer.GetCertHashString(HashAlgorithmName.SHA256),
            "preset", outpost + ".sqlite");
        using var db = new MemoryDb(database);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? text;
        while ((text = db.Get("result")) is null)
        {
            foreach (var item in reloaded.Poll().OfType<JsonObject>()) Process(reloaded, item, scripts, deadline.Token);
            await Task.Delay(8, deadline.Token);
        }
        var result = JsonValues.ParseObject(text);
        Assert.IsTrue(result.GetBoolean("denied"));
        Assert.IsTrue(result.GetBoolean("roomConflict"));
        Assert.IsTrue(result.GetBoolean("participantDenied"));
        Assert.AreEqual("Visible message", result.GetArray("session").Single().GetString("text"));
        Assert.AreEqual(outpost, result.GetArray("presets").Single().GetString("id"));
        CollectionAssert.AreEquivalent(new[] { "getPreset", "readRoom", "readChat", "listRooms" },
            result.GetArray("malformed").Select(item => JsonValues.GetText(item)).ToArray());
    }

    [TestMethod]
    public async Task OpenUiStreamsSeparatelyAndActionsContinueWithStructuredUserData()
    {
        await using var engine = new Engine(NewRoot());
        engine.Dispatch(new() { ["op"] = "configure",
            ["provider"] = new JsonObject { ["url"] = "http://fixture.invalid/v1", ["model"] = "fixture" },
            ["settings"] = new JsonObject { ["android_open_ui"] = "Use the OpenUI test catalog." } });
        engine.Dispatch(new() { ["op"] = "message", ["text"] = "Make a calculator" });
        var first = await Until(engine, "http_request");
        StringAssert.Contains(first.GetString("body"), "interactive-ui");
        var tool = new JsonObject { ["id"] = "activate", ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "activate_skill", ["arguments"] = """{"name":"interactive-ui"}""" } };
        ReplyDelta(first, new() { ["tool_calls"] = new JsonArray(tool) }, done: true);
        var second = await Until(engine, "http_request");
        StringAssert.Contains(second.GetString("body"), "OpenUI test catalog");

        ReplyDelta(second, new() { ["content"] = "Before\n````markdown\n```openui-lang\nliteral\n```\n````\n``" });
        ReplyDelta(second, new() { ["content"] = "`openui-lang\nroot = Col([title])\n" });
        var fragment = await Until(engine, "open_ui_delta");
        Assert.AreEqual("root = Col([title])\n", fragment.GetString("text"));
        ReplyDelta(second, new() { ["content"] = "title = Title(\"Estimate\")\n``" });
        ReplyDelta(second, new() { ["content"] = "`\nAfter" }, done: true);
        await Until(engine, "completed");

        var blocks = engine.Workspace.Chat.GetArray("blocks");
        var surface = blocks.Single(block => block.GetString("open_ui").Length > 0);
        Assert.AreEqual("root = Col([title])\ntitle = Title(\"Estimate\")\n", surface.GetString("open_ui"));
        StringAssert.Contains(blocks.First(block => block.GetString("content").StartsWith("Before")).GetString("content"),
            "```openui-lang\nliteral");
        Assert.IsTrue(blocks.Any(block => block.GetString("content") == "After"));
        engine.Dispatch(new() { ["op"] = "message", ["chat"] = engine.Workspace.ChatId,
            ["text"] = "Use my estimate",
            ["open_ui_action"] = new JsonObject { ["block"] = fragment.GetString("block"),
                ["data"] = new JsonObject { ["days"] = 3 } } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonObject? followUp = null;
        while (followUp is null)
        {
            var events = engine.Poll().OfType<JsonObject>().ToArray();
            foreach (var item in events)
            {
                if (item.GetString("kind") == "error") Assert.Fail(item.GetString("message"));
                if (item.GetString("kind") == "http_request" && item.GetString("body").Contains("Use my estimate"))
                    followUp = item;
            }
            if (followUp is null) await Task.Delay(8, deadline.Token);
        }
        var message = JsonValues.ParseObject(followUp.GetString("body")).GetArray("messages").Last();
        Assert.AreEqual("user", message.GetString("role"));
        StringAssert.Contains(message.GetString("content"), "\"days\":3");
        Assert.IsTrue(engine.Workspace.Chat.GetArray("blocks").Any(block => block.GetString("content") == "Use my estimate"));
        Reply(engine, followUp, "Confirmed");
        await Until(engine, "completed");

        void ReplyDelta(JsonObject request, JsonObject delta, bool done = false)
        {
            var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) };
            engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = request.GetString("id"),
                ["data"] = "data: " + JsonValues.Serialize(chunk) + "\n\n", ["done"] = done });
        }
    }

    [TestMethod]
    [DataRow("../outside")]
    [DataRow("res/../outside")]
    [DataRow("res\\outside")]
    [DataRow("res/%2e%2e/outside")]
    [DataRow("INDEX.JS")]
    public void RejectsUnsafeOrAliasedArchiveEntries(string addedPath)
    {
        using var certificate = Certificate();
        var plugin = Pack(NewRoot(), "paths", certificate, """
            {"id":"paths","packageName":"test.paths","name":"Paths","apiVersion":2,"entry":"index.js"}
            """, "globalThis.handlers = {};");
        using (var archive = ZipFile.Open(plugin, ZipArchiveMode.Update))
        using (var entry = archive.CreateEntry(addedPath).Open())
            entry.WriteByte(1);
        Assert.ThrowsExactly<InvalidDataException>(() => PluginArchive.Verify(plugin));
    }

    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test plugin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Pack(string root, string name, X509Certificate2 certificate, string manifest, string js, Action<string>? files = null)
    {
        var source = Path.Combine(root, name);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(source, "index.js"), js);
        files?.Invoke(source);
        var package = Path.Combine(root, name + ".plugin");
        PluginArchive.Pack(source, package, certificate);
        return package;
    }

    private static string ChatDb(string root, string package, X509Certificate2 certificate, string id) =>
        Path.Combine(root, "plugins", "data", package + "." + certificate.GetCertHashString(HashAlgorithmName.SHA256), "chat", id + ".sqlite");

    private static void Configure(Engine engine) => engine.Dispatch(new() { ["op"] = "configure",
        ["provider"] = new JsonObject { ["url"] = "http://fixture.invalid/v1", ["model"] = "fixture" } });

    private static bool Offers(JsonObject request, string tool) => JsonValues.ParseObject(request.GetString("body")).GetArray("tools")
        .Any(item => item.GetObject("function").GetString("name") == tool);

    private static void Reply(Engine engine, JsonObject request, string text)
    {
        var fixture = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) };
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = request.GetString("id"), ["done"] = true, ["data"] = "data: " + JsonValues.Serialize(fixture) + "\n\n" });
    }

    private static void ReplyTool(Engine engine, JsonObject request, string name, JsonObject args, int promptTokens = 0)
    {
        var call = new JsonObject { ["id"] = JsonValues.NewId(), ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonValues.Serialize(args) } };
        var frame = new JsonObject { ["choices"] = new JsonArray(new JsonObject
            { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call) } }) };
        if (promptTokens > 0) frame["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens };
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = request.GetString("id"), ["done"] = true,
            ["data"] = "data: " + JsonValues.Serialize(frame) + "\n\n" });
    }

    private static async Task<JsonObject> NextModel(Engine engine, PluginScripts scripts, Action<JsonObject>? observe = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            JsonObject? request = null;
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                observe?.Invoke(item);
                if (item.GetString("kind") == "http_request") request = item;
                else Process(engine, item, scripts, timeout.Token);
            }
            if (request is not null) return request;
            await Task.Delay(8, timeout.Token);
        }
    }

    private static async Task<JsonObject> Until(Engine engine, string kind, PluginScripts? scripts = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            JsonObject? found = null;
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                if (item.GetString("kind") == kind) found = item;
                if (item.GetString("kind") != kind || kind != "error") Process(engine, item, scripts, timeout.Token);
            }
            if (found is not null) return found;
            await Task.Delay(8, timeout.Token);
        }
    }

    private static void Process(Engine engine, JsonObject item, PluginScripts? scripts, CancellationToken token)
    {
        var name = item.GetString("kind");
        Assert.IsFalse(name is "error" or "plugin_error", item.GetString("message"));
        if (scripts is null) return;
        if (name == "plugin_script") engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = item.GetString("id"),
            ["result"] = scripts.Step(item, token, engine.Port.TakeBinary, engine.Port.WriteBinary) });
        if (name == "plugin_isolate_close") scripts.Close(item.GetString("isolate"));
        if (name == "http_request")
        {
            if (item.GetString("body").Contains("\"room_ready\"", StringComparison.Ordinal) && Offers(item, "room_ready"))
                ReplyTool(engine, item, "room_ready", new());
            else Reply(engine, item, """["One","Two","Three","Four","Five","Six"]""");
        }
    }
}
