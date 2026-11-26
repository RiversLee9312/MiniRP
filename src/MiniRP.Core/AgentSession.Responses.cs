using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class AgentSession
{
    /// <summary>Streams OpenAI Responses and preserves encrypted reasoning for stateless replay.</summary>
    private static async Task<JsonObject> CompleteResponses(HostPort port, JsonObject request,
        Action<string, string> delta, CancellationToken cancellationToken, bool requireComplete, Action<int>? inputUsage)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        var pendingCalls = new Dictionary<int, JsonObject>();
        var output = new SortedDictionary<int, JsonObject>();
        var parts = new Dictionary<(string Kind, int Output, int Part), StringBuilder>();
        var promptTokens = 0;
        var awaitingCompletion = false;

        void Feed(string kind, string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            (kind == "reasoning" ? reasoning : content).Append(text);
            delta(kind, text);
        }

        void FeedPart(string kind, int index, int part, string text, bool complete = false)
        {
            if (text.Length == 0) return;
            var key = (kind, index, part);
            if (!parts.TryGetValue(key, out var seen)) parts[key] = seen = new();
            if (complete)
            {
                if (!text.StartsWith(seen.ToString(), StringComparison.Ordinal)) return;
                text = text[seen.Length..];
            }
            seen.Append(text);
            Feed(kind is "content" or "refusal" ? "content" : "reasoning", text);
        }

        void CompleteItem(JsonObject item, int index)
        {
            output[index] = (JsonObject)item.DeepClone();
            if (item.GetString("type") == "function_call")
            {
                if (item.GetString("status") == "incomplete") throw new IOException("The model did not finish the function call.");
                pendingCalls.Remove(index);
                calls[index] = FunctionCall(item);
            }
            var summary = item.GetArray("summary");
            if (summary.Count > 0 && !parts.Keys.Any(part => part.Output == index && part.Kind == "reasoning-text"))
            {
                for (var part = 0; part < summary.Count; part++)
                    FeedPart("summary", index, part, summary[part].GetString("text"), complete: true);
            }
            var contentParts = item.GetArray("content");
            for (var part = 0; part < contentParts.Count; part++)
            {
                var block = contentParts[part];
                if (block.GetString("type") == "output_text")
                    FeedPart("content", index, part, block.GetString("text"), complete: true);
                else if (block.GetString("type") == "refusal")
                {
                    if (requireComplete) throw IncompleteCheckpoint();
                    FeedPart("refusal", index, part, block.GetString("refusal"), complete: true);
                }
                else if (summary.Count == 0 && item.GetString("type") == "reasoning" &&
                         !parts.Keys.Any(part => part.Output == index && part.Kind == "summary"))
                    FeedPart("reasoning-text", index, part, block.GetString("text"), complete: true);
            }
        }

        void CompleteResponse(JsonObject response)
        {
            var status = response.GetString("status");
            if (response["error"] is not null || status == "failed")
                throw new IOException(response.GetObject("error").GetString("message", "The model response failed."));
            if (status == "incomplete") throw IncompleteResponse(response);
            if (status is "queued" or "in_progress" or "cancelled")
                throw new IOException("The model response did not finish.");
            awaitingCompletion = false;
            promptTokens = response.GetObject("usage").GetInt32("input_tokens", promptTokens);
            var position = 0;
            foreach (var completed in response.GetArray("output").OfType<JsonObject>())
                CompleteItem(completed, position++);
        }

        IOException IncompleteResponse(JsonObject response) => requireComplete ? IncompleteCheckpoint() :
            new IOException("The model response was incomplete: " + response.GetObject("incomplete_details").GetString("reason", "unknown"));

        await foreach (var item in ModelEvents.Read(port, request, false, cancellationToken))
        {
            var type = item.GetString("type");
            var index = item.GetInt32("output_index");
            var block = item.GetObject("item");
            switch (type)
            {
                case "error":
                    throw new IOException(item.GetString("message", item.GetObject("error").GetString("message", "The model response failed.")));
                case "response.failed":
                    throw new IOException(item.GetObject("response").GetObject("error").GetString("message", "The model response failed."));
                case "response.created":
                case "response.in_progress":
                    awaitingCompletion = true;
                    break;
                case "response.output_text.delta":
                case "response.output_text.done":
                    FeedPart("content", index, item.GetInt32("content_index"), item.GetString("delta", item.GetString("text")),
                        complete: type == "response.output_text.done");
                    break;
                case "response.refusal.delta":
                case "response.refusal.done":
                    if (requireComplete) throw IncompleteCheckpoint();
                    FeedPart("refusal", index, item.GetInt32("content_index"), item.GetString("delta", item.GetString("refusal")),
                        complete: type == "response.refusal.done");
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_summary_text.done":
                case "response.reasoning_summary_part.done":
                    if (!parts.Keys.Any(part => part.Output == index && part.Kind == "reasoning-text"))
                        FeedPart("summary", index, item.GetInt32("summary_index"),
                            item.GetString("delta", item.GetString("text", item.GetObject("part").GetString("text"))),
                            complete: type != "response.reasoning_summary_text.delta");
                    break;
                case "response.reasoning_text.delta":
                case "response.reasoning_text.done":
                    if (!parts.Keys.Any(part => part.Output == index && part.Kind == "summary"))
                        FeedPart("reasoning-text", index, item.GetInt32("content_index"),
                            item.GetString("delta", item.GetString("text")), complete: type == "response.reasoning_text.done");
                    break;
                case "response.output_item.added" when block.GetString("type") == "function_call":
                    pendingCalls[index] = block;
                    break;
                case "response.function_call_arguments.done":
                    if (pendingCalls.TryGetValue(index, out var call) && item["arguments"] is JsonValue arguments)
                    {
                        call["arguments"] = arguments.DeepClone();
                        call["status"] = "completed";
                        CompleteItem(call, index);
                    }
                    break;
                case "response.output_item.done":
                    CompleteItem(block, index);
                    break;
                case "response.completed":
                    CompleteResponse(item.GetObject("response"));
                    break;
                case "response.incomplete":
                    throw IncompleteResponse(item.GetObject("response"));
            }
            // Some compatible providers ignore stream=true and return a complete response.
            if (type == "response" || item.GetString("object") == "response") CompleteResponse(item);
            else if (item["error"] is not null) throw new IOException(item.GetObject("error").GetString("message", "The model response failed."));
        }
        if (awaitingCompletion || pendingCalls.Count > 0)
            throw new ModelEvents.Interrupted(new HostPort.HttpTransportError("The Responses stream ended before completion."), responseStarted: true);
        if (promptTokens > 0) inputUsage?.Invoke(promptTokens);
        var result = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() };
        if (reasoning.Length > 0)
        {
            result["reasoning_content"] = reasoning.ToString();
        }

        if (calls.Count > 0)
        {
            var array = new JsonArray(); foreach (var call in calls.Values)
            {
                array.AddNode(call);
            }

            result["tool_calls"] = array;
        }
        if (output.Count > 0)
        {
            var array = new JsonArray(); foreach (var block in output.Values)
            {
                array.AddNode(block);
            }

            result["response_items"] = array;
        }
        return result;
    }

    private static JsonObject FunctionCall(JsonObject item) => new()
    {
        ["id"] = item.GetString("call_id"),
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = item.GetString("name"), ["arguments"] = item.GetString("arguments") }
    };

    internal static JsonObject BuildResponses(Provider provider, JsonArray messages, JsonArray tools, Dictionary<JsonNode, List<MessageAttachments.Media>> media)
    {
        var input = new JsonArray();
        var instructions = new StringBuilder();
        foreach (var message in messages)
        {
            switch (message.GetString("role"))
            {
                case "system":
                    if (ReferenceEquals(message, messages[0]))
                        instructions.AppendLine(message.GetString("content"));
                    else
                        input.AddNode(new JsonObject { ["role"] = "developer", ["content"] = message.GetString("content") });
                    break;
                case "tool":
                    input.AddNode(new JsonObject { ["type"] = "function_call_output", ["call_id"] = message.GetString("tool_call_id"), ["output"] = message.GetString("content") });
                    break;
                case "assistant" when message is JsonObject assistant && assistant["response_items"] is JsonArray previous:
                    foreach (var item in previous)
                    {
                        input.AddNode(JsonValues.Clone(item));
                    }

                    break;
                case "assistant":
                    if (message.GetString("content").Length > 0)
                    {
                        input.AddNode(new JsonObject { ["role"] = "assistant", ["content"] = message.GetString("content") });
                    }

                    foreach (var call in message.GetArray("tool_calls"))
                    {
                        input.AddNode(new JsonObject { ["type"] = "function_call", ["call_id"] = call.GetString("id"), ["name"] = call.GetObject("function").GetString("name"), ["arguments"] = call.GetObject("function").GetString("arguments") });
                    }

                    break;
                default:
                    input.AddNode(new JsonObject { ["role"] = "user", ["content"] = message is not null && media.TryGetValue(message, out var attachments)
                        ? MessageAttachments.Parts(provider.Protocol, MessageAttachments.RequestText(message), attachments)
                        : JsonValue.Create(MessageAttachments.RequestText(message)) });
                    break;
            }
        }
        var definitions = new JsonArray();
        foreach (var tool in tools)
        {
            var function = tool.GetObject("function");
            definitions.AddNode(new JsonObject { ["type"] = "function", ["name"] = function.GetString("name"), ["description"] = function.GetString("description"), ["parameters"] = JsonValues.Clone(function["parameters"]), ["strict"] = false });
        }
        var body = new JsonObject { ["model"] = provider.Model, ["instructions"] = instructions.ToString(), ["input"] = input, ["stream"] = true, ["store"] = false, ["include"] = new JsonArray("reasoning.encrypted_content") };
        body["max_output_tokens"] = provider.MaxOutput;
        if (definitions.Count > 0)
        {
            body["tools"] = definitions;
        }

        if (provider.Thinking || provider.ReasoningEffort.Length > 0)
        {
            var reasoning = new JsonObject();
            if (provider.ReasoningEffort.Length > 0)
                reasoning["effort"] = provider.ReasoningEffort;
            if (!provider.Url.Contains("deepseek", StringComparison.OrdinalIgnoreCase) && provider.ReasoningEffort != "none")
                reasoning["summary"] = "auto";
            body["reasoning"] = reasoning;
        }

        return body;
    }
}
