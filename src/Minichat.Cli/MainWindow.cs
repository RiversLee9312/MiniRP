using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Minichat.Protocol;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Minichat.Cli;

internal sealed class MainWindow : Runnable
{
    private readonly NativeCore core;
    private readonly PlatformHost platform;
    private readonly EnvironmentSettings settings;
    private readonly TracePane main = new("chat");
    private readonly Dictionary<string, TracePane> sidebars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TracePane> agentPanes = new(StringComparer.Ordinal);
    private JsonObject[] loadedBlocks = [];
    private readonly Label footer = new() { Text = "/env  /preset-agent  /presets  /chats  /save  /config  /cot  /stop  /quit", X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private readonly CancellationTokenSource lifetime = new();
    private JsonArray presetItems = new();
    private string presetId = "default";
    private bool cot;

    public MainWindow(NativeCore core, PlatformHost platform, EnvironmentSettings settings)
    {
        this.core = core;
        this.platform = platform;
        this.settings = settings;
        Title = "Minichat";
        main.X = 1;
        main.Y = 1;
        main.Width = Dim.Fill(1);
        main.Height = Dim.Fill(2);
        main.Submitted += text => Submit("chat", text);
        Add(main, footer);
        Initialized += (_, _) => _ = Task.Run(Pump);
    }

    private async Task Pump()
    {
        while (!lifetime.IsCancellationRequested)
        {
            var batch = core.Poll();
            var updates = new List<JsonObject>();
            foreach (var node in batch)
            {
                var item = node!.AsObject();
                if (!platform.Handle(item))
                {
                    updates.Add(item);
                }
            }
            if (updates.Count > 0)
            {
                App?.Invoke(() =>
                {
                    foreach (var item in updates)
                    {
                        Receive(item);
                    }
                    main.Flush();
                    foreach (var panel in sidebars.Values)
                    {
                        panel.Flush();
                    }
                });
            }
            await Task.Delay(30);
        }
    }

    private void Receive(JsonObject item)
    {
        switch (item.GetString("kind"))
        {
            case "ready":
                presetItems = item.GetArray("presets");
                ShowPresets();
                break;
            case "presets":
                presetItems = item.GetArray("items");
                ShowPresets();
                break;
            case "chats":
                presetId = item.GetString("preset");
                ShowChoices("对话", item.GetArray("items"), id => core.Send(new() { ["op"] = "select_chat", ["preset"] = presetId, ["id"] = id }), true);
                break;
            case "loaded":
                presetId = item.GetString("preset");
                Title = "Minichat · " + item.GetObject("chat").GetString("name");
                var blocks = item.GetObject("chat").GetArray("blocks").OfType<JsonObject>().ToArray();
                loadedBlocks = blocks;
                main.Load(blocks.Where(block => Route(block.GetString("agent")) == "chat"));
                foreach (var panel in agentPanes)
                {
                    panel.Value.Load(blocks.Where(block => Route(block.GetString("agent")) == panel.Key));
                }
                main.FocusInput();
                break;
            case "error":
                main.Notice(item.GetString("message"));
                break;
            case "busy":
                Title = item.GetBoolean("value") ? "Minichat · 处理中" : "Minichat";
                break;
            case "models":
                ShowChoices("模型", item.GetArray("items"), id => { settings["DEEPSEEK_MODEL"] = id; SaveSettings(); });
                break;
            case "memory":
                main.Notice(JsonValues.Serialize(item["result"]));
                break;
            default:
                var role = Route(item.GetString("agent"));
                if (role != "chat")
                {
                    GetAgentPane(role).Receive(item);
                }
                else
                {
                    main.Receive(item);
                }
                break;
        }
    }

    private static string Route(string agent) => agent is "env" or "EnvAgent" ? "env" : agent is "preset" or "PresetAgent" ? "preset" : "chat";

    private void Submit(string role, string text)
    {
        var parts = text.Split(' ', 2);
        var argument = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "/quit":
                App?.RequestStop();
                break;
            case "/close" when role != "chat":
                CloseSidebar(role);
                break;
            case "/env":
                OpenSidebar("env");
                break;
            case "/preset-agent":
                OpenSidebar("preset");
                break;
            case "/stage":
                OpenSidebar("env");
                Send("env", argument.Length > 0 ? argument : "推进一小步", true);
                break;
            case "/canon":
                OpenSidebar("env");
                if (argument.Length > 0)
                {
                    Send("env", argument);
                }

                break;
            case "/presets":
                core.Send(new()
                {
                    ["op"] = "list_presets"
                });
                break;
            case "/chats":
                core.Send(new()
                {
                    ["op"] = "list_chats",
                    ["preset"] = presetId
                });
                break;
            case "/new":
                core.Send(new()
                {
                    ["op"] = "select_chat",
                    ["preset"] = presetId
                });
                break;
            case "/save":
                if (argument.Length > 0)
                {
                    core.Send(new() { ["op"] = "rename", ["name"] = argument });
                }

                core.Send(new()
                {
                    ["op"] = "save"
                });
                break;
            case "/stop":
                core.Send(new()
                {
                    ["op"] = "cancel"
                });
                break;
            case "/cot":
                cot = argument.Length > 0 ? argument is "on" or "true" : !cot;
                main.DefaultExpanded = cot;
                foreach (var panel in sidebars.Values)
                {
                    panel.DefaultExpanded = cot;
                }

                break;
            case "/config":
                ShowSettings();
                break;
            case "/models":
                core.Send(new()
                {
                    ["op"] = "models",
                    ["provider"] = settings.Configure()["provider"]!.DeepClone()
                });
                break;
            case "/memory":
                core.Send(new()
                {
                    ["op"] = "memory",
                    ["agent"] = role,
                    ["tool"] = argument.Length > 0 ? "db_search" : "db_list",
                    ["arguments"] = new JsonObject { ["query"] = argument }
                });
                break;
            default:
                if (text.StartsWith("/", StringComparison.Ordinal))
                {
                    main.Notice("未知命令。请使用底部列出的命令。");
                }
                else
                {
                    Send(role, text);
                }

                break;
        }
    }

