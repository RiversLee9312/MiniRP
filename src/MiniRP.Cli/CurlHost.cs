using System.Diagnostics;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class CurlHost(string workspace)
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "curl", "minirp-curl.exe");

    internal async Task<JsonObject> Handle(JsonObject request, CancellationToken ct)
    {
        var run = request.GetString("run");
        if (run.Length != 12 || !run.All(Uri.IsHexDigit)) throw new IOException("Invalid curl request.");
        var directory = Path.Combine(workspace, "curl-runs", run);
        var info = new ProcessStartInfo(Executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--disable");
        foreach (var argument in request.GetArray("args")) info.ArgumentList.Add(JsonValues.GetText(argument));
        foreach (var key in new[] { "CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE", "QLOGDIR" }) info.Environment.Remove(key);
        info.Environment["LDAPNOINIT"] = "1";
        var index = 0;
        foreach (var file in request.GetArray("files"))
        {
            info.Environment[$"MINIRP_CURL_URL_{index}"] = file.GetString("url");
            info.Environment[$"MINIRP_CURL_FILE_{index}"] = file.GetString("name");
            info.Environment[$"MINIRP_CURL_WRITE_{index}"] = file.GetBoolean("write") ? "1" : "0";
            index++;
        }

        await using var output = File.Create(Path.Combine(directory, "stdout"), 65536, FileOptions.Asynchronous);
        using var process = Process.Start(info) ?? throw new IOException("Could not start curl.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.GetInt32("seconds", 60)));
        var sendInput = SendInput(process, Path.Combine(directory, "stdin"), timeout.Token);
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        var errors = ReadErrors(process.StandardError, timeout.Token);
        try
        {
            var completion = process.WaitForExitAsync(timeout.Token);
            while (!completion.IsCompleted)
            {
                if (Directory.EnumerateFiles(directory).Any(file => new FileInfo(file).Length > 128L * 1024 * 1024))
                    throw new IOException("curl exceeded the Asset size limit.");
                await Task.Delay(40, timeout.Token);
            }
            await completion;
            await sendInput;
            await copy;
            var stderr = await errors;
            return new() { ["exit_code"] = process.ExitCode, ["stderr"] = stderr };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("curl timed out."); }
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
