using System.Drawing;
using System.Text.Json.Nodes;
using MiniRP.Protocol;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

internal enum ConfigurationSection { Providers, Models, Research }

internal sealed class ConfigurationDialog : Dialog
{
    public bool Saved { get; private set; }

    private static readonly (string Key, string Label)[] Providers =
    [
        ("MINIRP_PROVIDER_PROTOCOL", "协议：openai / responses / anthropic / gemini / ollama"),
        ("DEEPSEEK_BASE_URL", "API 地址"),
        ("DEEPSEEK_API_KEY", "API Key")
    ];
    private static readonly (string Key, string Label)[] Models =
    [
        ("DEEPSEEK_MODEL", "模型 ID"),
        ("DEEPSEEK_THINKING", "思考：enabled / disabled"),
        ("DEEPSEEK_REASONING_EFFORT", "思考强度（可选）"),
        ("DEEPSEEK_CONTEXT_WINDOW", "上下文大小（token，可选）"),
        ("DEEPSEEK_MAX_OUTPUT_TOKENS", "最大输出（token，可选）"),
        ("DEEPSEEK_COMPACT_AT_PERCENT", "压缩阈值（%，默认 80）"),
        ("DEEPSEEK_BACKGROUND_COMPACTION", "异步压缩：enabled / disabled（默认启用）"),
        ("DEEPSEEK_BACKGROUND_COMPACT_AT_PERCENT", "异步压缩触发点（%，默认 70）"),
        ("DEEPSEEK_COMPACT_AT", "压缩阈值（token，可选）"),
        ("MINIRP_DESCRIBE_IMAGES", "为当前模型使用图像描述（yes / no）"),
        ("MINIRP_DESCRIBE_AUDIO", "为当前模型使用音频描述（yes / no）"),
        ("MINIRP_IMAGE_DESCRIPTION_MODEL", "图像描述模型 ID（留空关闭）"),
        ("MINIRP_IMAGE_DESCRIPTION_PROTOCOL", "图像描述服务协议（留空沿用当前协议）"),
        ("MINIRP_IMAGE_DESCRIPTION_URL", "图像描述服务 API 地址（留空沿用当前地址）"),
        ("MINIRP_IMAGE_DESCRIPTION_KEY", "图像描述服务 API Key"),
        ("MINIRP_AUDIO_DESCRIPTION_MODEL", "音频描述模型 ID（留空关闭）"),
        ("MINIRP_AUDIO_DESCRIPTION_PROTOCOL", "音频描述服务协议（留空沿用当前协议）"),
        ("MINIRP_AUDIO_DESCRIPTION_URL", "音频描述服务 API 地址（留空沿用当前地址）"),
        ("MINIRP_AUDIO_DESCRIPTION_KEY", "音频描述服务 API Key")
    ];
    private static readonly (string Key, string Label)[] Research =
    [
        ("MINIRP_SEARCH_PROVIDER", "网页搜索：deepseek / tavily / exa / brave"),
        ("MINIRP_FETCH_PROVIDER", "网页抓取 API：deepseek / tavily / exa"),
        ("MINIRP_SEARCH_API_KEY", "DeepSeek 搜索和抓取 API Key（可选）"),
        ("MINIRP_TAVILY_API_KEY", "Tavily API Key（可选）"),
        ("MINIRP_EXA_API_KEY", "Exa API Key（可选）"),
        ("MINIRP_BRAVE_SEARCH_API_KEY", "Brave Search API Key（可选）")
    ];

    public ConfigurationDialog(string title, ConfigurationSection section, EnvironmentSettings settings,
        Action<Action<JsonObject>>? chooseTemplate = null)
    {
        Title = title;
        Width = Dim.Percent(85);
        Height = Dim.Percent(90);
        var form = new View { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill(2), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        var fields = new Dictionary<string, TextField>(StringComparer.Ordinal);
        var row = 0;
        JsonObject? selectedTemplate = null;
        if (section == ConfigurationSection.Providers && chooseTemplate is not null)
        {
            var template = new Button { Text = "选择提供商模板", X = 1, Y = row++ };
            template.Accepted += (_, _) => chooseTemplate(ApplyTemplate);
            form.Add(template);
        }
        var definitions = section switch
        {
            ConfigurationSection.Providers => Providers,
            ConfigurationSection.Models => Models,
            _ => Research,
        };
        foreach (var (key, label) in definitions)
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
            if (selectedTemplate is not null &&
                StringComparer.Ordinal.Equals(settings["DEEPSEEK_BASE_URL"].TrimEnd('/'), selectedTemplate.GetString("url").TrimEnd('/')))
            {
                settings["DEEPSEEK_MODEL"] = selectedTemplate.GetString("model");
                settings["DEEPSEEK_THINKING"] = "";
                settings["DEEPSEEK_REASONING_EFFORT"] = "";
            }
            Saved = true;
            App!.RequestStop();
        };
        AddButton(cancel);
        AddButton(confirm);

        void ApplyTemplate(JsonObject template)
        {
            selectedTemplate = template;
            var url = template.GetString("url");
            var protocol = template.GetString("protocol");
            if (!StringComparer.Ordinal.Equals(fields["DEEPSEEK_BASE_URL"].Value?.TrimEnd('/'), url))
                fields["DEEPSEEK_API_KEY"].Value = "";
            fields["MINIRP_PROVIDER_PROTOCOL"].Value = protocol;
            fields["DEEPSEEK_BASE_URL"].Value = url;
        }
    }
}
