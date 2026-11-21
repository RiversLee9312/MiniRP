using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginDevelopmentProject(string root, string id, string preset, PluginRuntime runtime, Action changed)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private string PackageId => "draft-" + id;
    private string PackageName => "local.minirp.p" + id;

    public async Task<JsonNode?> Execute(PluginProjectAssets draft, string action, JsonObject args, CancellationToken ct, string imageOutput = "")
    {
        ct.ThrowIfCancellationRequested();
        switch (action)
        {
            case "project_debug":
            case "project_publish":
                return await Build(draft, action == "project_publish", args, ct);
            case "project_card_catalog":
                return await runtime.OpenUiCatalog(args, ct);
            case "project_card_preview":
                var card = PluginPackage.Identifier(args.GetString("id"));
                return await Build(draft, false, args, ct, card, imageOutput);
            case "project_remove":
                var deployment = Path.Combine(root, "deployment.json");
                if (!File.Exists(deployment)) throw new InvalidOperationException("This project has no deployment.");
                runtime.RemoveDevelopment(PackageId, JsonValues.ParseObject(File.ReadAllText(deployment)).GetString("signer"));
                changed();
                return new JsonObject { ["removed"] = true, ["id"] = PackageId };
            default:
                throw new ArgumentException("Unknown plugin development command.");
        }
    }

    private async Task<JsonNode?> Build(PluginProjectAssets draft, bool publish, JsonObject args, CancellationToken ct,
        string? card = null, string imageOutput = "")
    {
        var build = Path.Combine(root, ".build-" + WorkspaceIds.New());
        var source = Path.Combine(build, "source");
        var artifact = Path.Combine(build, "draft.plugin");
        var preview = card is null ? Path.Combine(root, "preview") : Path.ChangeExtension(artifact, ".preview");
        try
        {
            await draft.Stage(source, ct);
            var manifestPath = Path.Combine(source, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Associate a manifest Asset with the path manifest.json.");
            if (new FileInfo(manifestPath).Length > MaxManifestBytes) throw new IOException("The draft manifest is too large.");
            var manifest = JsonValues.ParseObject(await File.ReadAllTextAsync(manifestPath, Utf8, ct));
            var packageName = manifest.GetString("packageName");
            if (manifest.GetString("id") != PackageId || packageName != PackageName && packageName != "local.minichat.p" + id)
                throw new InvalidDataException("Keep the draft's assigned id and packageName.");
            if (!manifest.ContainsKey("presetId"))
            {
                manifest["presetId"] = preset;
                AtomicFile.Write(manifestPath, JsonValues.Serialize(manifest));
            }
            if (manifest.GetString("presetId") != preset)
                throw new InvalidDataException("Keep the draft's assigned presetId.");
            var fingerprint = await runtime.PackDevelopment(source, artifact, Path.Combine(root, "signer.json"), ct);
            var url = publish ? "" : args.GetString("handler");
            var tested = await runtime.Debug(artifact, preview, preset, url, args.GetObject("event"), ct, card, args, imageOutput);
            if (card is not null) return tested;
            if (!publish) return new JsonObject { ["ok"] = true, ["result"] = tested };
            ct.ThrowIfCancellationRequested();
            AtomicFile.Write(Path.Combine(root, "deployment.json"), JsonValues.Serialize(new JsonObject { ["signer"] = fingerprint }));
            var package = runtime.Deploy(artifact, preset);
            changed();
            return runtime.Descriptor(package);
        }
        finally
        {
            if (Directory.Exists(build)) Directory.Delete(build, recursive: true);
        }
    }
}
