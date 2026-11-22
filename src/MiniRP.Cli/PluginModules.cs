using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class PluginModules : DocumentLoader
{
    private readonly Dictionary<string, StringDocument> documents;

    private PluginModules(JsonObject sources) => documents = sources.ToDictionary(
        item => item.Key,
        item => new StringDocument(new DocumentInfo(new Uri("minirp-plugin:///" + item.Key))
            { Category = ModuleCategory.Standard }, JsonValues.GetText(item.Value)),
        StringComparer.Ordinal);

    public static void Load(V8ScriptEngine engine, string entry, JsonObject sources)
    {
        var loader = new PluginModules(sources);
        engine.DocumentSettings.Loader = loader;
        if (engine.Evaluate(loader.documents[entry].Info, sources.GetString(entry)) is Task evaluation)
        {
            if (!evaluation.IsCompleted) throw new InvalidOperationException("Plugin modules must finish initializing before handling events.");
            evaluation.GetAwaiter().GetResult();
        }
    }

    public override Document LoadDocument(DocumentSettings settings, DocumentInfo? sourceInfo, string specifier,
        DocumentCategory category, DocumentContextCallback contextCallback)
    {
        var from = sourceInfo?.Uri is { Scheme: "minirp-plugin" } uri ? uri.AbsolutePath[1..] : "";
        if (!documents.ContainsKey(from) || specifier.Length > 1024 ||
            !(specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal)) ||
            !(specifier.EndsWith(".js", StringComparison.Ordinal) || specifier.EndsWith(".mjs", StringComparison.Ordinal)))
            throw Unresolved(from, specifier);

        var parts = from.Split('/').SkipLast(1).ToList();
        foreach (var part in specifier.Split('/'))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw Unresolved(from, specifier);
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) parts.Add(part);
            else throw Unresolved(from, specifier);
        }

        return documents.TryGetValue(string.Join('/', parts), out var document) ? document : throw Unresolved(from, specifier);
    }

    public override Task<Document> LoadDocumentAsync(DocumentSettings settings, DocumentInfo? sourceInfo, string specifier,
        DocumentCategory category, DocumentContextCallback contextCallback) =>
        Task.FromResult(LoadDocument(settings, sourceInfo, specifier, category, contextCallback));

    private static IOException Unresolved(string from, string specifier) =>
        new($"Cannot import '{specifier}' from '{from}' in this plugin. Use a relative path to a packaged .js or .mjs file.");
}
