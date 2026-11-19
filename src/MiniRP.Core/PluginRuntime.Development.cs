using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    public Task<string> PackDevelopment(string source, string path, string signer, CancellationToken ct) =>
        PluginArchive.Pack(source, path, (payload, token) => PluginDevelopmentSigner.Sign(signer, payload, port, token),
            draft =>
            {
                using var stream = File.OpenRead(draft);
                return ReadPackage(stream, ct: ct).Signer;
            }, ct);

    public async Task<JsonNode?> Debug(string path, string workspace, string preset, string url, JsonObject input, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        var package = ReadPackage(stream, path, ct: ct);
        await using var runtime = new PluginRuntime(workspace, port, emit,
            (_, _, _, _, _) => throw new UnauthorizedAccessException(), preview: true);
        runtime.SetPackages([package]);
        if (url.Length == 0)
        {
            await runtime.Loop(package)!.EnsureIsolate(package.Source).Ready.WaitAsync(ct);
            return new JsonObject { ["loaded"] = true, ["packageName"] = package.PackageName };
        }
        var handler = package.Bind(url);
        var environment = new JsonObject { ["preset"] = preset, ["session"] = "preview-" + JsonValues.NewId(), ["agent"] = "plugin" };
        return await runtime.Invoke(new(package, handler, input, environment), ct);
    }

    public PluginPackage Deploy(string path, string preset) => Install(path, inspect: false,
        PluginScope.Include(preset), new() { ["preset"] = preset });

    public void RemoveDevelopment(string id, string signer)
    {
        lock (packageGate)
        {
            var package = Volatile.Read(ref packages).FirstOrDefault(item => item.Id == id);
            if (package is null) return;
            if (package.BuiltIn || !package.Signed || package.Signer != signer)
                throw new UnauthorizedAccessException("Only this project's deployed plugin can be removed.");
            Uninstall(id);
        }
    }
}
