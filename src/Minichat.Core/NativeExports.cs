using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Minichat.Core;

public static class NativeExports
{
    private static readonly ConcurrentDictionary<long, Engine> Engines = new();
    private static long nextId;
    [UnmanagedCallersOnly(EntryPoint = "mc_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int Version() => 1;

    [UnmanagedCallersOnly(EntryPoint = "mc_create", CallConvs = [typeof(CallConvCdecl)])]
    public static long Create(nint root) => Create(root, seedDefaultPreset: true);

    [UnmanagedCallersOnly(EntryPoint = "mc_create_with_options", CallConvs = [typeof(CallConvCdecl)])]
    public static long CreateWithOptions(nint root, int seedDefaultPreset) => Create(root, seedDefaultPreset != 0);

    private static long Create(nint root, bool seedDefaultPreset)
    {
        try
        {
            var engine = new Engine(Marshal.PtrToStringUTF8(root)!, seedDefaultPreset);
            var id = Interlocked.Increment(ref nextId);
            Engines[id] = engine;
            return id;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "mc_dispatch", CallConvs = [typeof(CallConvCdecl)])]
    public static nint Dispatch(long id, nint json)
    {
        try
        {
            Engines[id].Dispatch(JsonValues.ParseObject(Marshal.PtrToStringUTF8(json)!));
            return Marshal.StringToCoTaskMemUTF8("{}");
        }
        catch (Exception error) { return Marshal.StringToCoTaskMemUTF8(JsonValues.Serialize(JsonValues.Error(error.Message))); }
    }

    [UnmanagedCallersOnly(EntryPoint = "mc_poll", CallConvs = [typeof(CallConvCdecl)])]
    public static nint Poll(long id)
    {
        try
        {
            return Marshal.StringToCoTaskMemUTF8(JsonValues.Serialize(Engines[id].Poll()));
        }
        catch (Exception error) { return Marshal.StringToCoTaskMemUTF8(JsonValues.Serialize(new System.Text.Json.Nodes.JsonArray(JsonValues.Error(error.Message)))); }
    }

    [UnmanagedCallersOnly(EntryPoint = "mc_binary_take", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe nint TakeBinary(long id, nint nonce, int* length)
    {
        *length = 0;
        try
        {
            if (Engines[id].Port.TakeBinary(Marshal.PtrToStringUTF8(nonce)!) is not { } bytes) return 0;
            var buffer = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            *length = bytes.Length;
            return buffer;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "mc_binary_write", CallConvs = [typeof(CallConvCdecl)])]
    public static int WriteBinary(long id, nint nonce, nint buffer, int length)
    {
        if (length is <= 0 or > HostPort.ChunkBytes) return 0;
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            Engines[id].Port.WriteBinary(Marshal.PtrToStringUTF8(nonce)!, bytes);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "mc_release", CallConvs = [typeof(CallConvCdecl)])]
    public static void Release(nint value) => Marshal.FreeCoTaskMem(value);

    [UnmanagedCallersOnly(EntryPoint = "mc_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void Destroy(long id)
    {
        if (Engines.TryRemove(id, out var engine))
        {
            // Hosts invoke teardown off their UI thread and retain the library until the final save completes.
            try { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { }
        }
    }
}
