# 验证 Android 改动

先按照[项目构建说明](../../README.md)生成 Android arm64 的 HarnessCore NativeAOT 库，再用 Gradle 编译应用和 Instrumentation 测试。应用启动时通过 JNI 加载该库。只重打 APK 会继续使用上一次生成的核心库。

Android 端用 Compose UI 测试验证导航、返回动画、Room 内切换 Chat和编辑行为。网络流程用 MockWebServer 提供确定的模型事件；测试可以挂起模型请求，确认界面仍能响应。插件和状态的端到端行为也由 HarnessCore 测试覆盖。

正常设备验证采用覆盖安装，保存已有会话和设置。需要验证首次运行时，可以用 Gradle 参数 `-Pminirp.isolatedTest=true` 构建独立 applicationId 版本。这样验证空数据、预设创建和插件入口时无需清空日常使用的应用。
