using System.Diagnostics;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class AssetCommandHost(string workspace)
{
    internal static string Executable(string tool)
    {
        var name = tool == "curl" ? "minirp-curl" : tool;
        return Path.Combine(AppContext.BaseDirectory, tool == "curl" ? "curl" : "ffmpeg",
            OperatingSystem.IsWindows() ? name + ".exe" : name);
    }

    internal async Task<JsonObject> Handle(JsonObject request, CancellationToken ct)
    {
        var run = request.GetString("run");
        var tool = request.GetString("tool", "curl");
        if (run.Length != 12 || !run.All(Uri.IsHexDigit) || tool is not ("curl" or "ffmpeg" or "ffprobe"))
            throw new IOException("Invalid native command request.");
        var media = tool != "curl";
        var executable = Executable(tool);
        if (!File.Exists(executable)) throw new IOException($"Install the MiniRP build with the {tool} runtime.");
        var directory = Path.Combine(workspace, media ? "ffmpeg-runs" : "curl-runs", run);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!media) info.ArgumentList.Add("--disable");
        foreach (var argument in request.GetArray("args")) info.ArgumentList.Add(JsonValues.GetText(argument));
        foreach (var key in new[] { "CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE", "QLOGDIR" }) info.Environment.Remove(key);
        info.Environment["LDAPNOINIT"] = "1";
        if (media)
        {
            info.Environment.Remove("FFREPORT");
            if (OperatingSystem.IsLinux()) info.Environment["LD_LIBRARY_PATH"] = Path.GetDirectoryName(executable);
        }
        var prefix = media ? "MINIRP_MEDIA" : "MINIRP_CURL";
        var index = 0;
        foreach (var file in request.GetArray("files"))
        {
            info.Environment[$"{prefix}_URL_{index}"] = file.GetString("url");
            info.Environment[$"{prefix}_FILE_{index}"] = file.GetString("name");
            info.Environment[$"{prefix}_WRITE_{index}"] = file.GetBoolean("write") ? "1" : "0";
            index++;
        }
        info.Environment.Remove($"{prefix}_URL_{index}");

        await using var output = File.Create(Path.Combine(directory, "stdout"), 65536, FileOptions.Asynchronous);
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {tool}.");
        var seconds = request.GetInt32("seconds");
        var started = Stopwatch.GetTimestamp();
        var sendInput = SendInput(process, Path.Combine(directory, "stdin"), ct);
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        var errors = ReadErrors(process.StandardError, ct);
        try
        {
            var completion = process.WaitForExitAsync(ct);
            while (!completion.IsCompleted)
            {
                if (seconds > 0 && Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds)
                    throw new IOException($"{tool} timed out.");
                if (Directory.EnumerateFiles(directory).Any(file => new FileInfo(file).Length > 128L * 1024 * 1024))
                    throw new IOException($"{tool} exceeded the Asset size limit.");
                await Task.Delay(40, ct);
            }
            await completion;
            await sendInput;
            await copy;
            var stderr = await errors;
            return new() { ["exit_code"] = process.ExitCode, ["stderr"] = stderr };
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<string> ReadErrors(StreamReader reader, CancellationToken ct)
    {
        var prefix = new char[2048];
        var count = await reader.ReadBlockAsync(prefix, ct);
        await reader.BaseStream.CopyToAsync(Stream.Null, ct);
        return new string(prefix, 0, count).Trim();
    }

    private static async Task SendInput(Process process, string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return;
            await using var input = File.OpenRead(path);
            await input.CopyToAsync(process.StandardInput.BaseStream, ct);
        }
        catch (IOException) when (process.HasExited) { }
        finally { process.StandardInput.Close(); }
    }
}
