using System.Text;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Core;

internal static class PluginSecrets
{
    internal static Task<JsonObject> ReadResearchKey(HostPort port, string provider, CancellationToken ct) =>
        Call(port, PluginSecretNames.Base, "get", provider, null, ct);

    internal static async Task<JsonNode?> Access(HostPort port, PluginPackage package, string method, JsonObject args,
        CancellationToken ct)
    {
        if (!package.Has("secrets") || !(package.Signed || package.BuiltIn))
            throw new UnauthorizedAccessException("Secret storage requires a signed plugin with the secrets permission.");
        if (method is not ("get" or "set" or "list" or "delete"))
            throw new ArgumentException("Unknown secret storage operation.");
        var key = "";
        if (method != "list" && (args["id"] is not JsonValue id || !id.TryGetValue<string>(out var text) ||
            !PluginSecretNames.ValidId(key = text)))
            throw new ArgumentException("Use a secret ID of 1 to 128 lowercase letters, digits or hyphens.");
        string? value = null;
        if (method == "set" && (args["secret"] is not JsonValue secret || !secret.TryGetValue(out value) ||
            value is null || Encoding.UTF8.GetByteCount(value) > 16 * 1024))
            throw new ArgumentException("Use a secret string of up to 16 KiB.");

        var owner = package.BuiltIn ? "builtin-" + package.Id : "signed-" + package.StorageKey;
        var result = await Call(port, owner, method, key, value, ct);
        return method switch
        {
            "get" => JsonValues.Clone(result["value"]),
            "list" => JsonValues.Clone(result["ids"]) ?? new JsonArray(),
            _ => null
        };
    }

    private static Task<JsonObject> Call(HostPort port, string owner, string operation, string key, string? value,
        CancellationToken ct)
    {
        var request = new JsonObject { ["owner"] = owner, ["operation"] = operation, ["key"] = key };
        if (operation == "set") request["value"] = value;
        return port.CallHost("plugin_secret", request, ct, TimeSpan.FromSeconds(10));
    }
}
