using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MiniRP.Cli;

/// <summary>Runs Web Crypto in a private Node process containing no plugin source.</summary>
internal sealed class PluginWebCryptoProcess : IDisposable
{
    private readonly Process process;

    internal PluginWebCryptoProcess(string provider)
    {
        const string run = """
            (async () => {
                const readline = require("node:readline");
                const execute = CREATE_PROVIDER(require("node:crypto").webcrypto);
                for await (const line of readline.createInterface({input: process.stdin, crlfDelay: Infinity})) {
                    process.stdout.write((await execute(line)) + "\n");
                }
            })().catch(() => { process.exitCode = 1; });
            """;
        var info = new ProcessStartInfo(BrowserSelection.Node)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        info.Environment.Remove("NODE_OPTIONS");
        info.Environment.Remove("NODE_PATH");
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(run.Replace("CREATE_PROVIDER", provider, StringComparison.Ordinal));
        process = Process.Start(info) ?? throw new IOException("Could not start the Web Crypto provider.");
        process.BeginErrorReadLine();
    }

    internal async Task<string> Run(string request, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(request.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        return await process.StandardOutput.ReadLineAsync(token)
            ?? throw new IOException("The Web Crypto provider exited.");
    }

    public void Dispose()
    {
        using (process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }
}
