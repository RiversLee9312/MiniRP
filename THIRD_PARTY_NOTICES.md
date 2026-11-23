# Third-party components

Dependencies are retained as maintained packages; this list also records directly bundled assets.

- .NET runtime and libraries, Microsoft: [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).
- Terminal.Gui and Terminal.Gui.Editor: [MIT](https://github.com/tui-cs/Terminal.Gui/blob/v2.5.0/LICENSE), [Editor and its upstream notices](https://github.com/tui-cs/Editor/tree/main/third_party).
- MCP C# SDK: [Apache-2.0](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/LICENSE).
- Playwright MCP and Playwright: [Apache-2.0](https://github.com/microsoft/playwright-mcp/blob/main/LICENSE).
- SharpZipLib 1.4.2: [MIT](https://github.com/icsharpcode/SharpZipLib/blob/v1.4.2/LICENSE.txt). The desktop runtime installer uses its BZip2 decoder and bundles the notice.
- SQLite native library: [public domain](https://www.sqlite.org/copyright.html); packaged via SQLitePCLRaw.lib.e_sqlite3.
- AndroidX, Jetpack Compose and Material 3: [Apache-2.0](https://source.android.com/docs/setup/about/licenses).
- MaterialKolor 5.0.1: [MIT](https://github.com/jordond/MaterialKolor/blob/5.0.1/LICENSE), bundled in the Android app with its license. The color engine ports [Material Color Utilities](https://github.com/material-foundation/material-color-utilities/blob/main/LICENSE) under Apache-2.0.
- ClearScript: [MIT](https://github.com/ClearFoundry/ClearScript/blob/master/License.txt).
- Javet: [Apache-2.0](https://github.com/caoccao/Javet/blob/main/LICENSE).
- V8: [BSD-3-Clause](https://github.com/v8/v8/blob/main/LICENSE).
- Web Streams polyfill 4.3.0: [MIT](https://github.com/MattiasBuelens/web-streams-polyfill/blob/master/LICENSE). The published JavaScript and license are bundled in the shared runtime.
- Bouncy Castle C# 2.7.0 and Java 1.87: [license](https://www.bouncycastle.org/licence/). The C# SDK signs detached CMS packages, with verification on desktop and the Android platform host.
- OkHttp: [Apache-2.0](https://github.com/square/okhttp/blob/master/LICENSE.txt).
- Multiplatform Markdown Renderer: [Apache-2.0](https://github.com/mikepenz/multiplatform-markdown-renderer/blob/develop/LICENSE).
- OpenUI and Intelligent UI components: [MIT](https://github.com/thesysdev/openui/blob/main/LICENSE). Bundled for Android and desktop WebUI interactive replies.
- React: [MIT](https://github.com/facebook/react/blob/main/LICENSE). MapLibre GL JS: [BSD-3-Clause](https://github.com/maplibre/maplibre-gl-js/blob/main/LICENSE.txt). The interactive map shows map provider attribution.
- Desktop WebUI: [Material Color Utilities (Apache-2.0)](https://github.com/material-foundation/material-color-utilities/blob/main/LICENSE), [react-markdown (MIT)](https://github.com/remarkjs/react-markdown/blob/main/license), [remark-gfm (MIT)](https://github.com/remarkjs/remark-gfm/blob/main/license), and [html-to-image (MIT)](https://github.com/bubkoo/html-to-image/blob/master/LICENSE).
- Material Symbols Rounded: Google Fonts official subset. Source and SHA-256 are in `assets/branding/material-symbols-source.json`; the Apache-2.0 license is bundled with the app.
- Desktop WebUI typography uses the Google Fonts subsets of [Google Sans Flex](https://github.com/googlefonts/googlesans-flex), licensed under SIL Open Font License 1.1. The WebUI bundles the Google Sans Flex and Material Symbols Rounded notices in its `licenses` directory.
- Turndown 7.2.2: [MIT](https://github.com/mixmark-io/turndown/blob/v7.2.2/LICENSE). The original JavaScript and license are included under Android assets and the desktop browser assets.
- Scout.Text.Regex 0.8.0: [MIT](https://github.com/willibrandon/scout/blob/main/LICENSE). Managed port of the ripgrep search engine.
- ReverseMarkdown 6.3.0 and AngleSharp 1.5.2: [ReverseMarkdown license](https://github.com/mysticmind/reversemarkdown-net/blob/6.3.0/LICENSE), [AngleSharp license](https://github.com/AngleSharp/AngleSharp/blob/devel/LICENSE).
- QEMU: [GPL terms](https://www.qemu.org/docs/master/about/license.html). Android uses [QEMU 11.1.3 source](https://download.qemu.org/qemu-11.1.3.tar.xz), [Termux's Android patches](https://github.com/termux/termux-packages/tree/a477f5ef93d52a6e2f6665aaedfdb16c66251c56/packages/qemu-system-x86-64-headless), and the Bionic patch and device configuration under `eng/container`. Windows uses the [August 26, 2025 Windows distribution](https://qemu.weilnetz.de/w64/2025/) based on [QEMU revision 5fa1466eb8](https://gitlab.com/qemu-project/qemu/-/tree/5fa1466eb8). The guest uses [Alpine Linux packages](https://pkgs.alpinelinux.org/packages?branch=v3.24&arch=x86_64) and their respective licenses.
- The native curl runtime bundles [curl](https://curl.se/docs/copyright.html), [Mbed TLS](https://github.com/Mbed-TLS/mbedtls/blob/v3.6.7/LICENSE), [libssh2](https://libssh2.org/license.html), [nghttp2](https://github.com/nghttp2/nghttp2/blob/v1.71.0/COPYING), and [zlib](https://zlib.net/zlib_license.html). The Android build also uses [OpenLDAP](https://www.openldap.org/software/release/license.html). The build scripts and file-access adaptations are in `eng/curl`.
- The Alpine image rebuilds [musl 1.2.6-r3](https://github.com/alpinelinux/aports/tree/8f79d39391b46d6dbf1800fb68b8212a21d5ee2e/main/musl) with an 8 MiB default pthread stack. `eng/container/build-musl.sh` records the source verification and build steps.

Agora was consulted for provider configuration behavior, without copying its GPL implementation. opencode was consulted for provider boundaries and terminal interaction patterns. The launcher foreground was generated with GPT-image; its source asset and prompt are retained under `assets/branding`.
