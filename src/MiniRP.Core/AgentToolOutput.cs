using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class AgentToolOutput(AgentAssetStorage storage)
{
    internal const int InlineCharacters = 8192;

    internal AgentToolOutput WithDatabase(MemoryDb db) => new(storage.WithDatabase(db));

    internal Task<JsonObject> Publish(string text, string name, string mime, CancellationToken ct)
    {
        if (text.Length <= InlineCharacters || text.EnumerateRunes().Take(InlineCharacters + 1).Count() <= InlineCharacters)
            return Task.FromResult(new JsonObject { ["output"] = text });
        return Store(text, name, mime, null, ct);
    }

    internal Task<JsonObject> Store(string text, string name, string mime, string? url, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(text) > MessageAttachments.MaxAssetBytes) throw new IOException("The output Asset is too large.");
        return StoreStream(name, mime, url, async (output, token) => await output.WriteAsync(Encoding.UTF8.GetBytes(text), token), ct);
    }

    internal Task<JsonObject> StoreStream(string name, string mime, string? url,
        Func<Stream, CancellationToken, Task> write, CancellationToken ct) => storage.StoreOutput(name, mime, url, write, ct);
}
