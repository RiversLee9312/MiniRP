using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MiniRP.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MiniRP.Cli;

/// <summary>Uses the official MCP SDK and Playwright's refs, auto-waiting and isolated contexts.</summary>
internal sealed partial class BrowserHost(string root, DesktopBrowserRuntime runtime) : IAsyncDisposable
{
    public BrowserHost(string root, string browserName = "") : this(root, new DesktopBrowserRuntime(root, root, browserName)) { }

    public BrowserSetup? Setup { get; set; }

    private sealed record Browser(McpClient Client, DirectoryInfo Output, DirectoryInfo Profile, BrowserProcess Driver,
        BrowserProcess? Embedded);
    private sealed class Job
    {
        public SemaphoreSlim Gate { get; } = new(1);
        public Browser? Browser { get; set; }
        public string? StartupError { get; set; }
        public bool Closed { get; set; }
    }
    private readonly ConcurrentDictionary<string, Job> jobs = new(StringComparer.Ordinal);

    public async Task<JsonObject> Execute(JsonObject request, CancellationToken token, Action? preparing = null)
    {
        var id = request.GetString("job");
        var tool = request.GetString("tool");
        var closing = tool == "browser_close";
        var job = closing ? jobs.GetValueOrDefault(id) : jobs.GetOrAdd(id, _ => new());
        if (job is null) return new() { ["closed"] = true };
        await job.Gate.WaitAsync(token);
        try
        {
            if (closing)
            {
                job.Closed = true;
                if (jobs.TryRemove(new KeyValuePair<string, Job>(id, job)) && job.Browser is { } closed)
                    await Close(closed);
                return new() { ["closed"] = true };
            }
            if (job.Closed) throw new InvalidOperationException("The browser job has ended.");
            if (job.StartupError is { } failure) throw new IOException(failure);
            if (job.Browser is null)
            {
                try
                {
                    preparing?.Invoke();
                    var selection = await runtime.Select(VerifyAgent, Setup, token);
                    using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
                    startup.CancelAfter(TimeSpan.FromSeconds(30));
                    job.Browser = await Open(selection, startup.Token);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    job.StartupError = error.Message;
                    throw;
                }
            }
            var browser = job.Browser;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            operation.CancelAfter(TimeSpan.FromMinutes(2));
            if (tool == "capture_page")
            {
                return await Capture(browser, operation.Token);
            }
            var args = request.GetObject("arguments");
            var values = new Dictionary<string, object?>();
            string target;
            switch (tool)
            {
                case "browser_navigate":
                    target = tool;
                    values["url"] = args.GetString("url");
                    break;
                case "browser_snapshot":
                    target = tool;
                    break;
                case "browser_click":
                    target = tool;
                    values["target"] = args.GetString("ref");
                    break;
                case "browser_type":
                    target = tool;
                    values["target"] = args.GetString("ref");
                    values["text"] = args.GetString("text");
                    values["submit"] = args.GetBoolean("submit");
                    break;
                case "browser_press_key":
                    target = tool;
                    values["key"] = args.GetString("key");
                    break;
                case "browser_wait":
                    target = "browser_wait_for";
                    values["time"] = Math.Clamp(args.GetInt32("seconds", 1), 1, 10);
                    break;
                case "browser_scroll":
                    target = "browser_evaluate";
                    values["function"] = args.GetString("direction") == "up" ? "() => window.scrollBy(0,-750)" : "() => window.scrollBy(0,750)";
                    break;
                default:
                    throw new ArgumentException("Unsupported browser tool: " + tool);
            }
            var text = await Call(browser, target, values, operation.Token);
            return new()
            {
                ["snapshot"] = text
            };
        }
        finally { job.Gate.Release(); }
    }

    public async Task RunWindow(string url, BrowserSetup setup, CancellationToken token)
    {
        if (!BrowserSelection.HasDisplay) throw new IOException("Open MiniRP with --web or --web --no-open without a graphical desktop.");
        var selection = await runtime.Select(VerifyAgent, setup, token);
        var profile = selection.NewProfile();
        try
        {
            await using var process = selection.StartWindow(profile.FullName, url);
            if (await process.Exit(token) != 0) throw new IOException("The " + selection.Name + " window exited unexpectedly.");
        }
        finally { BrowserProcess.Delete(profile); }
    }

