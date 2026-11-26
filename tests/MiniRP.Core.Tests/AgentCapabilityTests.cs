using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class AgentCapabilityTests
{
    [TestMethod]
    public async Task LegacyPluginAssetsJoinOnlyTheirOwnPresetAndDeletedAssetsStayDeleted()
    {
        var root = Directory.CreateTempSubdirectory("minirp-preset-assets-").FullName;
        try
        {
            var port = new HostPort(_ => { });
            await using var runtime = new PluginRuntime(root, port, _ => { },
                (_, _, _, _, _) => throw new InvalidOperationException());
            runtime.Load();
            var preset = WorkspaceIds.New();
            var workers = SessionFiles.PluginDirectory(root, preset);
            var directory = Path.Combine(root, "attachments");
            using var legacy = new MemoryDb(Path.Combine(workers, WorkspaceIds.New(), "session.sqlite"));
            var old = new AgentAssets(runtime, legacy, directory, () => new() { ["preset"] = preset, ["agent"] = "plugin" });
            var environment = new JsonObject { ["preset"] = preset };
            var oldTools = new AgentSkill.Turn(runtime.Skills("plugin", environment, assets: old, manageAssets: true), ["asset_storage"]);
            var created = await oldTools.Invoke("create_asset", new() { ["name"] = "existing.txt", ["text"] = "Existing" },
                CancellationToken.None);
            var url = created.GetString("url");

            using var primary = new MemoryDb(SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, preset)));
            using var shared = MemoryDb.OpenScratch(primary.Path);
            var parent = new AgentAssets(runtime, primary, directory, () => new() { ["preset"] = preset, ["agent"] = "preset" });
            var worker = new AgentAssets(runtime, shared, directory, () => new() { ["preset"] = preset, ["agent"] = "plugin" });
            using var unrelated = new MemoryDb(":memory:");
            var other = new AgentAssets(runtime, unrelated, directory, () => new() { ["preset"] = WorkspaceIds.New() });

            AgentAssets.ImportPluginGrants(primary, workers);
            Assert.AreEqual(8L, parent.Open(url, new()).Length);
            Assert.AreEqual(8L, worker.Open(url, new()).Length);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => other.Open(url, new()));

            var currentTools = new AgentSkill.Turn(runtime.Skills("plugin", environment, assets: worker, manageAssets: true), ["assets"]);
            await currentTools.Invoke("delete_asset", new() { ["url"] = url }, CancellationToken.None);
            AgentAssets.ImportPluginGrants(primary, workers);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => parent.Open(url, new()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PresetAgentCanReadButCannotChangeSharedPluginDraftAssets()
    {
        var root = Directory.CreateTempSubdirectory("minirp-draft-assets-").FullName;
        try
        {
            var port = new HostPort(_ => { });
            await using var runtime = new PluginRuntime(root, port, _ => { },
                (_, _, _, _, _) => throw new InvalidOperationException());
            runtime.Load();
            var preset = WorkspaceIds.New();
            using var db = new MemoryDb(Path.Combine(root, "preset.sqlite"));
            using var connection = MemoryDb.OpenScratch(db.Path);
            var directory = Path.Combine(root, "attachments");
            var parent = new AgentAssets(runtime, db, directory, () => new() { ["preset"] = preset, ["agent"] = "preset" },
                protectDraftAssets: true);
            var worker = new AgentAssets(runtime, connection, directory, () => new() { ["preset"] = preset, ["agent"] = "plugin" });
            var parentTools = new AgentSkill.Turn(runtime.Skills("preset", new() { ["preset"] = preset },
                assets: parent, manageAssets: true), ["assets"]);
            var created = await parentTools.Invoke("create_asset", new() { ["name"] = "fixture.txt", ["text"] = "original" },
                CancellationToken.None);
            var url = created.GetString("url");
            Assert.IsTrue(parent.Open(url, new()).Writable);

            worker.BindProject(Path.Combine(root, "draft"), WorkspaceIds.New(), preset, "Fixture");
            var draftTools = new AgentSkill.Turn(runtime.Skills("plugin", new() { ["preset"] = preset },
                assets: worker, manageAssets: true), ["project_assets"]);
            Assert.AreEqual("plugin-dev", draftTools.OwnerOf("plugin_file_add"));
            Assert.IsTrue(draftTools.Offers("plugin_debug"));
            Assert.IsFalse(draftTools.Offers("project_asset_add"));
            Assert.IsNull(parentTools.OwnerOf("plugin_file_add"));
            await draftTools.Invoke("plugin_file_add", new() { ["path"] = "fixture.txt", ["url"] = url }, CancellationToken.None);

            Assert.AreEqual(8L, parent.Open(url, new()).Length);
            Assert.IsFalse(parent.Open(url, new()).Writable);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => parent.PrepareWrite(url));
            var edit = new JsonObject { ["old_string"] = "original", ["new_string"] = "edited" };
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                parent.Edit("textEdit", url, edit, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                parentTools.Invoke("rename_asset", new() { ["url"] = url, ["name"] = "changed.txt" }, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                parentTools.Invoke("delete_asset", new() { ["url"] = url }, CancellationToken.None));
            Assert.AreEqual(1, (await worker.Edit("textEdit", url, edit, CancellationToken.None)).GetInt32("changes"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task UnifiedAssetSkillKeepsLegacyActivationsAndTraceCardsAndRespectsSessionCapabilities()
    {
        var root = Directory.CreateTempSubdirectory("minirp-skill-").FullName;
        try
        {
            var port = new HostPort(_ => { });
            await using var runtime = new PluginRuntime(root, port, _ => { },
                (_, _, _, _, _) => throw new InvalidOperationException());
            runtime.Load();
            var preset = WorkspaceIds.New();
            var environment = new JsonObject { ["preset"] = preset };
            using var db = new MemoryDb(":memory:");
            var assets = new AgentAssets(runtime, db, Path.Combine(root, "attachments"), () => environment);
            var provider = new Provider("fixture", "Fixture", "openai", "https://fixture.invalid", "", "fixture");
            AgentSkill.Turn Turn(bool persistent, string? retained = null, string? role = null) => new(runtime.Skills(
                role ?? (persistent ? "chat" : "temporary"), environment, assets: assets, provider: provider, attach: _ => { },
                manageAssets: persistent, watches: new(db, assets)), retained is null ? null : [retained]);

            foreach (var legacy in new[] { "view_asset", "edit_asset", "asset_storage" })
            {
                var turn = Turn(true);
                var result = await turn.Invoke("activate_skill", new() { ["name"] = legacy }, default);
                Assert.AreEqual("assets", result.GetString("name"));
                Assert.HasCount(20, result.GetArray("tools"));
                Assert.IsFalse(turn.Offers("grep"));
                Assert.IsFalse(turn.Offers("watch_asset"));
                var restored = Turn(true, legacy);
                CollectionAssert.AreEqual(new[] { "assets" }, restored.ActiveNames.ToArray());
                Assert.IsTrue(restored.Offers("grep"));
                Assert.IsTrue(restored.Offers("text_edit"));
                Assert.IsTrue(restored.Offers("create_asset"));
            }

            var temporary = Turn(false, "assets");
            Assert.IsTrue(temporary.Offers("grep"));
            Assert.IsTrue(temporary.Offers("text_edit"));
            Assert.IsFalse(temporary.Offers("create_asset"));
            Assert.IsFalse(temporary.Offers("delete_asset"));
            foreach (var role in new[] { "chat", "env", "preset", "search", "fetch", "temporary", "plugin" })
            {
                var enabled = Turn(role != "temporary", "assets", role);
                Assert.AreEqual("assets", enabled.OwnerOf("watch_asset"));
                Assert.AreEqual("assets", enabled.OwnerOf("unwatch_asset"));
            }

            var current = Turn(true, "assets");
            Assert.AreEqual("native", current.TraceSource("create_asset").GetString("kind"));
            foreach (var (tool, legacy) in new[] { ("grep", "view_asset"), ("text_edit", "edit_asset") })
            {
                var origin = current.TraceSource(tool)!;
                Assert.AreEqual("plugin", origin.GetString("kind"));
                Assert.AreEqual("assets", origin.GetString("skill"));
                var reference = current.Presentation(tool, ToolPresentation.Arguments, "{}")!.Reference;
                origin["skill"] = legacy;
                reference["skill"] = legacy;
                Assert.AreEqual("资源", runtime.ReadToolTraceLabels(origin, preset, tool, "chat", "zh-Hans")?.SkillTitle);
                Assert.IsNotNull(runtime.ReadToolPresentation(reference, preset, tool, ToolPresentation.Arguments, "{}", "zh-Hans", "chat"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task TemporaryAgentActivatesBothMediaSkillsAndCanReadTheirOutput()
    {
        var root = Directory.CreateTempSubdirectory("minirp-temporary-media-").FullName;
        try
        {
            var calls = new List<string>();
            var outputs = new List<string>();
            var round = 0;
            HostPort? port = null;
            port = new(request =>
            {
                var id = request.GetString("id");
                if (request.GetString("kind") == "ffmpeg_run")
                {
                    calls.Add(request.GetString("tool"));
                    File.WriteAllText(Path.Combine(root, "ffmpeg-runs", request.GetString("run"), "stdout"), "fixture");
                    port!.Reply(new() { ["id"] = id, ["result"] = new JsonObject { ["exit_code"] = 0 } });
                    return;
                }
                if (request.GetString("kind") != "http_request") return;

                var body = JsonValues.ParseObject(request.GetString("body"));
                var names = body.GetArray("tools").Select(tool => tool.GetObject("function").GetString("name")).ToArray();
                var messages = body.GetArray("messages");
                string tool;
                JsonObject arguments;
                switch (round++)
                {
                    case 0:
                        CollectionAssert.DoesNotContain(names, "ffmpeg");
                        CollectionAssert.DoesNotContain(names, "ffprobe");
                        tool = "activate_skill";
                        arguments = new() { ["name"] = "ffprobe" };
                        break;
                    case 1:
                        CollectionAssert.Contains(names, "ffprobe");
                        CollectionAssert.DoesNotContain(names, "ffmpeg");
                        tool = "ffprobe";
                        arguments = new() { ["args"] = new JsonArray("-version") };
                        break;
                    case 2:
                        outputs.Add(JsonValues.ParseObject(messages.Last(item => item.GetString("role") == "tool")
                            .GetString("content")).GetString("asset_url"));
                        tool = "activate_skill";
                        arguments = new() { ["name"] = "ffmpeg" };
                        break;
                    case 3:
                        CollectionAssert.Contains(names, "ffmpeg");
                        tool = "ffmpeg";
                        arguments = new() { ["args"] = new JsonArray("-version") };
                        break;
                    case 4:
                        outputs.Add(JsonValues.ParseObject(messages.Last(item => item.GetString("role") == "tool")
                            .GetString("content")).GetString("asset_url"));
                        port!.Reply(new() { ["id"] = id, ["done"] = true,
                            ["data"] = "data: {\"choices\":[{\"delta\":{\"content\":\"Done\"}}]}\n\n" });
                        return;
                    default:
                        throw new InvalidOperationException("Unexpected fixture request.");
                }
                var call = new JsonObject { ["index"] = 0, ["id"] = $"media-{round}", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonValues.Serialize(arguments) } };
                var choice = new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call) } };
                port!.Reply(new() { ["id"] = id, ["done"] = true,
                    ["data"] = "data: " + JsonValues.Serialize(new JsonObject { ["choices"] = new JsonArray(choice) }) + "\n\n" });
            });
            await using var runtime = new PluginRuntime(root, port, _ => { },
                (_, _, _, _, _) => throw new InvalidOperationException());
            runtime.Load();
            var factory = new AgentSession.Factory(port, runtime, Path.Combine(root, "attachments"), () => "",
                mediaAvailable: () => true);
            using var session = factory.Open("temporary", ":memory:",
                () => new("fixture", "Fixture", "openai", "https://fixture.invalid", "", "fixture"),
                () => new() { ["agent"] = "temporary" }, _ => { });
            var result = await session.Run("Use the media tools.", "Verify both media skills.", AgentSkill.SubagentTask, default);
            Assert.AreEqual("Done", result.GetString("content"));
            CollectionAssert.AreEqual(new[] { "ffprobe", "ffmpeg" }, calls);
            Assert.HasCount(2, outputs);
            foreach (var url in outputs)
                Assert.AreEqual(7L, session.Assets!.Open(url, new()).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DirectMediaUsesTheSelectedModelAndKeepsASampleUrl()
    {
        var root = Directory.CreateTempSubdirectory("minirp-media-").FullName;
        try
        {
            var directory = Path.Combine(root, "attachments");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "clip"), [1, 2, 3, 4]);
            var port = new HostPort(_ => { });
            await using var runtime = new PluginRuntime(root, port, _ => { },
                (_, _, _, _, _) => throw new InvalidOperationException());
            using var db = new MemoryDb(Path.Combine(root, "session.sqlite"));
            var assets = new AgentAssets(runtime, db, directory, () => new());
            var file = new JsonObject { ["id"] = "clip", ["name"] = "clip.mp4", ["mime"] = "video/mp4" };
            var url = assets.StoreFile(file);
            var video = assets.Open(url, new());
            var textOnly = new Provider("text", "Text", "gemini", "https://example.invalid", "", "text", InputModalities: ["text"]);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => assets.Present(video, textOnly, url, port, CancellationToken.None));
            var provider = textOnly with { InputModalities = ["text", "video"] };
            var sample = await assets.Present(video, provider, url, port, CancellationToken.None);
            var model = new ModelClient(port, () => provider, directory);
            var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Video sample",
                ["_harness_asset"] = true, ["attachments"] = new JsonArray(sample.Attachment) });
            var body = JsonValues.ParseObject(model.Request(provider, messages, null).GetString("body"));
            var inline = body.GetArray("contents")[0].GetArray("parts")[1].GetObject("inlineData");
            Assert.AreEqual("video/mp4", inline.GetString("mimeType"));
            Assert.AreEqual("AQIDBA==", inline.GetString("data"));
            var saved = assets.Open(sample.Result.GetString("sample_url"), new());
            Assert.AreEqual(4L, saved.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task SkillToolsFollowHostCapabilitiesAndRecheckStatelessCardsAfterRevocation()
    {
        const string Skill = "caps-display";
        const string CardTool = "plugin_caps_display-card";
        const string AssetTool = "plugin_caps_provide";
        var root = Directory.CreateTempSubdirectory("minirp-agent-capability-").FullName;
        try
        {
            var port = new HostPort(_ => { });
            await using var runtime = new PluginRuntime(root, port, _ => { }, (_, _, _, _, _) =>
                throw new InvalidOperationException("This fixture does not call the harness."));
            runtime.Load();
            var preset = WorkspaceIds.New();
            runtime.Install(CreateArchive(root), inspect: false, initialScope: PluginScope.Include(preset));
            var environment = new JsonObject { ["preset"] = preset };
            using var db = new MemoryDb(":memory:");
            var assets = new AgentAssets(runtime, db, Path.Combine(root, "attachments"), () => environment);

            var searchSkill = runtime.Skills("search", environment, assets: assets).Single(skill => skill.Name == Skill);
            CollectionAssert.DoesNotContain(ToolNames(searchSkill), CardTool);
            CollectionAssert.Contains(ToolNames(searchSkill), AssetTool);
            var reading = new List<JsonObject>();
            var chatSkill = runtime.Skills("chat", environment, showCard: reading.Add).Single(skill => skill.Name == Skill);
            CollectionAssert.Contains(ToolNames(chatSkill), CardTool);
            CollectionAssert.DoesNotContain(ToolNames(chatSkill), AssetTool);

            var arguments = new JsonObject { ["id"] = "summary" };
            var active = new AgentSkill.Turn([chatSkill], [Skill]);
            var result = await active.Invoke(CardTool, arguments, CancellationToken.None);
            Assert.IsTrue(result.GetBoolean("shown"));
            Assert.AreEqual("Session summary", reading.Single().GetString("open_ui_title"));

            runtime.SetScope("caps", PluginScope.Include(WorkspaceIds.New()));
            Assert.IsFalse(runtime.Skills("chat", environment, showCard: reading.Add).Any(skill => skill.Name == Skill));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                active.Invoke(CardTool, arguments, CancellationToken.None));
            Assert.HasCount(1, reading);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] ToolNames(AgentSkill skill) =>
        new AgentSkill.Turn([skill], [skill.Name]).Definitions()
            .Select(tool => tool.GetObject("function").GetString("name")).ToArray();

    private static string CreateArchive(string root)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "manifest.json"), """
            {
              "id": "caps", "packageName": "test.agentcaps", "name": "Agent capability test",
              "apiVersion": 2, "entry": "index.js",
              "actions": {"handlers": {"provide": "provide"}},
              "assets": [{"id": "file", "type": "file", "mimeType": "text/plain", "accessibility": "public", "path": "file.txt"}],
              "cards": [{"id": "summary", "title": "Session summary", "program": "root = Text(\"Summary\")"}],
              "skills": [{
                "name": "display", "description": "Display an available result.", "agents": ["chat", "search"],
                "cards": ["summary"], "tools": [{
                  "name": "provide", "description": "Provide the result as an asset.",
                  "handler": "minirp://actions/test.agentcaps/hooks/provide",
                  "parameters": {"type": "object"}, "returnType": "asset-url"
                }]
              }]
            }
            """);
        File.WriteAllText(Path.Combine(source, "file.txt"), "Fixture");
        File.WriteAllText(Path.Combine(source, "index.js"),
            "globalThis.handlers = { provide() { return 'minirp://assets/test.agentcaps/file'; } };");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Agent capability fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var archive = Path.Combine(root, "caps.plugin");
        PluginArchive.Pack(source, archive, certificate);
        return archive;
    }
}
