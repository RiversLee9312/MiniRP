using System.Globalization;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class EnvironmentSettings(string root)
{
    private const string Prefix = "MINIRP_";
    private const string LegacyPrefix = "MINICHAT_";
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private string FileName => Path.Combine(root, ".env");
    public string this[string key]
    {
        get
        {
            lock (gate)
            {
                var configured = Read(key);
                if (configured is not null) return configured;
                return key.StartsWith(Prefix, StringComparison.Ordinal)
                    ? Read(LegacyPrefix + key[Prefix.Length..]) ?? ""
                    : "";
            }
        }
        set
        {
            lock (gate)
            {
                values[key] = value;
            }
        }
    }

    private string? Read(string key) => values.GetValueOrDefault(key) ?? Environment.GetEnvironmentVariable(key);

    public void Load()
    {
        if (!File.Exists(FileName))
        {
            return;
        }
        foreach (var line in File.ReadLines(FileName))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (trimmed.StartsWith('#') || separator <= 0)
            {
                continue;
            }
            values[trimmed[..separator]] = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
        }
    }

    public void Save()
    {
        // Keep comments and unrelated values when editing the existing .env.
        var lines = File.Exists(FileName) ? File.ReadAllLines(FileName).ToList() : [];
        KeyValuePair<string, string>[] snapshot;
        lock (gate)
        {
            snapshot = values.ToArray();
        }

        foreach (var item in snapshot)
        {
            if (item.Value.Contains('\n') || item.Value.Contains('\r'))
            {
                throw new ArgumentException("Configuration values must be a single line.");
            }
            var index = lines.FindIndex(line => line.Split('=', 2)[0].Trim() == item.Key);
            var text = item.Key + "=" + item.Value;
            if (index >= 0)
            {
                lines[index] = text;
            }
            else
            {
                lines.Add(text);
            }
        }
        AtomicFile.Write(FileName, string.Join('\n', lines) + "\n");
    }

    public JsonObject Configure() => new()
    {
        ["op"] = "configure",
        ["provider"] = new JsonObject
        {
            ["id"] = "environment",
            ["name"] = "Environment",
            ["protocol"] = this["MINIRP_PROVIDER_PROTOCOL"].Length > 0 ? this["MINIRP_PROVIDER_PROTOCOL"] : "openai",
            ["url"] = this["DEEPSEEK_BASE_URL"].Length > 0 ? this["DEEPSEEK_BASE_URL"] : "https://api.deepseek.com",
            ["key"] = this["DEEPSEEK_API_KEY"],
            ["model"] = this["DEEPSEEK_MODEL"].Length > 0 ? this["DEEPSEEK_MODEL"] : "deepseek-flash",
            ["thinking"] = this["DEEPSEEK_THINKING"] == "enabled",
            ["context_window"] = int.TryParse(this["DEEPSEEK_CONTEXT_WINDOW"], out var context) ? context : 128000,
            ["max_output"] = int.TryParse(this["DEEPSEEK_MAX_OUTPUT_TOKENS"], out var output) ? output : 8192,
            ["compact_at"] = int.TryParse(this["DEEPSEEK_COMPACT_AT"], out var threshold) ? threshold : 0,
            ["compact_at_percent"] = int.TryParse(this["DEEPSEEK_COMPACT_AT_PERCENT"], out var percent) ? percent : null,
            ["background_compaction"] = !this["DEEPSEEK_BACKGROUND_COMPACTION"].Equals("disabled", StringComparison.OrdinalIgnoreCase),
            ["background_compact_at_percent"] = int.TryParse(this["DEEPSEEK_BACKGROUND_COMPACT_AT_PERCENT"], out var start) ? start : 70,
            ["reasoning_effort"] = this["DEEPSEEK_REASONING_EFFORT"]
        },
        ["settings"] = new JsonObject
        {
            ["fetch_backend"] = this["MINIRP_FETCH_BACKEND"].Length > 0 ? this["MINIRP_FETCH_BACKEND"] : "api_then_local",
            ["search_region"] = RegionInfo.CurrentRegion.TwoLetterISORegionName,
            ["search_url"] = "https://api.deepseek.com",
            ["search_key"] = this["MINIRP_SEARCH_API_KEY"].Length > 0 ? this["MINIRP_SEARCH_API_KEY"]
                : (this["DEEPSEEK_BASE_URL"].Length == 0 || this["DEEPSEEK_BASE_URL"].Contains("deepseek", StringComparison.OrdinalIgnoreCase)) ? this["DEEPSEEK_API_KEY"] : ""
        }
    };
}
