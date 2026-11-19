using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Minichat.Cli;
using Minichat.Core;

namespace Minichat.Core.Tests;

[TestClass]
public sealed class ExtensionTests
{
    [TestMethod]
    public async Task HomeStartsWithoutConversationAndDeletionRemovesChatAndPresetFromCatalog()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = "default" });
        await Until(engine, "home");
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "conversations")));

        engine.Dispatch(new() { ["op"] = "system_message", ["text"] = "System note" });
        await Until(engine, "system");
        var chat = engine.Workspace.ChatId;
        engine.Dispatch(new() { ["op"] = "delete_chat", ["preset"] = "default", ["id"] = chat });
        await Until(engine, "deleted");
        Assert.AreEqual("", engine.Workspace.ChatId);
        Assert.IsFalse(File.Exists(Path.Combine(root, "conversations", "default", chat + ".json")));
        engine.Dispatch(new() { ["op"] = "delete_preset", ["preset"] = "default", ["id"] = "default" });
        await Until(engine, "deleted");
        Assert.AreNotEqual("default", engine.Workspace.PresetId);
        engine.Dispatch(new() { ["op"] = "library_page" });
        Assert.AreEqual(1, (await Until(engine, "library_page")).GetArray("items").Count);
    }

    [TestMethod]
    public async Task NewPresetCanBeNamedAndReopenedWithoutCreatingAChat()
    {
        var root = NewRoot();
        await using var engine = new Engine(root);
        Configure(engine);
        engine.Dispatch(new() { ["op"] = "create_preset", ["name"] = "New preset", ["open"] = true });
        var created = await Until(engine, "preset_opened");
        var preset = created.GetString("preset");
        Assert.IsTrue(created.GetBoolean("created"));
        Assert.AreNotEqual("default", preset);
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
        engine.Dispatch(new() { ["op"] = "home", ["preset"] = "default" });
        await Until(engine, "home");
        engine.Dispatch(new() { ["op"] = "open_preset", ["preset"] = "default" });
        Assert.AreEqual(0, (await Until(engine, "preset_opened")).GetArray("blocks").Count);
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
        await using var engine = new Engine(root);
        var parent = Path.Combine(root, "conversations", "default");
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "legacy.json"), """
            {"name":"Old","chat":{"history":[null,{"role":"user","content":"An old message"}]},"trace":[false,null],"blocks":{}}
            """);
        engine.Dispatch(new() { ["op"] = "select_chat", ["preset"] = "default", ["id"] = "legacy" });
        var loaded = await Until(engine, "loaded");
        Assert.AreEqual("An old message", loaded.GetObject("chat").GetArray("blocks")[0].GetString("content"));
        Assert.IsTrue(File.Exists(Path.Combine(parent, "legacy", "migration", "python-v1.json")));
    }

    [TestMethod]
    public async Task DelegatedPromptHidesWorldAndSteerReachesTheNextRequestAsSystem()
    {
        var root = NewRoot();
        var workspace = new Workspace(root);
        workspace.UsePreset("default");
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
                 "hooks":{"message.system":"minichat://actions/test.fixture/hooks/relay","plugin.command":"minichat://actions/test.fixture/hooks/store"},
                 "settings":[{"key":"options","type":"page","title":"Options","items":[{"key":"flag","type":"boolean","title":"Flag","default":true}]}]}
                """, """
                let calls = 0;
                globalThis.handlers = {
                  async relay(_, api) { await api.actions.invoke({url: "minichat://actions/test.fixture/hooks/store"}); },
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
        engine.Dispatch(new() { ["op"] = "plugin_setting", ["id"] = "base", ["key"] = "topics", ["value"] = true });
        await Until(engine, "plugin_settings", scripts);
        engine.Dispatch(new() { ["op"] = "home_request", ["locale"] = "en", ["request"] = "home" });
        var result = await Until(engine, "home_suggestions", scripts);
        Assert.AreEqual("home", result.GetString("request"));
        Assert.AreEqual(5, result.GetArray("items").Count);
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
                 "assets":[{"id":"preview","type":"dynamic","mimeType":"image/png","accessibility":"private","handler":"minichat://actions/test.attachment/hooks/preview"}],
                 "attachments":[{"id":"text","name":"Plain text","mimeTypes":["text/plain"],"handler":"minichat://actions/test.attachment/hooks/convert","thumbnail":"preview"}]}
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
            { ["id"] = "abc", ["name"] = "file.txt", ["mime"] = "text/plain", ["plugin"] = "attachment", ["type"] = "text" }) });
        var request = await Until(engine, "http_request", scripts);
        StringAssert.Contains(request.GetString("body"), "Parsed ABC");
        Assert.IsFalse(request.GetString("body").Contains("thumbnail_url", StringComparison.Ordinal));
        var attachment = engine.Workspace.Chat.GetArray("blocks").First().GetArray("attachments").First();
        Assert.AreEqual("minichat://assets/test.attachment/preview?attachment=abc", attachment.GetString("thumbnail_url"));
        Assert.IsFalse(attachment!.AsObject().ContainsKey("thumbnail"));
        engine.Dispatch(new() { ["op"] = "asset_open", ["url"] = attachment.GetString("thumbnail_url"), ["attachment"] = attachment.DeepClone() });
        var stream = (await Until(engine, "asset_result", scripts)).GetObject("result").GetString("handle");
        engine.Dispatch(new() { ["op"] = "asset_pull", ["handle"] = stream });
        var chunk = (await Until(engine, "asset_result", scripts)).GetObject("result").GetString("transfer");
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, engine.Port.TakeBinary(chunk));
        engine.Dispatch(new() { ["op"] = "asset_close", ["handle"] = stream });
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
             "hooks":{"plugin.command":"minichat://actions/test.publisher/hooks/announce"},
             "assets":[
                {"id":"secret","type":"file","path":"large.bin","mimeType":"application/octet-stream","accessibility":"private"},
                {"id":"shared","type":"file","path":"large.bin","mimeType":"application/octet-stream","accessibility":"protected"},
                {"id":"generated","type":"dynamic","handler":"minichat://actions/test.publisher/hooks/generate","mimeType":"application/octet-stream","accessibility":"public"}]}
            """, """
            globalThis.handlers = {
              async announce(_, api) {
                await api.actions.publish({url:"minichat://actions/test.publisher/events/ready",
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
                 "actions":{"handlers":{"consume":"consume"},"subscriptions":{"minichat://actions/test.publisher/events/ready":"minichat://actions/test.{{{{id}}}}/hooks/consume"}}}
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
             "hooks":{"plugin.command":"minichat://actions/test.slow/hooks/run"}}
            """, """
            globalThis.handlers = { async run(_, api) { await api.http.request({url:"https://fixture.invalid/wait"}); } };
            """);
        var fast = Pack(root, "fast", signer, """
            {"id":"fast","packageName":"test.fast","name":"Fast","apiVersion":2,"entry":"index.js",
             "permissions":["db"],"actions":{"handlers":{"run":"run"}},
             "hooks":{"plugin.command":"minichat://actions/test.fast/hooks/run"}}
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

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "minichat-extensions-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public async Task PluginPresetScopeFiltersSettingsAndHarnessAndPersistsAcrossRestarts()
    {
        var root = NewRoot();
        var workspace = new Workspace(root);
        var outpost = workspace.CreatePreset("Outpost");
        using var signer = Certificate();
        using var scripts = new PluginScripts();
        var path = Pack(root, "scoped", signer, """
            {"id":"scoped","packageName":"test.scoped","name":"Scoped","apiVersion":2,"entry":"index.js",
             "permissions":["db","harness.read"], "actions":{"handlers":{"run":"run"}},
             "hooks":{"plugin.command":"minichat://actions/test.scoped/hooks/run"},
             "settings":[{"key":"preview","type":"boolean","title":"Preview","default":true}]}
            """, """
            globalThis.handlers = { async run(_, api) {
              const presets = await api.harness.listPresets({limit: 10});
              let denied = false;
              try { await api.harness.getPreset({preset: "default"}); } catch { denied = true; }
              await api.db.put({scope: "preset", key: "result", content: JSON.stringify({presets, denied})});
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
            engine.Dispatch(new() { ["op"] = "home", ["preset"] = "default" });
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

        reloaded.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "scoped" });
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
        Assert.AreEqual(outpost, result.GetArray("presets").Single().GetString("id"));
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

    private static void Reply(Engine engine, JsonObject request, string text)
    {
        var fixture = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) };
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = request.GetString("id"), ["done"] = true, ["data"] = "data: " + JsonValues.Serialize(fixture) + "\n\n" });
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
                Process(engine, item, scripts, timeout.Token);
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
        if (name == "plugin_script_close") scripts.Finish(item.GetString("isolate"), item.GetString("run"));
        if (name == "plugin_isolate_close") scripts.Close(item.GetString("isolate"));
        if (name == "http_request") Reply(engine, item, """["One","Two","Three","Four","Five","Six"]""");
    }
}