    private async Task VerifyAgent(BrowserSelection selection, CancellationToken token)
    {
        var browser = await Open(selection, token);
        try
        {
            await Call(browser, "browser_navigate", new() { ["url"] = "about:blank" }, token);
            var text = await Call(browser, "browser_evaluate", new()
            {
                ["function"] = "() => '" + DesktopBrowserRuntime.ProbeTitle + "'"
            }, token);
            if (!text.Contains(DesktopBrowserRuntime.ProbeTitle, StringComparison.Ordinal))
                throw new IOException("The browser could not run the FetchAgent tools.");
        }
        finally { await Close(browser); }
    }

    private async Task<Browser> Open(BrowserSelection selection, CancellationToken token)
    {
        var script = BrowserSelection.PlaywrightPath(root, Path.Combine("@playwright", "mcp", "cli.js"));
        if (!File.Exists(script))
            throw new FileNotFoundException("Install browser tools with npm ci in the project directory.");
        var profile = selection.NewProfile();
        var output = Directory.CreateTempSubdirectory("minirp-browser-output-");
        BrowserProcess? embedded = null;
        BrowserProcess? driver = null;
        try
        {
            var arguments = new List<string> { script, "--no-webmcp", "--image-responses", "omit", "--output-dir", output.FullName };
            if (selection.Family == BrowserFamily.Chromium)
                arguments.AddRange(["--headless", "--sandbox", "--browser", "chromium",
                    "--executable-path", selection.Executable, "--user-data-dir", profile.FullName]);
            else
            {
                embedded = selection.StartEmbedded("agent", profile.FullName);
                var endpoint = await embedded.Endpoint(profile.FullName, chromium: false, token);
                arguments.AddRange(["--cdp-endpoint", endpoint.AbsoluteUri, "--cdp-timeout", "12000"]);
            }
            // Pass paths and arguments directly to Node so Windows does not reinterpret them through cmd.exe.
            driver = BrowserProcess.StartDriver(BrowserSelection.Node, output.FullName, arguments);
            var transport = new StreamClientTransport(driver.StandardInput, driver.StandardOutput);
            return new(await McpClient.CreateAsync(transport, cancellationToken: token), output, profile, driver, embedded);
        }
        catch
        {
            if (driver is not null) await driver.DisposeAsync();
            if (embedded is not null) await embedded.DisposeAsync();
            BrowserProcess.Delete(profile);
            BrowserProcess.Delete(output);
            throw;
        }
    }

    private static async Task<string> Call(Browser job, string name, Dictionary<string, object?> args, CancellationToken token)
    {
        var result = await job.Client.CallToolAsync(name, args, cancellationToken: token);
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (result.IsError == true)
        {
            throw new IOException(text);
        }
        foreach (Match match in SnapshotLink().Matches(text))
        {
            var path = Path.GetFullPath(Path.Combine(job.Output.FullName, match.Groups[1].Value));
            if (Path.GetRelativePath(job.Output.FullName, path).StartsWith("..", StringComparison.Ordinal) || !File.Exists(path))
            {
                continue;
            }
            text += "\n" + await File.ReadAllTextAsync(path, token);
        }
        return text;
    }

    private static async Task<JsonObject> Capture(Browser job, CancellationToken token)
    {
        var turndown = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "browser", "turndown.js"), token);
        var browser = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "browser", "browser.js"), token);
        // Fixed host code only. The model cannot submit JavaScript or access the host filesystem.
        var function = "() => { " + turndown + "\n" + browser + "\nconst meta=window.minirp.capture(); return {...meta,content:window.minirp.content}; }";
        var text = await Call(job, "browser_evaluate", new()
        {
            ["function"] = function
        }, token);
        var start = text.IndexOf("### Result\n", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new IOException("Browser did not return capture data.");
        }
        var json = text[(start + "### Result\n".Length)..];
        // MCP appends a second markdown section after the JSON value.
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var reader = new System.Text.Json.Utf8JsonReader(bytes);
        var result = JsonNode.Parse(ref reader)!.AsObject();
        return result;
    }

    [GeneratedRegex(@"\[Snapshot\]\(([^)]+)\)")]
    private static partial Regex SnapshotLink();

    public async ValueTask DisposeAsync()
    {
        foreach (var job in jobs.Values)
        {
            if (job.Browser is { } browser) await Close(browser);
        }
    }

    private static async ValueTask Close(Browser browser)
    {
        try
        {
            await browser.Driver.DisposeAsync();
            await browser.Client.DisposeAsync();
        }
        finally
        {
            if (browser.Embedded is not null) await browser.Embedded.DisposeAsync();
            BrowserProcess.Delete(browser.Profile);
            BrowserProcess.Delete(browser.Output);
        }
    }
}
