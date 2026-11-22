# 验证 Android 改动

先按照[项目构建说明](../../README.md)生成 Android arm64 的 HarnessCore NativeAOT 库，再用 Gradle 编译应用和 Instrumentation 测试。应用启动时通过 JNI 加载该库。只重打 APK 会继续使用上一次生成的核心库。

Android 端用 Compose UI 测试验证导航、返回动画、Room 内切换 Chat和编辑行为。网络流程用 MockWebServer 提供确定的模型事件；测试可以挂起模型请求，确认界面仍能响应。插件和状态的端到端行为也由 HarnessCore 测试覆盖。

正常设备验证采用覆盖安装，保存已有会话和设置。需要验证首次运行时，可以用 Gradle 参数 `-Pminirp.isolatedTest=true` 构建独立 applicationId 版本。这样验证空数据、预设创建和插件入口时无需清空日常使用的应用。

后台回复的首次授权流程由 `BackgroundTurnsTest` 在干净的测试版上验证。测试用 MockWebServer 暂停一轮回复，切到桌面后释放响应，检查回复完成、通知撤下以及重新打开时的聊天内容。另用主动停止和模拟失败检查中断提示。Android 16 及以上验证应用发出的 Live Updates 请求，系统是否提升通知由设备与用户设置决定。

`LiveUpdatesDeviceProbe` 用于核对真机状态栏。在设备所有者用户下安装 `-Pminirp.isolatedTest=true` 构建的应用和测试 APK，再用以下命令运行探针。

```sh
adb shell am instrument --user 0 -w -r -e inspectLiveUpdates true -e class 'com.minirp.app.LiveUpdatesDeviceProbe#observeBackgroundTurn' com.minichat.app.qa.test/androidx.test.runner.AndroidJUnitRunner
```

首次运行时在真机上允许隔离测试版发送通知。测试用本地模型响应验证切到桌面后回复完成、通知撤下，同时输出系统是否允许、请求是否有效及通知是否已被提升。已开启通知的测试版可以重复运行，用同一轮流程比较不同的系统设置。

在已验证的 ColorOS 17 上，MiniRP 的「设置 → 后台回复 → 实时更新」打开应用通知设置。开启其中的「流体云显示实时活动」后，进行中的回复会在桌面显示流体云；本次测试设备上的开关初始为关闭，关闭时后台回复仍能完成。
