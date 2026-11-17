using System.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Minichat.Cli;

internal sealed class ConfigurationDialog : Dialog
{
    private static readonly (string Key, string Label)[] Definitions =
    [
        ("MINICHAT_PROVIDER_PROTOCOL", "协议：openai / responses / anthropic / gemini / ollama"),
        ("DEEPSEEK_BASE_URL", "API 地址"),
        ("DEEPSEEK_API_KEY", "API Key"),
        ("MINICHAT_SEARCH_API_KEY", "搜索 API Key（DeepSeek，可选）"),
        ("DEEPSEEK_MODEL", "模型 ID"),
        ("DEEPSEEK_THINKING", "思考：enabled / disabled"),
        ("DEEPSEEK_REASONING_EFFORT", "思考强度（可选）"),
        ("DEEPSEEK_CONTEXT_WINDOW", "上下文大小（token，可选）"),
        ("DEEPSEEK_MAX_OUTPUT_TOKENS", "最大输出（token，可选）"),
        ("DEEPSEEK_COMPACT_AT", "压缩阈值（token，可选）"),
        ("MINICHAT_FETCH_BACKEND", "网页抓取：api / local / api_then_local")
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
