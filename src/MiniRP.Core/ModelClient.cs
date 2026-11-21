using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Provider-neutral messages and explicit wire adapters. No runtime type discovery.</summary>
public sealed class ModelClient(HostPort port, Func<Provider> selected, string? attachmentRoot = null)
{
    internal Provider SelectedProvider
    {
        get
        {
            var provider = selected();
            return provider with { MaxOutput = Math.Min(provider.MaxOutput, Math.Max(1, provider.ContextWindow - 1024)) };
        }
    }
    internal ModelClient Bind(Provider provider) => new(port, () => provider, attachmentRoot);

    internal int EstimateInput(JsonArray messages, JsonArray tools)
    {
        var provider = SelectedProvider;
        return ContextBudget.Estimate(Build(provider, messages, tools, includeMediaData: false).Body);
    }
    static JsonObject Headers(Provider p, ProviderPlans.Gateway gateway)
    {
        var h = new JsonObject { ["Content-Type"] = "application/json" };
        if (p.Protocol == "anthropic")
        {
            h["anthropic-version"] = "2023-06-01";
            if (gateway is ProviderPlans.Gateway.OpenCodeGo or ProviderPlans.Gateway.CommandCode)
                h["Authorization"] = "Bearer " + p.Key;
            else
                h["x-api-key"] = p.Key;
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
        var plan = ProviderPlans.Find(p);
        var gateway = plan?.Wire ?? ProviderPlans.Gateway.Standard;
        var catalogUrl = gateway == ProviderPlans.Gateway.OpenCodeGo ? "https://opencode.ai/inference/go/v1/models" :
            p.Url + (p.Protocol == "ollama" ? "/api/tags" : "/models");
        var result = new JsonArray();
        var url = catalogUrl;
        do
        {
            JsonObject response;
            try
            {
                response = await port.Request(new()
                {
                    ["url"] = url,
                    ["method"] = "GET",
                    ["headers"] = Headers(p, gateway),
                    ["stream"] = false
                }, ct);
            }
            catch (HostPort.HttpError e) when (plan?.Model.Length > 0 && url == catalogUrl && e.Status is 404 or 405 or 501)
            {
                break;
            }
            foreach (var model in response.GetArray(p.Protocol is "gemini" or "ollama" ? "models" : "data"))
            {
                if (gateway == ProviderPlans.Gateway.CommandCode && !CommandCodeSupports(model, p.Protocol)) continue;
                var id = model.GetString(p.Protocol == "gemini" ? "name" : p.Protocol == "ollama" ? "model" : "id").Replace("models/", "");
                if (id.Length > 0)
                {
                    var metadata = (JsonObject)model!.DeepClone();
                    metadata["id"] = id;
                    metadata["name"] = model.GetString("displayName", model.GetString("display_name", model.GetString("name", id)));
                    if (gateway == ProviderPlans.Gateway.OpenCodeGo)
                    {
                        var limit = metadata.GetObject("limit");
                        if (limit["context"] is { } context) metadata["context_length"] = context.DeepClone();
                        if (limit["output"] is { } output) metadata["max_output_tokens"] = output.DeepClone();
                        if (metadata.GetObject("modalities")["input"] is { } input) metadata["input_modalities"] = input.DeepClone();
                    }
                    result.AddNode(metadata);
                }
            }
            var next = response.GetString("nextPageToken");
            url = next.Length > 0 ? catalogUrl + "?pageToken=" + Uri.EscapeDataString(next) :
                p.Protocol == "anthropic" && response.GetBoolean("has_more") && response.GetString("last_id") is { Length: > 0 } cursor
                    ? catalogUrl + "?after_id=" + Uri.EscapeDataString(cursor) : "";
        } while (url.Length > 0);
        if (result.Count == 0 && plan?.Model is { Length: > 0 } fallback)
            result.AddNode(new JsonObject { ["id"] = fallback, ["name"] = fallback });
        return result;
    }

    private static bool CommandCodeSupports(JsonNode? model, string protocol)
    {
        var path = protocol switch { "anthropic" => "/messages", "responses" => "/responses", _ => "/chat/completions" };
        return model.GetArray("supported_endpoints").Select(value => JsonValues.GetText(value))
            .Any(endpoint => endpoint == path || endpoint == "/v1" + path);
    }

    internal async Task<JsonArray> EffortLevels(Provider p, CancellationToken ct)
    {
        if (p.Protocol != "ollama") return new();
        var response = await port.Request(new()
        {
            ["url"] = p.Url + "/api/show",
            ["method"] = "POST",
            ["headers"] = Headers(p, ProviderPlans.Gateway.Standard),
            ["body"] = JsonValues.Serialize(new JsonObject { ["model"] = p.Model }),
            ["stream"] = false
        }, ct);
        return (JsonArray)response.GetObject("thinking").GetArray("values").DeepClone();
    }
    internal HostPort Port => port;

    internal JsonObject Request(Provider provider, JsonArray messages, AgentSkill.Turn? skills, string? openCodeSession = null)
    {
        var (path, body) = Build(provider, messages, skills?.Definitions() ?? new(), includeMediaData: true);
        var gateway = ProviderPlans.Find(provider)?.Wire ?? ProviderPlans.Gateway.Standard;
        var headers = Headers(provider, gateway);
        if (gateway == ProviderPlans.Gateway.OpenCodeGo && openCodeSession is not null)
            headers["x-opencode-session"] = openCodeSession;
        return new() { ["url"] = provider.Url + path, ["method"] = "POST", ["headers"] = headers,
            ["body"] = JsonValues.Serialize(body), ["stream"] = true };
    }

    private (string Path, JsonObject Body) Build(Provider p, JsonArray messages, JsonArray tools, bool includeMediaData)
    {
        Dictionary<JsonNode, List<MessageAttachments.Media>> media = attachmentRoot is null ? new() :
            new MessageAttachments(attachmentRoot).Resolve(messages, p, includeData: includeMediaData);
        if (p.Protocol == "responses")
            return ("/responses", AgentSession.BuildResponses(p, messages, tools, media));
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
                    else if (MessageAttachments.Text(message) is { Length: > 0 } text)
                    {
                        parts.AddNode(new JsonObject { ["type"] = "text", ["text"] = text });
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
            if (p.ReasoningEffort.Length > 0)
                body["output_config"] = new JsonObject { ["effort"] = p.ReasoningEffort };
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
                    else if (MessageAttachments.Text(m) is { Length: > 0 } text)
                    {
                        parts.AddNode(new JsonObject { ["text"] = text });
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

            if (p.Thinking || p.ReasoningEffort.Length > 0)
            {
                var thinking = new JsonObject { ["includeThoughts"] = true };
                if (p.ReasoningEffort.Length > 0)
                    thinking["thinkingLevel"] = p.ReasoningEffort.ToUpperInvariant();
                body.GetObject("generationConfig")["thinkingConfig"] = thinking;
            }

            return ($"/models/{Uri.EscapeDataString(p.Model)}:streamGenerateContent?alt=sse", body);
        }
        var gateway = ProviderPlans.Find(p)?.Wire ?? ProviderPlans.Gateway.Standard;
        var normalized = new JsonArray();
        foreach (var message in messages)
        {
            var clean = new JsonObject { ["role"] = message.GetString("role"), ["content"] = MessageAttachments.Text(message) };
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

            if (message?["reasoning_content"] is not null &&
                ((gateway == ProviderPlans.Gateway.KimiCode && message.GetArray("tool_calls").Count > 0) ||
                p.Url.Contains("deepseek", StringComparison.OrdinalIgnoreCase) || p.Model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)))
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
            open["think"] = p.ReasoningEffort.Length > 0 ? JsonValue.Create(p.ReasoningEffort) : JsonValue.Create(p.Thinking);
            open["options"] = new JsonObject { ["num_ctx"] = p.ContextWindow, ["num_predict"] = p.MaxOutput };

            return ("/api/chat", open);
        }
        var effort = p.ReasoningEffort;
        if (p.Url.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            var thinking = effort.Length == 0 ? p.Thinking : effort != "none";
            open["thinking"] = new JsonObject { ["type"] = thinking ? "enabled" : "disabled" };
        }
        if (effort.Length > 0)
        {
            if (Uri.TryCreate(p.Url, UriKind.Absolute, out var reasoningEndpoint) && reasoningEndpoint.Host == "openrouter.ai")
                open["reasoning"] = new JsonObject { ["effort"] = effort };
            else
                open["reasoning_effort"] = effort;
        }
        var modelName = p.Model.AsSpan(p.Model.LastIndexOf('/') + 1);
        var modernOpenAi = modelName.StartsWith("gpt-5", StringComparison.Ordinal) || modelName.StartsWith("gpt-6", StringComparison.Ordinal) ||
            (modelName.Length > 1 && modelName[0] == 'o' && char.IsAsciiDigit(modelName[1]));
        open[modernOpenAi || gateway == ProviderPlans.Gateway.MiniMax ? "max_completion_tokens" : "max_tokens"] = p.MaxOutput;
        if (gateway == ProviderPlans.Gateway.MiniMax) open["reasoning_split"] = true;
        if (gateway == ProviderPlans.Gateway.CommandCode || (Uri.TryCreate(p.Url, UriKind.Absolute, out var endpoint) && endpoint.Host == "api.openai.com"))
            open["stream_options"] = new JsonObject { ["include_usage"] = true };

        return ("/chat/completions", open);
    }
}
