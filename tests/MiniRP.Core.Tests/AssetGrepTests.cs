using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class AssetGrepTests
{
    [TestMethod]
    public async Task TextGrepUsesRipgrepSyntaxAndPaginatesContext()
    {
        const string input = "intro\r\n中文 ABC\r\nbetween\nlast XYZ\nend";
        var args = new JsonObject { ["pattern"] = "[[:upper:]]{3}$", ["context"] = 1, ["limit"] = 1 };
        using var first = new StringReader(input);
        var result = await AssetGrep.Text(first, args, default);
        Assert.AreEqual(1, result.GetInt32("match_count"));
        Assert.AreEqual(4, result.GetInt32("next_line"));
        var lines = result.GetArray("lines");
        CollectionAssert.AreEqual(new[] { "intro", "中文 ABC", "between" }, lines.Select(line => line.GetString("text")).ToArray());
        Assert.AreEqual(4, lines[1].GetInt32("column"));
        using var second = new StringReader(input);
        args["from_line"] = result.GetInt32("next_line");
        var continued = await AssetGrep.Text(second, args, default);
        Assert.AreEqual("last XYZ", continued.GetArray("lines").Single(line => line.GetInt32("line") == 4).GetString("text"));
        Assert.IsNull(continued["next_line"]);
    }

    [TestMethod]
    public async Task RawGrepFindsLiteralBytesAcrossRowsAndChunks()
    {
        var bytes = Enumerable.Range(0, 65).Select(value => (byte)value).ToArray();
        async IAsyncEnumerable<byte[]> Chunks([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return bytes[..31];
            yield return bytes[31..33];
            yield return bytes[33..];
        }
        var asset = new PluginRuntime.AssetContent("application/octet-stream", null, Chunks, Text: "extracted text");
        var literal = await AssetGrep.Raw(asset, new() { ["pattern"] = "1e 1f | 20 21", ["literal"] = true, ["context"] = 1 }, default);
        var matched = literal.GetArray("lines")[0];
        Assert.AreEqual(30, matched.GetInt32("match_offset"));
        Assert.AreEqual("00 01 02 03 04 05 06 07 | 08 09 0A 0B 0C 0D 0E 0F | 10 11 12 13 14 15 16 17 | 18 19 1A 1B 1C 1D 1E 1F", matched.GetString("text"));
        Assert.AreEqual(32, literal.GetArray("lines")[1].GetInt32("offset"));
        var regex = await AssetGrep.Raw(asset, new() { ["pattern"] = @"07 \| 08", ["limit"] = 1 }, default);
        Assert.AreEqual(7, regex.GetArray("lines").Single().GetInt32("match_offset"));
        Assert.AreEqual(32, regex.GetInt32("next_offset"));
    }
}
