using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Provider-neutral messages and explicit wire adapters. No runtime type discovery.</summary>
public sealed class ModelClient(HostPort port, Func<Provider> selected, string? attachmentRoot = null)
{
    internal Provider SelectedProvider => selected();
    internal ModelClient Bind(Provider provider) => new(port, () => provider, attachmentRoot);

    internal void ValidateAsset(JsonObject attachment) =>
        new MessageAttachments(attachmentRoot ?? throw new InvalidOperationException("Asset attachments are unavailable."))
            .Validate(new JsonArray(attachment.DeepClone()), selected());
    public int InputCharacterLimit
    {
        get
        {
            var provider = selected();
            return (int)Math.Clamp((provider.ContextWindow - (long)provider.MaxOutput) * 2, 2048, 16000000);
        }
    }
    public int CompactionCharacterLimit
    {
        get
        {
            var provider = selected();
            var tokens = provider.CompactAtPercent is { } percent ? provider.ContextWindow * (long)percent / 100
                : provider.CompactAt > 0 ? provider.CompactAt : provider.ContextWindow * 80L / 100;
            return CompactionCharacters(provider, tokens);
        }
    }
    internal static int CompactionCharacters(Provider provider, long tokens) =>
        (int)Math.Clamp(Math.Min(tokens, Math.Max(1024, provider.ContextWindow - provider.MaxOutput)) * 2L, 2048, 16000000);
    static JsonObject Headers(Provider p)
    {
        var h = new JsonObject { ["Content-Type"] = "application/json" };
        if (p.Protocol == "anthropic")
        {
            h["x-api-key"] = p.Key;
            h["anthropic-version"] = "2023-06-01";
        }
        else if (p.Protocol == "gemini")
        {
            h["x-goog-api-key"] = p.Key;
        }
        else if (p.Key.Length > 0)
        {
            h["Authorization"] = "Bearer " + p.Key;
        }

        return h;
    }
    public async Task<JsonArray> Models(Provider p, CancellationToken ct)
    {
        var suffix = p.Protocol == "ollama" ? "/api/tags" : "/models";
        var result = new JsonArray();
        var url = p.Url + suffix;
        do
        {
            var response = await port.Request(new()
            {
                ["url"] = url,
                ["method"] = "GET",
                ["headers"] = Headers(p),
                ["stream"] = false
            }, ct);
            foreach (var model in response.GetArray(p.Protocol is "gemini" or "ollama" ? "models" : "data"))
            {
                var id = model.GetString(p.Protocol == "gemini" ? "name" : p.Protocol == "ollama" ? "model" : "id").Replace("models/", "");
                if (id.Length > 0)
                {
                    var metadata = (JsonObject)model!.DeepClone();
                    metadata["id"] = id;
                    metadata["name"] = model.GetString("displayName", id);
                    result.AddNode(metadata);
                }
            }
            var next = response.GetString("nextPageToken");
            url = next.Length > 0 ? p.Url + suffix + "?pageToken=" + Uri.EscapeDataString(next) : "";
        } while (url.Length > 0);
        return result;
    }
    public Task<JsonObject> Complete(JsonArray messages, JsonArray tools, Action<string, string> delta, CancellationToken ct) =>
        Complete(messages, tools, delta, ct, requireComplete: false);

    internal async Task<JsonObject> Complete(JsonArray messages, JsonArray tools, Action<string, string> delta, CancellationToken ct,
        bool requireComplete)
    {
        var p = selected();
        if (p.Url.Length == 0 || p.Model.Length == 0)
        {
            throw new InvalidOperationException("Configure a provider and model first.");
        }

        Dictionary<JsonNode, List<MessageAttachments.Media>> media = attachmentRoot is null ? new() : new MessageAttachments(attachmentRoot).Resolve(messages, p.Protocol);
        if (p.Protocol == "responses")
        {
            return await ResponsesProtocol.Complete(port, p, messages, tools, delta, ct, media, requireComplete);
        }
        var (path, body) = Build(p, messages, tools, media);
        var request = new JsonObject { ["url"] = p.Url + path, ["method"] = "POST", ["headers"] = Headers(p), ["body"] = JsonValues.Serialize(body), ["stream"] = true };
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        var thinkingBlocks = new SortedDictionary<int, JsonObject>();
        var geminiParts = new JsonArray();



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
                    calls[index] = Call(block.GetString("id"), block.GetString("name"));
                }

