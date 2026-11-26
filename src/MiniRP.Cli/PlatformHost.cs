using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class PlatformHost(Action<JsonObject> send, Func<string, byte[]?> takeBinary, Action<string, byte[]> writeBinary,
    BrowserHost browser, string workspace, DesktopSecretStorage? secretStorage = null,
    Func<string, string?>? researchDefault = null) : IAsyncDisposable
{
    private static readonly string ApiUserAgent = "Mini Agent Harness/" +
        typeof(PlatformHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];

    public Func<JsonObject, CancellationToken, Task<JsonObject>>? Picker { get; set; }
    public Func<JsonObject, CancellationToken, Task<JsonObject>>? OpenUi { get; set; }
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> requests = new();
    private readonly List<Task> work = [];
    private readonly PluginScripts scripts = new();
    private readonly ContainerHost container = new(workspace);
    private readonly AssetCommandHost assetCommands = new(workspace);
    private readonly DesktopSecretStorage secrets = secretStorage ?? new(workspace);
    private readonly HttpClient pluginHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) })
        { Timeout = Timeout.InfiniteTimeSpan };

    public bool Handle(JsonObject message)
    {
        switch (message.GetString("kind"))
        {
            case "plugin_secret":
                Start(message, _ =>
                {
                    Reply(message.GetString("id"), new() { ["result"] = secrets.Handle(message, researchDefault) });
                    return Task.CompletedTask;
                });
                return true;
            case "http_request":
                Start(message, token => Http(message, token), message.GetBoolean("stream") ? null :
                    TimeSpan.FromMilliseconds(message.GetInt32("timeout_ms", 240000)));
                return true;
            case "browser_request":
                var id = message.GetString("id");
                Start(message, async token =>
                {
                    var result = await browser.Execute(message, token, () => Reply(id, new() { ["preparing"] = true }));
                    Reply(id, new() { ["result"] = result });
                });
                return true;
            case "plugin_script":
                var step = scripts.Prepare(message, takeBinary, writeBinary);
                Start(message, async token =>
                {
                    Reply(message.GetString("id"), new() { ["result"] = await step(token) });
                });
                return true;
            case "plugin_http":
            case "plugin_picker":
            case "agent_asset_sample":
                Start(message, token => PluginRequest(message, token));
                return true;
            case "container_runtime":
            case "container_run":
                Start(message, async token => Reply(message.GetString("id"), new() { ["result"] = await container.Handle(message, token) }));
                return true;
            case "curl_download":
            case "ffmpeg_run":
                Start(message, async token => Reply(message.GetString("id"), new() { ["result"] = await assetCommands.Handle(message, token) }));
                return true;
            case "open_ui_catalog":
            case "open_ui_preview":
                if (OpenUi is { } renderer)
                {
                    Start(message, async token => Reply(message.GetString("id"), new() { ["result"] = await renderer(message, token) }),
                        TimeSpan.FromSeconds(30));
                    return true;
                }
                Reply(message.GetString("id"), new() { ["result"] = new JsonObject
                {
                    ["available"] = false, ["reason"] = "Open the Web interface to render OpenUI cards."
                } });
                return true;
            case "plugin_isolate_close":
                if (scripts.Detach(message.GetString("isolate")) is { } retired) Track(Task.Run(retired.Dispose));
                return true;
            case "cancel_io":
                if (requests.TryGetValue(message.GetString("id"), out var source)) Cancel(source);
                return true;
            default:
                return false;
        }
    }

    private void Start(JsonObject message, Func<CancellationToken, Task> execute, TimeSpan? timeout = null)
    {
        var id = message.GetString("id");
        var source = new CancellationTokenSource();
        if (timeout is { } limit) source.CancelAfter(limit);
        requests[id] = source;
        Track(Task.Run(async () =>
        {
            using (source)
            {
                try
                {
                    source.Token.ThrowIfCancellationRequested();
                    await execute(source.Token);
                }
                catch (Exception error)
                {
                    var reply = new JsonObject { ["error"] = error.Message };
                    if (message.GetString("kind") == "http_request" && IsHttpTransportFailure(error, source.Token))
                        reply["network_error"] = true;
                    Reply(id, reply);
                }
                finally { requests.TryRemove(id, out _); }
            }
        }));
    }

    private static bool IsHttpTransportFailure(Exception error, CancellationToken token) =>
        !token.IsCancellationRequested && error switch
        {
            HttpRequestException request => request.HttpRequestError is HttpRequestError.NameResolutionError or
                HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError,
            IOException => true,
            OperationCanceledException { InnerException: TimeoutException } => true,
            _ => false
        };

    private static void Cancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Track(Task task)
    {
        lock (work)
        {
            work.RemoveAll(item => item.IsCompletedSuccessfully);
            work.Add(task);
        }
    }

    private void Reply(string id, JsonObject reply)
    {
        reply["op"] = "io_reply";
        reply["id"] = id;
        send(reply);
    }

    private async Task Http(JsonObject message, CancellationToken token)
    {
        var id = message.GetString("id");
        using var request = new HttpRequestMessage(new HttpMethod(message.GetString("method", "GET")), message.GetString("url"));
        if (message.ContainsKey("body"))
        {
            request.Content = new StringContent(message.GetString("body"), Encoding.UTF8, "application/json");
        }
        foreach (var header in message.GetObject("headers"))
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, JsonValues.GetText(header.Value)))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, JsonValues.GetText(header.Value));
            }
        }
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", ApiUserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode || !message.GetBoolean("stream"))
        {
            Reply(id, new()
            {
                ["data"] = await response.Content.ReadAsStringAsync(token),
                ["status"] = status,
                ["done"] = true
            });
            return;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0)
            {
                break;
            }
            Reply(id, new()
            {
                ["data_base64"] = Convert.ToBase64String(buffer, 0, count),
                ["status"] = status
            });
        }
        Reply(id, new()
        {
            ["done"] = true,
            ["status"] = status
        });
    }

    private async Task PluginRequest(JsonObject message, CancellationToken token)
    {
        var result = message.GetString("kind") switch
        {
            "plugin_http" => await PluginHttp(message, token),
            "agent_asset_sample" => await AgentMediaSampler.Sample(Path.Combine(workspace, "attachments"), message, token),
            _ => await (Picker ?? throw new InvalidOperationException("An interactive picker is unavailable in stdio mode."))(message, token)
        };
        Reply(message.GetString("id"), new() { ["result"] = result });
    }

    private async Task<JsonObject> PluginHttp(JsonObject message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(message.GetInt32("timeout_ms", 15000)));
        var token = timeout.Token;
        using var request = new HttpRequestMessage(new HttpMethod(message.GetString("method")), message.GetString("url"));
        if (message.ContainsKey("body")) request.Content = new StringContent(message.GetString("body"), Encoding.UTF8);
        foreach (var header in message.GetObject("headers"))
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, JsonValues.GetText(header.Value)))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, JsonValues.GetText(header.Value));
        }
        using var response = await pluginHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[1024 * 1024 + 1];
        var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token);
        if (read == bytes.Length) throw new IOException("Plugin HTTP response exceeded 1 MiB.");
        var headers = new JsonObject();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        return new() { ["status"] = (int)response.StatusCode, ["headers"] = headers, ["body"] = Encoding.UTF8.GetString(bytes, 0, read) };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var request in requests.Values) Cancel(request);
        Task[] pending;
        lock (work) pending = work.ToArray();
        try { await Task.WhenAll(pending); }
        finally
        {
            http.Dispose();
            pluginHttp.Dispose();
            scripts.Dispose();
        }
    }
}
