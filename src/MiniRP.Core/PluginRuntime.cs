using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Runtime.CompilerServices;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime : IAsyncDisposable
{
    private static string Resource(string name)
    {
        using var stream = typeof(PluginRuntime).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static readonly Lazy<string> Bootstrap = new(() => Resource("MiniRP.WebStreams.js") + "\n" + Resource("MiniRP.PluginBootstrap.js"));
    private static readonly Lazy<string> WebCrypto = new(() => Resource("MiniRP.PluginWebCrypto.js"));
    private static readonly Lazy<string> WebCryptoProvider = new(() => Resource("MiniRP.PluginWebCryptoProvider.js"));
    private static readonly Lazy<PluginPackage> Base = new(() =>
    {
        var manifest = JsonValues.ParseObject(Resource("MiniRP.BasePlugin.manifest.json"));
        ValidateStateDeclarations(manifest, signed: true);
        ValidateToolPresentations(manifest, signed: true);
        var resources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["notices.md"] = "MiniRP.BasePlugin.notices.md",
            ["LICENSE"] = "MiniRP.BasePlugin.license"
        };
        return new(manifest.GetString("id"), manifest.GetString("name"), Resource("MiniRP.BasePlugin.index.js"), manifest, BuiltIn: true,
            Assets: PluginPackage.ReadAssets(manifest, resources.ContainsKey), Resources: resources);
    });

    private readonly string root;
    private readonly string files;
    private readonly HostPort port;
    private readonly Action<JsonObject> emit;
    private readonly Func<PluginPackage, JsonObject, string, JsonObject, CancellationToken, Task<JsonNode?>> harness;
    private readonly Action<JsonObject>? stateChanged;
    private readonly bool preview;
    private readonly string mediaFiles;
    private readonly CancellationTokenSource stop = new();
    private readonly object packageGate = new();
    private readonly Dictionary<string, EventLoop> loops = new(StringComparer.Ordinal);
    private readonly List<Task> retirements = [];
    private PluginPackage[] packages = [];
    private sealed record Invocation(PluginPackage Package, string Handler, JsonObject Event, JsonObject Environment,
        bool Installation = false, Task<bool>? Committed = null, bool SkillTool = false,
        Func<string, JsonObject, AssetContent?>? OpenSuppliedAsset = null,
        Func<AssetContent, string, CancellationToken, Task<JsonObject>>? PresentAsset = null,
        Func<string, string, JsonObject, CancellationToken, Task<JsonObject>>? EditSuppliedAsset = null,
        AgentAssets.ResearchScope? Research = null, AgentToolOutput? Output = null, PluginPackage? AssetOwner = null);

    private sealed class Isolate
    {
        private readonly CancellationTokenSource retired = new();
        public string Id { get; }
        public Task Ready { get; }
        public CancellationToken Token { get; }

        public Isolate(string id, Task ready)
        {
            Id = id;
            Ready = ready;
            Token = retired.Token;
        }

        public void Retire()
        {
            retired.Cancel();
            retired.Dispose();
        }
    }

    private sealed class EventLoop(PluginRuntime owner)
    {
        private readonly CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(owner.stop.Token);
        public readonly Channel<Invocation> Queue = Channel.CreateBounded<Invocation>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        public CancellationToken Token { get; private set; }
        private Task worker = Task.CompletedTask;
        private readonly object loadGate = new();
        private Isolate? isolate;
        private bool retiring;

        public void Start()
        {
            Token = source.Token;
            worker = Task.Run(() => owner.ReadEvents(Queue.Reader, Token));
        }

        public Isolate EnsureIsolate(PluginPackage package)
        {
            lock (loadGate)
            {
                if (retiring) throw new OperationCanceledException(Token);
                if (isolate is { } current)
                {
                    if (!current.Ready.IsFaulted && !current.Ready.IsCanceled) return current;
                    Reset(current);
                }
                var id = JsonValues.NewId();
                return isolate = new(id, LoadIsolate(id, package));
            }
        }

        public bool IsCurrent(Isolate previous)
        {
            lock (loadGate) return isolate == previous;
        }

        public void Reset(Isolate previous)
        {
            lock (loadGate)
            {
                if (isolate != previous) return;
                isolate = null;
                owner.emit(new() { ["kind"] = "plugin_isolate_close", ["isolate"] = previous.Id });
            }
            previous.Retire();
        }

        private async Task LoadIsolate(string id, PluginPackage package)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var request = new JsonObject
            {
                ["action"] = "load", ["isolate"] = id,
                ["bootstrap"] = Bootstrap.Value,
                ["web_crypto"] = WebCrypto.Value,
                ["web_crypto_provider"] = WebCryptoProvider.Value
            };
            if (package.Modules is { } sources)
            {
                request["entry"] = package.Manifest.GetString("entry");
                var modules = new JsonObject();
                foreach (var (path, source) in sources) modules[path] = source;
                request["modules"] = modules;
            }
            else request["source"] = package.Source;
            await owner.port.CallHost("plugin_script", request, deadline.Token);
        }

        public async Task Stop()
        {
            Task cancelled;
            lock (loadGate)
            {
                retiring = true;
                Queue.Writer.TryComplete();
                cancelled = source.CancelAsync();
                if (isolate is { } current) Reset(current);
            }
            await cancelled;
            await worker;
            source.Dispose();
        }
    }

    public PluginRuntime(string workspace, HostPort port, Action<JsonObject> emit,
        Func<PluginPackage, JsonObject, string, JsonObject, CancellationToken, Task<JsonNode?>> harness, bool preview = false,
        Action<JsonObject>? stateChanged = null, TimeProvider? clock = null, string? mediaFiles = null)
    {
        root = Path.Combine(workspace, "plugins");
        files = Path.Combine(workspace, "attachments");
        this.mediaFiles = mediaFiles ?? files;
        this.port = port;
        this.emit = emit;
        this.harness = harness;
        this.stateChanged = stateChanged;
        this.preview = preview;
        hostAssets = new(clock ?? TimeProvider.System);
    }

    public void Load()
    {
        Directory.CreateDirectory(root);
        var installed = new List<PluginPackage> { Base.Value };
        foreach (var path in Directory.EnumerateFiles(root, "*.plugin").Order(StringComparer.Ordinal).Take(32))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var package = ReadPackage(stream, path, allowLegacy: true);
                if (Path.GetFileNameWithoutExtension(path) == package.Id && installed.All(item => item.PackageName != package.PackageName))
                    installed.Add(package with { Enabled = ReadEnabled(package) });
            }
            catch (Exception error) { Fault(Path.GetFileName(path), error.Message); }
        }
        SetPackages(installed.ToArray());
    }

    private PluginPackage ReadPackage(Stream source, string path = "", bool allowLegacy = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
#if MINIRP_ANDROID_BIONIC
        return PluginPackage.Read(source, path, allowLegacy, (payload, signature) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, ct);
            var reply = port.CallHost("plugin_verify", new()
            {
                ["payload"] = Convert.ToBase64String(payload),
                ["signature"] = Convert.ToBase64String(signature)
            }, deadline.Token, TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            var signer = reply.GetString("signer");
            if (signer.Length != 64 || signer.Any(value => !char.IsAsciiHexDigit(value)))
                throw new InvalidDataException("Invalid plugin signer fingerprint.");
            return signer.ToUpperInvariant();
        });
