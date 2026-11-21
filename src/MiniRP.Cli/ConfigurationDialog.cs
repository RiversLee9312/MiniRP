using System.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

internal sealed class ConfigurationDialog : Dialog
{
    private static readonly (string Key, string Label)[] Definitions =
    [
        ("MINIRP_PROVIDER_PROTOCOL", "协议：openai / responses / anthropic / gemini / ollama"),
        ("DEEPSEEK_BASE_URL", "API 地址"),
        ("DEEPSEEK_API_KEY", "API Key"),
        ("DEEPSEEK_MODEL", "模型 ID"),
        ("DEEPSEEK_THINKING", "思考：enabled / disabled"),
        ("DEEPSEEK_REASONING_EFFORT", "思考强度（可选）"),
        ("DEEPSEEK_CONTEXT_WINDOW", "上下文大小（token，可选）"),
        ("DEEPSEEK_MAX_OUTPUT_TOKENS", "最大输出（token，可选）"),
        ("DEEPSEEK_COMPACT_AT_PERCENT", "压缩阈值（%，默认 80）"),
        ("DEEPSEEK_BACKGROUND_COMPACTION", "异步压缩：enabled / disabled（默认启用）"),
        ("DEEPSEEK_BACKGROUND_COMPACT_AT_PERCENT", "异步压缩触发点（%，默认 70）"),
        ("DEEPSEEK_COMPACT_AT", "压缩阈值（token，可选）"),
        ("MINIRP_SEARCH_PROVIDER", "网页搜索：deepseek / tavily / exa / brave"),
        ("MINIRP_FETCH_PROVIDER", "网页抓取 API：deepseek / tavily / exa"),
        ("MINIRP_FETCH_BACKEND", "网页抓取策略：api / local / api_then_local"),
        ("MINIRP_SEARCH_API_KEY", "DeepSeek 搜索和抓取 API Key（可选）"),
        ("MINIRP_TAVILY_API_KEY", "Tavily API Key（可选）"),
        ("MINIRP_EXA_API_KEY", "Exa API Key（可选）"),
        ("MINIRP_BRAVE_SEARCH_API_KEY", "Brave Search API Key（可选）")
    ];

    public ConfigurationDialog(EnvironmentSettings settings, Action save)
    {
        Title = "模型与 API";
        Width = Dim.Percent(85);
        Height = Dim.Percent(90);
        var form = new View { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill(2), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        var fields = new Dictionary<string, TextField>(StringComparer.Ordinal);
        var row = 0;
        foreach (var (key, label) in Definitions)
        {
            form.Add(new Label { Text = label, X = 1, Y = row++ });
            var field = new TextField { Value = settings[key], X = 1, Y = row++, Width = Dim.Fill(2), Secret = key.EndsWith("KEY", StringComparison.Ordinal) };
            fields[key] = field;
            form.Add(field);
        }
        form.SetContentSize(new Size(50, row));
        Add(form);
        var cancel = new Button { Text = "取消" };
        cancel.Accepted += (_, _) => App!.RequestStop();
        var confirm = new Button { Text = "保存" };
        confirm.Accepted += (_, _) =>
        {
            foreach (var field in fields)
            {
                settings[field.Key] = field.Value.Value ?? "";
            }
            save();
            App!.RequestStop();
        };
        AddButton(cancel);
        AddButton(confirm);
    }
}
