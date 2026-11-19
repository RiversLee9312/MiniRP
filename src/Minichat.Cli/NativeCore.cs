using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Minichat.Protocol;

namespace Minichat.Cli;

internal sealed partial class NativeCore : IDisposable
{
    private readonly long handle;

    static NativeCore()
    {
        var sqlite = OperatingSystem.IsWindows() ? "e_sqlite3.dll" : OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so";
        // A standalone NativeAOT library does not inherit the managed host's NuGet native search paths.
        // Load its packaged dependency once, retaining it for the process lifetime.
        NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, sqlite));
    }

    public NativeCore(string root)
    {
        handle = Create(root);
        if (handle == 0)
        {
            throw new IOException("Could not initialize the native chat engine.");
        }
    }

    public void Send(JsonObject command)
    {
        var value = Dispatch(handle, JsonValues.Serialize(command));
        var result = JsonValues.ParseObject(Read(value));
        if (result.ContainsKey("error"))
        {
            throw new InvalidOperationException(result.GetString("error"));
        }
    }

    public JsonArray Poll() => JsonNode.Parse(Read(PollNative(handle)))!.AsArray();

    internal byte[] TakeBinary(string id)
    {
        var buffer = TakeBinaryNative(handle, id, out int length);
        if (buffer == 0) throw new IOException("Binary chunk is unavailable.");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            return bytes;
        }
        finally { Release(buffer); }
    }

    internal unsafe void WriteBinary(string id, byte[] bytes)
    {
        fixed (byte* buffer = bytes)
            if (WriteBinaryNative(handle, id, (nint)buffer, bytes.Length) == 0)
                throw new IOException("Binary transfer was rejected.");
    }

    private static string Read(nint value)
    {
        try
        {
            return Marshal.PtrToStringUTF8(value)!;
        }
        finally { Release(value); }
    }

    public void Dispose() => Destroy(handle);

    [LibraryImport("libminichatcore", EntryPoint = "mc_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial long Create(string root);
    [LibraryImport("libminichatcore", EntryPoint = "mc_dispatch", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Dispatch(long handle, string json);
    [LibraryImport("libminichatcore", EntryPoint = "mc_poll")]
    private static partial nint PollNative(long handle);
    [LibraryImport("libminichatcore", EntryPoint = "mc_binary_take", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint TakeBinaryNative(long handle, string id, out int length);
    [LibraryImport("libminichatcore", EntryPoint = "mc_binary_write", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int WriteBinaryNative(long handle, string id, nint buffer, int length);
    [LibraryImport("libminichatcore", EntryPoint = "mc_release")]
    private static partial void Release(nint value);
    [LibraryImport("libminichatcore", EntryPoint = "mc_destroy")]
    private static partial void Destroy(long handle);
}
