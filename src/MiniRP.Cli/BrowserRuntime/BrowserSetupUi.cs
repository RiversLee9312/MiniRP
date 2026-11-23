using System.Diagnostics;
using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

internal sealed class BrowserSetupDeferredException() : IOException("Browser download deferred. The next browser request can try again.");

internal static class BrowserSetupUi
{
    private const string Title = "MiniRP browser runtime";
    private const string Continue = "Download and continue";
    private const string Later = "Later";

    public static async Task<string> Desktop(CefRuntimeRelease release,
        Func<Action<CefInstallProgress>, CancellationToken, Task<string>> install, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var helper = BrowserSelection.Helper("minirp-webview2");
            await using var prompt = BrowserProcess.Start(helper, "confirm", release.DownloadBytes.ToString(CultureInfo.InvariantCulture));
            var result = await prompt.Exit(token);
            if (result != 0) throw Deferred();
            var control = Directory.CreateTempSubdirectory("minirp-browser-setup-");
            try
            {
                await using var progress = BrowserProcess.Start(helper, "progress", control.FullName);
                return await WithWindow(install, update =>
                {
                    var next = Path.Combine(control.FullName, "next");
                    var percent = update.Phase == "downloading" ? Percent(update) : -1;
                    File.WriteAllText(next, percent.ToString(CultureInfo.InvariantCulture));
                    File.Move(next, Path.Combine(control.FullName, "minirp-progress"), overwrite: true);
                }, progress.Exit, () => File.WriteAllText(Path.Combine(control.FullName, "minirp-done"), ""), token);
            }
            finally { BrowserProcess.Delete(control); }
        }
        if (BrowserSelection.FindOnPath("zenity") is { } zenity)
        {
            await using var prompt = BrowserProcess.Start(zenity, "--question", "--no-markup", "--title=" + Title,
                "--text=" + Describe(release), "--ok-label=" + Continue, "--cancel-label=" + Later);
            if (await prompt.Exit(token) != 0) throw Deferred();
            var info = new ProcessStartInfo(zenity) { UseShellExecute = false, RedirectStandardInput = true };
            foreach (var argument in new[] { "--progress", "--no-markup", "--auto-close", "--title=" + Title,
                    "--text=Preparing CEF…", "--percentage=0" })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("Could not open download progress.");
            async Task<int> Exit(CancellationToken stopping)
            {
                await process.WaitForExitAsync(stopping);
                return process.ExitCode;
            }
            void Write(string line)
            {
                try { process.StandardInput.WriteLine(line); process.StandardInput.Flush(); }
                catch (IOException) { }
            }
            try
            {
                return await WithWindow(install, update => Write("# " + Describe(update) + "\n" + Percent(update)),
                    Exit, () => Write("100"), token);
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        if (Console.IsInputRedirected)
            throw new IOException("Open the terminal interface to approve the browser download.");
        Console.Error.WriteLine(Describe(release));
        Console.Error.WriteLine("[D] " + Continue + "  [L] " + Later);
        if (!string.Equals(await Console.In.ReadLineAsync(token), "d", StringComparison.OrdinalIgnoreCase))
            throw Deferred();
        return await install(update => Console.Error.Write("\r" + Describe(update).PadRight(75)), token);
    }

    private static async Task<string> WithWindow(Func<Action<CefInstallProgress>, CancellationToken, Task<string>> install,
        Action<CefInstallProgress> progress, Func<CancellationToken, Task<int>> waitForWindow, Action complete, CancellationToken token)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        var result = install(progress, stopping.Token);
        var closed = waitForWindow(stopping.Token);
        try
        {
            if (await Task.WhenAny(result, closed) == closed && !result.IsCompleted)
                await stopping.CancelAsync();
            return await result;
        }
        finally
        {
            complete();
            await Task.WhenAny(closed, Task.Delay(600));
            await stopping.CancelAsync();
            try { await closed; }
            catch (OperationCanceledException) { }
        }
    }

    public static async Task<string> Terminal(IApplication app, CefRuntimeRelease release,
        Func<Action<CefInstallProgress>, CancellationToken, Task<string>> install, CancellationToken token)
    {
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            if (token.IsCancellationRequested) return;
            using var dialog = new Dialog { Title = Title, Width = Dim.Percent(85), Height = 10 };
            var choice = false;
            var text = new Label { X = 1, Y = 1, Width = Dim.Fill(1), Height = 4, Text = Describe(release) };
            var later = new Button { Text = Later };
            var download = new Button { Text = Continue };
            later.Accepted += (_, _) => app.RequestStop(dialog);
            download.Accepted += (_, _) => { choice = true; app.RequestStop(dialog); };
            dialog.Add(text);
            dialog.AddButton(later);
            dialog.AddButton(download);
            using var registration = token.Register(() => app.Invoke(() => app.RequestStop(dialog)));
            app.Run(dialog);
            accepted.TrySetResult(choice);
        });
        if (!await accepted.Task.WaitAsync(token)) throw Deferred();

        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            if (token.IsCancellationRequested) return;
            var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var dialog = new Dialog { Title = Title, Width = Dim.Percent(75), Height = 7 };
            var label = new Label { X = 1, Y = 1, Width = Dim.Fill(1), Text = "Preparing CEF…" };
            var cancel = new Button { Text = "Cancel" };
            cancel.Accepted += (_, _) => app.RequestStop(dialog);
            dialog.Add(label);
            dialog.AddButton(cancel);
            var download = Task.Run(async () =>
            {
                try
                {
                    var path = await install(update => app.Invoke(() =>
                    {
                        if (!stopping.IsCancellationRequested) label.Text = Describe(update);
                    }), stopping.Token);
                    result.TrySetResult(path);
                }
                catch (Exception error) { result.TrySetException(error); }
                finally { app.Invoke(() => { if (!stopping.IsCancellationRequested) app.RequestStop(dialog); }); }
            });
            using var registration = token.Register(() => app.Invoke(() => app.RequestStop(dialog)));
            app.Run(dialog);
            stopping.Cancel();
            _ = download.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default);
        });
        return await result.Task.WaitAsync(token);
    }

    internal static BrowserSetupDeferredException Deferred() => new();
    private static string Describe(CefRuntimeRelease release) =>
        $"MiniRP needs CEF to open its WebUI window and let FetchAgent read pages. Download size: {release.DownloadBytes / 1048576d:F0} MiB.";
    private static int Percent(CefInstallProgress p) => p.Phase == "downloading" && p.TotalBytes > 0
        ? (int)Math.Clamp(100d * p.BytesReceived / p.TotalBytes, 0, 99) : 99;
    private static string Describe(CefInstallProgress p) => p.Phase switch
    {
        "downloading" => $"Downloading CEF… {Percent(p)}%",
        "verifying" => "Verifying the browser download…",
        "extracting" => "Installing CEF…",
        _ => "Checking browser capabilities…"
    };
}