#else
        return PluginPackage.Read(source, path, allowLegacy);
#endif
    }

    private void SetPackages(PluginPackage[] installed)
    {
        lock (packageGate)
        {
            var former = packages;
            Volatile.Write(ref packages, installed);
            retirements.RemoveAll(task => task.IsCompletedSuccessfully);
            var ids = installed.Select(item => item.Generation).ToHashSet(StringComparer.Ordinal);
            foreach (var package in former.Where(package => !ids.Contains(package.Generation)))
            {
                if (loops.Remove(package.Generation, out var loop)) retirements.Add(loop.Stop());
                retirements.Add(hostAssets.Revoke(package.Generation));
            }
        }
    }

    private EventLoop? Loop(PluginPackage package)
    {
        lock (packageGate)
        {
            if (stop.IsCancellationRequested || !package.Enabled || !IsCurrent(package)) return null;
            if (loops.TryGetValue(package.Generation, out var existing)) return existing;
            var created = new EventLoop(this);
            loops.Add(package.Generation, created);
            created.Start();
            return created;
        }
    }

    public PluginPackage Install(string source, bool inspect, PluginScope? initialScope = null, JsonObject? environment = null)
    {
        if (!source.EndsWith(".plugin", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a .plugin archive.");
        using var input = File.OpenRead(source);
        var package = ReadPackage(input);
        lock (packageGate)
        {
            var conflicts = Volatile.Read(ref packages).Where(item => item.Id == package.Id || item.PackageName == package.PackageName);
            if (conflicts.Any(existing => existing.Id != package.Id || existing.BuiltIn ||
                existing.Signed && (existing.Signer != package.Signer || existing.PackageName != package.PackageName)))
                throw new InvalidOperationException("An installed package can only be updated by its verified signer.");
            if (inspect) return package;
            var target = Path.Combine(root, package.Id + ".plugin");
            var isNew = !File.Exists(target);
            if (isNew && Directory.EnumerateFiles(root, "*.plugin").Take(32).Count() >= 32)
                throw new InvalidOperationException("At most 32 plugins can be installed.");
            package = package with { Enabled = ReadEnabled(package) };
            var temp = target + ".tmp";
            input.Position = 0;
            try
            {
                using (var output = File.Create(temp)) input.CopyTo(output);
                if (isNew && (initialScope is not null || package.PresetId.Length > 0))
                    SaveScope(package, initialScope ?? PluginScope.Include(package.PresetId));
                if (isNew) File.Move(temp, target);
                else File.Replace(temp, target, destinationBackupFileName: null);
            }
            finally { File.Delete(temp); }
            package = package with { ArchivePath = target };
            SetPackages([.. Volatile.Read(ref packages).Where(item => item.Id != package.Id), package]);
            PublishEnabled(package, environment ?? new());
            return package;
        }
    }

    public void Uninstall(string id)
    {
        lock (packageGate)
        {
            if (Volatile.Read(ref packages).Any(package => package.Id == id && package.BuiltIn))
                throw new InvalidOperationException("Built-in plugins cannot be uninstalled.");
            File.Delete(Path.Combine(root, PluginPackage.Identifier(id) + ".plugin"));
            SetPackages(Volatile.Read(ref packages).Where(item => item.Id != id).ToArray());
        }
    }

    public JsonArray List(string? locale = null) => new(Volatile.Read(ref packages).Select(package => (JsonNode?)Descriptor(package, locale)).ToArray());

    internal Dictionary<string, long> RoomLoadingLimits(string preset, IEnumerable<string> participants)
    {
        var roles = participants.ToHashSet(StringComparer.Ordinal);
        var limits = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var package in Volatile.Read(ref packages))
        {
            if (!Available(package, preset)) continue;
            foreach (var (role, maximum) in package.RoomLoadingProgress)
                if (roles.Contains(role)) limits[role] = limits.GetValueOrDefault(role) + maximum;
        }
        return limits;
    }

    public JsonObject Descriptor(PluginPackage package, string? locale = null) => new()
    {
        ["id"] = package.Id, ["name"] = package.LocalizedName(locale),
        ["description"] = package.Localization?.Text(package.Manifest, "description", locale) ?? package.Manifest.GetString("description"),
        ["packageName"] = package.PackageName, ["signer"] = package.Signer, ["presetId"] = package.PresetId,
        ["builtIn"] = package.BuiltIn, ["enabled"] = package.Enabled, ["generation"] = package.Generation,
        ["scope"] = Scope(package).Describe(),
        ["permissions"] = package.Manifest.GetArray("permissions").DeepClone(),
        ["network"] = package.Manifest.GetArray("network").DeepClone(),
        ["attachments"] = package.LocalizedManifest(locale).GetArray("attachments").DeepClone(),
        ["command"] = package.Manifest.GetObject("hooks").ContainsKey("plugin.command")
    };

    public JsonArray Settings(string preset, string? locale = null)
    {
        var items = new JsonArray();
        foreach (var package in Volatile.Read(ref packages))
        {
            if (!Available(package, preset)) continue;
            foreach (var setting in package.LocalizedManifest(locale).GetArray("settings").OfType<JsonObject>())
                items.AddNode(RenderSetting(package, setting, locale));
        }
        return items;
    }

    public JsonObject Page(string id, string key, string preset, string? locale = null)
    {
        if (id == "licenses" && key == "notices") id = "base";
        var package = Installed(id);
        RequireScope(package, preset);
        var setting = Setting(package.LocalizedManifest(locale), key);
        if (setting.GetString("type") != "page") throw new ArgumentException("This setting is not a page.");
        return RenderSetting(package, setting, locale, expandPage: true);
    }

    private JsonObject RenderSetting(PluginPackage package, JsonObject setting, string? locale, bool expandPage = false)
    {
        var item = new JsonObject();
        foreach (var (key, value) in setting)
        {
            if (key is not ("items" or "localizedTitle" or "localizedMarkdown") && (key != "icon" || package.Signed || package.BuiltIn))
                item[key] = JsonValues.Clone(value);
        }
        if (package.Localization?.Text(setting, "title", locale) is { } title) item["localizedTitle"] = title;
        if (package.Localization?.Text(setting, "markdown", locale) is { } localizedMarkdown) item["localizedMarkdown"] = localizedMarkdown;
        if (setting["options"] is JsonArray options && item["options"] is JsonArray renderedOptions)
        {
            for (var index = 0; index < options.Count; index++)
            {
                if (options[index] is not JsonObject option || renderedOptions[index] is not JsonObject rendered) continue;
                rendered.Remove("localizedTitle");
                if (package.Localization?.Text(option, "title", locale) is { } optionTitle) rendered["localizedTitle"] = optionTitle;
            }
        }
        item["plugin"] = package.Id;
        if (item.GetString("type") == "page" && !expandPage) return item;
        if (item.GetString("type") is "boolean" or "text" or "choice" && item.GetString("binding").Length == 0)
            item["value"] = SettingValue(package, item.GetString("key"));
        if (package.Pages?.GetValueOrDefault(item.GetString("key")) is { } markdown) item["markdown"] = markdown;
        if (package.UsesActions && item.GetString("content") is { Length: > 0 } asset)
            item["assetUrl"] = AssetUrl(package, asset);
        if (setting["items"] is JsonArray children)
            item["items"] = new JsonArray(children.OfType<JsonObject>().Select(child => RenderSetting(package, child, locale)).ToArray());
        return item;
    }

    public void SetSetting(string id, string key, JsonNode? value, JsonObject environment)
    {
        var package = Installed(id);
        RequireScope(package, environment.GetString("preset"));
        var setting = Setting(package, key);
        if (setting.GetString("binding").Length > 0 || value is not JsonValue typed) throw new ArgumentException("This setting does not store a value.");
        var valid = setting.GetString("type") switch
        {
            "boolean" => typed.TryGetValue<bool>(out _),
            "choice" => typed.TryGetValue<string>(out var choice) && setting.GetArray("options").Any(option => option.GetString("value") == choice),
            "text" => typed.TryGetValue<string>(out var text) && text.Length <= 16000,
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid setting value.");
        using var db = SettingsDb(package);
        db.Write("setting/" + key, JsonValues.Serialize(value), internalWrite: true);
        Publish("settings.changed", new() { ["key"] = key, ["value"] = JsonValues.Clone(value) }, environment, package.Id);
    }

    private JsonNode? SettingValue(PluginPackage package, string key)
    {
        var setting = Setting(package, key);
        if (setting.GetString("type") is not ("boolean" or "choice" or "text") || setting.GetString("binding").Length > 0)
            throw new ArgumentException("This setting does not store a value.");
        using var db = SettingsDb(package);
        var value = db.Get("setting/" + key);
        return value is null ? JsonValues.Clone(setting["default"]) : JsonNode.Parse(value);
    }

    private string SettingsPath(PluginPackage package) => Path.Combine(root, "data", package.StorageKey, "settings.sqlite");
    private MemoryDb SettingsDb(PluginPackage package) => new(SettingsPath(package));
    private static JsonObject Setting(PluginPackage package, string key) => Setting(package.Manifest, key);
    private static JsonObject Setting(JsonObject manifest, string key) =>
        PluginPackage.Settings(manifest.GetArray("settings")).FirstOrDefault(item => item.GetString("key") == key) ??
        throw new ArgumentException("Unknown plugin setting.");

    public void Publish(string name, JsonObject @event, JsonObject environment, string? only = null, string? exclude = null)
    {
        foreach (var package in Volatile.Read(ref packages))
        {
            if ((only is not null && package.Id != only) || package.Id == exclude || !Available(package, environment.GetString("preset"))) continue;
            var handler = package.Bind(package.Manifest.GetObject("hooks").GetString(name));
            if (handler.Length == 0) continue;
            // Context identifiers are available to every handler; transcript payloads require permission.
            var payload = package.Has("harness.read") || name is "plugin.command" or "settings.changed" ? (JsonObject)@event.DeepClone() : new JsonObject();
            payload["name"] = name;
            Enqueue(package, handler, payload, environment);
        }
    }

    private void PublishEnabled(PluginPackage package, JsonObject environment)
    {
        var handler = package.Bind(package.Manifest.GetObject("hooks").GetString("plugin.enabled"));
        if (handler.Length == 0 || Loop(package) is not { } loop) return;
        var scope = Scope(package);
        var preset = environment.GetString("preset");
        var selected = scope.Allows(preset) ? preset : scope.Presets.Order(StringComparer.Ordinal).FirstOrDefault(scope.Allows) ?? "";
        var context = selected == preset && scope.Allows(preset)
            ? (JsonObject)environment.DeepClone()
            : new JsonObject { ["preset"] = selected.Length == 0 ? null : JsonValue.Create(selected) };
        if (!loop.Queue.Writer.TryWrite(new(package, handler, new() { ["name"] = "plugin.enabled" }, context, Installation: true)))
            Fault(package.Id, "The plugin was installed but its event queue could not accept plugin.enabled.");
    }

    public IEnumerable<AgentSkill> Skills(string agent, JsonObject environment, bool builtInOnly = false, AgentAssets? assets = null,
        Action<JsonObject>? showCard = null, Provider? provider = null, Action<JsonObject>? attach = null,
        Func<SessionModelInfo?>? sessionInfo = null, string? locale = null, bool manageAssets = false,
        MediaDescriptions? descriptions = null, AgentAssetWatches? watches = null)
    {
        locale ??= environment.GetString("locale");
        JsonObject SessionContext() => WithSessionInfo(environment, sessionInfo?.Invoke());
        foreach (var package in Volatile.Read(ref packages))
        {
            if (builtInOnly && !package.BuiltIn || !Available(package, environment.GetString("preset"))) continue;
            foreach (var declaration in package.LocalizedManifest(locale).GetArray("skills"))
            {
                if (!declaration.GetArray("agents").Any(item => JsonValues.GetText(item) == agent)) continue;
                var skillName = declaration.GetString("name");
                var assetSkill = package.BuiltIn && package.Id == "base" && skillName == AgentAssetStorage.SkillId;
                var researchSkill = package.BuiltIn && package.Id == "base" && skillName == "search-full";
                if (assetSkill && assets is null) continue;
                var research = researchSkill ? assets?.Research : null;
                if (researchSkill && research is null) continue;
                var basicSearch = researchSkill && agent is "env" or "preset" or "plugin";
                var key = basicSearch ? "search" : package.BuiltIn ? skillName : package.Id + "-" + skillName;
                if (!package.BuiltIn && agent == "preset" && key == "preset-docs")
                    key = $"plugin/{package.Id}/{skillName}";
                var draft = package.BuiltIn && skillName == BuiltInSkillIds.PluginDev ? assets?.Project : null;
                var handlers = new Dictionary<string, string>(StringComparer.Ordinal);
                var presentations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                var assetTools = new HashSet<string>(StringComparer.Ordinal);
                var tools = draft is not null ? PluginProjectAssets.Definitions()
                    : assetSkill && manageAssets ? AgentAssetStorage.Definitions() : new JsonArray();
                if (assetSkill && watches is not null)
                    foreach (var tool in AgentAssetWatches.Definitions()) tools.AddNode(tool);
                var localTools = tools.Select(definition => definition.GetObject("function").GetString("name")).ToHashSet(StringComparer.Ordinal);
                var editHandler = assetSkill ? package.Bind("minirp://actions/minirp.base/hooks/edit-asset") : "";
                foreach (var tool in declaration.GetArray("tools"))
                {
                    if (basicSearch && tool.GetString("name") is not ("search" or "open_url")) continue;
                    var returnsAsset = tool.GetString("returnType") == "asset-url";
                    if (returnsAsset && assets is null) continue;
                    var handler = package.Bind(tool.GetString("handler"));
                    if (assetSkill && handler != editHandler && (provider is null || attach is null)) continue;
                    // Provider tool names have a 64-character limit.
                    var qualified = QualifiedTool(package, tool.GetString("name"));
                    if (qualified.Length > 64) throw new InvalidDataException("Plugin tool name is too long.");
                    handlers.Add(qualified, handler);
                    if (tool is JsonObject toolDeclaration && toolDeclaration["presentation"] is JsonObject)
                        presentations.Add(qualified, toolDeclaration);
                    if (returnsAsset) assetTools.Add(qualified);
                    tools.AddNode(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
                    {
                        ["name"] = qualified,
                        ["description"] = tool.GetString("description") + (returnsAsset ? " Returns an Asset URL or list. Activate assets to inspect it." : ""),
                        ["parameters"] = tool.GetObject("parameters").DeepClone()
                    } });
                }
                var stateTool = QualifiedTool(package, skillName + "-state");
                var cardTool = QualifiedTool(package, skillName + "-card");
                AddStateTools(package, declaration, tools, stateTool, showCard is null ? null : cardTool, locale);
                async Task<JsonObject> PresentAsset(AssetContent asset, string url, CancellationToken token)
                {
                    if (descriptions is not null && await descriptions.DescribeAsset(asset, url, token) is { } description)
                        return description;
                    var reading = await assets!.Present(asset, provider!, url, port, token);
                    if (reading.Attachment is not null) attach!(reading.Attachment);
                    return reading.Result;
                }
                var instructions = basicSearch
                    ? "Use search to find sources and open_url to read pages. Activate assets to read full Markdown through its Asset URL. Cached material may be stale."
                    : declaration.GetString("instructions");
                if (assetSkill && manageAssets) instructions += "\n" + AgentAssetStorage.OwnedAssetInstructions;
                if (draft is not null) instructions += "\n" + PluginProjectAssets.Instructions;
                var origin = new JsonObject { ["kind"] = "plugin", ["package"] = package.PackageName,
                    ["signer"] = package.Signer, ["skill"] = key == "search" && researchSkill ? key : skillName };
                yield return new AgentSkill(key, basicSearch ? "Find and read online sources." : declaration.GetString("description"), tools,
                    async (name, args, ct) =>
                    {
                        if (localTools.Contains(name))
                        {
                            if (assetSkill && AgentAssetWatches.IsTool(name)) return watches!.Set(name, args);
                            return draft is null ? await assets!.Manage(name, args, ct) : await draft.Execute(name, args, ct);
                        }
                        if (name == stateTool && declaration.GetArray("stateEntries").Count > 0)
                        {
                            if (!declaration.GetArray("stateEntries").Any(entry => JsonValues.GetText(entry) == args.GetString("key")))
                                throw new UnauthorizedAccessException("This skill cannot access that state entry.");
                            var state = State(package, environment, args, false);
                            state["db_key"] = ProjectionKey(package, state, environment);
                            return state;
                        }
                        if (name == cardTool && declaration.GetArray("cards").Count > 0)
                        {
                            var id = args.GetString("id");
                            if (showCard is null || !declaration.GetArray("cards").Any(card => JsonValues.GetText(card) == id))
                                throw new UnauthorizedAccessException("This skill cannot show that card in the current session.");
                            var card = WhileCurrent(package, environment.GetString("preset"), () => Card(package, id, environment, locale));
                            card["plugin_card"] = new JsonObject { ["package"] = package.PackageName, ["signer"] = package.Signer,
                                ["id"] = id, ["environment"] = environment.DeepClone() };
                            showCard(card);
                            return new JsonObject { ["shown"] = true, ["id"] = id };
                        }
                        var handler = handlers[name];
                        var assetEdit = assetSkill && handler == editHandler;
                        var assetView = assetSkill && !assetEdit;
                        var supplied = assets?.SuppliedUrls(args);
                        var invocation = new Invocation(package, handler, new()
                        { ["name"] = "skill.tool", ["skill"] = key, ["tool"] = name, ["arguments"] = args.DeepClone() }, SessionContext(),
                            SkillTool: true,
                            OpenSuppliedAsset: (url, options) => supplied?.Contains(url) == true ? assets!.Open(url, options) :
                                assetView ? assets?.Development?.OpenPublicAsset(url, options) : null,
                            PresentAsset: assetView ? PresentAsset : null,
                            EditSuppliedAsset: assetEdit ? assets!.Edit : null,
                            Research: research, Output: package.UsesActions ? assets?.Output : null);
                        var result = await InvokeSkillTool(invocation, provider, attach, assets, ct);
                        if (assetTools.Contains(name)) (assets ?? throw new InvalidOperationException("This agent has no asset reader.")).Grant(package, result, supplied);
                        return result;
                    },
                    Required: researchSkill, Instructions: instructions,
                    OnActivate: () => Enqueue(package, package.Bind(declaration.GetObject("hooks").GetString("skill.activated")), new() { ["name"] = "skill.activated", ["skill"] = key }, SessionContext()),
                    OnEvent: (name, payload) => Enqueue(package, package.Bind(declaration.GetObject("hooks").GetString(name)),
                        name is "tool.started" or "tool.finished" || package.Has("harness.read") ? payload : new() { ["name"] = name }, SessionContext()),
                    DisplayName: researchSkill ? "" : declaration.GetString("title", package.BuiltIn ? "" : key))
                {
                    PluginToolSource = name => localTools.Contains(name) ? null : origin,
                    PresentTool = presentations.Count == 0 ? null : (name, phase, text, plain) =>
                        presentations.TryGetValue(name, out var tool) ? PresentTool(package, environment.GetString("preset"),
                            origin.GetString("skill"), tool, name, phase, text, plain, locale) : null
                };
            }
        }
    }

    private const string SessionInfoKey = "_session_info";

    internal static JsonObject WithSessionInfo(JsonObject environment, SessionModelInfo? info)
    {
        if (info is null) return environment;
        var context = (JsonObject)environment.DeepClone();
        context[SessionInfoKey] = info.Describe();
        return context;
    }

    public async Task<JsonArray> ProcessAttachments(JsonArray attachments, JsonObject environment, CancellationToken ct)
    {
        var native = new JsonArray();
        foreach (var attachment in attachments.OfType<JsonObject>())
        {
            var type = attachment.GetString("type");
            if (type.Length == 0)
            {
                native.AddNode(attachment.DeepClone());
                continue;
            }
            var plugin = Volatile.Read(ref packages).FirstOrDefault(package => package.Id == attachment.GetString("plugin")) ??
                throw new InvalidOperationException("Attachment plugin is unavailable.");
            var declaration = plugin.Manifest.GetArray("attachments").FirstOrDefault(item => item.GetString("id") == type);
            if (declaration is null || !declaration.GetArray("mimeTypes").Any(item => Matches(JsonValues.GetText(item), attachment.GetString("mime"))))
                throw new InvalidDataException("Attachment does not match the plugin type.");
            var payload = new JsonObject { ["name"] = "attachment.process", ["attachment"] = attachment.DeepClone() };
            if (await Invoke(new(plugin, plugin.Bind(declaration.GetString("handler")), payload, environment), ct) is not JsonObject result ||
                result.GetString("text") is not { Length: > 0 } text || text.Length > 64000)
                throw new InvalidDataException("Plugin attachment must return up to 64,000 characters of text.");
            var processed = (JsonObject)attachment.DeepClone();
            processed["text"] = text;
            if (plugin.UsesActions && declaration.GetString("thumbnail") is { Length: > 0 } thumbnail)
            {
                processed["thumbnail_url"] = AssetUrl(plugin, thumbnail) + "?attachment=" + Workspace.Safe(attachment.GetString("id"));
            }
            native.AddNode(processed);
        }
        return native;
    }

    private static bool Matches(string pattern, string mime) => pattern == mime || pattern == "*/*" ||
        (pattern.EndsWith("/*", StringComparison.Ordinal) && mime.StartsWith(pattern[..^1], StringComparison.Ordinal));

    private void Enqueue(PluginPackage package, string handler, JsonObject @event, JsonObject environment)
    {
        if (handler.Length > 0)
            EnqueueRoute(package, handler, (JsonObject)@event.DeepClone(), (JsonObject)environment.DeepClone());
    }

    private bool EnqueueRoute(PluginPackage package, string handler, JsonObject @event, JsonObject environment,
        Task<bool>? committed = null, PluginPackage? assetOwner = null) =>
        Available(package, environment.GetString("preset")) &&
        Loop(package)?.Queue.Writer.TryWrite(new(package, handler, @event, environment, Committed: committed, AssetOwner: assetOwner)) == true;

    private async Task ReadEvents(ChannelReader<Invocation> reader, CancellationToken token)
    {
        try
        {
            await foreach (var invocation in reader.ReadAllAsync(token))
            {
                if (invocation.Committed is { } committed && !await committed.WaitAsync(token)) continue;
                if (!invocation.Installation && !Scope(invocation.Package).Allows(invocation.Environment.GetString("preset"))) continue;
                if (invocation.AssetOwner is { } owner &&
                    (!IsCurrent(owner) || !Available(owner, invocation.Environment.GetString("preset")))) continue;
                try { await Invoke(invocation, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { Fault(invocation.Package.Id, "Plugin handler was cancelled or timed out."); }
                catch (Exception error) { Fault(invocation.Package.Id, error.Message); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<JsonNode?> Invoke(Invocation invocation, CancellationToken ct)
    {
        await foreach (var result in RunScript(invocation, binary: false, ct)) return result.Value;
        return null;
    }

    public Task<JsonNode?> InvokeBaseHook(string hook, JsonObject payload, JsonObject environment, CancellationToken ct)
    {
        var package = Volatile.Read(ref packages).Single(plugin => plugin.BuiltIn && plugin.Id == "base");
        payload["name"] = hook;
        return Invoke(new(package, package.Bind(package.Manifest.GetObject("hooks").GetString(hook)), payload, environment), ct);
    }

    private sealed record ScriptOutput(JsonNode? Value = null, byte[]? Bytes = null);

    private async IAsyncEnumerable<ScriptOutput> RunScript(Invocation invocation, bool binary, [EnumeratorCancellation] CancellationToken ct)
    {
        RequireInvocation(invocation);
        var loop = Loop(invocation.Package) ?? throw new InvalidOperationException("The plugin is no longer loaded.");
        var isolate = loop.EnsureIsolate(invocation.Package);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, loop.Token, isolate.Token);
        var token = cancellation.Token;
        try { await isolate.Ready.WaitAsync(token); }
        catch
        {
            if (isolate.Ready.IsCompleted && !isolate.Ready.IsCompletedSuccessfully) loop.Reset(isolate);
            throw;
        }
        var run = JsonValues.NewId();
        using var codecs = new AssetCodecs(mediaFiles, port);
        await using var readers = new AssetReaders(TimeProvider.System);
        var staged = new List<string>();
        var finished = false;
        var submitted = false;
        var grantedFiles = new HashSet<string>(StringComparer.Ordinal);
        if (invocation.Event["attachment"] is JsonObject attachment) grantedFiles.Add(attachment.GetString("id"));
        var request = new JsonObject
        {
            ["run"] = run, ["action"] = "start",
            ["payload"] = JsonValues.Serialize(new JsonObject
            {
                ["handler"] = invocation.Handler, ["event"] = invocation.Event.DeepClone(),
                ["environment"] = new JsonObject(invocation.Environment.Where(pair => !pair.Key.StartsWith('_'))
                    .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValues.Clone(pair.Value)))),
                ["packageName"] = invocation.Package.PackageName, ["legacy"] = !invocation.Package.UsesActions, ["binary"] = binary,
                ["development"] = invocation.Package.BuiltIn && invocation.Package.Id == "base"
            })
        };
        try
        {
            token.ThrowIfCancellationRequested();
            long bytes = 0;
            while (true)
            {
                RequireInvocation(invocation);
                token.ThrowIfCancellationRequested();
                if (!loop.IsCurrent(isolate)) throw new IOException("Plugin isolate was retired.");
                var upload = port.ReserveBinary();
                request["upload"] = upload;
                request["isolate"] = isolate.Id;
                JsonObject result;
                byte[]? chunk;
                try
                {
                    submitted = true;
                    result = await port.CallHost("plugin_script", request, token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    loop.Reset(isolate);
                    throw;
                }
                finally
                {
                    chunk = port.TakeBinary(upload);
                    foreach (var transfer in staged) port.TakeBinary(transfer);
                    staged.Clear();
                }
                token.ThrowIfCancellationRequested();
                RequireInvocation(invocation);
                if (!loop.IsCurrent(isolate)) throw new IOException("Plugin isolate was retired.");
                if (result.GetBoolean("binary"))
                {
                    if (!binary || chunk is null || (bytes += chunk.Length) > PluginPackage.MaxPackageBytes)
                        throw new IOException("Invalid dynamic asset chunk or asset size.");
                    yield return new(Bytes: chunk);
                }
                if (result.GetBoolean("done"))
                {
                    if (result.GetString("error") is { Length: > 0 } error)
                    {
                        if (invocation.SkillTool)
                        {
                            var diagnostic = result.GetObject("diagnostic");
                            var details = diagnostic.GetObject("details");
                            details["package"] = invocation.Package.PackageName;
                            details["handler"] = invocation.Handler;
                            throw new ToolFailure(error, diagnostic.GetString("code", "plugin_handler_error"),
                                diagnostic.GetString("phase", "handler"),
                                diagnostic.GetString("next_step", "Check the handler error and its fixture or required permissions."), details);
                        }
                        throw new InvalidOperationException(error);
                    }
                    finished = true;
                    var output = result["result"];
                    if (output is not null && JsonValues.Serialize(output).Length > 1024 * 1024)
                        throw new InvalidOperationException("Plugin result exceeded 1 MiB.");
                    if (!binary) yield return new(JsonValues.Clone(output));
                    yield break;
                }
                var pending = result.GetArray("requests");
                if (pending.Count == 0 && binary && result.GetBoolean("ready"))
                {
                    request = new() { ["run"] = run, ["action"] = "pull" };
                    continue;
                }
                if (pending.Count == 0) throw new InvalidOperationException("Plugin waited without issuing a host request.");
                var replies = new JsonArray();
                foreach (var item in pending)
                {
                    token.ThrowIfCancellationRequested();
                    if (!loop.IsCurrent(isolate)) throw new IOException("Plugin isolate was retired.");
                    var reply = new JsonObject { ["id"] = item.GetInt32("id") };
                    var method = item.GetString("method");
                    try
                    {
                        var args = item.GetObject("args");
                        if (method == "assets.pull")
                        {
                            if (await readers.Pull(args.GetString("handle"), token) is { } bytesRead)
                            {
                                var transfer = port.StageBinary(bytesRead);
                                staged.Add(transfer);
                                reply["transfer"] = transfer;
                            }
                            else reply["result"] = null;
                        }
                        else
                            reply["result"] = await Api(invocation, method, args, grantedFiles, readers, codecs, token);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        reply["error"] = error.Message;
                        if (invocation.SkillTool)
                        {
                            var failure = ToolDiagnostics.FromException(invocation.Event.GetString("tool"), null, error);
                            reply["error"] = failure.GetString("error");
                            var diagnostic = (JsonObject)failure.GetObject("diagnostic").DeepClone();
                            if (diagnostic.GetString("phase") == "execution") diagnostic["phase"] = "host_api";
                            if (diagnostic["details"] is not JsonObject details)
                                diagnostic["details"] = details = new();
                            details["api"] = method;
                            reply["diagnostic"] = diagnostic;
                        }
                    }
                    replies.AddNode(reply);
                }
                request = new() { ["run"] = run, ["action"] = "resume", ["replies"] = replies };
            }
        }
        finally
        {
            foreach (var transfer in staged) port.TakeBinary(transfer);
            if (submitted && loop.IsCurrent(isolate) && !loop.Token.IsCancellationRequested)
            {
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(loop.Token, isolate.Token);
                cleanup.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await port.CallHost("plugin_script", new()
                    {
                        ["run"] = run, ["isolate"] = isolate.Id, ["action"] = "finish",
                        ["abort"] = binary && !finished
                    }, cleanup.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { loop.Reset(isolate); }
            }
        }
    }

    private void RequireInvocation(Invocation invocation)
    {
        if (invocation.Event.GetString("name") == "skill.tool" && !invocation.SkillTool)
            throw new UnauthorizedAccessException("Only the agent runtime can issue a skill.tool event.");
        if (invocation.Installation) RequireCurrent(invocation.Package);
        else RequireScope(invocation.Package, invocation.Environment.GetString("preset"));
    }

    private async Task<JsonNode?> Api(Invocation invocation, string method, JsonObject args, HashSet<string> grantedFiles,
        AssetReaders readers, AssetCodecs codecs, CancellationToken ct)
    {
        var package = invocation.Package;
        var ctx = invocation.Environment;
        RequireScope(package, ctx.GetString("preset"));
        if (preview && !(method.StartsWith("db.", StringComparison.Ordinal) || method.StartsWith("state.", StringComparison.Ordinal) || method.StartsWith("assets.", StringComparison.Ordinal) ||
            method.StartsWith("actions.", StringComparison.Ordinal) || method.StartsWith("text.", StringComparison.Ordinal) ||
            method.StartsWith("codecs.", StringComparison.Ordinal) || method == "settings.get"))
            throw new UnauthorizedAccessException("The preview runs with isolated settings, storage, actions and assets.");
        if (method == "development.run" && invocation.SkillTool && package.BuiltIn && package.Id == "base")
            return await harness(package, ctx, "pluginDevelopment", args, ct);
        if (invocation.Research is { } research && package.BuiltIn && package.Id == "base" &&
            method.StartsWith("research.", StringComparison.Ordinal))
            return await ResearchApi(research, method[9..], args, ct);
        if (method == "assetView.present" && invocation.PresentAsset is { } present)
        {
            var url = args.GetString("url");
            var asset = args.GetString("handle") is { Length: > 0 } handle ? readers.Content(handle) : ResolveAsset(url, new());
            return await present(asset, url, ct);
        }
        if (invocation.SkillTool && invocation.PresentAsset is not null && method is "assetView.grep" or "assetView.rawGrep")
        {
            var asset = ResolveAsset(args.GetString("url"), new());
            return method == "assetView.grep" ? await codecs.Grep(asset, args, ct) : await AssetGrep.Raw(asset, args, ct);
        }
        if (invocation.EditSuppliedAsset is { } edit && method is "assetEdit.binPatch" or "assetEdit.textEdit")
            return await edit(method[10..], args.GetString("url"), args, ct);
        if (method == "text.encode") return AssetCodecs.EncodeText(args);
        if (method == "text.decode") return AssetCodecs.DecodeText(args);
        if (method == "text.read") return await codecs.ReadText(ResolveAsset(args.GetString("url"), new()), args, ct);
        if (method.StartsWith("codecs.", StringComparison.Ordinal))
        {
            var (info, output) = await codecs.Media(method[7..], ResolveAsset(args.GetString("url"), new()), args, ct);
            if (output is null) return info;
            var opened = readers.Open(output);
            foreach (var (key, value) in info) opened[key] = JsonValues.Clone(value);
            return opened;
        }
        if (method is "state.get" or "state.set") return State(package, ctx, args, method == "state.set");
        if (method == "url.open" && !package.UsesActions) return OpenUrl(args.GetString("url"), ctx, package.Id, package.BuiltIn);
        if (method == "actions.publishOutput")
        {
            if (invocation.Output is not { } output)
                throw new UnauthorizedAccessException("Publishing output requires a Skill Action with an active Agent session.");
            if (args["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                throw new ArgumentException("Supply the tool output as text.");
            return await output.Publish(text, args.GetString("name", "tool-output.txt"), args.GetString("mime", "text/plain"), ct);
        }
        if (method.StartsWith("actions.", StringComparison.Ordinal))
            return WhileCurrent(package, ctx.GetString("preset"), () => DispatchAction(package, method, args, ctx));
        if (method == "assets.open") return readers.Open(ResolveAsset(args.GetString("url"), args.GetObject("options")));
        if (method == "assets.describe") return Describe(args.GetString("url"), ResolveAsset(args.GetString("url"), new()));
        if (method == "assets.notifyChanged")
            return WhileCurrent(package, ctx.GetString("preset"), () => NotifyAssetChanged(package, args.GetString("url"), ctx));
        if (method == "assets.close")
        {
            await readers.Close(args.GetString("handle"));
            return null;
        }
        if (method == "settings.get")
        {
            Require(package, "settings");
            return SettingValue(package, args.GetString("key"));
        }
        if (method.StartsWith("secretStorage.", StringComparison.Ordinal))
            return await PluginSecrets.Access(port, package, method[14..], args, ct);
        if (method.StartsWith("db.", StringComparison.Ordinal))
        {
            Require(package, "db");
            return WhileCurrent(package, ctx.GetString("preset"), () => Database(package.StorageKey, ctx, method[3..], args));
        }
        if (method == "http.request")
        {
            Require(package, "http");
            return await Http(package, args, ct);
        }
        if (method is "picker.media" or "picker.file")
        {
            Require(package, "picker");
            var selection = await port.CallHost("plugin_picker", new() { ["picker"] = method[7..], ["plugin"] = package.Id,
                ["name"] = package.LocalizedName(ctx.GetString("locale")), ["arguments"] = args.DeepClone() }, ct);
            foreach (var item in selection.GetArray("files")) grantedFiles.Add(item.GetString("id"));
            return selection;
        }
        if (method == "attachments.open")
        {
            var id = args.GetString("id");
            if (!grantedFiles.Contains(id)) throw new UnauthorizedAccessException("This attachment was not granted to the handler.");
            var path = Path.Combine(files, Workspace.Safe(id));
            return readers.Open(new("application/octet-stream", new FileInfo(path).Length, token => ReadFile(path, token)));
        }
        if (method == "attachments.read" && !package.UsesActions)
        {
            var id = args.GetString("id");
            if (!grantedFiles.Contains(id)) throw new UnauthorizedAccessException("This attachment was not granted to the handler.");
            await using var stream = File.OpenRead(Path.Combine(files, Workspace.Safe(id)));
            stream.Position = Math.Clamp(args.GetInt32("offset"), 0, stream.Length);
            var start = stream.Position;
            var content = new byte[(int)Math.Min(256 * 1024, stream.Length - start)];
            await stream.ReadExactlyAsync(content, ct);
            return new JsonObject { ["base64"] = Convert.ToBase64String(content), ["offset"] = start, ["total"] = stream.Length,
                ["nextOffset"] = stream.Position < stream.Length ? JsonValue.Create(stream.Position) : null };
        }
        if (method.StartsWith("harness.", StringComparison.Ordinal))
        {
            var op = method[8..];
            Require(package, op == "generate" ? "model" :
                op is "getSessionInfo" or "readSession" or "readChat" or "listRooms" or "readRoom" or "listChats" or "listPresets" or "getPreset" or "getWorldDate" ? "harness.read" : "harness.write");
            if (op == "getSessionInfo")
            {
                if (args.Count != 0) throw new ArgumentException("This API reads only the current session.");
                return JsonValues.Clone(ctx[SessionInfoKey]);
            }
            if (op == "send" && args.GetArray("attachments").Any(item => !grantedFiles.Contains(item.GetString("id"))))
                throw new UnauthorizedAccessException("A sent attachment was not granted to this handler.");
            return await harness(package, ctx, op, args, ct);
        }
        throw new ArgumentException("Unknown plugin API method.");

        AssetContent ResolveAsset(string url, JsonObject options) => invocation.OpenSuppliedAsset?.Invoke(url, options) ??
            (invocation.PresentAsset is not null
                ? throw new UnauthorizedAccessException(
                    "Use an Asset URL supplied in this session. Copy the exact minirp://assets/ URL from an attachment or an authorised tool result.")
                : MessageAttachments.IsAttachmentUrl(url)
                    ? throw new UnauthorizedAccessException("This workspace Asset was not supplied to the handler.")
                    : OpenAsset(url, package, options, ctx));
    }

    public JsonObject OpenUrl(string value, JsonObject environment, string sender = "", bool platform = false)
    {
        if (!PluginPackage.IsInteractionUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid URL Interaction address.");
        if (uri.Host == "app")
        {
            if (!platform || uri.AbsolutePath is not ("/language" or "/providers" or "/models" or "/plugins") || uri.Query.Length > 0)
                throw new UnauthorizedAccessException("This app URL is unavailable.");
            return OpenAppAction(new(AppActions, "hooks", uri.AbsolutePath[1..]), platform);
        }
        else if (uri.Host == "plugins")
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) throw new ArgumentException("Expected plugin and route.");
            var plugin = Volatile.Read(ref packages).FirstOrDefault(item => item.Id == parts[0]) ??
                throw new ArgumentException("Target plugin is unavailable.");
            var handler = plugin.Manifest.GetObject("routes").GetString(parts[1]);
            if (handler.Length == 0) throw new ArgumentException("Target plugin does not declare this route.");
            return InvokeAction(plugin, handler, new() { ["name"] = "url.open", ["url"] = value, ["sender"] = sender, ["route"] = parts[1] },
                (JsonObject)environment.DeepClone());
        }
        throw new ArgumentException("Unknown URL Interaction host.");
    }

    public void OpenSettingLink(string id, string key, JsonObject environment)
    {
        var package = Installed(id);
        RequireScope(package, environment.GetString("preset"));
        var setting = Setting(package, key);
        if (setting.GetString("type") != "link") throw new ArgumentException("This setting is not a link.");
        if (package.UsesActions) DispatchAction(package, "actions.invoke", new() { ["url"] = setting.GetString("url") }, environment);
        else OpenUrl(setting.GetString("url"), environment, package.Id, package.BuiltIn);
    }

    private JsonObject Database(string plugin, JsonObject context, string operation, JsonObject args)
    {
        var scope = args.GetString("scope", "session");
        using var db = new MemoryDb(ScopedDatabase("data", plugin, context, scope));
        var method = operation switch
        {
            "get" => "db_read", "put" => "db_write", "patch" => "db_patch", "delete" => "db_delete",
            "list" => "db_list", "search" => "db_search", _ => throw new ArgumentException("Unknown database operation.")
        };
        if (args.GetString("content").Length > 256 * 1024 || args.GetString("new_string").Length > 256 * 1024)
            throw new ArgumentException("Plugin database entry is too large.");
        if (operation == "get" && db.Get(args.GetString("key")) is null) return new() { ["missing"] = true };
        if (operation != "search") return db.Call(method, args);
        var query = (JsonObject)args.DeepClone();
        query["scope"] = args.GetString("match", "all");
        return db.Call(method, query);
    }

    private static readonly string[] DatabaseScopes = ["session", "chat", "room", "preset"];

    private static string DatabaseScope(JsonObject environment, string scope) =>
        DatabaseScopes.Contains(scope, StringComparer.Ordinal) ? environment.GetString(scope) : "";

    private string ScopedDatabase(string area, string plugin, JsonObject environment, string scope)
    {
        if (!DatabaseScopes.Contains(scope, StringComparer.Ordinal))
            throw new ArgumentException("Unknown database scope. Choose session, chat, room, or preset.");
        var id = environment.GetString(scope);
        if (id.Length == 0)
        {
            var available = string.Join(", ", DatabaseScopes.Where(value => environment.GetString(value).Length > 0));
            var guidance = preview && scope is ("chat" or "room")
                ? " To test a card with chat or room data, pass the Action fixture to plugin_card_preview. To test the installed Action, invoke it from its real entry point."
                : " Invoke the handler from an entry point with the required scope.";
            throw new InvalidOperationException(
                $"Database scope '{scope}' is unavailable for this event. Available scopes: {(available.Length == 0 ? "none" : available)}.{guidance}");
        }
        var grouped = environment.GetBoolean("_ensemble");
        if (grouped && scope == "chat")
            return Path.Combine(root, area, plugin, "chat", WorkspaceIds.Require(environment.GetString("room")), WorkspaceIds.Require(id) + ".sqlite");
        if (!grouped && scope == "room") scope = "chat";
        return Path.Combine(root, area, plugin, scope,
            (scope == "session" ? Workspace.Safe(id) : WorkspaceIds.Require(id)) + ".sqlite");
    }

    private async Task<JsonObject> Http(PluginPackage plugin, JsonObject args, CancellationToken ct)
    {
        if (!PluginPackage.TryHttp(args.GetString("url"), out var url) ||
            !plugin.Manifest.GetArray("network").Any(origin => JsonValues.GetText(origin) == url.GetLeftPart(UriPartial.Authority)))
            throw new UnauthorizedAccessException("This HTTP(S) origin is not declared in the plugin manifest.");
        var method = args.GetString("method", "GET").ToUpperInvariant();
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE") || args.GetString("body").Length > 256 * 1024)
            throw new ArgumentException("Unsupported HTTP request.");
        var request = (JsonObject)args.DeepClone();
        request["method"] = method;
        request["url"] = url.AbsoluteUri;
        request["timeout_ms"] = Math.Clamp(args.GetInt32("timeout_ms", 15000), 1000, 30000);
        return await port.CallHost("plugin_http", request, ct);
    }

    private static void Require(PluginPackage package, string permission)
    {
        if (!package.Has(permission)) throw new UnauthorizedAccessException("Plugin permission is required: " + permission);
    }

    private void Fault(string id, string message) => emit(new() { ["kind"] = "plugin_error", ["plugin"] = id, ["message"] = message });

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        Task[] retired;
        lock (packageGate)
        {
            retired = [.. retirements, .. loops.Values.Select(loop => loop.Stop())];
            loops.Clear();
        }
        try { await Task.WhenAll(retired); }
        finally
        {
            await hostAssets.DisposeAsync();
            stop.Dispose();
        }
    }
}
