using System.Text;
using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class ProviderTests
{
    [TestMethod]
    [DataRow("openai", "https://api.deepseek.com", "max", "reasoning_effort", "max")]
    [DataRow("openai", "https://fixture.invalid/v1", "fixture-deliberate", "reasoning_effort", "fixture-deliberate")]
    [DataRow("openai", "https://openrouter.ai/api/v1", "low", "reasoning.effort", "low")]
    [DataRow("responses", "https://fixture.invalid/v1", "none", "reasoning.effort", "none")]
    [DataRow("anthropic", "https://fixture.invalid/v1", "medium", "output_config.effort", "medium")]
    [DataRow("gemini", "https://fixture.invalid/v1beta", "low", "generationConfig.thinkingConfig.thinkingLevel", "LOW")]
    [DataRow("ollama", "https://fixture.invalid", "high", "think", "high")]
    public void ModelEffortUsesTheProtocolField(string protocol, string url, string effort, string field, string expected)
    {
        var provider = new Provider("fixture", "Fixture", protocol, url, "", "fixture", ReasoningEffort: effort);
        var model = new ModelClient(new HostPort(_ => { }), () => provider);
        var body = JsonValues.ParseObject(model.Request(provider, new(), null).GetString("body"));
        Assert.AreEqual(expected, field.Split('.').Aggregate((JsonNode?)body, (node, key) => node?[key])?.GetValue<string>());
        if (url.Contains("deepseek")) Assert.AreEqual("enabled", body["thinking"].GetString("type"));

        var defaultBody = JsonValues.ParseObject(model.Request(provider with { ReasoningEffort = "" }, new(), null).GetString("body"));
        Assert.AreEqual(protocol == "ollama" ? "false" : null,
            field.Split('.').Aggregate((JsonNode?)defaultBody, (node, key) => node?[key])?.ToString());
    }

    [TestMethod]
    public async Task OllamaModelDetailsReturnOnlyAdvertisedEffortValues()
    {
        var model = Fixture("ollama", (request, _) =>
        {
            Assert.EndsWith("/api/show", request.GetString("url"));
            Assert.AreEqual("POST", request.GetString("method"));
            Assert.AreEqual("fixture", JsonValues.ParseObject(request.GetString("body")).GetString("model"));
            return """{"thinking":{"values":["low","high"],"default":"low"}}""";
        });
        var provider = new Provider("fixture", "Fixture", "ollama", "http://fixture.invalid", "", "fixture");
        var levels = await model.EffortLevels(provider, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "low", "high" }, levels.Select(level => level!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    [DataRow("openai", "gpt-6-astra", "/chat/completions")]
    [DataRow("responses", "responses", "/responses")]
    [DataRow("anthropic", "claude", "/messages")]
    public async Task CommandCodeSelectsModelsForTheConfiguredProtocol(string protocol, string id, string suffix)
    {
        const string url = "https://api.commandcode.ai/provider/v1";
        var provider = new Provider("fixture", "Fixture", protocol, url, "fixture-key", id);
        var model = Fixture(protocol, (request, _) =>
        {
            Assert.AreEqual(url + "/models", request.GetString("url"));
            Assert.AreEqual("Bearer fixture-key", request.GetObject("headers").GetString("Authorization"));
            return """
                {"data":[{"id":"gpt-6-astra","supported_endpoints":["/chat/completions"]},
                {"id":"responses","supported_endpoints":["/v1/responses"]},
                {"id":"claude","supported_endpoints":["/messages"]},
                {"id":"decision","supported_endpoints":["/systemone"]}]}
                """;
        });
        Assert.AreEqual(id, (await model.Models(provider, default)).Single().GetString("id"));
        var request = model.Request(provider, new(), null);
        Assert.AreEqual(url + suffix, request.GetString("url"));
        Assert.AreEqual("Bearer fixture-key", request.GetObject("headers").GetString("Authorization"));
        Assert.IsFalse(request.GetObject("headers").ContainsKey("x-api-key"));
        if (protocol == "openai") Assert.AreEqual(provider.MaxOutput, JsonValues.ParseObject(request.GetString("body")).GetInt32("max_completion_tokens"));
    }

    [TestMethod]
    public async Task OpenCodeGoUsesThePublicCatalogAndPersistsItsSessionAcrossTurnsAndReopen()
    {
        const string url = "https://opencode.ai/inference/go/openai/v1";
        var provider = new Provider("fixture", "Fixture", "openai", url, "fixture-key", "go-test");
        var requests = new List<string>();
        var fixture = Fixture("openai", (request, _) =>
        {
            if (request.GetString("method") == "GET")
            {
                Assert.AreEqual("https://opencode.ai/inference/go/v1/models", request.GetString("url"));
                return """
                    {"data":[
                      {"id":"go-test","limit":{"context":262144,"output":32768},"modalities":{"input":["text","image/png"]},"api":{"chat-completions":{}}},
                      {"id":"responses-test","api":{"responses":{}}},
                      {"id":"messages-test","api":{"messages":{}}}
                    ]}
                    """;
            }
            Assert.AreEqual(url + "/chat/completions", request.GetString("url"));
            requests.Add(request.GetObject("headers").GetString("x-opencode-session"));
            return "data: {\"choices\":[{\"delta\":{\"content\":\"Done.\"}}]}\n\ndata: [DONE]\n\n";
        });
        var choice = (await fixture.Models(provider, default)).Single();
        Assert.AreEqual("go-test", choice.GetString("id"));
        Assert.AreEqual(262144, choice.GetInt32("context_length"));
        Assert.AreEqual(32768, choice.GetInt32("max_output_tokens"));
        Assert.AreEqual("image/png", choice.GetArray("input_modalities")[1]?.GetValue<string>());
        Assert.AreEqual("responses-test", (await fixture.Models(provider with { Protocol = "responses" }, default)).Single().GetString("id"));
        Assert.AreEqual("messages-test", (await fixture.Models(provider with { Protocol = "anthropic", Url = "https://opencode.ai/inference/go/anthropic/v1" }, default)).Single().GetString("id"));

        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var database = Path.Combine(directory.FullName, "session.sqlite");
            var branch = Path.Combine(directory.FullName, "branch.sqlite");
            var model = fixture.Bind(provider);
            using (var session = TestAgentSession.Open("title", database, model, _ => { }))
            {
                await session.Run("Fixture", "One", default);
                await session.Run("Fixture", "Two", default);
                session.Db.SnapshotSession(branch);
            }
            using (var session = TestAgentSession.Open("title", database, model, _ => { }))
                await session.Run("Fixture", "Three", default);
            using (var session = TestAgentSession.Open("title", branch, model, _ => { }))
                await session.Run("Fixture", "Branch", default);
        }
        finally { directory.Delete(recursive: true); }
        Assert.HasCount(4, requests);
        Assert.StartsWith("ses_", requests[0]);
        Assert.IsTrue(requests.Take(3).All(id => id == requests[0]));
        Assert.AreNotEqual(requests[0], requests[3]);
    }

    [TestMethod]
    [DataRow("openai")]
    [DataRow("responses")]
    [DataRow("anthropic")]
    [DataRow("gemini")]
    [DataRow("ollama")]
    public void MediaSamplesHonorCatalogMimeTypesAndUseEachProtocolsImageFormat(string protocol)
    {
        var provider = Provider.From(new() { ["protocol"] = protocol, ["url"] = "https://fixture.invalid/v1", ["model"] = "fixture",
            ["modalities"] = new JsonObject { ["input"] = new JsonArray("text", "image/PNG") } });
        Assert.IsTrue(MessageAttachments.Supports(provider, "image/png"));
        Assert.IsFalse(MessageAttachments.Supports(provider, "image/jpeg"));
        Assert.IsFalse(MessageAttachments.Supports(provider with { InputModalities = ["text"] }, "image/png"));
        Assert.IsTrue(MessageAttachments.Supports(provider with { InputModalities = ["image"] }, "image/png"));
        if (protocol != "ollama")
            Assert.IsTrue(MessageAttachments.Supports(provider with { InputModalities = ["file"] }, "application/pdf"));

        var directory = Directory.CreateTempSubdirectory();
        try
        {
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1ioAAAAASUVORK5CYII=";
            File.WriteAllBytes(Path.Combine(directory.FullName, "sample"), Convert.FromBase64String(png));
            var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Inspect the sample.",
                ["_harness_asset"] = true, ["attachments"] = new JsonArray(new JsonObject { ["id"] = "sample", ["mime"] = "image/png", ["name"] = "sample.png" }) });
            var model = new ModelClient(new HostPort(_ => { }), () => provider, directory.FullName);
            var body = JsonValues.ParseObject(model.Request(provider, messages, null).GetString("body"));
            var content = protocol switch
            {
                "responses" => body.GetArray("input")[0].GetArray("content")[1],
                "gemini" => body.GetArray("contents")[0].GetArray("parts")[1],
                "ollama" => body.GetArray("messages")[0].GetArray("images")[0],
                _ => body.GetArray("messages")[0].GetArray("content")[1]
            };
            var actual = protocol switch
            {
                "openai" => content.GetObject("image_url").GetString("url"),
                "responses" => content.GetString("image_url"),
                "anthropic" => content.GetObject("source").GetString("data"),
                "gemini" => content.GetObject("inlineData").GetString("data"),
                _ => content?.GetValue<string>()
            };
            Assert.AreEqual(protocol is "openai" or "responses" ? "data:image/png;base64," + png : png, actual);
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public void KimiCodeReplaysTheReasoningReturnedWithToolCalls()
    {
        var provider = new Provider("fixture", "Kimi Code", "openai", "https://api.kimi.com/coding/v1", "fixture-key", "k3.1-pro");
        var model = new ModelClient(new HostPort(_ => { }), () => provider);
        var messages = JsonNode.Parse("""
            [{"role":"assistant","reasoning_content":"Need the fixture.","tool_calls":[{"id":"call","function":{"name":"get","arguments":"{}"}}]},
             {"role":"tool","tool_call_id":"call","content":"fixture result"}]
            """)!.AsArray();
        var request = model.Request(provider, messages, null);
        var body = JsonValues.ParseObject(request.GetString("body"));
        Assert.AreEqual("Need the fixture.", body.GetArray("messages")[0].GetString("reasoning_content"));
        Assert.AreEqual("Bearer fixture-key", request.GetObject("headers").GetString("Authorization"));
        Assert.IsFalse(body.ContainsKey("thinking"));
    }

    [TestMethod]
    public async Task ArkPlanOffersItsDocumentedAliasOnMissingCatalogButSurfacesAuthErrors()
    {
        const string url = "https://ark.cn-beijing.volces.com/api/plan/v3";
        var provider = new Provider("fixture", "Fixture", "openai", url, "fixture-key", "");
        var status = 404;
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") != "http_request") return;
            Assert.AreEqual(url + "/models", request.GetString("url"));
            port!.Reply(new() { ["id"] = request.GetString("id"), ["status"] = status, ["done"] = true });
        });
        var model = new ModelClient(port, () => provider);
        Assert.AreEqual("ark-code-latest", (await model.Models(provider, default)).Single().GetString("id"));
        status = 401;
        var error = await Assert.ThrowsExactlyAsync<HostPort.HttpError>(() => model.Models(provider, default));
        Assert.AreEqual(401, error.Status);
    }

    [TestMethod]
    public async Task Utf8SurvivesEveryPossibleByteBoundary()
    {
        var expected = "中文🙂é مرحبا";
        var model = Fixture("openai", (_, _) => Event(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = expected } })
        }), splitBytes: true);
        using var session = TestAgentSession.Open("title", ":memory:", model, _ => { });
        var result = await session.Run("Provider fixture", "test", CancellationToken.None);
        Assert.AreEqual(expected, result.GetString("content"));
    }

    [TestMethod]
    public async Task AnthropicThinkingSignatureIsReplayedBeforeToolResults()
    {
        var model = Fixture("anthropic", (request, round) =>
        {
            if (round == 0)
            {
                return Event(JsonValues.ParseObject("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""")) +
                    Event(JsonValues.ParseObject("""{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"A thought"}}""")) +
                    Event(JsonValues.ParseObject("""{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"opaque-signature"}}"""));
            }
            var body = JsonValues.ParseObject(request.GetString("body"));
            Assert.AreEqual("opaque-signature", body.GetArray("messages")[1].GetArray("content")[0].GetString("signature"));
            return Event(JsonValues.ParseObject("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"done"}}"""));
        });
        using var session = TestAgentSession.Open("chat", ":memory:", model, _ => { });
        await session.Run("Provider fixture", "test", CancellationToken.None);
        var next = await session.Run("Provider fixture", "continue", CancellationToken.None);
        Assert.AreEqual("done", next.GetString("content"));
    }

    [TestMethod]
    public async Task GeminiThoughtSignaturesSurviveStatelessReplay()
    {
        var model = Fixture("gemini", (request, round) =>
        {
            if (round > 0)
            {
                var contents = JsonValues.ParseObject(request.GetString("body")).GetArray("contents");
                Assert.AreEqual("gemini-signature", contents[1].GetArray("parts")[0].GetString("thoughtSignature"));
            }
            return Event(JsonValues.ParseObject("""{"candidates":[{"content":{"parts":[{"text":"answer","thoughtSignature":"gemini-signature"}]}}]}"""));
        });
        using var session = TestAgentSession.Open("chat", ":memory:", model, _ => { });
        await session.Run("Provider fixture", "test", CancellationToken.None);
        await session.Run("Provider fixture", "continue", CancellationToken.None);
    }

    [TestMethod]
    public async Task GeminiRepliesToParallelCallsTogetherWithTheOriginalIds()
    {
        var model = Fixture("gemini", (request, round) =>
        {
            if (round == 0)
                return Event(JsonValues.ParseObject("""
                    {"candidates":[{"content":{"parts":[
                      {"functionCall":{"id":"first","name":"db_list","args":{}},"thoughtSignature":"signature"},
                      {"functionCall":{"id":"second","name":"db_list","args":{}}}
                    ]}}]}
                    """));
            var contents = JsonValues.ParseObject(request.GetString("body")).GetArray("contents");
            Assert.AreEqual("signature", contents[1].GetArray("parts")[0].GetString("thoughtSignature"));
            var results = contents[^1].GetArray("parts");
            CollectionAssert.AreEqual(new[] { "first", "second" }, results.Select(result => result.GetObject("functionResponse").GetString("id")).ToArray());
            return Event(JsonValues.ParseObject("""{"candidates":[{"content":{"parts":[{"text":"Done."}]}}]}"""));
        });
        using var session = TestAgentSession.Open("chat", ":memory:", model, _ => { });
        Assert.AreEqual("Done.", (await session.Run("Provider fixture", "test", CancellationToken.None)).GetString("content"));
    }

    [TestMethod]
    public async Task OllamaReplaysObjectArgumentsThinkingAndNamedToolResults()
    {
        var model = Fixture("ollama", (request, round) =>
        {
            Assert.EndsWith("/api/chat", request.GetString("url"));
            if (round == 0)
                return """
                    {"message":{"thinking":"Inspect memory.","tool_calls":[{"function":{"name":"db_list","arguments":{}}}]},"done":false}
                    {"message":{"tool_calls":[{"function":{"name":"db_read","arguments":{"key":"fixture"}}}]},"done":false}
                    {"done":true}
                    """;
            var messages = JsonValues.ParseObject(request.GetString("body")).GetArray("messages");
            var assistant = messages.Single(message => message.GetString("role") == "assistant");
            Assert.AreEqual("Inspect memory.", assistant.GetString("thinking"));
            var calls = assistant.GetArray("tool_calls");
            Assert.HasCount(2, calls);
            var function = calls[1].GetObject("function");
            Assert.AreEqual(1, function.GetInt32("index", -1));
            Assert.AreEqual("fixture", function.GetObject("arguments").GetString("key"));
            CollectionAssert.AreEqual(new[] { "db_list", "db_read" }, messages.Where(message => message.GetString("role") == "tool")
                .Select(message => message.GetString("tool_name")).ToArray());
            return """{"message":{"content":"Done."},"done":true}""";
        });
        using var session = TestAgentSession.Open("chat", ":memory:", model, _ => { });
        Assert.AreEqual("Done.", (await session.Run("Provider fixture", "test", CancellationToken.None)).GetString("content"));
    }

    [TestMethod]
    public async Task ResponsesRetainsEncryptedReasoningAndFunctionCallIds()
    {
        var model = Fixture("responses", (request, round) =>
        {
            Assert.EndsWith("/responses", request.GetString("url"));
            if (round > 0)
            {
                var input = JsonValues.ParseObject(request.GetString("body")).GetArray("input");
                Assert.IsTrue(input.Any(item => item.GetString("encrypted_content") == "encrypted-state"));
                Assert.IsTrue(input.Any(item => item.GetString("type") == "function_call_output" && item.GetString("call_id") == "call-1"));
                return Event(JsonValues.ParseObject("""{"type":"response.output_text.done","text":"done"}"""));
            }
            return Event(JsonValues.ParseObject("""{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"r1","encrypted_content":"encrypted-state","summary":[]}}""")) +
                Event(JsonValues.ParseObject("""{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"f1","call_id":"call-1","name":"db_list","arguments":"","status":"in_progress"}}""")) +
                Event(JsonValues.ParseObject("""{"type":"response.function_call_arguments.done","output_index":1,"item_id":"f1","arguments":"{}"}""")) +
                Event(JsonValues.ParseObject("""{"type":"response.completed","response":{"status":"completed"}}"""));
        });
        using var session = TestAgentSession.Open("chat", ":memory:", model, _ => { });
        Assert.AreEqual("done", (await session.Run("Provider fixture", "test", CancellationToken.None)).GetString("content"));
        Assert.AreEqual("call-1", session.History.Single(message => message.GetString("role") == "tool").GetString("tool_call_id"));
    }

    [TestMethod]
    public async Task ResponsesReplaysInterruptedStreamsWithoutExecutingToolsAndRendersRefusals()
    {
        var model = Fixture("responses", (_, round) => round == 0
            ? Event(JsonValues.ParseObject("""{"type":"response.created","response":{"status":"in_progress"}}""")) +
              Event(JsonValues.ParseObject("""{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","call_id":"discarded","name":"db_list","arguments":"{}"}}"""))
            : Event(JsonValues.ParseObject("""{"type":"response.refusal.delta","delta":"Cannot "}""")) +
              Event(JsonValues.ParseObject("""{"type":"response.refusal.done","refusal":"Cannot comply."}""")) +
              Event(JsonValues.ParseObject("""{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"Cannot comply."}]}]}}""")));
        using var session = TestAgentSession.Open("chat", ":memory:", model, _ => { });
        Assert.AreEqual("Cannot comply.", (await session.Run("Provider fixture", "test", CancellationToken.None)).GetString("content"));
        Assert.IsFalse(session.History.Any(message => message.GetString("role") == "tool"));
    }

    [TestMethod]
    [DataRow("completed")]
    [DataRow("streamed")]
    [DataRow("json")]
    public async Task ResponsesEmitsSummaryOnceAcrossProviderFormats(string format)
    {
        const string output = """
            {"object":"response","output":[
              {"type":"reasoning","summary":[{"type":"summary_text","text":"Check the facts"}]},
              {"type":"message","content":[{"type":"output_text","text":"Done."}]}
            ]}
            """;
        var model = Fixture("responses", (_, _) =>
        {
            if (format == "json") return output;
            var completed = Event(new() { ["type"] = "response.completed", ["response"] = JsonValues.ParseObject(output) });
            return format == "completed" ? completed :
                Event(JsonValues.ParseObject("""{"type":"response.reasoning_summary_text.delta","output_index":0,"summary_index":0,"delta":"Check "}""")) +
                Event(JsonValues.ParseObject("""{"type":"response.reasoning_summary_text.done","output_index":0,"summary_index":0,"text":"Check the facts"}""")) +
                completed;
        });
        var trace = new StringBuilder();
        using var session = TestAgentSession.Open("title", ":memory:", model, item =>
        {
            if (item.GetString("type") == "reasoning") trace.Append(item.GetString("text"));
        });
        var response = await session.Run("Provider fixture", "test", CancellationToken.None);
        Assert.AreEqual("Check the facts", trace.ToString());
        Assert.AreEqual(trace.ToString(), response.GetString("reasoning_content"));
        Assert.AreEqual("Done.", response.GetString("content"));
    }

    private static string Event(JsonObject data) => "data: " + JsonValues.Serialize(data) + "\n\n";

    private static ModelClient Fixture(string protocol, Func<JsonObject, int, string> respond, bool splitBytes = false)
    {
        var round = 0;
        HostPort? port = null;
        port = new(request =>
        {
            if (request.GetString("kind") == "cancel_io")
            {
                return;
            }

            var data = respond(request, round++);
            var bytes = Encoding.UTF8.GetBytes(data);
            var step = splitBytes ? 1 : bytes.Length;
            for (var offset = 0; offset < bytes.Length; offset += step)
            {
                port!.Reply(new() { ["id"] = request.GetString("id"), ["data_base64"] = Convert.ToBase64String(bytes, offset, Math.Min(step, bytes.Length - offset)) });
            }
            port!.Reply(new() { ["id"] = request.GetString("id"), ["done"] = true });
        });
        return new(port, () => new("fixture", "Fixture", protocol, "http://fixture.invalid/v1", "test", "model"));
    }
}
