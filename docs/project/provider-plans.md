# 接入模型订阅

MiniRP 是采用 编程智能体范式的原生 Agentic RP Harness。它的会话可以多轮调用工具，读写代码和文件，并委托子智能体完成任务。提供标准模型 API 的订阅可以从 MiniRP 直接接入。使用套餐前，按官方说明确认适用客户端、用途、地区、模型和计费规则。

Android 在「设置 → 提供商 → 添加提供商」选模板，输入对应套餐的 API Key，再到「模型」启用并分配模型。接口未提供模型列表时，可以按 ID 添加模型。Windows 在 `/config` 选择模板并填写 API Key 和模型 ID。模板会填入协议和 Base URL。套餐控制台显示账户专属地址时，请使用控制台中的地址。

## 模板与官方说明

- **OpenCode Go** 提供 Chat Completions、Responses 和 Messages 模板。使用已订阅 Go 的工作区 API Key。MiniRP 从独立的 Go 模型接口加载列表，为每个智能体会话发送稳定的会话标识。[第三方 Harness 接入](https://opencode.ai/docs/console/harnesses/)
- **Command Code Provider API · GOAT** 提供 Chat Completions、Responses 和 Messages 模板，自动按模型支持的接口筛选列表。Claude 选 Messages。GOAT 或其他包含 Provider API 的档位可创建 API Key。[Provider API](https://commandcode.ai/docs/provider)
- **Kimi Code** 提供中国与海外的 Chat Completions 模板。默认显示 `kimi-for-coding` 作为无模型列表时的起点，也可以填写套餐实际开放的 `k3.1-pro` 等模型 ID。MiniRP 在后续工具调用请求中传回推理内容。[API 接入与模型权限](https://www.kimi.com/code/docs/)
- **火山方舟 Agent Plan** 提供 Chat Completions 和 Responses 模板。使用 Agent Plan 专属 Key，`ark-code-latest` 沿用控制台的模型路由。[兼容工具接入](https://console.volcengine.com/ark/region%3Acn-beijing/docs/agent-plan-personal-other-tools)
- **BytePlus ModelArk Coding Plan** 使用套餐专用 Chat Completions 地址和 `ark-code-latest`。该套餐的额度面向官方支持的编程工具，配置前请核对自己的用途。[快速开始](https://docs.byteplus.com/en/docs/modelark/coding-plan-personal-get-started) · [使用范围](https://docs.byteplus.com/en/docs/modelark/coding-plan-personal-faq?redirect=1)
- **阿里云百炼** 提供 Coding Plan 和 Token Plan 两个模板。按套餐使用对应的专属 Key 和模型 ID。[各套餐 Base URL 与用途](https://help.aliyun.com/zh/model-studio/base-url)
- **MiniMax M Plan** 提供中国与海外的 Chat Completions 模板。填写对应平台的订阅 Key，支持把推理与回答分开显示。[中国](https://platform.minimax.cn/docs/m-plan/other-tools) · [海外](https://platform.minimax.io/docs/m-plan/other-tools)
- **小米 MiMo Token Plan** 提供中国、新加坡、欧洲三个集群模板。根据套餐控制台的地址选集群，再填对应 Key。[快速接入](https://mimo.mi.com/docs/zh-CN/tokenplan/Token%20Plan/quick-access)
- **腾讯云 TokenHub Token Plan** 使用套餐专属 Key，以及通用或 Hy 套餐列出的模型 ID。[个人版快速入门](https://cloud.tencent.com/document/product/1823/130119/)
- **百度千帆 Token Plan** 在千帆获取订阅专属 Key，再选择套餐支持的模型。该套餐使用千帆 V2 地址。[接入示例](https://cloud.baidu.com/doc/qianfan/s/Smovaaszj) · [个人套餐](https://cloud.baidu.com/product/qianfan_home/token_plan_personal.html)
- **华为云 MaaS Token Plan** 使用套餐专用的 `/plan/v2` 地址和所选模型 ID。[官方接入参数](https://support.huaweicloud.com/Token-Plan-maas/tokenplan-maas-0008.html)
- **讯飞星辰 Astron Token Plan** 使用 Token Plan 专用地址。模型 ID `astron-code-latest` 可通过模板填入。[套餐接入说明](https://www.xfyun.cn/doc/spark/TokenPlan.html)
- **阶跃星辰 Step Plan** 提供中国和海外模板，使用各地区的 `/step_plan/v1` 入口。[中国](https://platform.stepfun.com/step-plan) · [海外](https://platform.stepfun.ai/step-plan) · [智能体接入示例](https://docs.openclaw.ai/providers/stepfun)
- **Kilo Pass** 通过 Kilo Gateway API 和个人 API Key 使用。开启订阅后在 Kilo 核对可用额度及计费方式。[API Key](https://kilo.ai/docs/gateway/authentication) · [套餐与计费](https://kilo.ai/inference)
- **Synthetic** 使用 OpenAI 兼容模型入口，按账户显示的额度和计费方式使用。[Chat Completions](https://dev.synthetic.new/docs/openai/chat-completions) · [套餐与额度](https://synthetic.new/rate-limits)
- **Ollama Cloud** 使用 Ollama 原生协议和从 Ollama 账号创建的 API Key。[云模型 API](https://docs.ollama.com/cloud)

符合条件的 ChatGPT Pro 或 Business 用户可在官方领取关联到 API 组织的额度，再用该组织的项目 API Key 配置现有的 **OpenAI** 模板。[订阅 API 额度](https://developers.openai.com/api/docs/guides/chatgpt-api-credits)

符合条件的 Claude Max、Ultra 或 Team 用户可领取到 Claude Console 组织的 API 额度，再用该组织的 API Key 配置现有的 **Anthropic** 模板。[Claude 订阅 API 额度](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)

各服务的技术兼容性和订阅资格分别核对。例如，Z.ai 的 GLM Coding Plan 额度仅适用于其支持的工具，MiniRP 的 **Z.ai** 模板对应平台 API。[GLM Coding Plan 常见问题](https://docs.z.ai/devpack/faq)
