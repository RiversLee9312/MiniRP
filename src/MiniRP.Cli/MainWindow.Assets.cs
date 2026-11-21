using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MiniRP.Protocol;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

internal sealed partial class MainWindow
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> assetRequests = new();

    private async void AttachToPreset(string text)
    {
        var app = App;
        if (app is null || presetId.Length == 0) return;
        var preset = presetId;
        try
        {
            var result = await attachmentPicker.Choose(app, new() { ["name"] = "PresetAgent", ["picker"] = "file" }, lifetime.Token);
            var files = result.GetArray("files");
            if (files.Count == 0) return;
            app.Invoke(() =>
            {
                if (lifetime.IsCancellationRequested) return;
                var pane = GetAgentPane("preset");
                if (preset != presetId)
                {
                    pane.Notice("预设已切换，请重新选择文件。");
                    return;
                }
                try
                {
                    core.Send(new()
                    {
                        ["op"] = "message", ["agent"] = "preset", ["preset"] = preset,
                        ["text"] = text.Length > 0 ? text : "请导入这些设定资料。",
                        ["attachments"] = files.DeepClone()
                    });
                }
                catch (InvalidOperationException error) { pane.Notice(error.Message); }
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { app.Invoke(() => GetAgentPane("preset").Notice(error.Message)); }
    }

    private async void DownloadAsset(string role, string block, string url)
    {
        var request = new JsonObject { ["op"] = "display_asset_open", ["preset"] = presetId,
            ["room"] = role == "preset" ? "" : chatId, ["conversation"] = role == "chat" ? ensembleChatId : "",
            ["block"] = block, ["url"] = url };
        var handle = "";
        try
        {
            var info = await AssetQuery(request);
            handle = info.GetString("handle");
            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            App?.Invoke(() => ready.TrySetResult(ChooseAssetPath(info.GetString("name"))));
            var path = await ready.Task.WaitAsync(lifetime.Token);
            if (path.Length == 0) return;
            var destination = Path.GetFullPath(path);
            var partial = destination + "." + JsonValues.NewId() + ".part";
            try
            {
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    while (true)
                    {
                        var chunk = await AssetQuery(new() { ["op"] = "asset_pull", ["handle"] = handle });
                        if (chunk.GetBoolean("done")) break;
                        await output.WriteAsync(core.TakeBinary(chunk.GetString("transfer")), lifetime.Token);
                    }
                }
                File.Move(partial, destination);
                App?.Invoke(() => main.Notice("已保存 " + destination));
            }
            finally { File.Delete(partial); }
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            App?.Invoke(() => main.Notice(error.Message));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            if (handle.Length > 0 && !lifetime.IsCancellationRequested)
                core.Send(new() { ["op"] = "asset_close", ["handle"] = handle });
        }
    }

    private async Task<JsonObject> AssetQuery(JsonObject request)
    {
        var id = "asset-" + JsonValues.NewId();
        var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        assetRequests[id] = pending;
        try
        {
            request["request"] = id;
            core.Send(request);
            var item = await pending.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            if (item.GetString("kind") == "error") throw new IOException(item.GetString("message"));
            return item.GetObject("result");
        }
        finally { assetRequests.TryRemove(id, out _); }
    }

    private string ChooseAssetPath(string name)
    {
        var safe = new string(name.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)).Take(180).ToArray()).Trim(' ', '.');
        using var dialog = new Dialog { Title = "保存文件", Width = Dim.Percent(65), Height = 7 };
        var field = new TextField { Text = safe.Length == 0 ? "asset" : safe, X = 1, Y = 1, Width = Dim.Fill(1) };
        var save = new Button { Text = "保存" };
        var cancel = new Button { Text = "取消" };
        var selected = "";
        save.Accepted += (_, _) =>
        {
            selected = field.Text.Trim();
            App!.RequestStop();
        };
        cancel.Accepted += (_, _) => App!.RequestStop();
        dialog.Add(field);
        dialog.AddButton(cancel);
        dialog.AddButton(save);
        App!.Run(dialog);
        return selected;
    }
}
