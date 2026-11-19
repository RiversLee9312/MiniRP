using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace MiniRP.Core;

/// <summary>Identifies presets, rooms and chats with canonical UUIDs.</summary>
public static class WorkspaceIds
{
    private static readonly Guid LegacyNamespace = new("d84b68b1-6e24-5c56-8d52-54bb4e94c5dd");

    public static string New() => Guid.NewGuid().ToString("D");

    public static bool IsValid(string id) =>
        Guid.TryParseExact(id, "D", out var guid) && guid != Guid.Empty &&
        string.Equals(id, guid.ToString("D"), StringComparison.Ordinal);

    public static string Require(string id) => IsValid(id) ? id :
        throw new ArgumentException("Preset, Room and Chat identifiers must be canonical lowercase UUIDs.");

    // Legacy conversion is restricted to workspace migration and restoring saved app state.
    public static string Preset(string id) => Legacy("preset/", id);
    public static string Room(string canonicalPreset, string id) => Legacy("room/" + Require(canonicalPreset) + "/", id);
    public static string Chat(string canonicalRoom, string id) => Legacy("chat/" + Require(canonicalRoom) + "/", id);

    private static string Legacy(string path, string id)
    {
        if (Guid.TryParseExact(id, "D", out var valid) && valid != Guid.Empty) return valid.ToString("D");
        Workspace.Safe(id);
        var name = Encoding.UTF8.GetBytes(path + id);
        var key = new byte[16 + name.Length];
        LegacyNamespace.TryWriteBytes(key, bigEndian: true, out _);
        name.CopyTo(key, 16);
        // The Linux-bionic host does not ship the .NET OpenSSL backend.
        var digest = new Sha1Digest();
        digest.BlockUpdate(key, 0, key.Length);
        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        hash[6] = (byte)(hash[6] & 0x0f | 0x50);
        hash[8] = (byte)(hash[8] & 0x3f | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }
}
