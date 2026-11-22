# Android 交互式回复

ChatAgent 在 Android 上能按需激活 `interactive-ui` 技能。技能的说明由实际运行的 OpenUI 组件库生成。智能体可以在同一条回复中组合 Markdown 和 OpenUI Lang，应用随模型流更新对应的聊天条目。这个流程沿用已配置的模型和流式协议。

PresetAgent 成功保存新预设后会显示「回到首页」卡片。该按钮返回当前预设的首页，方便开始新对话。预设智能体或插件智能体仍在工作时，按钮保留当前会话并提示用户等待。

OpenUI 提供布局、图表、表格、富文本、表单、滑块、选择器、绘图和本地计算等组件。地图使用 MapLibre，与可绑定的地点选择联动。地图的可选连线表示地点的顺序概览。模型使用用户提供或搜索验证过的坐标与图片 URL。

聊天 Markdown 和 OpenUI 卡片中的表格都会换行显示长表头和单元格。列数较多时，可以在表格内部横滑查看其余列。

`$变量` 保存界面状态，`@Set` 与 `@Reset` 更新界面，公式和数据筛选在本地求值。用户触发 `@ToAssistant` 时，宿主把可读文本作为用户消息，并附上本次表单字段和显式上下文。使用 `$变量` 绑定的表单将要提交的值显式写入上下文，例如 `@ToAssistant("比较我的方案", {plan: $plan})`。界面程序存入对话，分支时一起保留此前的界面。未提交的输入和本地状态在应用本次运行期间缓存。

实现位于 `apps/android/genui`。Gradle 用该目录的 npm 锁文件构建 APK 内的静态资源。聊天、加载状态和展开面板由 Compose 管理。独立的 WebView 渲染 OpenUI，加载固定的本地页面，并通过限定来源的消息通道接收界面事件。页面只执行随 APK 打包的代码。允许用户打开 HTTPS 链接，地图需要网络获取街道底图。

PluginAgent 可以在发布前用同一个 WebView 离线渲染自己的草稿卡片，回读诊断、界面文本和控件，并重放交互。表单和链接操作会记录在预览结果中，支持图像输入的模型还能直接查看渲染后的 PNG。测试入口见[插件联动指南](../plugins/automation.md#在草稿中调试卡片)。

可以用以下请求验证：

- 「做一个可以调整人数和天数的旅行预算计算器，调整时立即重算总额。」
- 「比较三种方案，用可切换显示的图表和表格，最后让我选一个。」
- 「找出三个适合步行到达的景点，用地图展示地点，允许点击切换详情。」

[Open Intelligent UI](https://github.com/thesysdev/open-intelligent-ui) · [OpenUI Lang](https://github.com/thesysdev/openui) · [MapLibre GL JS](https://maplibre.org/maplibre-gl-js/docs/)
