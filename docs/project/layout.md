# 项目结构

| 目录 | 内容 |
| --- | --- |
| `src/MiniRP.Core` | HarnessCore 的 C# NativeAOT 实现。负责智能体、模型协议、会话与版本化、SQLite 记忆、插件清单验证、Actions、Assets、事件队列。 |
| `src/MiniRP.Protocol` | 跨项目共用的协议与 JSON 表示。 |
| `src/MiniRP.Cli` | 桌面 Terminal.Gui 界面与平台适配层。ClearScript 提供 V8，平台负责 HTTP、选择文件和浏览器。 |
| `apps/android` | Android Studio 工程。Kotlin 和 Compose 页面、Paging 3、JNI 适配、Javet V8、OkHttp、系统 picker 与 WebView。 |
| `tests/MiniRP.Core.Tests` | HarnessCore、模型协议和插件端到端测试。 |
| `apps/android/app/src/androidTest` | Compose UI、Paging 和 JNI 集成测试。 |
| `sdk/javascript` | 插件作者使用的 TypeScript 声明、URL helper 和 manifest Schema。 |
| `sdk/MiniRP.Plugin.Tool` | 生成证书、打包、签名及验签命令。 |
| `examples/stream-assets` | 可签名运行的插件示例。 |
| `eng` | NativeAOT、桌面和 Android 构建入口。 |
| `assets/branding` | 品牌图片及图标来源记录。 |

智能体在 HarnessCore 中共用一套会话与工具运行机制。UI 通过 C ABI 收发 JSON 命令和事件，大资源通过独立的原始字节传输入口交付。插件声明由同一个核心解析，Android 与桌面仅提供 V8 和平台 I/O。

Android 由 ViewModel 持有界面状态，Compose 随生命周期收集。对话列表用消息和 Markdown 块的稳定 key 定位，抽屉使用 Paging 3 从 SQLite 目录分批读取。页面导航使用 Navigation Compose 类型路由，返回手势由 AndroidX 和 Material3 组件处理。

本地工作区数据、附件、插件包、证书与构建产物均由 [.gitignore](../../.gitignore) 排除。SDK 源码、示例源码、依赖锁文件和构建配置纳入版本控制。
