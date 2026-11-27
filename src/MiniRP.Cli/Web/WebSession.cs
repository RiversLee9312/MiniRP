using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed partial class WebSession : IAsyncDisposable
{
    private readonly NativeCore core;
    private readonly PlatformHost platform;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> queries = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> interactions = new();
    private readonly ConcurrentDictionary<string, JsonObject> interactionEvents = new();
    private readonly object publication = new();
    private readonly List<Channel<JsonObject[]>> listeners = [];
    private readonly List<JsonObject> journal = [];
    private readonly Task pump;
    public readonly EnvironmentSettings Settings;
    public readonly string Root;
    public readonly DirectoryInfo Staging = Directory.CreateTempSubdirectory("minirp-web-");
    public static string StaticRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public WebSession(NativeCore core, PlatformHost platform, EnvironmentSettings settings, string root,
        JsonObject? ready, (string Preset, string Chat, string Conversation) selection)
    {
        this.core = core;
        this.platform = platform;
        Settings = settings;
        Root = root;
        platform.Picker = AskBrowser;
        platform.OpenUi = OpenUi;
        Send(settings.Configure(web: true));
        if (ready is not null) journal.Add(ready);
        Send(new() { ["op"] = "list_presets" });
        Send(new() { ["op"] = "plugins_list" });
        Send(new() { ["op"] = "plugin_settings" });
        if (selection.Chat.Length > 0)
        {
            Send(new() { ["op"] = "select_chat", ["preset"] = selection.Preset, ["id"] = selection.Chat });
            if (selection.Conversation.Length > 0)
                Send(new() { ["op"] = "select_ensemble_chat", ["chat"] = selection.Chat, ["conversation"] = selection.Conversation });
        }
        else if (ready is not null)
            Send(new() { ["op"] = "home", ["preset"] = selection.Preset });
        pump = Pump();
    }

    public void Send(JsonObject command) => core.Send(command);
    public byte[] TakeBinary(string transfer) => core.TakeBinary(transfer);

    public async Task<JsonObject> Query(JsonObject command, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        queries[id] = completion;
        command["request"] = id;
        try
        {
            Send(command);
            var result = await completion.Task.WaitAsync(ct);
            if (result.GetString("kind") is "error" or "models_error" || result.GetString("error").Length > 0)
                throw new InvalidOperationException(result.GetString("message", result.GetString("error")));
            return result;
        }
        finally { queries.TryRemove(id, out _); }
    }

    public async IAsyncEnumerable<JsonObject[]> Listen(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<JsonObject[]>(128);
        JsonObject[] initial;
        lock (publication)
        {
            initial = [.. journal, .. interactionEvents.Values];
            listeners.Add(channel);
        }
        try
        {
            yield return initial;
            await foreach (var batch in channel.Reader.ReadAllAsync(ct)) yield return batch;
        }
        finally
        {
            lock (publication) listeners.Remove(channel);
        }
    }

    public bool CompleteInteraction(string id, JsonObject result) =>
        interactions.TryGetValue(id, out var pending) && pending.TrySetResult(result);

    public async Task<JsonObject> StageAttachments(IFormFileCollection files, CancellationToken ct, JsonObject? picker = null)
    {
        if (files.Count > 20 || files.Sum(file => file.Length) > 16_000_000)
            throw new InvalidOperationException("Choose up to 20 files with a combined size of 16 MB.");
        var directory = Path.Combine(Root, "attachments");
        Directory.CreateDirectory(directory);
        var stored = new List<string>();
        var items = new JsonArray();
        try
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
                var mime = WebDesktop.ContentType(name);
                if (picker is not null && !PickerAccepts(picker, mime)) continue;
                var id = JsonValues.NewId();
                var path = Path.Combine(directory, id);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                stored.Add(path);
                await file.CopyToAsync(output, ct);
                items.Add(new JsonObject { ["id"] = id, ["name"] = name, ["mime"] = mime, ["size"] = file.Length });
            }
            return new() { ["files"] = items };
        }
        catch
        {
            foreach (var path in stored) File.Delete(path);
            throw;
        }
    }

    public JsonObject? Picker(string id) =>
        interactionEvents.TryGetValue(id, out var item) && item.GetString("kind") == "web_picker" ? item : null;

    private static bool PickerAccepts(JsonObject picker, string mime)
    {
        var arguments = picker.GetObject("arguments");
        if (picker.GetString("picker") == "media")
        {
            var category = arguments.GetString("type");
            if (category is "image" or "video")
            {
                if (!mime.StartsWith(category + "/", StringComparison.Ordinal)) return false;
            }
            else if (!mime.StartsWith("image/", StringComparison.Ordinal) && !mime.StartsWith("video/", StringComparison.Ordinal))
                return false;
        }
        var filters = arguments.GetArray("mimeTypes");
        return filters.Count == 0 || filters.Any(item =>
        {
            var filter = JsonValues.GetText(item);
            return filter == mime || filter == "*/*" ||
                filter.EndsWith("/*", StringComparison.Ordinal) && mime.StartsWith(filter[..^1], StringComparison.Ordinal);
        });
    }

    private async Task<JsonObject> AskBrowser(JsonObject request, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var item = (JsonObject)request.DeepClone();
        item["kind"] = request.GetString("kind") switch
        {
            "plugin_picker" => "web_picker",
            "web_browser_setup" => "web_browser_setup",
            _ => "web_preview"
        };
        var result = BeginInteraction(id, item);
        try { return await result.WaitAsync(ct); }
        finally { EndInteraction(id); }
    }

    public async Task<string> InstallBrowser(CefRuntimeRelease release,
        Func<Action<CefInstallProgress>, CancellationToken, Task<string>> install, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        var ct = lifetime.Token;
        var accepted = await AskBrowser(new() { ["kind"] = "web_browser_setup", ["phase"] = "offer",
            ["total"] = release.DownloadBytes }, ct);
        if (!accepted.GetBoolean("accept")) throw BrowserSetupUi.Deferred();

        var id = Guid.NewGuid().ToString("N");
        var cancellation = BeginInteraction(id, new() { ["kind"] = "web_browser_setup", ["phase"] = "downloading",
            ["total"] = release.DownloadBytes, ["received"] = 0 });
        var download = install(value =>
        {
            lock (publication)
            {
                if (!interactions.ContainsKey(id)) return;
                UpdateInteraction(id, new() { ["kind"] = "web_browser_setup", ["phase"] = value.Phase,
                    ["total"] = value.TotalBytes, ["received"] = value.BytesReceived });
            }
        }, ct);
        try
        {
            if (await Task.WhenAny(cancellation, download) == cancellation && !download.IsCompleted)
                await lifetime.CancelAsync();
            return await download;
        }
        finally { EndInteraction(id); }
    }

    private Task<JsonObject> BeginInteraction(string id, JsonObject item)
    {
        var result = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (publication)
        {
            interactions[id] = result;
            UpdateInteraction(id, item);
        }
        return result.Task;
    }

    private void UpdateInteraction(string id, JsonObject item)
    {
        item["id"] = id;
        interactionEvents[id] = item;
        Publish([item]);
    }

    private void EndInteraction(string id)
    {
        lock (publication)
        {
            interactions.TryRemove(id, out _);
            interactionEvents.TryRemove(id, out _);
            Publish([new() { ["kind"] = "web_interaction_closed", ["id"] = id }]);
        }
    }

    private async Task<JsonObject> OpenUi(JsonObject request, CancellationToken ct)
    {
        var catalog = Path.Combine(StaticRoot, "openui", "card-prompt.txt");
        if (!File.Exists(catalog))
            return new() { ["available"] = false, ["reason"] = "OpenUI assets are missing from this build." };
        if (request.GetString("kind") == "open_ui_preview") return await AskBrowser(request, ct);
        var text = await File.ReadAllTextAsync(catalog, ct);
        var start = Math.Clamp(request.GetInt32("offset"), 0, text.Length);
        var query = request.GetString("query").Trim();
        if (query.Length > 0)
        {
            var match = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0) return new() { ["available"] = true, ["found"] = false };
            start = Math.Max(start, match - 80);
        }
        if (start < text.Length && char.IsLowSurrogate(text[start])) start--;
        var end = Math.Min(start + Math.Clamp(request.GetInt32("length", 4000), 1, 8000), text.Length);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end++;
        return new() { ["available"] = true, ["text"] = text[start..end], ["offset"] = start,
            ["next_offset"] = end == text.Length ? null : JsonValue.Create(end) };
    }

    private async Task Pump()
    {
        while (!stop.IsCancellationRequested)
        {
            var batch = new List<JsonObject>();
            foreach (var item in core.Poll().OfType<JsonObject>())
            {
                if (platform.Handle(item)) continue;
                var kind = item.GetString("kind");
                var request = item.GetString("request");
                if (request.Length > 0 && queries.TryGetValue(request, out var completion))
                {
                    if (kind is not ("models_loading" or "busy")) completion.TrySetResult(item);
                    if (kind is "error" or "asset_result" or "transcript_page" or "preset_activity" or "ensemble_archive" or "ensemble_member_trace" or "memory" or "models" or "models_error" or "model_effort_levels" or "plugin_page" or "plugin")
                        continue;
                }
                if (kind is "transcript_page" or "preset_activity" or "ensemble_archive" or "ensemble_member_trace") continue;
                if (kind == "ready")
                {
                    var preset = item.GetString("preset");
                    if (preset.Length > 0) Send(new() { ["op"] = "home", ["preset"] = preset });
                }
                if (kind is "home" or "loaded" or "preset_opened")
                {
                    Send(new() { ["op"] = "list_chats", ["preset"] = item.GetString("preset") });
                    Send(new() { ["op"] = "plugin_settings" });
                }
                if (kind == "library_changed")
                {
                    Send(new() { ["op"] = "list_presets" });
                    if (item.GetString("preset") is { Length: > 0 } preset)
                        Send(new() { ["op"] = "list_chats", ["preset"] = preset });
                }
                batch.Add(item);
            }
            if (batch.Count > 0)
            {
                lock (publication)
                {
                    foreach (var item in batch) Retain(item);
                    var live = batch.Where(item => item.GetString("kind") != "transcript_checkpoint").ToArray();
                    if (live.Length > 0) Publish(live);
                }
            }
            try { await Task.Delay(24, stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Publish(JsonObject[] batch)
    {
        for (var index = listeners.Count - 1; index >= 0; index--)
        {
            var listener = listeners[index];
            if (listener.Writer.TryWrite(batch)) continue;
            listener.Writer.TryComplete();
            listeners.RemoveAt(index);
        }
    }

    private void Retain(JsonObject item)
    {
        var kind = item.GetString("kind");
        if (kind == "transcript_checkpoint")
        {
            RetainTranscriptCheckpoint(item);
            return;
        }
        if (IsTrace(item) || kind == "ensemble_message") RequestTranscriptCheckpoint();
        switch (kind)
        {
            case "home" or "loaded":
            case "preset_opened" when item.GetBoolean("selected") || item.GetBoolean("created"):
                journal.RemoveAll(entry => entry.GetString("kind") is not ("ready" or "presets" or "plugins" or "busy"));
                break;
            case "preset_opened":
                journal.RemoveAll(entry => entry.GetString("kind") == "preset_opened" || IsTrace(entry) &&
                    entry.GetString("agent") is "preset" or "PresetAgent" or "PluginAgent");
                break;
            case "ensemble_chat_loaded":
                journal.RemoveAll(entry => entry.GetString("kind") is "ensemble_chat_loaded" or "ensemble_message" ||
                    IsTrace(entry) && entry.GetString("conversation").Length > 0);
                break;
            case "ensemble_activity":
                journal.RemoveAll(entry => entry.GetString("kind") == kind &&
                    entry.GetString("preset") == item.GetString("preset") && entry.GetString("chat") == item.GetString("chat") &&
                    entry.GetString("conversation") == item.GetString("conversation") && entry.GetString("participant") == item.GetString("participant"));
                break;
            case "ready" or "presets" or "chats" or "plugins" or "plugin_settings" or "preset_config" or
                "ensemble_chats" or "world_changed" or "title" or "generating" or "busy":
                journal.RemoveAll(entry => entry.GetString("kind") == kind);
                break;
            case "room_loading":
                journal.RemoveAll(entry => entry.GetString("kind") == kind);
                if (!item.GetBoolean("value")) return;
                break;
            case "delta" or "open_ui_delta":
                if (journal.LastOrDefault() is { } previous && previous.GetString("kind") == kind &&
                    previous.GetString("block") == item.GetString("block") && previous.GetString("type") == item.GetString("type"))
                {
                    var joined = (JsonObject)previous.DeepClone();
                    joined["text"] = previous.GetString("text") + item.GetString("text");
                    journal[^1] = joined;
                    return;
                }
                break;
            case "error" or "plugin_error" or "library_changed" or "message_accepted" or "plugin_action" or "deleted" or
                "archive_imported" or "archive_exported" or "models" or "memory":
                return;
        }
        journal.Add(item);
    }

    private static bool IsTrace(JsonObject item) => item.GetString("kind") is "user" or "system" or "delta" or
        "turn_start" or "turn_end" or "steer_queued" or "context_trace" or "tool_start" or "tool_end" or "tool_trace_update" or
        "open_ui_delta" or "open_ui_end" or "open_ui_card" or "open_ui_card_update" or "plugin_agent_status" or "reply_end";

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        platform.Picker = null;
        platform.OpenUi = null;
        foreach (var item in interactions.Values) item.TrySetCanceled();
        foreach (var item in queries.Values) item.TrySetCanceled();
        await pump;
        stop.Dispose();
        Staging.Delete(recursive: true);
    }
}
