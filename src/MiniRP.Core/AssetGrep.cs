using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Scout.Text.Regex;

namespace MiniRP.Core;

internal static class AssetGrep
{
    private const int RowBytes = 32;
    private const int PreviewLength = 480;
    private const int OutputLength = 6000;
    private const int MaxLineChars = 2 * 1024 * 1024;

    private sealed record Row(int Position, string Text, int? Match);

    public static Task<JsonObject> Text(TextReader reader, JsonObject args, CancellationToken ct)
    {
        var pattern = Pattern(args);
        var ignoreCase = args.GetBoolean("ignore_case");
        int? Find(string line)
        {
            var index = line.IndexOf(pattern, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return index < 0 ? null : index;
        }
        var match = args.GetBoolean("literal") ? Find : Compile(pattern, ignoreCase);
        var from = Math.Max(1, args.GetInt32("from_line", 1));
        return Search(TextRows(reader, Math.Max(1, from - Context(args)), from, match, ct), args, from, raw: false, ct);
    }

    public static async Task<JsonObject> Raw(PluginRuntime.AssetContent asset, JsonObject args, CancellationToken ct)
    {
        var pattern = Pattern(args);
        var literal = args.GetBoolean("literal");
        var bytes = literal ? ParseHex(pattern) : [];
        var match = literal ? null : Compile(pattern, args.GetBoolean("ignore_case", true));
        var from = Math.Clamp(args.GetInt32("from_offset"), 0, MessageAttachments.MaxAssetBytes);
        var start = from - Math.Min(Context(args), from / RowBytes) * RowBytes;
        await using var stream = new AssetStream(asset, ct);
        return await Search(RawRows(stream, start, from, bytes, match, ct), args, from, raw: true, ct);
    }

    private static int Context(JsonObject args) => Math.Clamp(args.GetInt32("context"), 0, 5);

    private static string Pattern(JsonObject args) => args.GetString("pattern") is { Length: > 0 and <= 512 } pattern
        ? pattern : throw new ArgumentException("Provide a grep pattern of 1 to 512 characters.");

    private static Func<string, int?> Compile(string pattern, bool ignoreCase)
    {
        var regex = ByteRegex.Compile(ignoreCase ? "(?i)" + pattern : pattern);
        return text =>
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return regex.Find(bytes) is { } match ? Encoding.UTF8.GetCharCount(bytes.AsSpan(0, match.Start)) : null;
        };
    }

