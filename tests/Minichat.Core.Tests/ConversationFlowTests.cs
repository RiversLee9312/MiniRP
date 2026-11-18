using System.Text.Json.Nodes;
using Minichat.Core;

namespace Minichat.Core.Tests;

[TestClass]
public sealed class ConversationFlowTests
{
    [TestMethod]
    public async Task OpeningANewChatFirstImportsPriorPresetMemory()
    {
        var workspace = new Workspace(NewRoot());
        workspace.Select("default", null);
        var legacyPath = Path.Combine(workspace.ChatRoot, "agents", "preset.sqlite");
        using (var legacy = new MemoryDb(legacyPath))
        {
            legacy.Write("preset/preferences", "Use the shorter version.");
        }
        workspace.Save();

        await using var engine = new Engine(workspace.Root);
        await Run(engine, new() { ["op"] = "select_chat", ["preset"] = "default" });
        Assert.AreNotEqual(workspace.ChatId, engine.Workspace.ChatId);
        Assert.AreEqual("Use the shorter version.", await Read(engine, "preset", "preset/preferences"));
        Assert.IsTrue(File.Exists(legacyPath));
    }

    [TestMethod]
    public async Task BranchRestoresChatWorldAndSearchMemoryButSharesThePreset()
    {
        await using var engine = new Engine(NewRoot());
        Configure(engine);
        var original = (await Run(engine, new() { ["op"] = "select_chat", ["preset"] = "default" })).Single(IsLoaded).GetObject("chat").GetString("id");
        await Write(engine, "env", "fact", "before");
        await Write(engine, "search", "query", "before");
        var turn = await Run(engine, new() { ["op"] = "message", ["text"] = "Continue the story" }, "Reply");
        var checkpoint = turn.Single(item => item.GetString("kind") == "user");
        Assert.IsFalse(string.IsNullOrEmpty(checkpoint.GetString("version")));

        await Run(engine, new() { ["op"] = "message", ["agent"] = "env", ["text"] = "Update the world" }, "add_fact", "Added.");
        await Run(engine, new() { ["op"] = "message", ["agent"] = "preset", ["text"] = "Shared preset edit" }, "Preset updated.");
        await Write(engine, "env", "fact", "after", 1);
        await Write(engine, "search", "query", "after", 1);
        await Write(engine, "preset", "shared", "same preset");

        var branch = (await Run(engine, new() { ["op"] = "branch", ["chat"] = original, ["block"] = checkpoint.GetString("block") }, "Branch reply"))
            .Single(IsLoaded).GetObject("chat");
        Assert.AreNotEqual(original, branch.GetString("id"));
        Assert.IsTrue(branch.GetArray("blocks").Any(item => item.GetString("content") == "Shared preset edit"));
        Assert.IsFalse(branch.GetArray("blocks").Any(item => item.GetString("content") == "Update the world"));
        Assert.AreEqual(0, engine.Workspace.State.GetArray("canon").Count);
        Assert.AreEqual("before", await Read(engine, "env", "fact"));
        Assert.AreEqual("before", await Read(engine, "search", "query"));
        Assert.AreEqual("same preset", await Read(engine, "preset", "shared"));

        await Run(engine, new() { ["op"] = "select_chat", ["preset"] = "default", ["id"] = original });
        Assert.AreEqual("after", await Read(engine, "env", "fact"));
        Assert.AreEqual("after", await Read(engine, "search", "query"));
        Assert.AreEqual("new fact", engine.Workspace.State.GetArray("canon").Single()!.ToString());
        Assert.AreEqual("same preset", await Read(engine, "preset", "shared"));
    }

