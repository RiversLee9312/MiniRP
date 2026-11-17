using System.Text;
using System.Text.Json.Nodes;

namespace Minichat.Core;

/// <summary>OpenAI Responses wire format, including stateless replay of encrypted reasoning items.</summary>
internal static class ResponsesProtocol
{
    public static async Task<JsonObject> Complete(HostPort port, Provider provider, JsonArray messages, JsonArray tools, Action<string, string> delta, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["url"] = provider.Url + "/responses",
            ["method"] = "POST",
            ["stream"] = true,
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + provider.Key, ["Content-Type"] = "application/json" },
            ["body"] = JsonValues.Serialize(Build(provider, messages, tools))
        };
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        var output = new SortedDictionary<int, JsonObject>();

        void Feed(string kind, string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            (kind == "reasoning" ? reasoning : content).Append(text);
            delta(kind, text);
        }

        await foreach (var item in ModelEvents.Read(port, request, false, cancellationToken))
        {
            var type = item.GetString("type");
            var index = item.GetInt32("output_index");
            var block = item.GetObject("item");
            switch (type)
            {
                case "error":
                case "response.failed":
                    throw new IOException(JsonValues.Serialize(item));
                case "response.output_text.delta":
                    Feed("content", item.GetString("delta"));
                    break;
                case "response.reasoning_summary_text.delta":
                    Feed("reasoning", item.GetString("delta"));
                    break;
                case "response.output_item.added" when block.GetString("type") == "function_call":
                    calls[index] = FunctionCall(block);
                    break;
                case "response.function_call_arguments.delta":
                    if (calls.TryGetValue(index, out var call))
                    {
                        call.GetObject("function")["arguments"] = call.GetObject("function").GetString("arguments") + item.GetString("delta");
                    }
                    break;
                case "response.output_item.done":
                    output[index] = (JsonObject)block.DeepClone();
                    if (block.GetString("type") == "function_call")
                    {
                        calls[index] = FunctionCall(block);
                    }

                    break;
                case "response.completed":
                    var response = item.GetObject("response");
                    var position = 0;
                    foreach (var completed in response.GetArray("output").OfType<JsonObject>())
                    {
                        output[position++] = (JsonObject)completed.DeepClone();
                    }
                    break;
            }
            // Some compatible providers ignore stream=true and return a complete response.
            if (type == "response" && item["output"] is JsonArray complete)
            {
                var position = 0;
                foreach (var completed in complete.OfType<JsonObject>())
                {
                    output[position] = (JsonObject)completed.DeepClone();
                    if (completed.GetString("type") == "function_call")
                    {
                        calls[position] = FunctionCall(completed);
                    }

                    foreach (var part in completed.GetArray("content"))
                    {
                        if (part.GetString("type") == "output_text")
                        {
                            Feed("content", part.GetString("text"));
                        }
                    }

                    position++;
                }
            }
        }
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

    private static JsonObject Build(Provider provider, JsonArray messages, JsonArray tools)
    {
        var input = new JsonArray();
        var instructions = new StringBuilder();
        foreach (var message in messages)
        {
            switch (message.GetString("role"))
            {
                case "system":
                    instructions.AppendLine(message.GetString("content"));
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
                    input.AddNode(new JsonObject { ["role"] = "user", ["content"] = message.GetString("content") });
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

        if (provider.Thinking)
        {
            body["reasoning"] = new JsonObject { ["effort"] = provider.ReasoningEffort.Length > 0 ? provider.ReasoningEffort : "medium", ["summary"] = "auto" };
        }

        return body;
    }
}
