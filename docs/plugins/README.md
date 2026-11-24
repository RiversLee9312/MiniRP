# 插件开发

在预设对话里，可以请 PresetAgent 委派插件开发。PluginAgent 从[第一次接手草稿](getting-started.md#pluginagent-第一次接手草稿)开始，跟着一个值班状态例子查文档、编辑文件和测试 Action。在自己的编辑器中开发，请从[本地环境和示例](getting-started.md#在本地开发插件)开始。

涉及多个智能体、状态和卡片时，参考[完整联动示例](automation.md)。已有源码时，看[打包、安装与调试](packaging.md)。发布后，从[真实入口验证已部署的插件](packaging.md#验证已部署的插件)。

## API 参考

- [运行时、清单与事件](runtime.md)介绍执行顺序、权限和宿主事件。
- [本地化](runtime.md#本地化)涵盖清单翻译、调用语言和运行时文案。
- [Actions、Assets 与平台 I/O](actions-assets.md)涵盖跨插件事件、分块资源、网络和文件选择。
- [Harness API](harness.md)涵盖预设、聊天、房间、角色会话和模型请求。
- [数据库与结构化状态](state.md)涵盖作用域、修订、只读投影和 OpenUI 卡片。
- [技能与设置](skills-settings.md)涵盖智能体工具、Asset 返回值和设置声明。

[SDK 类型](../../sdk/javascript/index.d.ts) · [清单 Schema](../../sdk/javascript/manifest.schema.json) · [预设设计](../presets/README.md)
