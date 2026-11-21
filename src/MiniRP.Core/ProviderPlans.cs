using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class ProviderPlans
{
    internal const string OpenCodeSessionKey = "_context/opencode-session";

    internal enum Gateway { Standard, OpenCodeGo, CommandCode, KimiCode, MiniMax }

    internal sealed record Plan(string Name, string Url, string Model = "", string Protocol = "openai", Gateway Wire = Gateway.Standard);

    private const string OpenCodeOpenAi = "https://opencode.ai/inference/go/openai/v1";
    private const string OpenCodeAnthropic = "https://opencode.ai/inference/go/anthropic/v1";
    private const string CommandCode = "https://api.commandcode.ai/provider/v1";
    private const string Ark = "https://ark.cn-beijing.volces.com/api/plan/v3";

    private static readonly Plan[] Plans =
    [
        new("OpenCode Go", OpenCodeOpenAi, Wire: Gateway.OpenCodeGo),
        new("OpenCode Go (Responses)", OpenCodeOpenAi, Protocol: "responses", Wire: Gateway.OpenCodeGo),
        new("OpenCode Go (Messages)", OpenCodeAnthropic, Protocol: "anthropic", Wire: Gateway.OpenCodeGo),
        new("Command Code Provider API · GOAT", CommandCode, Wire: Gateway.CommandCode),
        new("Command Code Provider API · GOAT (Responses)", CommandCode, Protocol: "responses", Wire: Gateway.CommandCode),
        new("Command Code Provider API · GOAT (Messages)", CommandCode, Protocol: "anthropic", Wire: Gateway.CommandCode),
        new("Kimi Code (China)", "https://api.kimi.com/coding/v1", "kimi-for-coding", Wire: Gateway.KimiCode),
        new("Kimi Code (Global)", "https://api.kimi.ai/coding/v1", "kimi-for-coding", Wire: Gateway.KimiCode),
        new("火山方舟 Agent Plan", Ark, "ark-code-latest"),
        new("火山方舟 Agent Plan (Responses)", Ark, "ark-code-latest", "responses"),
        new("BytePlus ModelArk Coding Plan", "https://ark.ap-southeast.bytepluses.com/api/coding/v3", "ark-code-latest"),
        new("阿里云百炼 Coding Plan", "https://coding.dashscope.aliyuncs.com/v1"),
        new("阿里云百炼 Token Plan", "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1"),
        new("MiniMax M Plan (China)", "https://api.minimax.cn/v1", "MiniMax-M3.1-Flash", Wire: Gateway.MiniMax),
        new("MiniMax M Plan (Global)", "https://api.minimax.io/v1", "MiniMax-M3.1-Flash", Wire: Gateway.MiniMax),
        new("Xiaomi MiMo Token Plan (China)", "https://token-plan-cn.xiaomimimo.com/v1", "mimo-v2.6-pro"),
        new("Xiaomi MiMo Token Plan (Singapore)", "https://token-plan-sgp.xiaomimimo.com/v1", "mimo-v2.6-pro"),
        new("Xiaomi MiMo Token Plan (Europe)", "https://token-plan-ams.xiaomimimo.com/v1", "mimo-v2.6-pro"),
        new("腾讯云 TokenHub Token Plan", "https://tokenhub.tencentmaas.com/plan/v3"),
        new("百度千帆 Token Plan", "https://qianfan.baidubce.com/v2"),
        new("华为云 MaaS Token Plan", "https://api.modelarts-maas.com/plan/v2", "glm-5.3"),
        new("讯飞星辰 Astron Token Plan", "https://maas-token-api.cn-huabei-1.xf-yun.com/v2", "astron-code-latest"),
        new("StepFun Step Plan (China)", "https://api.stepfun.com/step_plan/v1", "step-3.7-flash"),
        new("StepFun Step Plan (Global)", "https://api.stepfun.ai/step_plan/v1", "step-3.7-flash"),
        new("Kilo Gateway · Kilo Pass", "https://ai-gateway.kilo.ai/api/v1"),
        new("Synthetic", "https://api.synthetic.new/openai/v1"),
        new("Ollama Cloud", "https://ollama.com", Protocol: "ollama")
    ];

    internal static IEnumerable<JsonObject> Catalog() => Plans.Select(plan => new JsonObject
    {
        ["name"] = plan.Name, ["protocol"] = plan.Protocol, ["url"] = plan.Url,
        ["model"] = plan.Model, ["subscription"] = true
    });

    internal static Plan? Find(Provider provider) => Array.Find(Plans, plan =>
        plan.Url == provider.Url.TrimEnd('/') && plan.Protocol == provider.Protocol);
}
