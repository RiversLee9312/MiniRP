using System.ComponentModel;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal delegate Task<string> BrowserSetup(CefRuntimeRelease release,
    Func<Action<CefInstallProgress>, CancellationToken, Task<string>> install, CancellationToken token);

internal sealed class DesktopBrowserRuntime(string workspace, string userData, string preference)
{
    internal const string ProbeTitle = "MiniRP browser capability test";
    private static readonly HttpClient Loopback = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly SemaphoreSlim gate = new(1);
    private readonly CefRuntimeInstaller cef = new(Path.Combine(userData, "browser-runtimes"), BrowserSelection.Helper("minirp-cef"));
    private BrowserSelection? selected;

    public async Task<BrowserSelection> Select(Func<BrowserSelection, CancellationToken, Task> verifyAgent,
        BrowserSetup? setup, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (selected is not null) return selected;

            var candidates = preference == "cef" ? [] : await BrowserSelection.Discover(workspace, preference, token);
            foreach (var browser in candidates)
            {
                try
                {
                    return selected = await Verify(browser, verifyAgent, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    error is IOException or Win32Exception or InvalidOperationException or HttpRequestException or OperationCanceledException) { }
            }

            if (await cef.FindAsync(token) is { } available)
            {
                var browser = new BrowserSelection(BrowserFamily.Cef, available);
                return selected = await Verify(browser, verifyAgent, token);
            }
            if (!cef.IsAvailable || cef.Release is not { } release)
                throw new IOException("No compatible desktop browser was found. This build does not include a CEF host for this platform.");
            if (setup is null)
                throw new IOException("Open MiniRP's terminal or WebUI to approve downloading the browser runtime.");
            var path = await setup(release, (progress, ct) => cef.InstallAsync(progress,
                (executable, validate) => Verify(new(BrowserFamily.Cef, executable), verifyAgent, validate), ct), token);
            return selected = new(BrowserFamily.Cef, path);
        }
        finally { gate.Release(); }
    }

    private static async Task<BrowserSelection> Verify(BrowserSelection browser, Func<BrowserSelection, CancellationToken, Task> verifyAgent,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        if (browser.Family == BrowserFamily.Chromium)
        {
            if (!await browser.IsRecentChromium(ct))
                throw new IOException("The Chromium runtime version is too old.");
            if (BrowserSelection.HasDisplay)
                await VerifyChromiumWindow(browser, ct);
        }
        else
        {
            var profile = browser.NewProfile();
            try
            {
                await using var process = BrowserProcess.Start(browser.Executable, "probe", profile.FullName);
                if (await process.Exit(ct) != 0)
                    throw new IOException(browser.Name + " failed its runtime probe.");
                if (browser.Family == BrowserFamily.Cef && BrowserSelection.HasDisplay)
                {
                    await using var window = BrowserProcess.Start(browser.Executable, "probe-window", profile.FullName);
                    if (await window.Exit(ct) != 0)
                        throw new IOException("CEF could not open a desktop window.");
                }
                if (browser.Family == BrowserFamily.WebView2)
                {
                    var binary = (await File.ReadAllTextAsync(Path.Combine(profile.FullName, "minirp-runtime"), ct)).Trim();
                    var release = (await File.ReadAllTextAsync(Path.Combine(profile.FullName, "minirp-version"), ct)).Trim();
                    if (!Path.IsPathFullyQualified(binary) || !File.Exists(binary) || !Version.TryParse(release, out var version) ||
                        version.Major < BrowserSelection.MinimumMajor)
                        throw new IOException("The WebView2 Evergreen runtime is unsupported.");
                    browser = browser with { RuntimeBinary = binary };
                }
            }
            finally { BrowserProcess.Delete(profile); }
        }
        await verifyAgent(browser, ct);
        return browser;
    }

    private static async Task VerifyChromiumWindow(BrowserSelection browser, CancellationToken token)
    {
        var profile = browser.NewProfile();
        try
        {
            var url = "data:text/html," + Uri.EscapeDataString("<title>" + ProbeTitle + "</title><input type=file>");
            await using var process = browser.StartWindow(profile.FullName, url, probe: true);
            var endpoint = await process.Endpoint(profile.FullName, chromium: true, token);
            while (true)
            {
                try
                {
                    var json = await Loopback.GetStringAsync(new Uri(endpoint, "json/list"), token);
                    if (JsonNode.Parse(json) is JsonArray targets && targets.Any(item => item.GetString("title") == ProbeTitle))
                        return;
                }
                catch (HttpRequestException) when (!token.IsCancellationRequested) { }
                await Task.Delay(100, token);
            }
        }
        finally { BrowserProcess.Delete(profile); }
    }
}
