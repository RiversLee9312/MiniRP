using System.Text.Json.Nodes;
using Minichat.Cli;
using Minichat.Protocol;
using Terminal.Gui.App;

var root = Path.GetFullPath(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
using var core = new NativeCore(root);
var settings = new EnvironmentSettings(root);
settings.Load();
await using var browser = new BrowserHost(root, settings["MINICHAT_BROWSER"].Length > 0 ? settings["MINICHAT_BROWSER"] : "chrome");
using var platform = new PlatformHost(core, browser);
core.Send(settings.Configure());

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

using IApplication application = Application.Create().Init();
using var window = new MainWindow(core, platform, settings);
application.Run(window);