                if (change.GetString("type") == "input_json_delta" && calls.TryGetValue(index, out var call))
                {
                    call.GetObject("function")["arguments"] = call.GetObject("function").GetString("arguments") + change.GetString("partial_json");
                }

                Feed(change.GetString("type") == "thinking_delta" ? "reasoning" : "content", change.GetString(change.GetString("type") == "thinking_delta" ? "thinking" : "text"));
                if (data.GetArray("content").Count > 0)
                {
                    foreach (var b in data.GetArray("content"))
                    {
                        if (b.GetString("type") == "text")
                        {
                            Feed("content", b.GetString("text"));
                        }
                        else if (b.GetString("type") == "tool_use")
                        {
                            calls[calls.Count] = Call(b.GetString("id"), b.GetString("name"), JsonValues.Serialize(b?["input"]));
                        }
                    }
                }
            }
            else if (p.Protocol == "gemini")
            {
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
                var choice = p.Protocol == "ollama" ? data : data.GetArray("choices").FirstOrDefault() as JsonObject ?? new();
                if (requireComplete && choice.GetString("finish_reason", choice.GetString("done_reason")) is "length" or "content_filter")
                    throw IncompleteCheckpoint();
                var d = choice["delta"] as JsonObject ?? choice["message"] as JsonObject ?? new();
                Feed("content", d.GetString("content"));
                Feed("reasoning", d.GetString("reasoning_content", d.GetString("reasoning", d.GetString("thinking"))));
                int position = 0;
                foreach (var raw in d.GetArray("tool_calls"))
                {
                    var i = raw.GetInt32("index", position++);
                    if (!calls.TryGetValue(i, out var c))
                    {
                        calls[i] = c = Call(raw.GetString("id", JsonValues.NewId()), "");
                    }

                    var f = raw.GetObject("function");
                    if (f.GetString("name").Length > 0)
                    {
                        c.GetObject("function")["name"] = c.GetObject("function").GetString("name") + f.GetString("name");
                    }

                    c.GetObject("function")["arguments"] = c.GetObject("function").GetString("arguments") + (f["arguments"] is JsonObject ? JsonValues.Serialize(f["arguments"]) : f.GetString("arguments"));
                }
            }
        }
        await foreach (var data in ModelEvents.Read(port, request, p.Protocol == "ollama", ct))
        {
            Parse(data);
        }
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

    internal static IOException IncompleteCheckpoint() =>
        new("The model did not finish generating the context checkpoint; original history is retained.");
    static JsonObject Call(string id, string name, string args = "") => new() { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
    static (string, JsonObject) Build(Provider p, JsonArray messages, JsonArray tools, Dictionary<JsonNode, List<MessageAttachments.Media>> media)
    {
        if (p.Protocol == "anthropic")
        {
            var converted = new JsonArray();
            var system = new StringBuilder();
            foreach (var message in messages)
            {
                var role = message.GetString("role");
                if (role == "system")
                {
                    system.AppendLine(message.GetString("content"));
                    continue;
                }
                var parts = new JsonArray();
                if (role == "tool")
                {
                    parts.AddNode(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = message.GetString("tool_call_id"), ["content"] = message.GetString("content") });
                }
                else
                {
                    foreach (var thinking in message.GetArray("anthropic_thinking"))
                    {
                        parts.AddNode(JsonValues.Clone(thinking));
                    }

                    if (message is not null && media.TryGetValue(message, out var attachments))
                    {
                        parts = MessageAttachments.Parts(p.Protocol, MessageAttachments.Text(message), attachments);
                    }
                    else if (message.GetString("content").Length > 0)
                    {
                        parts.AddNode(new JsonObject { ["type"] = "text", ["text"] = message.GetString("content") });
                    }

                    foreach (var call in message.GetArray("tool_calls"))
                    {
                        parts.AddNode(new JsonObject { ["type"] = "tool_use", ["id"] = call.GetString("id"), ["name"] = call.GetObject("function").GetString("name"), ["input"] = JsonValues.ParseObject(call.GetObject("function").GetString("arguments", "{}")) });
                    }
                }
                if (parts.Count == 0)
                {
                    continue;
                }

                var targetRole = role == "assistant" ? "assistant" : "user";
                if (converted.LastOrDefault()?.GetString("role") == targetRole)
                {
                    foreach (var part in parts.ToArray())
                    {
                        converted.Last()!.GetArray("content").AddNode(JsonValues.Clone(part));
                    }
                }
                else
                {
                    converted.AddNode(new JsonObject { ["role"] = targetRole, ["content"] = parts });
                }
            }
            var t = new JsonArray();
            foreach (var tool in tools)
            {
                var f = tool.GetObject("function");
                t.AddNode(new JsonObject { ["name"] = f.GetString("name"), ["description"] = f.GetString("description"), ["input_schema"] = JsonValues.Clone(f["parameters"]) });
            }
            var body = new JsonObject { ["model"] = p.Model, ["system"] = system.ToString(), ["messages"] = converted, ["max_tokens"] = p.MaxOutput, ["stream"] = true };
            if (t.Count > 0)
            {
                body["tools"] = t;
            }
            if (p.Thinking)
            {
                var manual = p.Model.Contains("4-5", StringComparison.Ordinal) || p.Model.Contains("4-1", StringComparison.Ordinal) || p.Model.Contains("4-20", StringComparison.Ordinal) || p.Model.Contains("3-7", StringComparison.Ordinal);
                body["thinking"] = manual ? new JsonObject { ["type"] = "enabled", ["budget_tokens"] = Math.Max(1024, Math.Min(2048, p.MaxOutput - 1)) } : new JsonObject { ["type"] = "adaptive" };
            }
            return ("/messages", body);
        }
        if (p.Protocol == "gemini")
        {
            var contents = new JsonArray();
            var system = new StringBuilder();
            var names = new Dictionary<string, string>();
            foreach (var m in messages)
            {
                var role = m.GetString("role");
                if (role == "system")
                {
                    system.AppendLine(m.GetString("content"));
                    continue;
                }
                var parts = new JsonArray();
                if (role == "tool")
                {
                    parts.AddNode(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = names.GetValueOrDefault(m.GetString("tool_call_id"), "tool"), ["response"] = new JsonObject { ["result"] = m.GetString("content") } } });
                }
                else if (role == "assistant" && m?["gemini_parts"] is JsonArray originalParts)
                {
                    foreach (var part in originalParts)
                    {
                        parts.AddNode(JsonValues.Clone(part));
                    }

                    foreach (var call in m.GetArray("tool_calls"))
                    {
                        names[call.GetString("id")] = call.GetObject("function").GetString("name");
                    }
                }
                else
                {
                    if (m is not null && media.TryGetValue(m, out var attachments))
                    {
                        parts = MessageAttachments.Parts(p.Protocol, MessageAttachments.Text(m), attachments);
                    }
                    else if (m.GetString("content").Length > 0)
                    {
                        parts.AddNode(new JsonObject { ["text"] = m.GetString("content") });
                    }

                    foreach (var c in m.GetArray("tool_calls"))
                    {
                        var f = c.GetObject("function");
                        names[c.GetString("id")] = f.GetString("name");
                        var part = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = f.GetString("name"), ["args"] = JsonValues.ParseObject(f.GetString("arguments", "{}")) } };
                        if (c?["thoughtSignature"] is not null)
                        {
                            part["thoughtSignature"] = JsonValues.Clone(c["thoughtSignature"]);
                        }

                        parts.AddNode(part);
                    }
                }
                if (parts.Count > 0)
                {
                    contents.AddNode(new JsonObject { ["role"] = role == "assistant" ? "model" : "user", ["parts"] = parts });
                }
            }
            var declarations = new JsonArray();
            foreach (var t in tools)
            {
                var f = t.GetObject("function");
                declarations.AddNode(new JsonObject { ["name"] = f.GetString("name"), ["description"] = f.GetString("description"), ["parameters"] = JsonValues.Clone(f["parameters"]) });
            }
            var body = new JsonObject { ["contents"] = contents, ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system.ToString() }) } };
            body["generationConfig"] = new JsonObject { ["maxOutputTokens"] = p.MaxOutput };
            if (declarations.Count > 0)
            {
                body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
            }

            if (p.Thinking)
            {
                body.GetObject("generationConfig")["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true };
            }

            return ($"/models/{Uri.EscapeDataString(p.Model)}:streamGenerateContent?alt=sse", body);
        }
        var normalized = new JsonArray();
        foreach (var message in messages)
        {
            var clean = new JsonObject { ["role"] = message.GetString("role"), ["content"] = message.GetString("content") };
            if (message is not null && media.TryGetValue(message, out var attachments))
            {
                clean["content"] = MessageAttachments.Text(message);
                if (p.Protocol == "ollama")
                {
                    clean["images"] = new JsonArray(attachments.Select(file => (JsonNode?)JsonValue.Create(file.Data)).ToArray());
                }
                else if (attachments.Count > 0)
                {
                    clean["content"] = MessageAttachments.Parts(p.Protocol, MessageAttachments.Text(message), attachments);
                }
            }
            foreach (var key in new[] { "tool_call_id", "name" })
            {
                if (message?[key] is not null)
                {
                    clean[key] = JsonValues.Clone(message[key]);
                }
            }

            if (message?["reasoning_content"] is not null && (p.Url.Contains("deepseek", StringComparison.OrdinalIgnoreCase) || p.Model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)))
            {
                clean["reasoning_content"] = JsonValues.Clone(message["reasoning_content"]);
            }

            if (message?["tool_calls"] is JsonArray calls)
            {
                var cleanCalls = new JsonArray();
                foreach (var call in calls)
                {
                    cleanCalls.AddNode(new JsonObject { ["id"] = call.GetString("id"), ["type"] = "function", ["function"] = JsonValues.Clone(call?["function"]) });
                }

                clean["tool_calls"] = cleanCalls;
            }
            normalized.AddNode(clean);
        }
        var open = new JsonObject { ["model"] = p.Model, ["messages"] = normalized, ["stream"] = true };
        if (tools.Count > 0)
        {
            open["tools"] = tools.DeepClone();
        }

        if (p.Protocol == "ollama")
        {
            open["think"] = p.Thinking;
            open["options"] = new JsonObject { ["num_ctx"] = p.ContextWindow, ["num_predict"] = p.MaxOutput };

            return ("/api/chat", open);
        }
        if (p.Url.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            open["thinking"] = new JsonObject { ["type"] = p.Thinking ? "enabled" : "disabled" };
            if (p.Thinking && p.ReasoningEffort.Length > 0)
            {
                open["reasoning_effort"] = p.ReasoningEffort;
            }
        }
        var modernOpenAi = p.Model.StartsWith("gpt-5", StringComparison.Ordinal) || (p.Model.Length > 1 && p.Model[0] == 'o' && char.IsAsciiDigit(p.Model[1]));
        open[modernOpenAi ? "max_completion_tokens" : "max_tokens"] = p.MaxOutput;

        return ("/chat/completions", open);
    }
}
