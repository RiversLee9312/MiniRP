using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Minichat.Protocol;

namespace Minichat.Cli;

internal sealed class PlatformHost(NativeCore core, BrowserHost browser) : IDisposable
{
    public Func<JsonObject, CancellationToken, Task<JsonObject>>? Picker { get; set; }
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> requests = new();
    private readonly PluginScripts scripts = new();
    private readonly HttpClient pluginHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) })
        { Timeout = Timeout.InfiniteTimeSpan };

    public bool Handle(JsonObject message)
    {
        switch (message.GetString("kind"))
        {
            case "http_request":
                _ = Task.Run(() => Http(message));
                return true;
            case "browser_request":
                _ = Task.Run(() => Browser(message));
                return true;
            case "plugin_script":
            case "plugin_http":
            case "plugin_picker":
                _ = Task.Run(() => PluginRequest(message));
                return true;
            case "plugin_script_close":
                _ = Task.Run(() => scripts.Finish(message.GetString("isolate"), message.GetString("run")));
                return true;
            case "plugin_isolate_close":
                _ = Task.Run(() => scripts.Close(message.GetString("isolate")));
                return true;
            case "cancel_io":
                if (requests.TryGetValue(message.GetString("id"), out var source))
                {
                    source.Cancel();
                }
                return true;
            default:
                return false;
        }
    }

    private void Reply(string id, JsonObject reply)
    {
        reply["op"] = "io_reply";
        reply["id"] = id;
        core.Send(reply);
    }

    private async Task Http(JsonObject message)
    {
        var id = message.GetString("id");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(message.GetInt32("timeout_ms", 240000)));
        requests[id] = cancellation;
        try
        {
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
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode || !message.GetBoolean("stream"))
            {
                Reply(id, new()
                {
                    ["data"] = await response.Content.ReadAsStringAsync(cancellation.Token),
                    ["status"] = status,
                    ["done"] = true
                });
                return;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellation.Token);
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
        catch (Exception error)
        {
            Reply(id, new()
            {
                ["error"] = error.Message
            });
        }
        finally
        {
            requests.TryRemove(id, out _);
        }
    }

    private async Task Browser(JsonObject message)
    {
        var id = message.GetString("id");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        requests[id] = cancellation;
        try
        {
            Reply(id, new()
            {
                ["result"] = await browser.Execute(message, cancellation.Token)
            });
        }
        catch (Exception error) { Reply(id, new() { ["error"] = error.Message }); }
        finally { requests.TryRemove(id, out _); }
    }

    private async Task PluginRequest(JsonObject message)
    {
        var id = message.GetString("id");
        using var cancellation = new CancellationTokenSource();
        requests[id] = cancellation;
        try
        {
            var result = message.GetString("kind") switch
            {
                "plugin_script" => scripts.Step(message, cancellation.Token, core.TakeBinary, core.WriteBinary),
                "plugin_http" => await PluginHttp(message, cancellation.Token),
                _ => await (Picker ?? throw new InvalidOperationException("An interactive picker is unavailable in stdio mode."))(message, cancellation.Token)
            };
            Reply(id, new() { ["result"] = result });
        }
        catch (Exception error) { Reply(id, new() { ["error"] = error.Message }); }
        finally { requests.TryRemove(id, out _); }
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

    public void Dispose()
    {
        foreach (var request in requests.Values)
        {
            request.Cancel();
        }
        http.Dispose();
        pluginHttp.Dispose();
        scripts.Dispose();
    }
}