    private void Send(string role, string text, bool stage = false) => core.Send(new() { ["op"] = "message", ["agent"] = role, ["text"] = text, ["stage"] = stage });

    private void OpenSidebar(string role)
    {
        if (!sidebars.TryGetValue(role, out var panel))
        {
            panel = GetAgentPane(role);
            sidebars[role] = panel;
            Add(panel);
            Arrange();
        }
        panel.FocusInput();
    }

    private void CloseSidebar(string role)
    {
        if (sidebars.Remove(role, out var panel))
        {
            Remove(panel);
            Arrange();
            main.FocusInput();
        }
    }

    private void Arrange()
    {
        var count = sidebars.Count + 1;
        main.Width = count == 1 ? Dim.Fill(1) : Dim.Percent(100 / count);
        View previous = main;
        foreach (var panel in sidebars.Values)
        {
            panel.X = Pos.Right(previous) + 1;
            panel.Width = Dim.Percent(100 / count) - 1;
            previous = panel;
        }
    }

    private TracePane GetAgentPane(string role)
    {
        if (!agentPanes.TryGetValue(role, out var panel))
        {
            panel = new(role) { DefaultExpanded = cot, Y = 1, Height = Dim.Fill(2) };
            panel.Submitted += text => Submit(role, text);
            panel.Load(loadedBlocks.Where(block => Route(block.GetString("agent")) == role));
            agentPanes[role] = panel;
        }
        return panel;
    }

    private void ShowPresets() => ShowChoices("预设", presetItems, id => core.Send(new() { ["op"] = "list_chats", ["preset"] = id }));

    private void ShowChoices(string title, JsonArray items, Action<string> choose, bool allowNew = false)
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(65), Height = Dim.Percent(60) };
        var list = new ListView { Width = Dim.Fill(), Height = Dim.Fill(2) };
        var names = new ObservableCollection<string>(items.Select(item => string.IsNullOrWhiteSpace(item.GetString("name")) ? "新对话" : item.GetString("name")));
        if (allowNew)
        {
            names.Insert(0, "新建对话");
        }

        list.SetSource(names);
        list.SelectedItem = names.Count > 0 ? 0 : null;
        var cancel = new Button { Text = "取消" };
        cancel.Accepted += (_, _) => App!.RequestStop();
        var confirm = new Button { Text = "打开" };
        var chosen = false;
        void SelectItem()
        {
            if (chosen)
            {
                return;
            }
            var selected = list.SelectedItem;
            if (selected is null || selected < 0 || selected >= names.Count)
            {
                return;
            }

            var index = selected.Value - (allowNew ? 1 : 0);
            var id = index < 0 ? "" : items[index].GetString("id");
            chosen = true;
            App!.RequestStop();
            choose(id);
        }
        confirm.Accepted += (_, _) => SelectItem();
        list.Accepted += (_, _) => SelectItem();
        dialog.Add(list);
        dialog.AddButton(cancel);
        dialog.AddButton(confirm);
        App!.Run(dialog);
    }

    private void ShowSettings()
    {
        using var dialog = new ConfigurationDialog(settings, SaveSettings);
        App!.Run(dialog);
    }

    private void SaveSettings() => _ = Task.Run(() =>
    {
        try
        {
            settings.Save();
            core.Send(settings.Configure());
        }
        catch (Exception error) { App?.Invoke(() => main.Notice(error.Message)); }
    });

    protected override void Dispose(bool disposing)
    {
        lifetime.Cancel();
        foreach (var panel in agentPanes)
        {
            if (!sidebars.ContainsKey(panel.Key))
            {
                panel.Value.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