    [TestMethod]
    public async Task TitleUsesOnlyChatMessagesAndItsOwnModel()
    {
        await using var engine = new Engine(NewRoot());
        Configure(engine);
        await Run(engine, new() { ["op"] = "select_chat", ["preset"] = "default" });
        await Run(engine, new() { ["op"] = "message", ["agent"] = "env", ["text"] = "ENV_SECRET" }, "World reply");
        await Run(engine, new() { ["op"] = "message", ["agent"] = "preset", ["text"] = "PRESET_SECRET" }, "Preset reply");
        Assert.AreEqual("", engine.Workspace.Chat.GetString("name"));
        var requests = await Run(engine, new() { ["op"] = "message", ["text"] = "Chat subject" }, "Chat answer", "A good title");
        var titleRequest = requests.Where(item => item.GetString("kind") == "http_request")
            .Select(item => JsonValues.ParseObject(item.GetString("body"))).Single(body => body.GetString("model") == "title-fixture");
        var transcript = titleRequest.GetArray("messages").Last().GetString("content");
        StringAssert.Contains(transcript, "Chat subject");
        StringAssert.Contains(transcript, "Chat answer");
        Assert.IsFalse(transcript.Contains("ENV_SECRET", StringComparison.Ordinal));
        Assert.IsFalse(transcript.Contains("PRESET_SECRET", StringComparison.Ordinal));
        Assert.AreEqual("A good title", engine.Workspace.Chat.GetString("name"));
        await Run(engine, new() { ["op"] = "rename", ["name"] = "Manual title" });
        await Run(engine, new() { ["op"] = "message", ["text"] = "Follow-up" }, "Second answer");
        Assert.AreEqual("Manual title", engine.Workspace.Chat.GetString("name"));
    }

    [TestMethod]
    public async Task DisabledSearchAgentExposesItsToolsDirectlyToChat()
    {
        await using var engine = new Engine(NewRoot());
        Configure(engine);
        await Run(engine, new() { ["op"] = "select_chat", ["preset"] = "default" });
        await Run(engine, new() { ["op"] = "save_search_mode", ["enabled"] = false });
        var events = await Run(engine, new() { ["op"] = "message", ["text"] = "Find it" }, "virtual_search", "I checked the cache.", "Cache search");
        var requests = events.Where(item => item.GetString("kind") == "http_request").Select(item => JsonValues.ParseObject(item.GetString("body"))).ToArray();
        var tools = requests[0].GetArray("tools").Select(tool => tool.GetObject("function").GetString("name")).ToArray();
        CollectionAssert.Contains(tools, "real_fetch");
        CollectionAssert.Contains(tools, "virtual_search");
        CollectionAssert.DoesNotContain(tools, "search");
        Assert.IsTrue(requests.All(request => request.GetString("model") != "search-fixture"));
        Assert.IsTrue(events.Any(item => item.GetString("agent") == "ChatAgent" && item.GetString("name") == "virtual_search"));
    }

    [TestMethod]
    public async Task LibraryReturnsBoundedDisjointPagesAndNewestFirst()
    {
        var workspace = new Workspace(NewRoot());
        for (var index = 0; index < 71; index++)
        {
            workspace.Select("default", null);
            workspace.Chat["name"] = $"Conversation {index}";
            workspace.Save();
        }
        await using var engine = new Engine(workspace.Root);
        var first = await Page(engine, 0);
        var middle = await Page(engine, 30);
        var last = await Page(engine, 60);
        Assert.AreEqual(30, first.Count);
        Assert.AreEqual(30, middle.Count);
        Assert.AreEqual(11, last.Count);
        Assert.AreEqual("Conversation 70", first[0].GetString("name"));
        Assert.AreEqual(71, first.Concat(middle).Concat(last).Select(row => row.GetString("id")).Distinct().Count());
    }

