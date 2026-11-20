using System.Text;
using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class ProviderTests
{
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
