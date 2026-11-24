using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    internal static void ValidateStateDeclarations(JsonObject manifest, bool signed)
    {
        var entries = manifest.GetArray("stateEntries");
        var cards = manifest.GetArray("cards");
        if (!signed && (entries.Count > 0 || cards.Count > 0))
            throw new InvalidDataException("Structured state and cards require a signed plugin.");
        if (entries.Count > 32 || cards.Count > 24) throw new InvalidDataException("Too many state entries or cards.");
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject || !entryIds.Add(PluginPackage.Identifier(entry.GetString("key"))) ||
                entry.GetString("scope") is not ("session" or "chat" or "preset" or "room") ||
                entry.GetString("description").Length is < 1 or > 512 || entry["initial"] is not JsonObject)
                throw new InvalidDataException("A state entry needs a unique key, scope, description and initial JSON object.");
            if (entry.GetString("onChange") is { Length: > 0 } hook) PluginActions.BindRequired(manifest, hook);
        }
        var cardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (!cardIds.Add(PluginPackage.Identifier(card.GetString("id"))) ||
                card.GetString("title").Length is < 1 or > 120 ||
                card.GetString("program").Length is < 1 or > 32000 ||
                (card.GetString("state") is { Length: > 0 } key && !entryIds.Contains(key)))
                throw new InvalidDataException("A card needs a unique ID, title, a valid-size OpenUI program and an optional declared state key.");
            if (card is JsonObject declaration && declaration.TryGetPropertyValue("labels", out var value))
            {
                if (value is not JsonObject { Count: > 0 and <= 48 } labels || labels.Any(label =>
                    label.Key.Length is < 1 or > 48 || !char.IsAsciiLetter(label.Key[0]) ||
                    label.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
                    label.Value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) ||
                    string.IsNullOrWhiteSpace(text) || text.Length > 240))
                    throw new InvalidDataException("Card labels need 1–48 identifier keys with nonblank text of up to 240 characters.");
            }
        }
        foreach (var skill in manifest.GetArray("skills"))
        {
            ValidateReferences(skill, "stateEntries", entryIds);
            ValidateReferences(skill, "cards", cardIds);
        }
    }

    private static void ValidateReferences(JsonNode? skill, string name, HashSet<string> known)
    {
        var values = skill.GetArray(name).Select(item => JsonValues.GetText(item)).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length || values.Any(id => !known.Contains(id)))
            throw new InvalidDataException("A skill referenced an unknown or duplicate " + name + " ID.");
    }

    private JsonObject State(PluginPackage package, JsonObject environment, JsonObject args, bool write)
    {
        lock (packageGate) return StateInCurrentPackage(package, environment, args, write);
    }

    private JsonObject StateInCurrentPackage(PluginPackage package, JsonObject environment, JsonObject args, bool write)
    {
        RequireScope(package, environment.GetString("preset"));
        var key = args.GetString("key");
        var declaration = FindDeclaration(package, "stateEntries", "key", key);
        var scope = declaration.GetString("scope");
        using var db = new MemoryDb(ScopedDatabase("structured", package.StorageKey, environment, scope));
        var row = db.Query("SELECT content,revision FROM entries WHERE key=?", key).FirstOrDefault();
        var previous = row is null ? (JsonObject)declaration.GetObject("initial").DeepClone() : JsonValues.ParseObject(row["content"]);
        var revision = row is null ? 0 : int.Parse(row["revision"], System.Globalization.CultureInfo.InvariantCulture);
        var value = previous;
        if (write)
        {
            if (args.GetInt32("expected_revision", -1) != revision)
                throw new InvalidOperationException("State changed. Read the entry and use its current revision.");
            if (args["value"] is not JsonObject next || JsonValues.Serialize(next).Length > 32768)
                throw new ArgumentException("State value must be a JSON object up to 32,768 characters.");
            if (!JsonNode.DeepEquals(previous, next))
            {
                value = (JsonObject)next.DeepClone();
                var changed = new JsonObject { ["name"] = "state.changed", ["key"] = key,
                    ["scope"] = scope, ["revision"] = checked(revision + 1), ["value"] = value.DeepClone(),
                    ["previous"] = previous, ["package"] = package.PackageName, ["signer"] = package.Signer };
                var nextEnvironment = (JsonObject)environment.DeepClone();
                var handler = package.Bind(declaration.GetString("onChange"));
                // Reserve a queue slot before committing so backpressure cannot lose a revision.
                var committed = handler.Length == 0 ? null : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (committed is not null && !EnqueueRoute(package, handler, changed, nextEnvironment, committed.Task))
                    throw new InvalidOperationException("Plugin event queue is full. The state was not changed.");
                try
                {
                    revision = db.Write(key, JsonValues.Serialize(value), revision).GetInt32("revision");
                    committed?.SetResult(true);
                }
                finally { committed?.TrySetResult(false); }
                stateChanged?.Invoke(changed);
            }
        }
        return new() { ["key"] = key, ["value"] = value, ["revision"] = revision, ["scope"] = scope };
    }

    private static JsonNode FindDeclaration(PluginPackage package, string kind, string key, string id) =>
        package.Manifest.GetArray(kind).FirstOrDefault(item => item.GetString(key) == id) ??
        throw new ArgumentException("The plugin does not declare this " + kind + " entry.");

    internal const string StateProjectionPrefix = "_state/";
    private static string ProjectionKey(PluginPackage package, JsonObject state, JsonObject environment) =>
        StateProjectionPrefix + package.Id + "/" + state.GetString("key") +
        (state.GetString("scope") == "chat" && environment.GetBoolean("_ensemble") ? "/" + environment.GetString("chat") : "");

    internal JsonObject? ReadStateProjection(string agent, JsonObject environment, string key, Func<string, bool>? canReadChat)
    {
        if (!key.StartsWith(StateProjectionPrefix, StringComparison.Ordinal)) return null;
        var parts = key[StateProjectionPrefix.Length..].Split('/');
        if (parts.Length is not (2 or 3)) return null;
        var entry = parts[1];
        var package = Volatile.Read(ref packages).FirstOrDefault(item => item.Id == parts[0]);
        if (package is null || !Available(package, environment.GetString("preset")) ||
            !package.Manifest.GetArray("skills").Any(skill =>
                skill.GetArray("agents").Any(role => JsonValues.GetText(role) == agent) &&
                skill.GetArray("stateEntries").Any(item => JsonValues.GetText(item) == entry))) return null;
        var declaration = FindDeclaration(package, "stateEntries", "key", entry);
        var scope = declaration.GetString("scope");
        if (scope == "chat" && environment.GetBoolean("_ensemble"))
        {
            if (parts.Length != 3 || !WorkspaceIds.IsValid(parts[2]) ||
                (canReadChat is not null ? !canReadChat(parts[2]) : parts[2] != environment.GetString("chat"))) return null;
            environment = (JsonObject)environment.DeepClone();
            environment["chat"] = parts[2];
        }
        else if (parts.Length != 2) return null;
        if (DatabaseScope(environment, scope).Length == 0) return null;
        return State(package, environment, new() { ["key"] = entry }, false);
    }

    private JsonObject Card(PluginPackage package, string id, JsonObject environment, string? locale = null)
    {
        var card = FindDeclaration(package, "cards", "id", id);
        var state = card.GetString("state") is { Length: > 0 } key ? State(package, environment, new() { ["key"] = key }, false) : new JsonObject();
        locale ??= environment.GetString("locale");
        var title = package.Localization?.Text(card, "title", locale) ?? card.GetString("title");
        var data = JsonValues.Clone(state["value"]) as JsonObject;
        var labels = CardLocaleState(package, card, locale);
        if (labels.Count > 0)
        {
            data ??= new JsonObject();
            foreach (var entry in labels) data[entry.Key] = JsonValues.Clone(entry.Value);
        }
        return new() { ["open_ui"] = card.GetString("program"), ["open_ui_title"] = title,
            ["open_ui_state"] = data, ["open_ui_revision"] = state.GetInt32("revision") };
    }

    private static JsonObject CardLocaleState(PluginPackage package, JsonNode card, string? locale)
    {
        if (card["labels"] is not JsonObject source) return new();
        var labels = new JsonObject();
        foreach (var label in source)
            labels[label.Key] = package.Localization?.Text(source, label.Key, locale) ?? JsonValues.GetText(label.Value);
        return new()
        {
            ["labels"] = labels,
            ["locale"] = locale ?? "",
            ["title"] = package.Localization?.Text(card, "title", locale) ?? card.GetString("title"),
        };
    }

    internal JsonObject ReadCard(JsonObject reference, string? locale = null)
    {
        var package = Volatile.Read(ref packages).FirstOrDefault(item =>
            item.PackageName == PluginUrls.CanonicalPackage(reference.GetString("package")) && item.Signer == reference.GetString("signer"))
            ?? throw new UnauthorizedAccessException("The plugin card is unavailable.");
        var context = reference.GetObject("environment");
        RequireScope(package, context.GetString("preset"));
        return Card(package, reference.GetString("id"), context, locale ?? context.GetString("locale"));
    }

    private void AddStateTools(PluginPackage package, JsonNode? declaration, JsonArray tools, string stateTool, string? cardTool, string? locale)
    {
        if (declaration.GetArray("stateEntries") is { Count: > 0 } entries)
        {
            var instructions = entries.Select(key => FindDeclaration(package, "stateEntries", "key", JsonValues.GetText(key)))
                .Select(entry => entry.GetString("key") + " (" + entry.GetString("scope") + "): " +
                    (package.Localization?.Text(entry, "description", locale) ?? entry.GetString("description")));
            tools.AddNode(Tools.Define(stateTool, "Read read-only state and its session DB key. Use db_watch with the returned DB key for updates. Change state through the plugin's own tools. " + string.Join(" ", instructions), new()
            {
                ["key"] = new JsonObject { ["type"] = "string", ["enum"] = entries.DeepClone() },
            }, "key"));
        }
        if (cardTool is not null && declaration.GetArray("cards") is { Count: > 0 } cards)
            tools.AddNode(Tools.Define(cardTool, "Show a reusable OpenUI card from this plugin in your reply. The card uses the current stored state.",
                new() { ["id"] = new JsonObject { ["type"] = "string", ["enum"] = cards.DeepClone() } }, "id"));
    }
}
