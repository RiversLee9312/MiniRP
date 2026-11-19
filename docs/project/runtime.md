# 运行路径

客户端负责界面、网络和系统 I/O。HarnessCore 维护会话与任务顺序，接收一个命令后会发出流式事件。模型响应分块和平台 I/O 回复可以在长任务中直接送达核心。Android 使用 JNI，桌面使用同一套 C ABI。

普通聊天有一个直接面对用户的 ChatAgent。群像中的每个 Room 包含一条或多条 Chat 和一个角色阵容。Room 内共享 EnvAgent 和世界状态。角色只能向已加入的 Chat 发言，消息保存后由核心向该 Chat 的成员会话交付。每位角色在一个 Room 中有独立的 ChatAgent，会在启用智能体搜索时使用自己的 SearchAgent 会话。

插件与 Agent 的分工由 [清单与 Skill](../plugins/skills-settings.md) 决定。插件可以接收 [Actions 与宿主事件](../plugins/runtime.md)，并通过持久化状态和卡片与 Agent、用户交互。状态更新属于插件业务工具，Agent 可读取和订阅授权投影。
