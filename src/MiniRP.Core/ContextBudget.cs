using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Measures the model prompt, after the protocol adapter has removed storage-only fields.</summary>
internal sealed class ContextBudget(Provider provider)
{
    public int Capacity { get; } = Math.Max(1, provider.ContextWindow - provider.MaxOutput);
    public int Hard { get; } = Limit(provider, provider.CompactAtPercent is { } percent
        ? provider.ContextWindow * (long)percent / 100
        : provider.CompactAt > 0 ? provider.CompactAt : provider.ContextWindow * 80L / 100);
    public int Soft { get; } = Limit(provider, provider.ContextWindow * (long)provider.BackgroundCompactAtPercent / 100);
    public bool Background => provider.BackgroundCompaction && Soft < Hard;

    private static int Limit(Provider provider, long tokens) =>
        (int)Math.Clamp(tokens, 1, Math.Max(1, provider.ContextWindow - provider.MaxOutput));

    public static int Estimate(JsonObject body)
    {
        var count = 4;
        foreach (var key in new[] { "system", "systemInstruction", "instructions", "messages", "contents", "input" })
            count += Count(body[key]);
        return count + Count(body["tools"], inSchema: true);
    }

    private static int Count(JsonNode? node, bool inSchema = false) => node switch
    {
        JsonObject obj => (!inSchema ? Media(obj) : null) ?? (3 + obj.Sum(pair => pair.Key switch
        {
            // Provider bookkeeping and opaque replay handles have no text representation in the prompt.
            "id" or "call_id" or "tool_call_id" or "tool_use_id" or "encrypted_content" or
                "signature" or "thoughtSignature" or "status" when !inSchema => 0,
            "images" when !inSchema && pair.Value is JsonArray images => images.Count * 1024,
            _ => Text(pair.Key) + Count(pair.Value, inSchema || pair.Key is "input" or "args" or "response")
        })),
        JsonArray items => items.Sum(item => Count(item, inSchema)),
        JsonValue value => Text(JsonValues.GetText(value)),
        _ => 0
    };

    private static int? Media(JsonObject part)
    {
        if (part["inlineData"] is JsonObject inline)
            return inline.GetString("mimeType").StartsWith("image/", StringComparison.Ordinal) ? 1024 : 4096;
        return part.GetString("type") switch
        {
            "input_image" or "image_url" or "image" => 1024,
            "document" or "file" or "input_file" or "input_audio" => 4096,
            _ => null
        };
    }

    // A local fallback for models that do not expose token counts. Count decoded UTF-8
    // rather than escaped JSON or base64.
    internal static int Text(string text) => (Encoding.UTF8.GetByteCount(text) + 2) / 3;
}

/// <summary>Reuses provider usage only while the priced prefix and routing still match.</summary>
internal sealed class ContextUsage(MemoryDb db)
{
    private const string Key = "_context/usage-anchor";
    private JsonObject? anchor = Read(db);

    private static JsonObject? Read(MemoryDb db)
    {
        try { return JsonNode.Parse(db.Get(Key) ?? "null") as JsonObject; }
        catch (JsonException) { return null; }
    }

    public int Forecast(ModelClient model, JsonArray messages, JsonArray tools, int estimate)
    {
        if (anchor is null) return estimate;
        var count = anchor.GetInt32("count");
        if (count <= 0 || count > messages.Count || anchor.GetString("route") != Route(model.SelectedProvider) ||
            // New attachments can displace older files from the adapter's upload budget.
            messages.Skip(count).Any(message => message.GetArray("attachments").Count > 0) ||
            anchor.GetString("prefix") != Prefix(messages, tools, count))
            return estimate;
        return (int)Math.Min(int.MaxValue, (long)anchor.GetInt32("actual") + Math.Max(0, estimate - anchor.GetInt32("estimate")));
    }

    public void Observe(ModelClient model, JsonArray messages, JsonArray tools, int estimate, int actual)
    {
        anchor = new()
        {
            ["route"] = Route(model.SelectedProvider), ["prefix"] = Prefix(messages, tools, messages.Count),
            ["count"] = messages.Count, ["estimate"] = estimate, ["actual"] = actual
        };
    }

    public void Invalidate() => anchor = null;
    public (string, string) Record() => (Key, JsonValues.Serialize(anchor));

    internal static string Route(Provider provider) => Fingerprint(JsonValues.Serialize(new JsonArray(provider.Id,
        provider.Protocol, provider.Url, provider.Model, provider.Thinking, provider.ReasoningEffort,
        provider.ContextWindow, provider.MaxOutput)));

    internal static string Fingerprint(string text) => Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(text)));

    private static string Prefix(JsonArray messages, JsonArray tools, int count) =>
        Fingerprint(JsonValues.Serialize(tools) + "\n" +
            JsonValues.Serialize(new JsonArray(messages.Take(count).Select(JsonValues.Clone).ToArray())));
}
