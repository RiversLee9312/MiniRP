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
    public static long Create(nint root)
    {
        try
        {
            var engine = new Engine(Marshal.PtrToStringUTF8(root)!);
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

    [UnmanagedCallersOnly(EntryPoint = "mc_release", CallConvs = [typeof(CallConvCdecl)])]
    public static void Release(nint value) => Marshal.FreeCoTaskMem(value);

    [UnmanagedCallersOnly(EntryPoint = "mc_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void Destroy(long id)
    {
        if (Engines.TryRemove(id, out var engine))
        {
            // Hosts invoke teardown off their UI thread and retain the library until the final save completes.
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