    internal static byte[] ParseHex(string pattern)
    {
        var parts = pattern.Replace('|', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part.Length != 2 || !part.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Provide space-separated HEX bytes, for example 50 4B 03 04.");
        return Convert.FromHexString(string.Concat(parts));
    }

    private static async Task<JsonObject> Search(IAsyncEnumerable<Row> rows, JsonObject args, int from, bool raw, CancellationToken ct)
    {
        var context = Context(args);
        var limit = Math.Clamp(args.GetInt32("limit", 20), 1, 100);
        var preceding = new Queue<Row>();
        var output = new JsonArray();
        var lastOutput = -1;
        var after = 0;
        var matched = 0;
        var available = OutputLength;
        int? next = null;

        void Print(Row row, int width)
        {
            if (row.Position <= lastOutput || available == 0) return;
            width = Math.Min(width, available);
            var start = row.Match is { } hint && !raw && hint >= width ? Math.Max(0, hint - width / 3) : 0;
            if (start > 0 && start < row.Text.Length && char.IsLowSurrogate(row.Text[start])) start--;
            var end = Math.Min(row.Text.Length, start + width);
            if (end < row.Text.Length && char.IsLowSurrogate(row.Text[end])) end--;
            var item = new JsonObject { [raw ? "offset" : "line"] = row.Position, ["text"] = row.Text[start..end] };
            if (row.Match is { } match) item[raw ? "match_offset" : "column"] = raw ? row.Position + match : match + 1;
            if (start > 0 || end < row.Text.Length)
            {
                item["truncated"] = true;
                if (start > 0) item["start_column"] = start + 1;
            }
            output.AddNode(item);
            lastOutput = row.Position;
            available -= Math.Min(available, end - start + 40);
        }

        await foreach (var row in rows.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (matched == limit && after == 0)
            {
                next = row.Position;
                break;
            }
            if (row.Position >= from && row.Match is not null)
            {
                if (matched == limit || available < PreviewLength + 40)
                {
                    next = row.Position;
                    break;
                }
                foreach (var before in preceding)
                    if (available >= PreviewLength + 40 + 160) Print(before, 120);
                Print(row, PreviewLength);
                matched++;
                after = context;
            }
            else if (after > 0)
            {
                Print(row, 120);
                after--;
            }
            if (context == 0) continue;
            if (preceding.Count == context) preceding.Dequeue();
            preceding.Enqueue(row);
        }
        return new() { ["match_count"] = matched, ["lines"] = output,
            [raw ? "next_offset" : "next_line"] = next, ["truncated"] = next is not null };
    }

    private static async IAsyncEnumerable<Row> TextRows(TextReader reader, int start, int from, Func<string, int?> match,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        var position = 1;
        var skipLf = false;
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is > 0 and var count)
        {
            var index = 0;
            while (index < count)
            {
                if (skipLf && buffer[index] == '\n') index++;
                skipLf = false;
                var separator = buffer.AsSpan(index, count - index).IndexOfAny('\r', '\n');
                var size = separator < 0 ? count - index : separator;
                if (position >= start)
                {
                    if (line.Length + size > MaxLineChars)
                        throw new IOException($"Text line {position} exceeds {MaxLineChars} characters. Use raw_grep to search the underlying bytes.");
                    line.Append(buffer, index, size);
                }
                index += size;
                if (separator < 0) break;
                if (position >= start)
                {
                    var text = line.ToString();
                    yield return new(position, text, position >= from ? match(text) : null);
                }
                position++;
                line.Clear();
                skipLf = buffer[index++] == '\r';
            }
        }
        if (line.Length > 0)
        {
            var text = line.ToString();
            yield return new(position, text, position >= from ? match(text) : null);
        }
    }

    private static async IAsyncEnumerable<Row> RawRows(Stream stream, int start, int from, byte[] literal, Func<string, int?>? regex,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (start > 0)
        {
            var skip = new byte[Math.Min(65536, start)];
            var remaining = start;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(skip.AsMemory(0, Math.Min(skip.Length, remaining)), ct);
                if (read == 0) yield break;
                remaining -= read;
            }
        }
        var buffer = new byte[RowBytes + Math.Max(0, literal.Length - 1)];
        var count = 0;
        for (var position = start; ; position += RowBytes)
        {
            count += await stream.ReadAtLeastAsync(buffer.AsMemory(count), buffer.Length - count, false, ct);
            if (count == 0) yield break;
            var length = Math.Min(RowBytes, count);
            var text = FormatHex(buffer.AsSpan(0, length));
            int? match = null;
            if (position >= from)
            {
                if (regex is not null)
                {
                    if (regex(text) is { } column)
                        match = Math.Min(length - 1, column / 26 * 8 + column % 26 / 3);
                }
                else if (buffer.AsSpan(0, count).IndexOf(literal) is >= 0 and var offset && offset < length)
                    match = offset;
            }
            yield return new(position, text, match);
            count -= length;
            if (count > 0) Array.Copy(buffer, length, buffer, 0, count);
        }
    }

    private static string FormatHex(ReadOnlySpan<byte> row)
    {
        var hex = Convert.ToHexString(row);
        var formatted = new StringBuilder(row.Length * 3 + 6);
        for (var index = 0; index < row.Length; index++)
        {
            if (index != 0) formatted.Append(index % 8 == 0 ? " | " : " ");
            formatted.Append(hex.AsSpan(index * 2, 2));
        }
        return formatted.ToString();
    }
}
