using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed partial class DesktopSecretStorage(string workspace)
{
    private const string Extension = ".sealed";
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string root = Path.Combine(workspace, "plugins", "secrets");

    public JsonObject Handle(JsonObject request, Func<string, string?>? researchDefault)
    {
        var owner = request.GetString("owner");
        var key = request.GetString("key");
        switch (request.GetString("operation"))
        {
            case "get":
                return new() { ["value"] = Get(owner, key) ?? (owner == PluginSecretNames.Base ? researchDefault?.Invoke(key) : null) };
            case "set":
                Set(owner, key, request.GetString("value"));
                return new();
            case "delete":
                try { File.Delete(Location(owner, key)); }
                catch (DirectoryNotFoundException) { }
                return new();
            case "list":
                var directory = DirectoryFor(owner);
                var ids = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "key-*" + Extension)
                        .Select(path => Path.GetFileNameWithoutExtension(path)[4..])
                        .Where(PluginSecretNames.ValidId).Order(StringComparer.Ordinal)
                    : Enumerable.Empty<string>();
                return new() { ["ids"] = new JsonArray(ids.Select(id => JsonValue.Create(id)).ToArray()) };
            default:
                throw new ArgumentException("Unknown secret storage operation.");
        }
    }

    public string? Get(string owner, string key)
    {
        byte[] encrypted;
        try { encrypted = File.ReadAllBytes(Location(owner, key)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var plaintext = Protect(encrypted, Scope(owner, key), encrypt: false);
        try
        {
            if (plaintext.Length == 0 || plaintext[0] != 1) throw new CryptographicException("Invalid secret storage data.");
            return Encoding.UTF8.GetString(plaintext.AsSpan(1));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Set(string owner, string key, string value)
    {
        var path = Location(owner, key);
        var plaintext = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        plaintext[0] = 1;
        Encoding.UTF8.GetBytes(value.AsSpan(), plaintext.AsSpan(1));
        try
        {
            WriteAtomically(path, Protect(plaintext, Scope(owner, key), encrypt: true), overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private string DirectoryFor(string owner) => Path.Combine(root, Convert.ToHexString(Scope(owner, "")));

    private string Location(string owner, string key) => PluginSecretNames.ValidId(key)
        ? Path.Combine(DirectoryFor(owner), "key-" + key + Extension)
        : throw new ArgumentException("Invalid secret ID.");

    private static byte[] Scope(string owner, string key) => SHA256.HashData(Encoding.UTF8.GetBytes(owner + "\0" + key));

    private static void WriteAtomically(string path, byte[] data, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }

    private static byte[] Protect(byte[] data, byte[] scope, bool encrypt)
    {
        if (OperatingSystem.IsWindows()) return WindowsProtect(data, scope, encrypt);

        var key = LoadMasterKey(encrypt);
        try
        {
            using var cipher = new AesGcm(key, 16);
            if (encrypt)
            {
                var output = new byte[28 + data.Length];
                var nonce = output.AsSpan(0, 12);
                RandomNumberGenerator.Fill(nonce);
                cipher.Encrypt(nonce, data, output.AsSpan(28), output.AsSpan(12, 16), scope);
                return output;
            }
            if (data.Length < 28) throw new CryptographicException("Invalid secret storage data.");
            var plaintext = new byte[data.Length - 28];
            cipher.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plaintext, scope);
            return plaintext;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] LoadMasterKey(bool create)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(profile)) throw new IOException("Plugin secrets require a local user profile.");
        var masterPath = Path.Combine(profile, "MiniRP", "plugin-secrets.key");
        if (!File.Exists(masterPath) && create)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            try { WriteAtomically(masterPath, key, overwrite: false); }
            catch (IOException) when (File.Exists(masterPath)) { }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        if (File.GetUnixFileMode(masterPath) != PrivateFile)
            throw new IOException("The plugin secret storage key must be accessible only to the current user.");
        return File.ReadAllBytes(masterPath);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, nint pointer)
    {
        public readonly int Length = length;
        public readonly nint Pointer = pointer;
    }

    private static unsafe byte[] WindowsProtect(byte[] value, byte[] scope, bool encrypt)
    {
        fixed (byte* inputPointer = value, scopePointer = scope)
        {
            var input = new DataBlob(value.Length, (nint)inputPointer);
            var entropy = new DataBlob(scope.Length, (nint)scopePointer);
            const int noUi = 1;
            var success = encrypt
                ? CryptProtectData(in input, 0, in entropy, 0, 0, noUi, out var output)
                : CryptUnprotectData(in input, 0, in entropy, 0, 0, noUi, out output);
            if (success == 0) throw new CryptographicException("Windows could not open plugin secret storage.");
            try
            {
                var bytes = new byte[output.Length];
                Marshal.Copy(output.Pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (!encrypt) CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Pointer, output.Length));
                LocalFree(output.Pointer);
            }
        }
    }

    [LibraryImport("crypt32.dll")]
    private static partial int CryptProtectData(in DataBlob value, nint description, in DataBlob entropy, nint reserved,
        nint prompt, int flags, out DataBlob result);

    [LibraryImport("crypt32.dll")]
    private static partial int CryptUnprotectData(in DataBlob value, nint description, in DataBlob entropy, nint reserved,
        nint prompt, int flags, out DataBlob result);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
