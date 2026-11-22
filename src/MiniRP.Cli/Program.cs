using System.Text.Json.Nodes;
using MiniRP.Cli;
using MiniRP.Protocol;
using Terminal.Gui.App;

var root = Path.GetFullPath(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
var web = args.Contains("--web", StringComparer.Ordinal);
var noOpen = args.Contains("--no-open", StringComparer.Ordinal);
using var core = new NativeCore(root, seedDefaultPreset: false);
var settings = new EnvironmentSettings(root);
settings.Load();
var browserName = settings["MINIRP_BROWSER"];
if (browserName.Length == 0) browserName = OperatingSystem.IsLinux() ? "chromium" : "chrome";
await using var browser = new BrowserHost(root, browserName);
await using var platform = new PlatformHost(core.Send, core.TakeBinary, core.WriteBinary, browser, root);
core.Send(settings.Configure(web));
var containerImage = Environment.GetEnvironmentVariable("MINIRP_CONTAINER_IMAGE") ?? Path.Combine(AppContext.BaseDirectory, "container-image");
if (File.Exists(Path.Combine(containerImage, "ROOM_VM"))) core.Send(new() { ["op"] = "_container_available" });
if (File.Exists(CurlHost.Executable)) core.Send(new() { ["op"] = "_curl_available" });

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
    var window = new MainWindow(core, platform, settings, pluginFiles);
    using (window)
    {
        application.Run(window);
        web = window.OpenWeb;
        selection = window.Selection;
        ready = window.Ready;
    }
    await window.PumpCompletion;
}
if (web)
{
    await using var session = new WebSession(core, platform, settings, root, ready, selection);
    await WebDesktop.Run(session, noOpen);
}
