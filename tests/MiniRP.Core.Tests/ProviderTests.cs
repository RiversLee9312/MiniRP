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
                return """{"data":[{"id":"go-test","limit":{"context":262144,"output":32768},"modalities":{"input":["text","image"]}}]}""";
            }
            Assert.AreEqual(url + "/chat/completions", request.GetString("url"));
            requests.Add(request.GetObject("headers").GetString("x-opencode-session"));
            return "data: {\"choices\":[{\"delta\":{\"content\":\"Done.\"}}]}\n\ndata: [DONE]\n\n";
        });
        var choice = (await fixture.Models(provider, default)).Single();
        Assert.AreEqual(262144, choice.GetInt32("context_length"));
        Assert.AreEqual(32768, choice.GetInt32("max_output_tokens"));
        Assert.HasCount(2, choice.GetArray("input_modalities"));

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
                return Event(JsonValues.ParseObject("""{"type":"response.output_text.delta","delta":"done"}"""));
            }
            return Event(JsonValues.ParseObject("""{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"r1","encrypted_content":"encrypted-state","summary":[]}}""")) +
                Event(JsonValues.ParseObject("""{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"f1","call_id":"call-1","name":"db_list","arguments":"{}"}}"""));
        });
        using var session = TestAgentSession.Open("chat", ":memory:", model, _ => { });
        Assert.AreEqual("done", (await session.Run("Provider fixture", "test", CancellationToken.None)).GetString("content"));
        Assert.AreEqual("call-1", session.History.Single(message => message.GetString("role") == "tool").GetString("tool_call_id"));
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
