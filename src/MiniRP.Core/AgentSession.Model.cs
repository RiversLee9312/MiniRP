using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class AgentSession
{
    private Task<JsonObject> Complete(ModelClient model, JsonArray messages, AgentSkill.Turn? skills, Action<string, string> delta,
        CancellationToken ct, bool requireComplete = false, Action<int>? inputUsage = null) =>
        CompleteModel(model, messages, skills, delta, ct, requireComplete, inputUsage, openCodeSession.Value);

    internal static async Task<JsonObject> CompleteModel(ModelClient model, JsonArray messages, AgentSkill.Turn? skills,
        Action<string, string> delta, CancellationToken ct, bool requireComplete = false, Action<int>? inputUsage = null,
        string? openCodeSession = null)
    {
        var p = model.SelectedProvider;
        if (p.Url.Length == 0 || p.Model.Length == 0)
        {
            throw new InvalidOperationException("Configure a provider and model first.");
        }

        var request = model.Request(p, messages, skills, openCodeSession);
        if (p.Protocol == "responses")
            return await CompleteResponses(model.Port, request, delta, ct, requireComplete, inputUsage);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        var thinkingBlocks = new SortedDictionary<int, JsonObject>();
        var geminiParts = new JsonArray();
        var promptTokens = 0;

        void Feed(string type, string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            (type == "reasoning" ? reasoning : content).Append(text);
            delta(type, text);
        }
        void Parse(JsonObject data)
        {
            if (data["error"] is not null)
            {
                throw new IOException(JsonValues.Serialize(data["error"]));
            }

            if (p.Protocol == "anthropic")
            {
                var usage = data.GetObject("message").GetObject("usage");
                if (usage.Count == 0) usage = data.GetObject("usage");
                promptTokens = Math.Max(promptTokens, usage.GetInt32("input_tokens") +
                    usage.GetInt32("cache_read_input_tokens") + usage.GetInt32("cache_creation_input_tokens"));
                if (requireComplete && data.GetObject("delta").GetString("stop_reason", data.GetString("stop_reason")) is
                    "max_tokens" or "model_context_window_exceeded" or "refusal")
                    throw IncompleteCheckpoint();
                int index = data.GetInt32("index");
                var block = data.GetObject("content_block");
                var change = data.GetObject("delta");
                if (block.GetString("type") is "thinking" or "redacted_thinking")
                {
                    thinkingBlocks[index] = (JsonObject)block.DeepClone();
                }
                if (thinkingBlocks.TryGetValue(index, out var thinking))
                {
                    if (change.GetString("type") == "thinking_delta")
                    {
                        thinking["thinking"] = thinking.GetString("thinking") + change.GetString("thinking");
                    }

                    if (change.GetString("type") == "signature_delta")
                    {
                        thinking["signature"] = thinking.GetString("signature") + change.GetString("signature");
                    }
                }
                if (block.GetString("type") == "tool_use")
                {
                    var initial = block.GetObject("input");
                    calls[index] = Call(block.GetString("id"), block.GetString("name"), initial.Count > 0 ? JsonValues.Serialize(initial) : "");
                }
                if (block.GetString("type") == "text") Feed("content", block.GetString("text"));

                if (change.GetString("type") == "input_json_delta" && calls.TryGetValue(index, out var call))
                {
                    call.GetObject("function")["arguments"] = call.GetObject("function").GetString("arguments") + change.GetString("partial_json");
                }

                Feed(change.GetString("type") == "thinking_delta" ? "reasoning" : "content", change.GetString(change.GetString("type") == "thinking_delta" ? "thinking" : "text"));
                if (data.GetArray("content").Count > 0)
                {
                    var blocks = data.GetArray("content");
                    for (var position = 0; position < blocks.Count; position++)
                    {
                        var b = blocks[position];
                        if (b.GetString("type") == "text")
                        {
                            Feed("content", b.GetString("text"));
                        }
                        else if (b.GetString("type") == "tool_use")
                        {
                            calls[position] = Call(b.GetString("id"), b.GetString("name"), JsonValues.Serialize(b?["input"]));
                        }
                        else if (b is JsonObject thought && b.GetString("type") is "thinking" or "redacted_thinking")
                        {
                            thinkingBlocks[position] = (JsonObject)thought.DeepClone();
                            Feed("reasoning", thought.GetString("thinking"));
                        }
                    }
                }
            }
            else if (p.Protocol == "gemini")
            {
                promptTokens = Math.Max(promptTokens, data.GetObject("usageMetadata").GetInt32("promptTokenCount"));
                foreach (var candidate in data.GetArray("candidates"))
                {
                    if (requireComplete && candidate.GetString("finishReason") is { Length: > 0 } reason && reason != "STOP")
                        throw IncompleteCheckpoint();
                    foreach (var part in candidate.GetObject("content").GetArray("parts"))
                    {
                        geminiParts.AddNode(JsonValues.Clone(part));
                        Feed(part.GetBoolean("thought") ? "reasoning" : "content", part.GetString("text"));
                        if (part?["functionCall"] is JsonObject fn)
                        {
                            var call = Call(JsonValues.NewId(), fn.GetString("name"), JsonValues.Serialize(fn["args"]));
                            if (part?["thoughtSignature"] is not null)
                            {
                                call["thoughtSignature"] = JsonValues.Clone(part["thoughtSignature"]);
                            }

                            calls[calls.Count] = call;
                        }
                    }
                }
            }
            else
            {
                promptTokens = Math.Max(promptTokens, p.Protocol == "ollama" ? data.GetInt32("prompt_eval_count") :
                    data.GetObject("usage").GetInt32("prompt_tokens"));
                var choice = p.Protocol == "ollama" ? data : data.GetArray("choices").FirstOrDefault() as JsonObject ?? new();
                if (requireComplete && choice.GetString("finish_reason", choice.GetString("done_reason")) is "length" or "content_filter")
                    throw IncompleteCheckpoint();
                var d = choice["delta"] as JsonObject ?? choice["message"] as JsonObject ?? new();
                var refusal = d.GetString("refusal");
                if (requireComplete && refusal.Length > 0) throw IncompleteCheckpoint();
                Feed("content", d.GetString("content", refusal));
                Feed("reasoning", d.GetString("reasoning_content", d.GetString("reasoning", d.GetString("thinking"))));
                int position = 0;
                foreach (var raw in d.GetArray("tool_calls"))
                {
                    var f = raw.GetObject("function");
                    if (p.Protocol == "ollama" && f["arguments"] is JsonObject arguments)
                    {
                        calls[calls.Count] = Call(raw.GetString("id", JsonValues.NewId()), f.GetString("name"), JsonValues.Serialize(arguments));
                        continue;
                    }
                    var i = raw.GetInt32("index", position++);
                    if (!calls.TryGetValue(i, out var c))
                    {
                        calls[i] = c = Call(raw.GetString("id", JsonValues.NewId()), "");
                    }

                    if (f.GetString("name").Length > 0)
                    {
                        c.GetObject("function")["name"] = c.GetObject("function").GetString("name") + f.GetString("name");
                    }

                    c.GetObject("function")["arguments"] = c.GetObject("function").GetString("arguments") + (f["arguments"] is JsonObject ? JsonValues.Serialize(f["arguments"]) : f.GetString("arguments"));
                }
            }
        }
        await foreach (var data in ModelEvents.Read(model.Port, request, p.Protocol == "ollama", ct))
        {
            Parse(data);
        }
        if (promptTokens > 0) inputUsage?.Invoke(promptTokens);
        var result = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() };
        if (geminiParts.Count > 0)
        {
            result["gemini_parts"] = geminiParts;
        }
        if (thinkingBlocks.Count > 0)
        {
            var blocks = new JsonArray();
            foreach (var block in thinkingBlocks.Values)
            {
                blocks.AddNode(block);
            }

            result["anthropic_thinking"] = blocks;
        }
        if (reasoning.Length > 0)
        {
            result["reasoning_content"] = reasoning.ToString();
        }

        if (calls.Count > 0)
        {
            var a = new JsonArray();
            foreach (var c in calls.Values)
            {
                a.AddNode(c);
            }

            result["tool_calls"] = a;
        }
        return result;
    }

    private static IOException IncompleteCheckpoint() =>
        new("The model did not finish generating the context checkpoint; original history is retained.");
    static JsonObject Call(string id, string name, string args = "") => new() { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
}
