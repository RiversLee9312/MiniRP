using System.Globalization;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class EnvironmentSettings(string root, DesktopSecretStorage? secretStorage = null)
{
    private const string Prefix = "MINIRP_";
    private const string LegacyPrefix = "MINICHAT_";
    private static readonly Dictionary<string, string> ResearchKeys = new(StringComparer.Ordinal)
    {
        ["deepseek"] = "MINIRP_SEARCH_API_KEY",
        ["tavily"] = "MINIRP_TAVILY_API_KEY",
        ["exa"] = "MINIRP_EXA_API_KEY",
        ["brave"] = "MINIRP_BRAVE_SEARCH_API_KEY"
    };
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly DesktopSecretStorage secrets = secretStorage ?? new(root);
    private readonly object gate = new();
    private string FileName => Path.Combine(root, ".env");
    private string DesktopFile => Path.Combine(root, ".minirp-desktop.json");
    private JsonObject desktop = new();

    public JsonObject Desktop()
    {
        lock (gate) return (JsonObject)desktop.DeepClone();
    }

    public JsonObject DesktopForSettings()
    {
        var current = Desktop();
        var configured = new JsonArray();
        foreach (var service in ResearchKeys.Keys)
            if (!string.IsNullOrWhiteSpace(secrets.Get(PluginSecretNames.Base, service) ?? ResearchDefault(service)))
                configured.Add(service);
        var defaults = LegacyConfiguration();
        current["environment"] = JsonValues.Clone(defaults["provider"]);
        var research = defaults.GetObject("settings");
        research.Remove("_research_secret_storage");
        research["configured_keys"] = configured;
        current["default_research"] = JsonValues.Clone(research);
        return current;
    }

    public void SaveDesktop(JsonObject data)
    {
        lock (gate)
        {
            if (data["research"] is JsonObject research) RemoveResearchSecrets(research, overwrite: true);
            data["preferences"] = JsonValues.Clone(desktop["preferences"]);
            AtomicFile.Write(DesktopFile, JsonValues.Serialize(data));
            desktop = (JsonObject)data.DeepClone();
        }
    }

    private bool RemoveResearchSecrets(JsonObject research, bool overwrite)
    {
        bool hadSecrets = research.ContainsKey("research_keys") || research.ContainsKey("search_key");
        var keys = research.GetObject("research_keys");
        foreach (var service in ResearchKeys.Keys)
        {
            var value = keys[service] ?? (service == "deepseek" ? research["search_key"] : null);
            if (value is JsonValue text && text.TryGetValue<string>(out var secret) &&
                (overwrite || secrets.Get(PluginSecretNames.Base, service) is null))
                secrets.Set(PluginSecretNames.Base, service, secret);
        }
        research.Remove("research_keys");
        research.Remove("search_key");
        return hadSecrets;
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
                var service = ResearchKeys.FirstOrDefault(item => item.Value == key).Key;
                if (service is not null) return secrets.Get(PluginSecretNames.Base, service) ?? ResearchDefault(service) ?? "";
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
                var service = ResearchKeys.FirstOrDefault(item => item.Value == key).Key;
                if (service is not null) secrets.Set(PluginSecretNames.Base, service, value);
                else values[key] = value;
            }
        }
    }

    private string? Read(string key) => values.GetValueOrDefault(key) ?? Environment.GetEnvironmentVariable(key);

    public string? ResearchDefault(string service)
    {
        if (!ResearchKeys.TryGetValue(service, out var key)) return null;
        var value = Environment.GetEnvironmentVariable(key) ?? Environment.GetEnvironmentVariable(LegacyPrefix + key[Prefix.Length..]);
        if (!string.IsNullOrWhiteSpace(value) || service != "deepseek") return value;
        var url = this["DEEPSEEK_BASE_URL"];
        return url.Length == 0 || url.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ? this["DEEPSEEK_API_KEY"] : null;
    }

    public void Load()
    {
        if (File.Exists(DesktopFile)) desktop = JsonValues.ParseObject(File.ReadAllText(DesktopFile));
        var lines = File.Exists(FileName) ? File.ReadAllLines(FileName) : [];
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (trimmed.StartsWith('#') || separator <= 0)
            {
                continue;
            }
            values[trimmed[..separator]] = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
        }
        if (desktop["research"] is JsonObject research && RemoveResearchSecrets(research, overwrite: false))
            AtomicFile.Write(DesktopFile, JsonValues.Serialize(desktop));
        var migrated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (service, key) in ResearchKeys)
        {
            var legacy = LegacyPrefix + key[Prefix.Length..];
            if (values.TryGetValue(key, out var value) || values.TryGetValue(legacy, out value))
            {
                if (secrets.Get(PluginSecretNames.Base, service) is null) secrets.Set(PluginSecretNames.Base, service, value);
                values.Remove(key);
                values.Remove(legacy);
                migrated.Add(key);
                migrated.Add(legacy);
            }
        }
        if (migrated.Count > 0)
            AtomicFile.Write(FileName, string.Join('\n', lines.Where(line =>
            {
                var entry = line.Trim();
                var separator = entry.IndexOf('=');
                return separator <= 0 || !migrated.Contains(entry[..separator]);
            })) + "\n");
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
        if (!web)
        {
            AddDescription("image");
            AddDescription("audio");
        }
        var routing = configured.GetObject("agents");
        foreach (var group in routing)
        {
            if (!web && group.Key is "image_description" or "audio_description") continue;
            var provider = group.Value.GetString("provider");
            var model = group.Value.GetString("model");
            var source = provider == "environment" ? fallback : providers.GetValueOrDefault(provider);
            if (source is null || model.Length == 0) continue;
            var modelOptions = source.GetObject("model_options")[model] as JsonObject;
            var target = (JsonObject)source.DeepClone();
            target.Remove("model_options");
            target["model"] = model;
            if (modelOptions is not null)
                foreach (var pair in modelOptions) target[pair.Key] = JsonValues.Clone(pair.Value);
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
        if (web) settings["_web_transcripts"] = true;
        foreach (var option in configured.GetObject("research")) settings[option.Key] = JsonValues.Clone(option.Value);
        settings["_research_secret_storage"] = true;
        var prompt = Path.Combine(AppContext.BaseDirectory, "wwwroot", "openui", "prompt.txt");
        if (web && File.Exists(prompt)) settings["android_open_ui"] = File.ReadAllText(prompt);
        return command;

        void AddDescription(string kind)
        {
            var prefix = "MINIRP_" + kind.ToUpperInvariant() + "_DESCRIPTION_";
            var model = this[prefix + "MODEL"].Trim();
            if (model.Length == 0) return;
            var url = this[prefix + "URL"].Trim();
            var protocol = this[prefix + "PROTOCOL"].Trim();
            var key = this[prefix + "KEY"];
            agents[kind + "_description"] = new JsonObject
            {
                ["id"] = "environment-" + kind + "-description", ["model"] = model,
                ["url"] = url.Length == 0 ? fallback.GetString("url") : url,
                ["protocol"] = protocol.Length == 0 ? fallback.GetString("protocol") : protocol,
                ["key"] = url.Length == 0 && key.Length == 0 ? fallback.GetString("key") : key
            };
        }
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
            ["describe_images"] = this["MINIRP_DESCRIBE_IMAGES"].Equals("yes", StringComparison.OrdinalIgnoreCase),
            ["describe_audio"] = this["MINIRP_DESCRIBE_AUDIO"].Equals("yes", StringComparison.OrdinalIgnoreCase),
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
            ["_research_secret_storage"] = true,
            ["search_region"] = RegionInfo.CurrentRegion.TwoLetterISORegionName,
            ["search_url"] = "https://api.deepseek.com"
        }
    };
}
