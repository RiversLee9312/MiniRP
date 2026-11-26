using System.Text.Json.Nodes;
using MiniRP.Cli;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class MediaDescriptionTests
{
    [TestMethod]
    public void DesktopDescriptionModelsFollowTheActiveSettingsPage()
    {
        var root = Directory.CreateTempSubdirectory("minirp-description-settings-").FullName;
        try
        {
            var settings = new EnvironmentSettings(root);
            settings["DEEPSEEK_API_KEY"] = "chat-fixture";
            settings["MINIRP_IMAGE_DESCRIPTION_MODEL"] = "terminal-vision";
            settings["MINIRP_IMAGE_DESCRIPTION_PROTOCOL"] = "responses";
            settings["MINIRP_IMAGE_DESCRIPTION_URL"] = "https://vision.fixture.invalid";
            settings["MINIRP_IMAGE_DESCRIPTION_KEY"] = "";
            settings["MINIRP_AUDIO_DESCRIPTION_MODEL"] = "";
            settings.SaveDesktop(JsonValues.ParseObject("""
                {
                  "providers": [{"id":"fixture","url":"https://audio.fixture.invalid","protocol":"openai"}],
                  "agents": {"audio_description":{"provider":"fixture","model":"web-audio"}}
                }
                """));

            var terminal = settings.Configure().GetObject("agents");
            var vision = terminal.GetObject("image_description");
            Assert.AreEqual("terminal-vision", vision.GetString("model"));
            Assert.AreEqual("responses", vision.GetString("protocol"));
            Assert.AreEqual("https://vision.fixture.invalid", vision.GetString("url"));
            Assert.AreEqual("", vision.GetString("key"));
            Assert.IsFalse(terminal.ContainsKey("audio_description"));

            var web = settings.Configure(web: true).GetObject("agents");
            Assert.AreEqual("web-audio", web.GetObject("audio_description").GetString("model"));
            Assert.IsFalse(web.ContainsKey("image_description"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("text-only", "text", true, false)]
    [DataRow("configured", null, true, false)]
    [DataRow("native", "media", false, false)]
    [DataRow("disabled", "text", false, false)]
    [DataRow("failure", "text", true, true)]
    [DataRow("excerpt", "text", true, false)]
    public async Task UserAttachmentDescriptionsReachTheConversation(string mode, string? input, bool expectDescriptions, bool failAudio)
    {
        var root = Directory.CreateTempSubdirectory("minirp-descriptions-").FullName;
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "attachments")).FullName;
            var photo = WorkspaceIds.New();
            var recording = WorkspaceIds.New();
            var imageBytes = "image fixture"u8.ToArray();
            var audioBytes = "audio fixture"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(directory, photo), imageBytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, recording), audioBytes);
            var requests = new List<JsonObject>();
            HostPort? port = null;
            port = new(request =>
            {
                if (request.GetString("kind") == "agent_asset_sample")
                {
                    Assert.AreEqual("excerpt", mode);
                    Assert.AreEqual("audio_clip", request.GetString("mode"));
                    Assert.AreEqual(30000, request.GetInt32("duration_ms"));
                    File.WriteAllBytes(Path.Combine(directory, request.GetString("output")), audioBytes);
                    port!.Reply(new() { ["id"] = request.GetString("id"), ["result"] = new JsonObject { ["mime"] = "audio/wav" } });
                    return;
                }
                if (request.GetString("kind") != "http_request") return;
                var payload = JsonValues.ParseObject(request.GetString("body"));
                requests.Add(payload);
                var content = payload.GetString("model") switch
                {
                    "vision" => "A red bicycle.",
                    "audio" => "A bell rings.",
                    _ => "Ready"
                };
                var data = failAudio && payload.GetString("model") == "audio"
                    ? """{"error":{"message":"Test audio error"}}"""
                    : """{"choices":[{"delta":{"content":""" + JsonValues.Serialize(JsonValue.Create(content)) + "}}]}";
                port!.Reply(new() { ["id"] = request.GetString("id"), ["done"] = true, ["data"] = $"data: {data}\n\n" });
            });
            await using var runtime = new PluginRuntime(root, port, _ => { },
                (_, _, _, _, _) => throw new InvalidOperationException());
            runtime.Load();
            var target = new Provider("chat", "Chat", "openai", "https://fixture.invalid", "", "chat",
                InputModalities: input == "text" ? ["text"] : input == "media" ? ["text", "image", "audio"] : null,
                DescribeImages: mode == "configured", DescribeAudio: mode == "configured");
            var image = target with { Model = "vision", InputModalities = ["image"], DescribeImages = false };
            var audio = target with { Model = "audio", InputModalities = ["audio"], DescribeAudio = false };
            var factory = new AgentSession.Factory(port, runtime, directory, () => "",
                descriptionModels: () => mode == "disabled" ? (null, null) : (image, audio));
            using var session = factory.Open("temporary", ":memory:", () => target, () => new() { ["agent"] = "temporary" }, _ => { });
            var attachments = new JsonArray(
                new JsonObject { ["id"] = photo, ["name"] = "private.png", ["mime"] = "image/png" },
                new JsonObject { ["id"] = recording, ["name"] = "private-audio", ["mime"] = mode == "excerpt" ? "audio/ogg" : "audio/wav" });
            await session.RunTurn("Act as a chat assistant.", "Private conversation text.", [], default, attachments: attachments);
            await session.Run("Act as a chat assistant.", "Continue.", default);

            CollectionAssert.AreEqual(expectDescriptions ? new[] { "vision", "audio", "chat", "chat" } : new[] { "chat", "chat" },
                requests.Select(request => request.GetString("model")).ToArray());
            if (expectDescriptions)
            {
                var imagePart = Media(requests[0]);
                Assert.AreEqual("image_url", imagePart.GetString("type"));
                Assert.AreEqual("data:image/png;base64," + Convert.ToBase64String(imageBytes), imagePart.GetObject("image_url").GetString("url"));
                var audioPart = Media(requests[1]);
                Assert.AreEqual("input_audio", audioPart.GetString("type"));
                Assert.AreEqual(Convert.ToBase64String(audioBytes), audioPart.GetObject("input_audio").GetString("data"));
                foreach (var request in requests.Take(2))
                {
                    var body = JsonValues.Serialize(request);
                    Assert.IsFalse(body.Contains("Private conversation text.", StringComparison.Ordinal));
                    Assert.IsFalse(body.Contains("private", StringComparison.Ordinal));
                    Assert.IsEmpty(request.GetArray("tools"));
                }
            }
            foreach (var request in requests.TakeLast(2))
            {
                var content = User(request).GetString("content");
                StringAssert.Contains(content, MessageAttachments.AssetPrefix + photo);
                StringAssert.Contains(content, MessageAttachments.AssetPrefix + recording);
                Assert.AreEqual(expectDescriptions, content.Contains("A red bicycle.", StringComparison.Ordinal));
                Assert.AreEqual(expectDescriptions && !failAudio, content.Contains("A bell rings.", StringComparison.Ordinal));
                Assert.AreEqual(failAudio, content.Contains("\"status\":\"unavailable\"", StringComparison.Ordinal));
                Assert.AreEqual(mode == "excerpt", content.Contains("Opening audio excerpt (up to 30 seconds)", StringComparison.Ordinal));
                Assert.IsFalse(content.Contains("Test audio error", StringComparison.Ordinal));
            }
            var original = session.History.First(message => message.GetString("role") == "user");
            Assert.AreEqual("Private conversation text.", original.GetString("content"));
            Assert.HasCount(2, original.GetArray("attachments"));
            Assert.HasCount(2, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonNode? User(JsonObject request) =>
        request.GetArray("messages").First(message => message.GetString("role") == "user");

    private static JsonNode? Media(JsonObject request) => User(request).GetArray("content")[1];
}
