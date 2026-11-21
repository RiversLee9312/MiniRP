using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class AgentCapabilityTests
{
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
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => assets.Present(video, textOnly, url, CancellationToken.None));
            var provider = textOnly with { InputModalities = ["text", "video"] };
            var sample = await assets.Present(video, provider, url, CancellationToken.None);
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
