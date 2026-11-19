# 打包、安装与调试

## 创建证书、打包和验签

下面的命令在项目根目录执行。为开发签名使用本地证书，正式发布可使用自己的 PKCS#12 证书。在需要让多个插件共享 `protected` Asset 时，用同一张证书签这些包。保持证书与私钥备份，更新已经安装的包时需要同一张证书。

1. 在当前终端设置 `MINIRP_SIGNING_PASSWORD`，签名工具从环境变量读取密码。把密钥存放在包目录以外。仓库的 `.gitignore` 会屏蔽 `.keys/` 和 `.pfx`。
2. 准备目录并生成私钥证书。以下为 PowerShell 示例。

       New-Item -ItemType Directory -Force .keys, artifacts/plugins
       dotnet run --project sdk/MiniRP.Plugin.Tool -- certificate .keys/dev.pfx

3. 打包并核对包名和证书的 SHA-256 指纹。

       dotnet run --project sdk/MiniRP.Plugin.Tool -- pack examples/stream-assets/plugin .keys/dev.pfx artifacts/plugins/stream-example.plugin
       dotnet run --project sdk/MiniRP.Plugin.Tool -- verify artifacts/plugins/stream-example.plugin

打包工具拒绝覆盖既有输出。每次打包使用新产物名或移动上一个产物。安装包为 ZIP，压缩后和解包后的大小上限均为 128 MiB，至多 256 个条目。签名为 detached CMS，存放在 `META-INF/minichat.p7s`。签名输入固定为 UTF-8 字节，首行为 `MCPLUGIN/2`，之后按条目名 ordinal 升序，每行是大写 SHA-256 十六进制摘要、TAB、准确条目名、LF，结尾有 LF。签名条目不参与摘要。证书用来证明包的签名身份，允许自签名。

## 安装和验证

- Android 在「设置 → 插件」选择已签名的 `.plugin`。检查安装确认中的包名、证书指纹、权限和网络 origin。安装后可运行示例，并可从设置打开示例声明的 Markdown 页。
- Android 可从已安装插件的操作菜单导出原始签名包。桌面可用 `/plugin-export stream-example stream-example.plugin`。
- 桌面输入 `/plugin-install path/to/stream-example.plugin`，用 `/plugins` 查看，用 `/plugin-run stream-example` 运行，用 `/plugin-remove stream-example` 卸载。交互文件选择需要图形终端会话。
- 测试示例时先打开一场聊天。它会生成 1,000,003 字节并按块消费，将大小写到插件 chat DB 的 `stream-example/last-size`。可在另一个调试 Hook 里用 `api.db.get({scope:"chat",key:"stream-example/last-size"})` 核对。
- 从旧版包迁移时，加入 `packageName`，设 `apiVersion:2`，把回调集中登记到 `actions.handlers`，把所有事件和回调声明改成 Hook URL，重新签名。安装 API 接受已签名的 v2 包。已在旧版安装的包有兼容读取路径，旧的未签名 DB 与新签名身份分开存储。

## 归属预设

预设自己的插件在签名前把 `presetId` 写入 `manifest.json`，值为预设的小写 UUID。通过预设委派创建的插件由宿主填写该字段。预设包和存档包均携带归属该预设的原始签名包，导入时重新验签，新装插件的作用域默认为该预设。归属决定插件随哪个预设导出，运行作用域继续由宿主管理。旧版已发布草稿可由本地部署记录核对归属和签名。

## 调试与发布

先用打包工具抓清单和签名错误，再分别验证安装、Hook、资源读取和取消。给大资源优先使用 `assets.open`，逐块处理并在退出时取消 reader。通过 `api.db` 保存跨调用或进程的状态，通过模块变量保存可重建的缓存。测试两个插件共享资源时，要分别覆盖同证书、不同证书和私有 Asset。

插件失败由宿主上报 `plugin_error`，错误包含插件短 ID 和失败消息。回调中应显式处理来自宿主 API 的 Promise 拒绝。同一个插件的 Hook 可以与本插件的资源生成交错执行，更新共享状态时要考虑 `await` 间的顺序。升级和复现版本问题时记录 SDK API 版本、包名、证书指纹和相关 Hook URL。
