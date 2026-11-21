using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using MiniRP.Protocol;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

internal sealed partial class MainWindow : Runnable
{
    private readonly NativeCore core;
    private readonly PlatformHost platform;
    private readonly EnvironmentSettings settings;
    private readonly PluginFiles attachmentPicker;
    private readonly TracePane main = new("chat");
    private readonly Dictionary<string, TracePane> sidebars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TracePane> agentPanes = new(StringComparer.Ordinal);
    private JsonObject[] loadedBlocks = [];
    private const string Commands = "/env  /preset-agent  /create-preset  /chats  /room-chats  /config  /cot  /stop  /quit";
    private readonly Label footer = new() { Text = Commands, X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private readonly CancellationTokenSource lifetime = new();
    private JsonArray presetItems = new();
    private JsonArray providerTemplates = new();
    private string presetId = "";
    private bool cot;
    private string generating = "";
    private string chatId = "";
    private string ensembleChatId = "";
    private JsonArray? ensembleChats;
    private string pluginInstallPath = "";

    public MainWindow(NativeCore core, PlatformHost platform, EnvironmentSettings settings, PluginFiles attachmentPicker)
    {
        this.core = core;
        this.platform = platform;
        this.settings = settings;
        this.attachmentPicker = attachmentPicker;
        Title = "MiniRP";
        main.X = 1;
        main.Y = 1;
        main.Width = Dim.Fill(1);
        main.Height = Dim.Fill(2);
        main.Submitted += text => Submit("chat", text);
        main.Download += (block, url) => DownloadAsset("chat", block, url);
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
                if (assetRequests.TryRemove(item.GetString("request"), out var pending))
                {
                    pending.TrySetResult(item);
                    continue;
                }
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
                presetId = item.GetString("preset");
                providerTemplates = item.GetArray("catalog");
                foreach (var template in providerTemplates.OfType<JsonObject>()) template["id"] = template.GetString("name");
                core.Send(new() { ["op"] = "list_presets" });
                break;
            case "presets":
                presetItems = item.GetArray("items");
                ShowPresets();
                break;
            case "chats":
                if (item.GetBoolean("setup_pending"))
                {
                    core.Send(new() { ["op"] = "home", ["preset"] = item.GetString("preset"), ["locale"] = "zh-Hans" });
                    break;
                }
                CloseSidebar("preset");
                presetId = item.GetString("preset");
                ShowChoices("对话", item.GetArray("items"), id => core.Send(new() { ["op"] = "select_chat", ["preset"] = presetId, ["id"] = id }), true);
                break;
            case "home":
                CloseSidebar("preset");
                presetId = item.GetString("preset");
                chatId = ensembleChatId = "";
                ensembleChats = null;
                loadedBlocks = [];
                main.Load([]);
                foreach (var panel in agentPanes.Values) panel.Load([]);
                break;
            case "loaded":
                chatId = item.GetObject("chat").GetString("id");
                ensembleChatId = "";
                ensembleChats = item["ensemble_chats"] as JsonArray;
                presetId = item.GetString("preset");
                Title = "MiniRP · " + item.GetObject("chat").GetString("name");
                var blocks = item.GetObject("chat").GetArray("blocks").OfType<JsonObject>().ToArray();
                loadedBlocks = blocks;
                main.Load(blocks.Where(block => Route(block.GetString("agent")) == "chat"));
                foreach (var panel in agentPanes)
                {
                    panel.Value.Load(blocks.Where(block => Route(block.GetString("agent")) == panel.Key));
                }
                main.FocusInput();
                if (ensembleChats is not null) PickEnsembleChat();
                break;
            case "ensemble_chats":
                if (item.GetString("chat") == chatId) ensembleChats = item.GetArray("items");
                break;
            case "ensemble_chat_loaded":
                if (item.GetString("chat") != chatId) break;
                ensembleChatId = item.GetString("conversation");
                main.Load(item.GetArray("blocks").OfType<JsonObject>());
                main.FocusInput();
                break;
            case "ensemble_message":
                if (item.GetString("conversation") != ensembleChatId || item.GetString("chat") != chatId) break;
                var message = item.GetObject("item");
                var speaker = message.GetString("speaker");
                if (message.GetString("role") == "assistant")
                {
                    var visible = (JsonObject)message.DeepClone();
                    if (speaker.Length > 0) visible["content"] = speaker + ": " + message.GetString("content");
                    main.Receive(new() { ["kind"] = "ensemble_assistant", ["item"] = visible });
                    break;
                }
                main.Receive(new() { ["kind"] = "user", ["block"] = message.GetString("id"),
                    ["text"] = speaker.Length == 0 ? message.GetString("content") : speaker + ": " + message.GetString("content") });
                break;
            case "preset_opened":
                if (item.GetBoolean("created") || item.GetBoolean("selected"))
                {
                    presetId = item.GetString("preset");
                    chatId = ensembleChatId = "";
                    ensembleChats = null;
                    main.Load([]);
                }
                OpenSidebar("preset");
                GetAgentPane("preset").Load(item.GetArray("blocks").OfType<JsonObject>());
                break;
            case "generating":
                generating = item.GetBoolean("value") ? item.GetString("agent") : "";
                break;
            case "steer_queued":
                if (item.GetString("chat") != chatId || item.GetString("preset") != presetId) break;
                var queuedConversation = item.GetString("conversation");
                if (queuedConversation.Length > 0 && queuedConversation != ensembleChatId) break;
                var queued = item.GetObject("item");
                var queuedText = queued.GetString("content");
                if (queuedConversation.Length == 0 && queued.GetString("kind") == "system") queuedText = "System: " + queuedText;
                GetAgentPane(Route(queued.GetString("agent"))).Receive(new() { ["kind"] = "user", ["block"] = queued.GetString("id"),
                    ["agent"] = queued.GetString("agent"), ["text"] = queuedText });
                break;
            case "system":
                GetAgentPane(Route(item.GetString("agent"))).Receive(new() { ["kind"] = "user", ["block"] = item.GetString("block"),
                    ["agent"] = item.GetString("agent"), ["text"] = "System: " + item.GetString("text") });
                break;
            case "plugins":
                main.Notice(string.Join(", ", item.GetArray("items").Select(plugin => plugin.GetString("id"))));
                break;
            case "plugin":
                if (pluginInstallPath.Length > 0 && item.GetString("request") == "inspect-plugin")
                {
                    var plugin = item.GetObject("item");
                    var path = pluginInstallPath;
                    pluginInstallPath = "";
                    var description = string.Join(", ", plugin.GetArray("permissions").Concat(plugin.GetArray("network")).Select(value => JsonValues.GetText(value)));
                    if (MessageBox.Query(App!, "Install " + plugin.GetString("name"), description, "Install", "Cancel") == 0)
                        core.Send(new() { ["op"] = "plugin_install", ["path"] = path });
                }
                break;
            case "plugin_error":
                main.Notice(item.GetString("plugin") + ": " + item.GetString("message"));
                break;
            case "error":
                main.Notice(item.GetString("message"));
                break;
            case "archive_exported":
                main.Notice("文件已导出");
                break;
            case "archive_imported":
                main.Notice("文件已导入");
                break;
            case "busy":
                Title = item.GetBoolean("value") ? "MiniRP · 处理中" : "MiniRP";
                break;
            case "models":
                ShowChoices("模型", item.GetArray("items"), id => { settings["DEEPSEEK_MODEL"] = id; SaveSettings(); });
                break;
            case "memory":
                main.Notice(JsonValues.Serialize(item["result"]));
                break;
            default:
                if (item.GetString("conversation") is { Length: > 0 } targetRoom && targetRoom != ensembleChatId) break;
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

    private static string Route(string agent) => agent is "env" or "EnvAgent" ? "env" : agent is "preset" or "PresetAgent" or "PluginAgent" ? "preset" : "chat";

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
            case "/create-preset":
                core.Send(new() { ["op"] = "create_preset", ["open"] = true,
                    ["name"] = argument.Length > 0 ? argument : "新预设", ["locale"] = "zh-Hans" });
                break;
            case "/attach" when role == "preset":
                AttachToPreset(argument);
                break;
            case "/room-chats":
                PickEnsembleChat();
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
            case "/system" when argument.Length > 0:
                core.Send(new() { ["op"] = generating == role ? "steer" : "system_message", ["agent"] = role,
                    ["role"] = "system", ["chat"] = chatId, ["preset"] = presetId, ["text"] = argument });
                break;
            case "/plugin-install" when argument.Length > 0:
                pluginInstallPath = Path.GetFullPath(argument.Trim('"'));
                core.Send(new() { ["op"] = "plugin_inspect", ["path"] = pluginInstallPath, ["request"] = "inspect-plugin" });
                break;
            case "/export-room" or "/export-preset" or "/export-save":
                TransferArchive("export", parts[0]["/export-".Length..], argument);
                break;
            case "/import-preset" or "/import-save":
                TransferArchive("import", parts[0]["/import-".Length..], argument);
                break;
            case "/plugin-export":
                var archive = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (archive.Length == 2) TransferArchive("export", "plugin", archive[1], archive[0]);
                else main.Notice("用法 /plugin-export <插件 ID> <文件>");
                break;
            case "/plugins":
                core.Send(new() { ["op"] = "plugins_list" });
                break;
            case "/plugin-run" when argument.Length > 0:
                core.Send(new() { ["op"] = "plugin_command", ["id"] = argument });
                break;
            case "/plugin-remove" when argument.Length > 0:
                core.Send(new() { ["op"] = "plugin_uninstall", ["id"] = argument });
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

    private void Send(string role, string text, bool stage = false)
    {
        if (role == "chat" && ensembleChats is not null && ensembleChatId.Length == 0)
        {
            PickEnsembleChat();
            return;
        }
        try { core.Send(new() { ["op"] = generating == role ? "steer" : "message", ["agent"] = role, ["chat"] = chatId, ["conversation"] = ensembleChatId, ["preset"] = presetId, ["text"] = text, ["stage"] = stage }); }
        catch (InvalidOperationException error) { main.Notice(error.Message); }
    }

    private void TransferArchive(string action, string type, string path, string plugin = "")
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
        {
            main.Notice("请输入文件路径");
            return;
        }
        try
        {
            core.Send(new() { ["op"] = "archive_" + action, ["type"] = type,
                ["path"] = Path.GetFullPath(path), ["preset"] = presetId, ["room"] = chatId, ["id"] = plugin });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            main.Notice(ex.Message);
        }
    }

    private void PickEnsembleChat()
    {
        if (ensembleChats is null) return;
        ShowChoices("聊天", ensembleChats, room => core.Send(new() { ["op"] = "select_ensemble_chat", ["chat"] = chatId, ["conversation"] = room }));
    }

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
        footer.Text = sidebars.ContainsKey("preset") ? "/attach  /close  " + Commands : Commands;
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
        if (role == "chat") return main;
        if (!agentPanes.TryGetValue(role, out var panel))
        {
            panel = new(role) { DefaultExpanded = cot, Y = 1, Height = Dim.Fill(2) };
            panel.Submitted += text => Submit(role, text);
            panel.Download += (block, url) => DownloadAsset(role, block, url);
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
        using var dialog = new ConfigurationDialog(settings, SaveSettings, providerTemplates.Count == 0 ? null : apply =>
            ShowChoices("提供商模板", providerTemplates, id =>
                apply(providerTemplates.OfType<JsonObject>().First(item => item.GetString("id") == id))));
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
