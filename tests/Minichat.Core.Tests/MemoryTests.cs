using System.Text.Json.Nodes;
using Minichat.Core;

namespace Minichat.Core.Tests;

[TestClass]
public sealed class MemoryTests
{
    [TestMethod]
    public void PagingDoesNotSplitEmojiOrTruncateEmbeddedNulls()
    {
        using var db = new MemoryDb(":memory:");
        db.Write("🙂/note", "x🙂y\0end");
        Assert.AreEqual("x🙂y\0end", db.Get("🙂/note"));
        Assert.AreEqual("🙂", db.Call("db_read", new() { ["key"] = "🙂/note", ["start"] = 1, ["length"] = 1 }).GetString("content"));
        Assert.AreEqual(1, db.Call("db_list", new() { ["prefix"] = "🙂/" }).GetArray("entries").Count);
    }

    [TestMethod]
    public void RetainsLongUnicodeEntriesAndRejectsStaleRevisions()
    {
        using var db = new MemoryDb(":memory:");
        var body = string.Concat(Enumerable.Repeat("完整正文🙂\n", 30000)) + "END";
        db.Call("db_write", new()
        {
            ["key"] = "网页/正文",
            ["content"] = body
        });
        Assert.AreEqual(body, db.Get("网页/正文"));
        Assert.ThrowsExactly<InvalidOperationException>(() => db.Call("db_write", new() { ["key"] = "网页/正文", ["content"] = "stale" }));
        db.Call("db_patch", new()
        {
            ["key"] = "网页/正文",
            ["old_string"] = "END",
            ["new_string"] = "DONE",
            ["expected_revision"] = 1
        });
        Assert.EndsWith("DONE", db.Get("网页/正文")!);
        Assert.ThrowsExactly<InvalidOperationException>(() => db.Call("db_delete", new() { ["key"] = "网页/正文", ["expected_revision"] = 1 }));
        Assert.AreEqual("网页/正文", db.Call("db_search", new()
        {
            ["query"] = "完整正文"
        }).GetArray("matches")[0].GetString("key"));
    }

    [TestMethod]
    public void UniquePatchesAndFrameworkArchiveProtectionAreEnforced()
    {
        using var db = new MemoryDb(":memory:");
        db.Write("note", "duplicate duplicate");
        Assert.ThrowsExactly<InvalidOperationException>(() => db.Call("db_patch", new() { ["key"] = "note", ["old_string"] = "duplicate", ["new_string"] = "one" }));
        Assert.ThrowsExactly<InvalidOperationException>(() => db.Write("_archive/history", "forged"));
        db.Write("_archive/history", "retained", internalWrite: true);
        Assert.AreEqual("retained", db.Get("_archive/history"));
    }
}
