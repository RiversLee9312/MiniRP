using System.Text.Json.Nodes;
using MiniRP.Cli;
using MiniRP.Protocol;
using Terminal.Gui.App;

var windowed = (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && args.Contains("--web-window", StringComparer.Ordinal);
var root = Path.GetFullPath(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ??
    (args.Contains("--user-workspace", StringComparer.Ordinal) ? UserWorkspace() : Directory.GetCurrentDirectory()));
var web = windowed || args.Contains("--web", StringComparer.Ordinal);
var noOpen = args.Contains("--no-open", StringComparer.Ordinal);
using var core = new NativeCore(root, seedDefaultPreset: false);
var secrets = new DesktopSecretStorage(root);
var settings = new EnvironmentSettings(root, secrets);
settings.Load();
await using var browser = new BrowserHost(root, new DesktopBrowserRuntime(root, BrowserData(), settings["MINIRP_BROWSER"]));
await using var platform = new PlatformHost(core.Send, core.TakeBinary, core.WriteBinary, browser, root, secrets, settings.ResearchDefault);
core.Send(settings.Configure(web));
var containerImage = Environment.GetEnvironmentVariable("MINIRP_CONTAINER_IMAGE") ?? Path.Combine(AppContext.BaseDirectory, "container-image");
if (File.Exists(Path.Combine(containerImage, "ROOM_VM"))) core.Send(new() { ["op"] = "_container_available" });
if (File.Exists(AssetCommandHost.Executable("curl"))) core.Send(new() { ["op"] = "_curl_available" });
if (File.Exists(AssetCommandHost.Executable("ffmpeg")) && File.Exists(AssetCommandHost.Executable("ffprobe")))
    core.Send(new() { ["op"] = "_ffmpeg_available" });

if (args.Contains("--stdio", StringComparer.Ordinal))
{
    // Automation/debugging host for the exact same native core, with no alternate agent runtime.
    using var cancellation = new CancellationTokenSource();
    var pump = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            foreach (var node in core.Poll())
            {
                var message = node!.AsObject();
                if (!platform.Handle(message))
                {
                    Console.WriteLine(JsonValues.Serialize(message));
                }
            }
            await Task.Delay(16);
        }
    });
    while (await Console.In.ReadLineAsync() is { } line)
    {
        try
        {
            core.Send(JsonValues.ParseObject(line));
        }
        catch (Exception error) { Console.WriteLine(JsonValues.Serialize(JsonValues.Error(error.Message))); }
    }
    cancellation.Cancel();
    await pump;
    return;
}

(string Preset, string Chat, string Conversation) selection = ("", "", "");
JsonObject? ready = null;
if (!web)
{
    using IApplication application = Application.Create().Init();
    var pluginFiles = new PluginFiles(root);
    platform.Picker = (message, ct) => pluginFiles.Choose(application, message, ct);
    browser.Setup = (release, install, ct) => BrowserSetupUi.Terminal(application, release, install, ct);
    var window = new MainWindow(core, platform, settings, pluginFiles);
    using (window)
    {
        application.Run(window);
        web = window.OpenWeb;
        selection = window.Selection;
        ready = window.Ready;
    }
    await window.PumpCompletion;
    if (web) core.Send(settings.Configure(web: true));
}
if (web)
{
    await using var session = new WebSession(core, platform, settings, root, ready, selection);
    browser.Setup = session.InstallBrowser;
    if (windowed && OperatingSystem.IsWindows() && !noOpen) BrowserWindow.DetachConsole();
    try { await WebDesktop.Run(session, noOpen, windowed ? browser : null); }
    catch (BrowserSetupDeferredException) when (windowed && !noOpen) { }
    catch (Exception error) when (windowed && !noOpen)
    {
        await BrowserWindow.ShowError(error.Message);
        Environment.ExitCode = 1;
    }
}

static string UserWorkspace()
{
    if (OperatingSystem.IsLinux())
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var data = Path.IsPathFullyQualified(xdg ?? "") ? xdg! :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "minirp");
    }
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniRP");
}

static string BrowserData()
{
    var directory = Environment.GetEnvironmentVariable("MINIRP_BROWSER_DATA_DIR");
    return Path.IsPathFullyQualified(directory ?? "") ? directory! : UserWorkspace();
}
