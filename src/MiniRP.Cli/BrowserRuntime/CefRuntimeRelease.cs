using System.Runtime.InteropServices;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed record CefRuntimeRelease(string Version, string Platform, long DownloadBytes, string ArchiveSha256, string RuntimeSha256)
{
    public string ArchiveRoot => $"cef_binary_{Version}_{Platform}_minimal";
    public Uri DownloadUrl => new("https://cef-builds.spotifycdn.com/" + ArchiveRoot + ".tar.bz2");
    public string LibraryName => Platform.StartsWith("windows", StringComparison.Ordinal) ? "libcef.dll" : "libcef.so";

    internal static CefRuntimeRelease? ForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return null;
        var platform = (OperatingSystem.IsWindows() ? "windows" : "linux") + (RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "64",
            Architecture.Arm64 => "arm64",
            _ => ""
        });
        using var resource = typeof(CefRuntimeRelease).Assembly.GetManifestResourceStream("MiniRP.CefReleases.json")
            ?? throw new InvalidOperationException("The CEF release catalog is missing.");
        using var reader = new StreamReader(resource);
        var manifest = JsonValues.ParseObject(reader.ReadToEnd());
        var entry = manifest.GetObject("platforms").GetObject(platform);
        var archive = entry.GetString("sha256");
        var runtime = entry.GetString("runtimeSha256");
        var size = entry["size"]?.GetValue<long>() ?? 0;
        return IsSha256(archive) && IsSha256(runtime) && size > 0
            ? new(manifest.GetString("version"), platform, size, archive, runtime)
            : null;
    }

    internal static bool IsSha256(string value) =>
        value.Length == 64 && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
}