    [TestMethod]
    public async Task GeminiReceivesVideoBytesAndHistoryStoresOnlyAttachmentReferences()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        Configure(engine, "gemini");
        await Run(engine, new() { ["op"] = "select_chat", ["preset"] = "default" });
        var attachments = Path.Combine(root, "attachments");
        Directory.CreateDirectory(attachments);
        File.WriteAllBytes(Path.Combine(attachments, "movie"), [1, 2, 3, 4]);
        var result = await Run(engine, new()
        {
            ["op"] = "message",
            ["text"] = "Describe this clip",
            ["attachments"] = new JsonArray(new JsonObject { ["id"] = "movie", ["name"] = "clip.mp4", ["mime"] = "video/mp4" })
        }, "Clip reply", "Video description");
        var request = JsonValues.ParseObject(result.First(item => item.GetString("kind") == "http_request").GetString("body"));
        var inline = request.GetArray("contents").Last().GetArray("parts").Last().GetObject("inlineData");
        Assert.AreEqual("video/mp4", inline.GetString("mimeType"));
        Assert.AreEqual("AQIDBA==", inline.GetString("data"));
        Assert.IsFalse(JsonValues.Serialize(engine.Workspace.Chat).Contains("AQIDBA==", StringComparison.Ordinal));
        Assert.AreEqual("movie", engine.Workspace.Chat.GetArray("messages")[0].GetArray("attachments")[0].GetString("id"));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "minichat-flow-" + Guid.NewGuid().ToString("N"));
    private static bool IsLoaded(JsonNode? item) => item.GetString("kind") == "loaded";
    private static void Configure(Engine engine, string protocol = "openai")
    {
        static JsonObject Model(string name, string protocol) => new() { ["model"] = name, ["protocol"] = protocol, ["url"] = "http://fixture.invalid/v1" };
        engine.Dispatch(new() { ["op"] = "configure", ["provider"] = Model("chat-fixture", protocol), ["agents"] = new JsonObject
        {
            ["title"] = Model("title-fixture", "openai"), ["env"] = Model("env-fixture", "openai"), ["search"] = Model("search-fixture", "openai")
        } });
    }

    private static async Task Write(Engine engine, string role, string key, string body, int revision = 0) =>
        await Memory(engine, role, "db_write", new() { ["key"] = key, ["content"] = body, ["expected_revision"] = revision });

    private static async Task<string> Read(Engine engine, string role, string key) =>
        (await Memory(engine, role, "db_read", new() { ["key"] = key })).GetString("content");

    private static async Task<JsonObject> Memory(Engine engine, string role, string tool, JsonObject arguments)
    {
        engine.Dispatch(new() { ["op"] = "memory", ["agent"] = role, ["tool"] = tool, ["arguments"] = arguments });
        return (await Wait(engine, "memory")).GetObject("result");
    }

    private static async Task<JsonArray> Page(Engine engine, int offset)
    {
        engine.Dispatch(new() { ["op"] = "library_page", ["preset"] = "default", ["offset"] = offset, ["limit"] = 30 });
        return (await Wait(engine, "library_page")).GetArray("items");
    }

    private static async Task<JsonObject> Wait(Engine engine, string kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                Assert.AreNotEqual("error", item.GetString("kind"), item.GetString("message"));
                if (item.GetString("kind") == kind)
                {
                    return item;
                }
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<List<JsonObject>> Run(Engine engine, JsonObject command, params string[] responses)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<JsonObject>();
        var busyFinished = false;
        var receivedTitle = false;
        var response = 0;
        engine.Dispatch(command);
        do
        {
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                events.Add(item);
                var kind = item.GetString("kind");
                Assert.AreNotEqual("error", kind, item.GetString("message"));
                if (kind == "http_request")
                {
                    var body = JsonValues.ParseObject(item.GetString("body"));
                    if (response >= responses.Length && body.GetString("model") == "title-fixture")
                    {
                        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = item.GetString("id"), ["error"] = "No title fixture" });
                        continue;
                    }
                    Assert.IsLessThan(responses.Length, response, "Unexpected model call.");
                    var text = responses[response++];
                    var delta = text is "add_fact" or "virtual_search"
                        ? new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "tool-call", ["type"] = "function", ["function"] = new JsonObject { ["name"] = text, ["arguments"] = text == "add_fact" ? "{\"fact\":\"new fact\"}" : "{}" } }) }
                        : new JsonObject { ["content"] = text };
                    var stream = body.ContainsKey("contents")
                        ? new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) } }) }
                        : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) };
                    engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = item.GetString("id"), ["done"] = true, ["data"] = "data: " + JsonValues.Serialize(stream) + "\n\n" });
                }
                busyFinished |= kind == "busy" && !item.GetBoolean("value");
                receivedTitle |= kind == "title";
            }
            if (busyFinished && response == responses.Length && (!events.Where(item => item.GetString("kind") == "http_request").Any(item => item.GetString("body").Contains("title-fixture", StringComparison.Ordinal)) || receivedTitle || responses.Length == 1))
            {
                return events;
            }
            await Task.Delay(10, timeout.Token);
        } while (true);
    }
}
