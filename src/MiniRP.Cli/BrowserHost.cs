using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MiniRP.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MiniRP.Cli;

/// <summary>Uses the official MCP SDK and Playwright's refs, auto-waiting and isolated contexts.</summary>
internal sealed partial class BrowserHost(string root, string browserName = "chrome") : IAsyncDisposable
{
    private sealed record Job(McpClient Client, string Directory);
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);

    public async Task<JsonObject> Execute(JsonObject request, CancellationToken token)
    {
        var id = request.GetString("job");
        var tool = request.GetString("tool");
        await gate.WaitAsync(token);
        try
        {
            if (tool == "browser_close")
            {
                if (jobs.Remove(id, out var closing))
                {
                    await closing.Client.DisposeAsync();
                }
                return new()
                {
                    ["closed"] = true
                };
            }
            if (!jobs.TryGetValue(id, out var job))
            {
                var directory = Path.Combine(Path.GetTempPath(), "minirp-browser-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                const string module = "node_modules/@playwright/mcp/cli.js";
                var script = Path.Combine(AppContext.BaseDirectory, module);
                if (!File.Exists(script)) script = Path.Combine(root, module);
                if (!File.Exists(script))
                {
                    throw new FileNotFoundException("Install browser tools with npm ci in the project directory.", script);
                }
                var transport = new StdioClientTransport(new()
                {
                    Command = "node",
                    Arguments = [script, "--headless", "--isolated", "--no-webmcp", "--sandbox", "--browser", browserName, "--image-responses", "omit", "--output-dir", directory],
                    WorkingDirectory = directory
                });
                var client = await McpClient.CreateAsync(transport, cancellationToken: token);
                jobs[id] = job = new(client, directory);
            }
            if (tool == "capture_page")
            {
                return await Capture(job, token);
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
                    values["ref"] = args.GetString("ref");
                    values["element"] = args.GetString("ref");
                    break;
                case "browser_type":
                    target = tool;
                    values["ref"] = args.GetString("ref");
                    values["element"] = args.GetString("ref");
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
            var text = await Call(job, target, values, token);
            return new()
            {
                ["snapshot"] = text
            };
        }
        finally { gate.Release(); }
    }

    private static async Task<string> Call(Job job, string name, Dictionary<string, object?> args, CancellationToken token)
    {
        var result = await job.Client.CallToolAsync(name, args, cancellationToken: token);
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (result.IsError == true)
        {
            throw new IOException(text);
        }
        foreach (Match match in SnapshotLink().Matches(text))
        {
            var path = Path.GetFullPath(Path.Combine(job.Directory, match.Groups[1].Value));
            if (Path.GetRelativePath(job.Directory, path).StartsWith("..", StringComparison.Ordinal) || !File.Exists(path))
            {
                continue;
            }
            text += "\n" + await File.ReadAllTextAsync(path, token);
        }
        return text;
    }

    private static async Task<JsonObject> Capture(Job job, CancellationToken token)
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
            await job.Client.DisposeAsync();
        }
        gate.Dispose();
    }
}
