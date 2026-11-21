using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class MemoryDb : IDisposable
{
    private nint _db;
    private readonly object _gate = new();
    private readonly bool _fts;
    private const string EntrySchema = "entries(key TEXT PRIMARY KEY,content TEXT NOT NULL,revision INTEGER NOT NULL,updated_at TEXT NOT NULL)";
    private const string HistoryMessages = "_history/messages/";
    private const string IndexedKeys = "key GLOB '" + HistoryMessages + "*' OR " +
        "(key NOT GLOB '_context/*' AND key NOT GLOB '_archive/*' AND key NOT GLOB '_history/*')";
    public string Path
    {
        get;
    }

    public MemoryDb(string path) : this(path, readOnly: false) { }

    private MemoryDb(string path, bool readOnly, bool initialize = true)
    {
        Path = path;
        if (!readOnly && path != ":memory:")
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        }

        var vfs = OperatingSystem.IsWindows() ? "win32-longpath" : null;
        Check(Open(NativePath(path), out _db, (readOnly ? 0x00000001 : 0x00000002 | 0x00000004) | 0x00010000, vfs));
        BusyTimeout(_db, 5000);
        if (readOnly || !initialize) return;
        if (path != ":memory:")
        {
            Execute("PRAGMA journal_mode=WAL");
        }

        Execute("CREATE TABLE IF NOT EXISTS " + EntrySchema);
        try
        {
            var hasIndex = Query("SELECT 1 FROM sqlite_master WHERE name='entry_search'").Count != 0;
            if (hasIndex && Query("PRAGMA user_version")[0]["user_version"] == "1")
            {
                _fts = true;
                return;
            }
            Execute("BEGIN IMMEDIATE");
            try
            {
                if (hasIndex) Execute("DROP TABLE IF EXISTS entry_search");
                Execute("CREATE VIRTUAL TABLE IF NOT EXISTS entry_search USING fts5(key,content,tokenize='trigram')");
                Execute("INSERT INTO entry_search(key,content) SELECT key,content FROM entries WHERE (" + IndexedKeys +
                    ") AND key NOT IN (SELECT key FROM entry_search)");
                Execute("PRAGMA user_version=1");
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
            _fts = true;
            if (hasIndex && int.Parse(Query("PRAGMA freelist_count")[0]["freelist_count"]) >= 256)
            {
                Execute("VACUUM");
                Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
        }
        catch (InvalidOperationException) { _fts = false; }
    }

    internal static MemoryDb ReadOnly(string path) => new(path, readOnly: true);

    internal static MemoryDb OpenScratch(string path) => new(path, readOnly: false, initialize: false);

    internal bool HasEntriesTable() => Query("SELECT name FROM sqlite_master WHERE type='table' AND name='entries'").Count > 0;

    public void Execute(string sql, params object?[] values)
    {
        lock (_gate)
        {
            var statement = Prepare(sql, values);
            try
            {
                var result = Step(statement);
                if (result is not (100 or 101))
                {
                    Check(result);
                }
            }
            finally { FinalizeStatement(statement); }
        }
    }

    public List<Dictionary<string, string>> Query(string sql, params object?[] values)
    {
        lock (_gate)
        {
            var statement = Prepare(sql, values);
            try
            {
                var result = new List<Dictionary<string, string>>();
                int step;
                while ((step = Step(statement)) == 100)
                {
                    var row = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (var i = 0; i < ColumnCount(statement); i++)
                    {
                        row[Marshal.PtrToStringUTF8(ColumnName(statement, i))!] = Marshal.PtrToStringUTF8(ColumnText(statement, i), ColumnBytes(statement, i)) ?? "";
                    }

                    result.Add(row);
                }
                if (step != 101)
                {
                    Check(step);
                }

                return result;
            }
            finally { FinalizeStatement(statement); }
        }
    }

    public string? Get(string key) => Query("SELECT content FROM entries WHERE key=?", key).FirstOrDefault()?.GetValueOrDefault("content");

    public void Snapshot(string path) => Execute("VACUUM INTO ?", NativePath(path));

    internal void SnapshotSession(string path)
    {
        lock (_gate)
        {
            Execute("ATTACH DATABASE ? AS branch", NativePath(path));
            try
            {
                Execute("CREATE TABLE branch." + EntrySchema);
                Execute("INSERT INTO branch.entries SELECT key,content,revision,updated_at FROM main.entries WHERE key<>?",
                    ProviderPlans.OpenCodeSessionKey);
            }
            finally { Execute("DETACH DATABASE branch"); }
        }
    }

    private static string NativePath(string path)
    {
        if (!OperatingSystem.IsWindows() || path == ":memory:") return path;
        var full = System.IO.Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    public JsonObject Write(string key, string content, int? revision = null, bool internalWrite = false)
    {
        ValidateKey(key, internalWrite);
        lock (_gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                var old = Query("SELECT revision FROM entries WHERE key=?", key).FirstOrDefault();
                var current = old is null ? 0 : int.Parse(old["revision"]);
                if (!internalWrite && ((current > 0 && revision != current) || (revision.HasValue && revision != current)))
                {
                    throw new InvalidOperationException($"revision conflict: {key}, current={current}");
                }

                Upsert(key, content, current);
                Execute("COMMIT");
                return new()
                {
                    ["key"] = key,
                    ["revision"] = current + 1,
                    ["total_chars"] = content.Length
                };
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }

    internal void WriteInternal(params (string Key, string? Content)[] entries)
    {
        lock (_gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                foreach (var (key, content) in entries)
                {
                    ValidateKey(key, true);
                    if (content is null)
                    {
                        Execute("DELETE FROM entries WHERE key=?", key);
                        continue;
                    }
                    var old = Query("SELECT revision FROM entries WHERE key=?", key).FirstOrDefault();
                    Upsert(key, content, old is null ? 0 : int.Parse(old["revision"]));
                }
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }

    internal void UpdateInternal(string key, Func<string?, string> update)
    {
        ValidateKey(key, true);
        lock (_gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                var old = Query("SELECT content, revision FROM entries WHERE key=?", key).FirstOrDefault();
                Upsert(key, update(old?["content"]), old is null ? 0 : int.Parse(old["revision"]));
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }

    private void Upsert(string key, string content, int current)
    {
        Execute("INSERT INTO entries VALUES(?,?,?,?) ON CONFLICT(key) DO UPDATE SET content=excluded.content,revision=excluded.revision,updated_at=excluded.updated_at", key, content, current + 1, DateTimeOffset.UtcNow.ToString("O"));
        if (!_fts || !ShouldIndex(key)) return;
        if (current != 0) Execute("DELETE FROM entry_search WHERE key=?", key);
        Execute("INSERT INTO entry_search(key,content) VALUES(?,?)", key, content);
    }

    private static bool ShouldIndex(string key) => key.StartsWith(HistoryMessages, StringComparison.Ordinal) ||
        !(key.StartsWith("_context/", StringComparison.Ordinal) || key.StartsWith("_archive/", StringComparison.Ordinal) ||
          key.StartsWith("_history/", StringComparison.Ordinal));

    public JsonObject Call(string name, JsonObject args)
    {
        lock (_gate)
        {
            var key = args.GetString("key");
            var limit = Math.Clamp(args.GetInt32("limit", 30), 1, 200);
            var rows = new JsonArray();
            if (name == "db_list")
            {
                var prefix = args.GetString("prefix");
                var result = Query("SELECT key,revision,length(content) AS chars,substr(content,1,220) AS snippet FROM entries WHERE substr(key,1,?)=? AND key>? AND (?=0 OR substr(key,1,1)<>'_') ORDER BY key LIMIT ?", prefix.EnumerateRunes().Count(), prefix, args.GetString("after"), args.GetBoolean("notes_only") ? 1 : 0, limit + 1);
                foreach (var row in result.Take(limit))
                {
                    rows.AddNode(new JsonObject { ["key"] = row["key"], ["revision"] = int.Parse(row["revision"]), ["chars"] = int.Parse(row["chars"]), ["snippet"] = row["snippet"] });
                }

                return new()
                {
                    ["entries"] = rows,
                    ["next_after"] = result.Count > limit ? result[limit - 1]["key"] : null
                };
            }
            if (name == "db_read")
            {
                var row = Query("SELECT content,revision FROM entries WHERE key=?", key).FirstOrDefault() ?? throw new InvalidOperationException("entry not found");
                var body = row["content"];
                var start = Math.Clamp(args.GetInt32("start"), 0, body.Length);
                if (start > 0 && start < body.Length && char.IsLowSurrogate(body[start]) && char.IsHighSurrogate(body[start - 1]))
                {
                    start--;
                }

                var end = Math.Min(body.Length, start + Math.Clamp(args.GetInt32("length", 12000), 1, 64000));
                if (end > 0 && end < body.Length && char.IsHighSurrogate(body[end - 1]) && char.IsLowSurrogate(body[end]))
                {
                    end++;
                }

                return new()
                {
                    ["key"] = key,
                    ["revision"] = int.Parse(row["revision"]),
                    ["content"] = body[start..end],
                    ["total_chars"] = body.Length,
                    ["start"] = start,
                    ["next_offset"] = end < body.Length ? end : null
                };
            }
            if (name == "db_write")
            {
                return Write(key, args.GetString("content"), args.ContainsKey("expected_revision") ? args.GetInt32("expected_revision") : null);
            }

            if (name == "db_patch")
            {
                var row = Query("SELECT content,revision FROM entries WHERE key=?", key).FirstOrDefault() ?? throw new InvalidOperationException("entry not found");
                var old = args.GetString("old_string");
                if (old.Length == 0 || !row["content"].Contains(old, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("old_string not found");
                }

                if (!args.GetBoolean("replace_all") && row["content"].IndexOf(old, StringComparison.Ordinal) != row["content"].LastIndexOf(old, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("old_string is not unique");
                }

                var revision = int.Parse(row["revision"]);
                if (args.ContainsKey("expected_revision") && args.GetInt32("expected_revision") != revision)
                {
                    throw new InvalidOperationException("revision conflict");
                }

                return Write(key, row["content"].Replace(old, args.GetString("new_string"), StringComparison.Ordinal), revision);
            }
            if (name == "db_delete")
            {
                ValidateKey(key, false);
                Execute("BEGIN IMMEDIATE");
                try
                {
                    var row = Query("SELECT revision FROM entries WHERE key=?", key).FirstOrDefault();
                    if (row is null || !args.ContainsKey("expected_revision") || int.Parse(row["revision"]) != args.GetInt32("expected_revision"))
                    {
                        throw new InvalidOperationException("revision conflict");
                    }
                    Execute("DELETE FROM entries WHERE key=?", key);
                    if (_fts)
                    {
                        Execute("DELETE FROM entry_search WHERE key=?", key);
                    }

                    Execute("COMMIT");
                }
                catch { Execute("ROLLBACK"); throw; }
                return new()
                {
                    ["deleted"] = true,
                    ["key"] = key
                };
            }
            if (name == "db_search")
            {
                var query = args.GetString("query");
                if (query.Length == 0)
                {
                    throw new InvalidOperationException("query required");
                }

                var scope = args.GetString("scope", "all");
                if (scope is not ("all" or "keys" or "content"))
                {
                    throw new InvalidOperationException("invalid scope");
                }

                var prefix = args.GetString("prefix");
                var notesOnly = args.GetBoolean("notes_only");
                var useFts = _fts && (notesOnly || prefix.StartsWith(HistoryMessages, StringComparison.Ordinal) ||
                    prefix.Length > 0 && prefix[0] != '_') && query.EnumerateRunes().Count() >= 3;
                var expression = (scope == "keys" ? "key : " : scope == "content" ? "content : " : "") + "\"" + query.Replace("\"", "\"\"") + "\"";
                var condition = useFts ? "key IN (SELECT key FROM entry_search WHERE entry_search MATCH ?)" : scope switch
                {
                    "keys" => "instr(lower(key),lower(?))>0",
                    "content" => "instr(lower(content),lower(?))>0",
                    _ => "instr(lower(key||char(10)||content),lower(?))>0"
                };
                var offset = Math.Max(0, args.GetInt32("offset"));
                var order = args.GetBoolean("newest_first") ? "updated_at DESC, key DESC" : "key";
                var result = Query("SELECT key,revision,substr(content,max(1,instr(lower(content),lower(?))-60),220) AS snippet FROM entries WHERE " + condition + " AND substr(key,1,?)=? AND (?=0 OR substr(key,1,1)<>'_') ORDER BY " + order + " LIMIT ? OFFSET ?", query, useFts ? expression : query, prefix.EnumerateRunes().Count(), prefix, notesOnly ? 1 : 0, limit + 1, offset);
                foreach (var row in result.Take(limit))
                {
                    rows.AddNode(new JsonObject { ["key"] = row["key"], ["revision"] = int.Parse(row["revision"]), ["snippet"] = row["snippet"] });
                }

                return new()
                {
                    ["matches"] = rows,
                    ["next_offset"] = result.Count > limit ? offset + limit : null,
                    ["algorithm"] = useFts ? "fts5_trigram" : "literal_scan"
                };
            }
            throw new InvalidOperationException("unknown DB tool");
        }
    }

    private static void ValidateKey(string key, bool internalWrite)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 500 || key.Contains('\0'))
        {
            throw new InvalidOperationException("invalid key");
        }

        if (!internalWrite && (key.StartsWith("_context/", StringComparison.Ordinal) || key.StartsWith("_archive/", StringComparison.Ordinal) || key.StartsWith("_history/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("framework archive is read-only");
        }
    }
    private nint Prepare(string sql, object?[] args)
    {
        Check(PrepareStatement(_db, sql, -1, out var statement, 0));
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                Check(args[i] switch
                {
                    int value => BindInt64(statement, i + 1, value),
                    long value => BindInt64(statement, i + 1, value),
                    null => BindNull(statement, i + 1),
                    _ => BindText(statement, i + 1, args[i]!.ToString()!, Encoding.UTF8.GetByteCount(args[i]!.ToString()!), -1)
                });
            }

            return statement;
        }
        catch { FinalizeStatement(statement); throw; }
    }
    private void Check(int result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(ErrorMessage(_db)) ?? $"SQLite {result}");
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_db != 0)
            {
                Close(_db);
                _db = 0;
            }
        }
    }

    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string path, out nint db, int flags, string? vfs);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_close_v2")] private static partial int Close(nint db);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_busy_timeout")] private static partial int BusyTimeout(nint db, int ms);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)] private static partial int PrepareStatement(nint db, string sql, int length, out nint statement, nint tail);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_step")] private static partial int Step(nint statement);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_finalize")] private static partial int FinalizeStatement(nint statement);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_bind_text", StringMarshalling = StringMarshalling.Utf8)] private static partial int BindText(nint statement, int index, string text, int length, nint destructor);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_bind_int64")] private static partial int BindInt64(nint statement, int index, long value);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_bind_null")] private static partial int BindNull(nint statement, int index);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_column_count")] private static partial int ColumnCount(nint statement);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_column_text")] private static partial nint ColumnText(nint statement, int index);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_column_bytes")] private static partial int ColumnBytes(nint statement, int index);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_column_name")] private static partial nint ColumnName(nint statement, int index);
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_errmsg")] private static partial nint ErrorMessage(nint db);
}
