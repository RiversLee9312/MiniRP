using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MiniRP.Cli;

internal sealed class BrowserProcess(Process process) : IAsyncDisposable
{
    private readonly Process child = process;

    public Stream StandardInput => child.StandardInput.BaseStream;
    public Stream StandardOutput => child.StandardOutput.BaseStream;

    public static BrowserProcess Start(string executable, params string[] args) => Launch(StartInfo(executable, args));

    public static BrowserProcess StartWebView2(string executable, string runtime, params string[] args)
    {
        var info = StartInfo(executable, args);
        info.Environment["MINIRP_WEBVIEW2_EXECUTABLE"] = runtime;
        return Launch(info);
    }

    public static BrowserProcess StartDriver(string executable, string workingDirectory, IReadOnlyList<string> args)
    {
        var info = StartInfo(executable, args);
        info.WorkingDirectory = workingDirectory;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return Launch(info);
    }

    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return info;
    }

    private static BrowserProcess Launch(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new IOException("Could not start the browser process.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return new(process);
    }

    public static async Task<string> Read(string executable, CancellationToken token, params string[] args)
    {
        var info = StartInfo(executable, args);
        info.RedirectStandardOutput = true;
        await using var browser = Launch(info);
        var output = await browser.child.StandardOutput.ReadToEndAsync(token);
        if (await browser.Exit(token) != 0) throw new IOException("The browser discovery command failed.");
        return output;
    }

    public async Task<int> Exit(CancellationToken token)
    {
        await child.WaitForExitAsync(token);
        return child.ExitCode;
    }

    public async Task<Uri> Endpoint(string profile, bool chromium, CancellationToken token)
    {
        var file = Path.Combine(profile, chromium ? "DevToolsActivePort" : "minirp-cdp");
        while (!child.HasExited)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(file))
                {
                    var contents = (await File.ReadAllTextAsync(file, token)).Trim();
                    var endpoint = chromium ? "http://127.0.0.1:" + contents.Split('\n')[0].Trim() : contents;
                    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
                        uri.Host == "127.0.0.1" && uri.Port is > 1023 and < 65536 && uri.PathAndQuery == "/" &&
                        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0)
                        return uri;
                }
            }
            catch (IOException) when (!token.IsCancellationRequested) { }
            await Task.Delay(80, token);
        }
        throw new IOException("The browser exited before it became ready.");
    }

    public async ValueTask DisposeAsync()
    {
        using (child)
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or OperationCanceledException) { }
        }
    }

    public static void Delete(DirectoryInfo directory)
    {
        try { directory.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
