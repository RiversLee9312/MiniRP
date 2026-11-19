using System.Text.Json.Nodes;
using MiniRP.Protocol;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

internal sealed class PluginFiles(string root)
{
    internal async Task<JsonObject> Choose(IApplication app, JsonObject message, CancellationToken ct)
    {
        var selection = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                using var dialog = new OpenDialog
                {
                    Title = "Choose files for " + message.GetString("name"),
                    OpenMode = OpenMode.File, MustExist = true, AllowsMultipleSelection = true
                };
                app.Run(dialog);
                selection.TrySetResult(dialog.Canceled ? [] : dialog.MultiSelected.Take(20).ToArray());
            }
            catch (Exception error) { selection.TrySetException(error); }
        });
        var paths = await selection.Task.WaitAsync(ct);
        var directory = Path.Combine(root, "attachments");
        Directory.CreateDirectory(directory);
        var files = new JsonArray();
        long remaining = 16_000_000;
        foreach (var path in paths)
        {
            var mime = Mime(Path.GetExtension(path).ToLowerInvariant());
            var args = message.GetObject("arguments");
            var filters = args.GetArray("mimeTypes");
            if (message.GetString("picker") == "media" &&
                !(args.GetString("type") switch
                {
                    "image" => mime.StartsWith("image/", StringComparison.Ordinal),
                    "video" => mime.StartsWith("video/", StringComparison.Ordinal),
                    _ => mime.StartsWith("image/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal)
                })) continue;
            if (filters.Count > 0 && !filters.Any(item => Matches(JsonValues.GetText(item), mime))) continue;
            await using var input = File.OpenRead(path);
            remaining -= input.Length;
            if (remaining < 0) throw new IOException("Selected files exceed 16 MB.");
            var id = JsonValues.NewId();
            await using var destination = File.Create(Path.Combine(directory, id));
            await input.CopyToAsync(destination, ct);
            files.AddNode(new JsonObject { ["id"] = id, ["mime"] = mime, ["name"] = Path.GetFileName(path) });
        }
        return new() { ["files"] = files };
    }

    private static bool Matches(string filter, string mime) => filter == mime || filter == "*/*" ||
        (filter.EndsWith("/*", StringComparison.Ordinal) && mime.StartsWith(filter[..^1], StringComparison.Ordinal));

    private static string Mime(string ext) => ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp",
        ".gif" => "image/gif", ".mp4" => "video/mp4", ".webm" => "video/webm", ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".pdf" => "application/pdf",
        ".txt" => "text/plain", ".md" => "text/markdown", ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
