using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginDevelopmentProject(string root, string id, string preset, PluginRuntime runtime, Action changed)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private string PackageId => "draft-" + id;
    private string PackageName => "local.minirp.p" + id;
    private string LegacyPackageName => "local.minichat.p" + id;

    internal PluginRuntime.AssetContent? OpenPublicAsset(string url, JsonObject options)
    {
        if (PluginUrls.Route(url, "assets").Split('/') is not [var packageName, _] ||
            packageName != PackageName && packageName != LegacyPackageName) return null;
        return runtime.OpenDevelopmentPublicAsset(PackageId, DeploymentSigner(), url, options, preset);
    }

    public async Task<JsonNode?> Execute(PluginProjectAssets draft, string action, JsonObject args, CancellationToken ct, string imageOutput = "")
    {
        ct.ThrowIfCancellationRequested();
        switch (action)
        {
            case "project_debug":
            case "project_publish":
                return await Build(draft, action == "project_publish", args, ct);
            case "project_verify":
                var package = runtime.DevelopmentDeployment(PackageId, DeploymentSigner());
                var workspace = Path.Combine(root, ".verify-" + WorkspaceIds.New());
                try { return await Verify(package, workspace, args, ct); }
                finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true); }
            case "project_card_catalog":
                return await runtime.OpenUiCatalog(args, ct);
            case "project_card_preview":
                var card = PluginPackage.Identifier(args.GetString("id"));
                return await Build(draft, false, args, ct, card, imageOutput);
            case "project_remove":
                runtime.RemoveDevelopment(PackageId, DeploymentSigner());
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
        var preview = !publish && card is null ? Path.Combine(root, "preview") : Path.Combine(build, "preview");
        try
        {
            await draft.Stage(source, ct);
            var manifestPath = Path.Combine(source, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Associate a manifest Asset with the path manifest.json.");
            if (new FileInfo(manifestPath).Length > MaxManifestBytes) throw new IOException("The draft manifest is too large.");
            var manifest = JsonValues.ParseObject(await File.ReadAllTextAsync(manifestPath, Utf8, ct));
            var packageName = manifest.GetString("packageName");
            if (manifest.GetString("id") != PackageId || packageName != PackageName && packageName != LegacyPackageName)
                throw new InvalidDataException("Keep the draft's assigned id and packageName.");
            if (!manifest.ContainsKey("presetId"))
            {
                manifest["presetId"] = preset;
                AtomicFile.Write(manifestPath, JsonValues.Serialize(manifest));
            }
            if (manifest.GetString("presetId") != preset)
                throw new InvalidDataException("Keep the draft's assigned presetId.");
            var fingerprint = await runtime.PackDevelopment(source, artifact, Path.Combine(root, "signer.json"), ct);
            var tested = await runtime.Debug(artifact, preview, preset, args.GetString("handler"),
                args.GetObject("event"), ct, card, args, imageOutput);
            if (card is not null) return tested;
            RequireExpectedResult(args, tested, publish ? "Draft check before installation" : "Draft Action check");
            if (!publish) return new JsonObject { ["ok"] = true, ["result"] = tested };
            ct.ThrowIfCancellationRequested();
            AtomicFile.Write(Path.Combine(root, "deployment.json"), JsonValues.Serialize(new JsonObject { ["signer"] = fingerprint }));
            var package = runtime.Deploy(artifact, preset);
            changed();
            var deployment = runtime.Descriptor(package);
            try
            {
                deployment["verification"] = await Verify(package, Path.Combine(build, "verify"), args, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                deployment["verification"] = new JsonObject
                {
                    ["ok"] = false, ["generation"] = package.Generation, ["error"] = error.Message,
                };
            }
            return deployment;
        }
        finally
        {
            if (Directory.Exists(build)) Directory.Delete(build, recursive: true);
        }
    }

    private string DeploymentSigner()
    {
        var deployment = Path.Combine(root, "deployment.json");
        if (!File.Exists(deployment))
            throw new InvalidOperationException("This project has no deployment. Use project_publish before testing the installed plugin.");
        return JsonValues.ParseObject(File.ReadAllText(deployment)).GetString("signer");
    }

    private async Task<JsonObject> Verify(PluginPackage package, string workspace, JsonObject args, CancellationToken ct)
    {
        var result = await runtime.VerifyDeployment(package, workspace, preset, args.GetString("handler"),
            args.GetObject("event"), ct);
        RequireExpectedResult(args, result, "Installed Action check");
        return new() { ["ok"] = true, ["generation"] = package.Generation, ["result"] = result };
    }

    private static void RequireExpectedResult(JsonObject args, JsonNode? result, string stage)
    {
        var field = args.ContainsKey("expected_result_json") ? "expected_result_json" : "expected_result";
        if (!args.TryGetPropertyValue(field, out var expected)) return;
        if (field == "expected_result_json")
        {
            if (args.ContainsKey("expected_result")) throw new InvalidDataException("Supply only one of expected_result or expected_result_json.");
            if (expected is not JsonValue value || !value.TryGetValue<string>(out var text))
                throw new InvalidDataException("expected_result_json must be a string containing a JSON value.");
            try { expected = JsonNode.Parse(text); }
            catch (JsonException error)
            {
                throw new InvalidDataException(
                    $"expected_result_json must contain valid JSON. Parsing stopped on line {(error.LineNumber ?? 0) + 1}, byte {(error.BytePositionInLine ?? 0) + 1}.",
                    error);
            }
        }
        if (JsonNode.DeepEquals(expected, result)) return;
        var guidance = field == "expected_result" && expected?.GetValueKind() == JsonValueKind.String &&
            result is JsonObject or JsonArray
            ? " Pass the JSON value directly to expected_result, or serialize it into expected_result_json."
            : " Check the fields, JSON types, and array order against the Action result.";
        var actual = JsonValues.Serialize(result);
        if (actual.Length > 512)
            guidance += " Run the same fixture without an expected result to inspect the full result.";
        throw new InvalidDataException($"{stage} failed. The result differs from {field}. " +
            $"Expected ({Kind(expected)}): {Preview(JsonValues.Serialize(expected))}. " +
            $"Actual handler result ({Kind(result)}): {Preview(actual)}.{guidance}");

        static string Preview(string json) => json.Length <= 512 ? json : json[..512] + "…";
        static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;
    }
}
