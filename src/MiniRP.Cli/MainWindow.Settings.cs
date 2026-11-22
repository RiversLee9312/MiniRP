using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MiniRP.Protocol;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

internal sealed partial class MainWindow
{
    private int settingsOpen;

    private async void ShowSettings()
    {
        var app = App;
        if (app is null || Interlocked.Exchange(ref settingsOpen, 1) != 0) return;
        try
        {
            var response = await QueryCore(new() { ["op"] = "plugin_settings" });
            await BrowseSettings(app, "设置", response.GetArray("items").OfType<JsonObject>()
                .Where(item => item.GetString("plugin") == "base"), root: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            app.Invoke(() => main.Notice(error.Message));
        }
        finally { Interlocked.Exchange(ref settingsOpen, 0); }
    }

    private async Task BrowseSettings(IApplication app, string title, IEnumerable<JsonObject> entries, bool root = false)
    {
        var supported = entries.Where(SupportsSetting).ToArray();
        if (supported.Length == 0)
        {
            await RunSettingsDialog(app, () =>
            {
                main.Notice("没有可用的设置。");
                return true;
            });
            return;
        }

        while (true)
        {
            var key = await ChooseSettingsItem(app, title, supported, "key", root ? "关闭" : "返回");
            if (key is null) return;
            var selected = supported.First(item => item.GetString("key") == key);
            var name = SettingTitle(selected, "key");
            switch (selected.GetString("type"))
            {
                case "page":
                    var response = await QueryCore(new() { ["op"] = "plugin_page", ["id"] = selected.GetString("plugin"), ["key"] = key });
                    var page = response.GetObject("item");
                    var children = page.GetArray("items").OfType<JsonObject>().ToArray();
                    if (children.Length > 0 && children.All(IsSettingDocument))
                    {
                        var documents = new List<string>();
                        foreach (var document in children) documents.Add(await ReadSettingDocument(document));
                        await ViewSettingText(app, name, string.Join("\n\n", documents));
                    }
                    else await BrowseSettings(app, name, children);
                    break;
                case "link":
                    await OpenSettingsLink(app, name, SettingLink(selected));
                    break;
                case "boolean":
                    await EditReasoningPreference(app, name);
                    break;
                case "choice":
                    await EditFetchMode(app, name, selected.GetArray("options").OfType<JsonObject>());
                    break;
            }
        }
    }

    private static bool SupportsSetting(JsonObject item) => item.GetString("type") switch
    {
        "page" => true,
        "link" => SettingLink(item) is "providers" or "models" or "research-services" or "plugins",
        "boolean" => item.GetString("binding") == "thoughts",
        "choice" => item.GetString("binding") == "fetch",
        _ => false
    };

    private static string SettingLink(JsonObject item)
    {
        const string prefix = "minirp://actions/minirp.app/hooks/";
        var url = item.GetString("url");
        return url.StartsWith(prefix, StringComparison.Ordinal) ? url[prefix.Length..] : "";
    }

    private async Task OpenSettingsLink(IApplication app, string title, string link)
    {
        if (link == "plugins")
        {
            var response = await QueryCore(new() { ["op"] = "plugins_list" });
            var names = response.GetArray("items").OfType<JsonObject>()
                .Select(item => item.GetString("name", item.GetString("id")));
            await ViewSettingText(app, title, string.Join('\n', names));
            return;
        }
        var section = link switch
        {
            "providers" => ConfigurationSection.Providers,
            "models" => ConfigurationSection.Models,
            _ => ConfigurationSection.Research
        };
        var saved = await RunSettingsDialog(app, () =>
        {
            using var dialog = new ConfigurationDialog(title, section, settings,
                providerTemplates.Count == 0 ? null : apply =>
                    ShowChoices("提供商模板", providerTemplates, id =>
                        apply(providerTemplates.OfType<JsonObject>().First(item => item.GetString("id") == id))));
            app.Run(dialog);
            return dialog.Saved;
        });
        if (saved) await SaveSettings();
    }

    private async Task EditReasoningPreference(IApplication app, string title)
    {
        var preference = await ChooseSettingsItem(app, title,
            [new() { ["value"] = "true", ["title"] = "开启" }, new() { ["value"] = "false", ["title"] = "关闭" }],
            "value", "取消", "保存", cot ? "true" : "false");
        if (preference is null) return;
        var value = preference == "true";
        var preferences = settings.Desktop().GetObject("preferences");
        preferences["thoughts"] = value;
        settings.SavePreferences(preferences);
        await RunSettingsDialog(app, () =>
        {
            SetCot(value);
            return true;
        });
    }

    private async Task EditFetchMode(IApplication app, string title, IEnumerable<JsonObject> options)
    {
        var active = settings.Configure().GetObject("settings").GetString("fetch_backend");
        var value = await ChooseSettingsItem(app, title, options, "value", "取消", "保存", active);
        if (value is null) return;

        settings["MINIRP_FETCH_BACKEND"] = value;
        var desktop = settings.Desktop();
        var research = desktop.GetObject("research");
        if (research.ContainsKey("fetch_backend"))
        {
            research["fetch_backend"] = value;
            settings.SaveDesktop(desktop);
        }
        await SaveSettings();
    }

    private static bool IsSettingDocument(JsonObject item) =>
        item.GetString("type") == "markdown" && (item["markdown"] is not null || item.GetString("assetUrl").Length > 0);

    private async Task<string> ReadSettingDocument(JsonObject item)
    {
        if (item["markdown"] is JsonValue inline) return inline.GetValue<string>();
        var info = await AssetQuery(new() { ["op"] = "asset_open", ["url"] = item.GetString("assetUrl") });
        var handle = info.GetString("handle");
        try
        {
            using var bytes = new MemoryStream();
            while (true)
            {
                var chunk = await AssetQuery(new() { ["op"] = "asset_pull", ["handle"] = handle });
                if (chunk.GetBoolean("done")) break;
                bytes.Write(core.TakeBinary(chunk.GetString("transfer")));
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        finally
        {
            if (!lifetime.IsCancellationRequested) core.Send(new() { ["op"] = "asset_close", ["handle"] = handle });
        }
    }

    private Task<string?> ChooseSettingsItem(IApplication app, string title, IEnumerable<JsonObject> entries, string idKey,
        string cancel, string select = "打开", string? current = null) =>
        RunSettingsDialog(app, () =>
        {
            var items = new JsonArray(entries.Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.GetString(idKey),
                ["name"] = SettingTitle(item, idKey) + (item.GetString(idKey) == current ? " ✓" : "")
            }).ToArray());
            string? selected = null;
            ShowChoices(title, items, value => selected = value, cancelLabel: cancel, selectLabel: select, selectedId: current);
            return selected;
        });

    private Task ViewSettingText(IApplication app, string title, string text) =>
        RunSettingsDialog(app, () =>
        {
            using var dialog = new Dialog { Title = title, Width = Dim.Percent(85), Height = Dim.Percent(85) };
            dialog.Add(new Editor { Text = text, ReadOnly = true, WordWrap = true, Width = Dim.Fill(), Height = Dim.Fill(2),
                ViewportSettings = ViewportSettingsFlags.HasScrollBars });
            var close = new Button { Text = "返回" };
            close.Accepted += (_, _) => app.RequestStop();
            dialog.AddButton(close);
            app.Run(dialog);
            return true;
        });

    private Task<T> RunSettingsDialog<T>(IApplication app, Func<T> show)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            try { result.TrySetResult(show()); }
            catch (Exception error) { result.TrySetException(error); }
        });
        return result.Task.WaitAsync(lifetime.Token);
    }

    private static string SettingTitle(JsonObject item, string fallback)
    {
        var titles = item.GetObject("titles");
        var culture = CultureInfo.CurrentUICulture;
        while (culture.Name.Length > 0)
        {
            if (titles.GetString(culture.Name) is { Length: > 0 } translated) return translated;
            culture = culture.Parent;
        }
        return item.GetString("title", item.GetString(fallback));
    }
}
