using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed record Provider(string Id, string Name, string Protocol, string Url, string Key, string Model, bool Thinking = false,
    int ContextWindow = 128000, int MaxOutput = 8192, int CompactAt = 0, string ReasoningEffort = "",
    int? CompactAtPercent = null, bool BackgroundCompaction = true, int BackgroundCompactAtPercent = 70,
    string[]? InputModalities = null)
{
    public static Provider From(JsonObject p) => new(
        p.GetString("id", "default"), p.GetString("name", "Custom"), p.GetString("protocol", "openai"), p.GetString("url").TrimEnd('/'),
        p.GetString("key"), p.GetString("model"), p.GetBoolean("thinking"),
        Math.Clamp(p.GetInt32("context_window", 128000), 2048, 10000000),
        Math.Clamp(p.GetInt32("max_output", 8192), 256, 1000000),
        Math.Max(0, p.GetInt32("compact_at")), p.GetString("reasoning_effort"),
        p.GetInt32("compact_at_percent") is >= 1 and <= 100 and var percent ? percent : null,
        p.GetBoolean("background_compaction", true), Math.Clamp(p.GetInt32("background_compact_at_percent", 70), 1, 99),
        (p["input_modalities"] ?? p.GetObject("architecture")["input_modalities"]) is JsonArray modalities
            ? modalities.Select(item => JsonValues.GetText(item).ToLowerInvariant()).ToArray() : null);
    public static JsonArray Catalog()
    {
        var items = new JsonArray(
        Item("OpenAI", "responses", "https://api.openai.com/v1"), Item("OpenAI Chat Completions", "openai", "https://api.openai.com/v1"), Item("Anthropic", "anthropic", "https://api.anthropic.com/v1"),
        Item("Google Gemini", "gemini", "https://generativelanguage.googleapis.com/v1beta"), Item("DeepSeek", "openai", "https://api.deepseek.com"),
        Item("OpenRouter", "openai", "https://openrouter.ai/api/v1"), Item("Groq", "openai", "https://api.groq.com/openai/v1"),
        Item("Mistral", "openai", "https://api.mistral.ai/v1"), Item("xAI", "openai", "https://api.x.ai/v1"),
        Item("Together", "openai", "https://api.together.xyz/v1"), Item("Fireworks", "openai", "https://api.fireworks.ai/inference/v1"),
        Item("SiliconFlow", "openai", "https://api.siliconflow.cn/v1"), Item("Alibaba Cloud", "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        Item("Moonshot", "openai", "https://api.moonshot.cn/v1"), Item("Z.ai", "openai", "https://api.z.ai/api/paas/v4"),
        Item("Cerebras", "openai", "https://api.cerebras.ai/v1"), Item("SambaNova", "openai", "https://api.sambanova.ai/v1"),
        Item("Perplexity", "openai", "https://api.perplexity.ai"), Item("NVIDIA", "openai", "https://integrate.api.nvidia.com/v1"),
        Item("GitHub Models", "openai", "https://models.github.ai/inference"), Item("Ollama", "ollama", "http://localhost:11434"),
        Item("LM Studio", "openai", "http://localhost:1234/v1"));
        foreach (var plan in ProviderPlans.Catalog()) items.AddNode(plan);
        items.AddNode(Item("Custom", "openai", ""));
        return items;
    }
    static JsonObject Item(string name, string protocol, string url) => new() { ["name"] = name, ["protocol"] = protocol, ["url"] = url };
}
