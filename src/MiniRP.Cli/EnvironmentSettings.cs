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
    private string DesktopFile => Path.Combine(root, ".minirp-desktop.json");
    private JsonObject desktop = new();

    public JsonObject Desktop()
    {
        lock (gate) return (JsonObject)desktop.DeepClone();
    }

    public void SaveDesktop(JsonObject data)
    {
        lock (gate)
        {
            data["preferences"] = JsonValues.Clone(desktop["preferences"]);
            AtomicFile.Write(DesktopFile, JsonValues.Serialize(data));
            desktop = (JsonObject)data.DeepClone();
        }
    }

    public void SavePreferences(JsonObject preferences)
    {
        lock (gate)
        {
            var updated = (JsonObject)desktop.DeepClone();
            updated["preferences"] = preferences;
            AtomicFile.Write(DesktopFile, JsonValues.Serialize(updated));
            desktop = updated;
        }
    }
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
        if (File.Exists(DesktopFile)) desktop = JsonValues.ParseObject(File.ReadAllText(DesktopFile));
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

    public JsonObject Configure(bool web = false)
    {
        var command = LegacyConfiguration();
        var configured = Desktop();
        var providers = configured.GetArray("providers").OfType<JsonObject>()
            .Where(provider => provider.GetString("id").Length > 0)
            .ToDictionary(provider => provider.GetString("id"), StringComparer.Ordinal);
        var fallback = command.GetObject("provider");
        var agents = new JsonObject();
        var routing = configured.GetObject("agents");
        foreach (var group in routing)
        {
            var provider = group.Value.GetString("provider");
            var model = group.Value.GetString("model");
            var source = provider == "environment" ? fallback : providers.GetValueOrDefault(provider);
            if (source is null || model.Length == 0) continue;
            var target = (JsonObject)source.DeepClone();
            target["model"] = model;
            if (group.Value?["options"] is JsonObject options)
                foreach (var pair in options) target[pair.Key] = JsonValues.Clone(pair.Value);
            if (group.Key == "default" || group.Key == "chat" && routing["default"] is null)
            {
                command["provider"] = target.DeepClone();
                if (group.Key == "chat") agents[group.Key] = target;
            }
            else agents[group.Key] = target;
        }
        command["agents"] = agents;
        var settings = command.GetObject("settings");
        foreach (var option in configured.GetObject("research")) settings[option.Key] = JsonValues.Clone(option.Value);
        var prompt = Path.Combine(AppContext.BaseDirectory, "wwwroot", "openui", "prompt.txt");
        if (web && File.Exists(prompt)) settings["android_open_ui"] = File.ReadAllText(prompt);
        return command;
    }

    public JsonObject LegacyConfiguration() => new()
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
            ["search_provider"] = this["MINIRP_SEARCH_PROVIDER"].Length > 0 ? this["MINIRP_SEARCH_PROVIDER"].Trim().ToLowerInvariant() : "deepseek",
            ["fetch_provider"] = this["MINIRP_FETCH_PROVIDER"].Length > 0 ? this["MINIRP_FETCH_PROVIDER"].Trim().ToLowerInvariant() : "deepseek",
            ["research_keys"] = new JsonObject
            {
                ["tavily"] = this["MINIRP_TAVILY_API_KEY"],
                ["exa"] = this["MINIRP_EXA_API_KEY"],
                ["brave"] = this["MINIRP_BRAVE_SEARCH_API_KEY"]
            },
            ["search_region"] = RegionInfo.CurrentRegion.TwoLetterISORegionName,
            ["search_url"] = "https://api.deepseek.com",
            ["search_key"] = this["MINIRP_SEARCH_API_KEY"].Length > 0 ? this["MINIRP_SEARCH_API_KEY"]
                : (this["DEEPSEEK_BASE_URL"].Length == 0 || this["DEEPSEEK_BASE_URL"].Contains("deepseek", StringComparison.OrdinalIgnoreCase)) ? this["DEEPSEEK_API_KEY"] : ""
        }
    };
}
