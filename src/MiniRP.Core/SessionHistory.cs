using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Indexes conversation text independently of the model's working context.</summary>
internal sealed class SessionHistory(MemoryDb db, string agent)
{
    private const string MessagePrefix = "_history/messages/";
    private const string ImportMarker = "_history/imported";
    private const string ArchivePrefix = "_archive/history/";
    internal const string RetrySubmission = "No result submitted. Complete with a submission tool or fail_request.";

    internal static bool IsVisible(JsonNode? message) =>
        !SessionInstructions.IsFrame(message) && !message.GetBoolean("_harness_asset") &&
        !message.GetBoolean("_harness_hidden") && (message.GetString("role") switch
        {
            "user" => message.GetString("content") is not (RetrySubmission or "Continue the active task using the preserved context and DB tools."),
            "assistant" or "system" => true,
            _ => false
        });

    public void Record(string role, string content)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            var key = $"{MessagePrefix}{DateTime.UtcNow.Ticks:D19}/{Guid.NewGuid():N}/{role}";
            db.Write(key, content, internalWrite: true);
        }
    }

    public void Backfill(JsonArray current)
    {
        if (db.Get(ImportMarker) is not null)
        {
            return;
        }

        ImportArchives(ArchivePrefix);
        ImportArchives("_archive/public/history/");
        Import(current);
        db.Write(ImportMarker, "1", internalWrite: true);
    }

    private void ImportArchives(string prefix)
    {
        var after = "";
        do
        {
            var page = db.Call("db_list", new() { ["prefix"] = prefix, ["after"] = after });
            foreach (var entry in page.GetArray("entries"))
            {
                try
                {
                    var archive = JsonNode.Parse(db.Get(entry.GetString("key")) ?? "[]");
                    Import(archive as JsonArray ?? archive.GetArray("history"));
                }
                catch (JsonException)
                {
                    // Old archives remain available in the DB even if they cannot be indexed as messages.
                }
            }
            after = page.GetString("next_after");
        } while (after.Length != 0);
    }

    public void Import(JsonArray messages)
    {
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (!IsVisible(message)) continue;
            var role = message.GetString("role");

            var content = message.GetString("content");
            if (role == "user")
            {
                content = OriginalRequest(content);
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            // Older snapshots overlap. The same text and role only need one recall entry during import.
            // A deduplication key, not a security proof. This stays managed on Android/bionic.
            var digest = Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(content)));
            var key = $"{MessagePrefix}legacy/{digest}/{role}";
            if (db.Get(key) is null)
            {
                db.Write(key, content, internalWrite: true);
            }
        }
    }

    public JsonObject Search(JsonObject args)
    {
        var page = db.Call("db_search", new()
        {
            ["query"] = args.GetString("query").Trim(),
            ["prefix"] = MessagePrefix,
            ["scope"] = "content",
            ["limit"] = Math.Clamp(args.GetInt32("limit", 12), 1, 30),
            ["offset"] = Math.Max(0, args.GetInt32("offset")),
            ["newest_first"] = true
        });
        var matches = new JsonArray();
        foreach (var match in page.GetArray("matches"))
        {
            var id = match.GetString("key")[MessagePrefix.Length..];
            matches.AddNode(new JsonObject { ["id"] = id, ["role"] = RoleOf(id), ["snippet"] = match.GetString("snippet") });
        }
        return new() { ["matches"] = matches, ["next_offset"] = JsonValues.Clone(page["next_offset"]) };
    }

    public JsonObject Recall(JsonObject args)
    {
        var id = args.GetString("id");
        var role = RoleOf(id);
        if (role is not ("user" or "assistant" or "system") || id.Length > 128)
        {
            throw new ArgumentException("Use an id returned by history_search in this session.");
        }
        var entry = db.Call("db_read", new()
        {
            ["key"] = MessagePrefix + id,
            ["start"] = args.GetInt32("start"),
            ["length"] = Math.Clamp(args.GetInt32("length", 6000), 1, 12000)
        });
        entry.Remove("key");
        entry.Remove("revision");
        entry["id"] = id;
        entry["role"] = role;
        return entry;
    }

    private string OriginalRequest(string content)
    {
        var separator = agent switch
        {
            "EnvAgent" when content.StartsWith("Conversation reference:\n", StringComparison.Ordinal) => "\nRequest:\n",
            "SearchAgent" when content.StartsWith("Main conversation reference:\n", StringComparison.Ordinal) => "\nRequest ",
            _ => ""
        };
        if (separator.Length == 0)
        {
            return content;
        }
        var split = content.LastIndexOf(separator, StringComparison.Ordinal);
        return split >= 0 ? content[(split + separator.Length)..] : "";
    }

    private static string RoleOf(string id) => id[(id.LastIndexOf('/') + 1)..];
}
