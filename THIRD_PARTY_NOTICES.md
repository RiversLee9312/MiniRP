# Third-party components

Dependencies are retained as maintained packages; this list also records directly bundled assets.

- .NET runtime and libraries, Microsoft: [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).
- Terminal.Gui and Terminal.Gui.Editor: [MIT](https://github.com/tui-cs/Terminal.Gui/blob/v2.5.0/LICENSE), [Editor and its upstream notices](https://github.com/tui-cs/Editor/tree/main/third_party).
- MCP C# SDK: [Apache-2.0](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/LICENSE).
- Playwright MCP and Playwright: [Apache-2.0](https://github.com/microsoft/playwright-mcp/blob/main/LICENSE).
- SQLite native library: [public domain](https://www.sqlite.org/copyright.html); packaged via SQLitePCLRaw.lib.e_sqlite3.
- AndroidX, Jetpack Compose and Material 3: [Apache-2.0](https://source.android.com/docs/setup/about/licenses).
- ClearScript: [MIT](https://github.com/ClearFoundry/ClearScript/blob/master/License.txt).
- Javet: [Apache-2.0](https://github.com/caoccao/Javet/blob/main/LICENSE).
- V8: [BSD-3-Clause](https://github.com/v8/v8/blob/main/LICENSE).
- Web Streams polyfill 4.3.0: [MIT](https://github.com/MattiasBuelens/web-streams-polyfill/blob/master/LICENSE). The published JavaScript and license are bundled in the shared runtime.
- Bouncy Castle C# 2.7.0 and Java 1.87: [license](https://www.bouncycastle.org/licence/). The C# SDK signs detached CMS packages, with verification on desktop and the Android platform host.
- OkHttp: [Apache-2.0](https://github.com/square/okhttp/blob/master/LICENSE.txt).
- Multiplatform Markdown Renderer: [Apache-2.0](https://github.com/mikepenz/multiplatform-markdown-renderer/blob/develop/LICENSE).
- OpenUI and Intelligent UI components: [MIT](https://github.com/thesysdev/openui/blob/main/LICENSE). Bundled for Android interactive replies.
- React: [MIT](https://github.com/facebook/react/blob/main/LICENSE). MapLibre GL JS: [BSD-3-Clause](https://github.com/maplibre/maplibre-gl-js/blob/main/LICENSE.txt). The interactive map shows map provider attribution.
- Material Symbols Rounded: Google Fonts official subset. Source and SHA-256 are in `assets/branding/material-symbols-source.json`; the Apache-2.0 license is bundled with the app.
- Turndown 7.2.2: [MIT](https://github.com/mixmark-io/turndown/blob/v7.2.2/LICENSE). The original JavaScript and license are included under Android assets and the desktop browser assets.

Agora was consulted for provider configuration behavior, without copying its GPL implementation. opencode was consulted for provider boundaries and terminal interaction patterns. The launcher foreground was generated with GPT-image; its source asset and prompt are retained under `assets/branding`.
