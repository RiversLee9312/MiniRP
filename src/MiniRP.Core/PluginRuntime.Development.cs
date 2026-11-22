using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    internal const string CardPreviewOutput = "_card_preview_output";

    private async Task<JsonNode?> InvokeSkillTool(Invocation invocation, Provider? provider, Action<JsonObject>? attach, AgentAssets? assets, CancellationToken ct)
    {
        var arguments = invocation.Event.GetObject("arguments");
        if (!invocation.Package.BuiltIn || invocation.Package.Id != "base" ||
            invocation.Event.GetString("tool") != "plugin_base_project_card_preview" ||
            !arguments.GetBoolean("screenshot") && arguments["viewport"] is null)
            return await Invoke(invocation, ct);
        if (provider is null || attach is null || !MessageAttachments.Supports(provider, "image/png"))
        {
            var result = await Invoke(invocation, ct);
            if (result is JsonObject preview)
                preview["screenshot"] = new JsonObject { ["captured"] = false, ["reason"] = "Image input is unavailable for this model protocol." };
            return result;
        }

        var id = "openui-preview-" + JsonValues.NewId();
        var path = Path.Combine(files, id);
        var environment = (JsonObject)invocation.Environment.DeepClone();
        environment[CardPreviewOutput] = id;
        var retained = false;
        try
        {
            var result = await Invoke(invocation with { Environment = environment }, ct);
            if (result.GetObject("screenshot").GetBoolean("captured"))
            {
                var attachment = new JsonObject { ["id"] = id, ["mime"] = "image/png", ["name"] = arguments.GetString("id") + ".png" };
                new MessageAttachments(files).Validate(new JsonArray(attachment.DeepClone()), provider);
                if (assets is not null) result!.AsObject()["asset_url"] = assets.StoreFile(attachment);
                attach(attachment);
                retained = true;
            }
            return result;
        }
        finally { if (!retained && File.Exists(path)) File.Delete(path); }
    }

    public Task<string> PackDevelopment(string source, string path, string signer, CancellationToken ct) =>
        PluginArchive.Pack(source, path, (payload, token) => PluginDevelopmentSigner.Sign(signer, payload, port, token),
            draft =>
            {
                using var stream = File.OpenRead(draft);
                return ReadPackage(stream, ct: ct).Signer;
            }, ct);

    internal Task<JsonObject> OpenUiCatalog(JsonObject args, CancellationToken ct) =>
        port.CallHost("open_ui_catalog", new()
        {
            ["offset"] = args.GetInt32("offset"), ["length"] = args.GetInt32("length", 4000),
            ["query"] = args.GetString("query"),
        }, ct, TimeSpan.FromSeconds(20));

    public async Task<JsonNode?> Debug(string path, string workspace, string preset, string url, JsonObject input,
        CancellationToken ct, string? card = null, JsonObject? options = null, string imageOutput = "")
    {
        using var stream = File.OpenRead(path);
        var package = ReadPackage(stream, path, ct: ct);
        return await Debug(package, workspace, preset, url, input, ct, card, options, imageOutput);
    }

    internal async Task<JsonNode?> VerifyDeployment(PluginPackage package, string workspace, string preset,
        string url, JsonObject input, CancellationToken ct)
    {
        RequireCurrent(package);
        var result = await Debug(package with { Enabled = true }, workspace, preset, url, input, ct);
        RequireCurrent(package);
        return result;
    }

    private async Task<JsonNode?> Debug(PluginPackage package, string workspace, string preset, string url, JsonObject input,
        CancellationToken ct, string? card = null, JsonObject? options = null, string imageOutput = "")
    {
        await using var runtime = new PluginRuntime(workspace, port, emit,
            (_, _, _, _, _) => throw new UnauthorizedAccessException(), preview: true, mediaFiles: mediaFiles);
        runtime.SetPackages([package]);
        var environment = new JsonObject { ["preset"] = preset, ["session"] = "preview-" + JsonValues.NewId(), ["agent"] = "plugin" };
        if (card is not null)
        {
            var ensemble = options.GetBoolean("ensemble");
            var chat = WorkspaceIds.New();
            environment["chat"] = chat;
            environment["room"] = ensemble ? WorkspaceIds.New() : chat;
            environment["_ensemble"] = ensemble;
            FindDeclaration(package, "cards", "id", card);
        }
        JsonNode? result;
        if (url.Length == 0)
        {
            await runtime.Loop(package)!.EnsureIsolate(package).Ready.WaitAsync(ct);
            result = new JsonObject { ["loaded"] = true, ["packageName"] = package.PackageName };
        }
        else
        {
            var handler = package.Bind(url);
            var fixture = (JsonObject)input.DeepClone();
            if (fixture.GetString("name").Length == 0) fixture["name"] = "actions.invoke";
            fixture["url"] = url;
            result = await runtime.Invoke(new(package, handler, fixture, environment), ct);
        }
        if (card is null) return result;
        var request = runtime.Card(package, card, environment);
        request["width"] = options.GetInt32("width");
        request["dark"] = JsonValues.Clone(options?["dark"]);
        request["steps"] = options.GetArray("steps").DeepClone();
        request["capture_image"] = imageOutput.Length > 0;
        if (imageOutput.Length > 0) request["viewport"] = ImageViewport.Read(options ?? new())?.Describe();
        if (request.GetArray("steps").Count > 6 || JsonValues.Serialize(request.GetArray("steps")).Length > 6000)
            throw new ArgumentException("Supply up to six short preview steps.");
        var rendered = await port.CallHost("open_ui_preview", request, ct, TimeSpan.FromSeconds(25));
        var image = rendered.GetString("image_base64");
        rendered.Remove("image_base64");
        if (imageOutput.Length > 0 && rendered.GetObject("screenshot").GetBoolean("captured"))
        {
            if (image.Length is 0 or > 2_800_000) throw new InvalidDataException("Invalid preview screenshot size.");
            var png = Convert.FromBase64String(image);
            if (!png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("Invalid preview screenshot format.");
            Directory.CreateDirectory(files);
            await File.WriteAllBytesAsync(Path.Combine(files, Workspace.Safe(imageOutput)), png, ct);
        }
        rendered["id"] = card;
        if (url.Length > 0) rendered["handler_result"] = result;
        return rendered;
    }

    public PluginPackage Deploy(string path, string preset) => Install(path, inspect: false,
        PluginScope.Include(preset), new() { ["preset"] = preset });

    internal PluginPackage DevelopmentDeployment(string id, string signer)
    {
        var package = Installed(id);
        RequireDevelopmentSigner(package, signer);
        return package;
    }

    internal AssetContent OpenDevelopmentPublicAsset(string id, string signer, string url, JsonObject options, string preset)
    {
        lock (packageGate)
        {
            var package = DevelopmentDeployment(id, signer);
            var (packageName, assetId) = ParseAssetUrl(url);
            if (package.PackageName != packageName)
                throw new UnauthorizedAccessException("Choose a public Asset declared by this project's deployed package.");
            var asset = package.Assets?.GetValueOrDefault(assetId);
            if (asset is null)
                throw new ArgumentException("The installed generation does not declare this Asset ID. Check the manifest's assets declarations and the installed generation.");
            if (asset.Accessibility != "public")
                throw new UnauthorizedAccessException(
                    "Choose a public Asset declared by this project's deployed package. This Asset requires an access grant.");
            return OpenAsset(url, null, options, new() { ["preset"] = preset }) with { Writable = false };
        }
    }

    public void RemoveDevelopment(string id, string signer)
    {
        lock (packageGate)
        {
            var package = Volatile.Read(ref packages).FirstOrDefault(item => item.Id == id);
            if (package is null) return;
            RequireDevelopmentSigner(package, signer);
            Uninstall(id);
        }
    }

    private static void RequireDevelopmentSigner(PluginPackage package, string signer)
    {
        if (package.BuiltIn || !package.Signed || package.Signer != signer)
            throw new UnauthorizedAccessException("Only this project's signed deployment is accessible.");
    }
}
