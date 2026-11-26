using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class AgentAssetCommand(HostPort port, AgentAssets assets, string root)
{
    private sealed record Destination(string Url, string Name, Func<Stream, CancellationToken, Task<JsonObject>> Write);

    internal AgentSkill Curl() => new("curl", "Run curl with Asset URLs for local input and output.",
        new(Tools.Define("curl", "Run curl arguments. Returns stdout as an Asset URL.", Fields(), "args")), Run,
        Instructions: "Use standard curl arguments without the command name. List each input Asset in read_assets and each writable destination in write_assets. " +
        "Use the exact Asset URLs in curl options, for example -o <Asset URL> and --data-binary @<Asset URL>. " +
        "Use file:///<Asset URL> as a URL to transfer an Asset through curl's file protocol. Stdout always goes to an Asset. " +
        "Set seconds only when the transfer needs a deadline.");

    internal AgentSkill Media(string tool, bool canCreateAssets)
    {
        var probe = tool == "ffprobe";
        var description = probe ? "Inspect media streams, codecs and duration using ffprobe."
            : "Convert, trim and combine media using ffmpeg.";
        var definition = probe ? Tools.Define("ffprobe", description, Fields(), "args") :
            Tools.Define("ffmpeg", description, Fields(), "args");
        return new(tool, description, new(definition), Run,
            Instructions: "Use upstream arguments without the command name. Declare every input URL in read_assets " +
                "and every writable output URL in write_assets. Pass the exact URLs as filenames. " +
                "Use pipe:0 and pipe:1 for standard input and output. Standard output can be returned as a temporary Asset. " +
                (canCreateAssets ? "Use create_asset to make an empty writable Asset for file output. "
                    : "For a new output, use pipe:1 with a format that can write to a pipe. ") +
                "Use an authorized Asset for media downloaded from the web. " +
                (probe ? "For metadata use -v error -show_streams -show_format -of json followed by the input Asset URL. "
                        : "For H.264 MP4 use -c:v libx264 -pix_fmt yuv420p -f mp4. Choose the output format explicitly with -f. " +
                          "Use -nostdin and -y for unattended runs. ") +
                "Run -version or -formats to inspect the runtime. Successful outputs are saved as Assets.");
    }

    private static JsonObject Fields() => new()
    {
        ["args"] = Tools.StringArray(), ["read_assets"] = Tools.StringArray(), ["write_assets"] = Tools.StringArray(),
        ["stdin_asset"] = Tools.String("Optional readable Asset to supply as standard input."),
        ["asset_url"] = Tools.String("Optional writable Asset for stdout. Otherwise a temporary Asset is created."),
        ["seconds"] = Tools.Integer()
    };

    private async Task<JsonNode?> Run(string tool, JsonObject args, CancellationToken ct)
    {
        var media = tool is "ffmpeg" or "ffprobe";
        var arguments = args.GetArray("args");
        if (arguments.Count is 0 or > 128 || JsonValues.Serialize(arguments).Length > 32768 ||
            arguments.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Contains('\0')))
            throw new ArgumentException($"Supply up to 128 {tool} arguments.");
        var reads = Urls(args, "read_assets");
        var writes = Urls(args, "write_assets");
        var stdout = args.GetString("asset_url");
        if (writes.Contains(stdout, StringComparer.Ordinal)) throw new ArgumentException("Use a separate Asset for stdout.");
        var destinations = writes.Select((url, index) => new Destination(url, "write-" + index, assets.PrepareWrite(url))).ToArray();
        var writeStdout = stdout.Length > 0 ? assets.PrepareWrite(stdout) : null;
        var id = JsonValues.NewId();
        var directory = Path.Combine(root, media ? "ffmpeg-runs" : "curl-runs", id);
        Directory.CreateDirectory(directory);
        try
        {
            var files = new JsonArray();
            if (args.GetString("stdin_asset") is { Length: > 0 } stdin) await Stage(stdin, "stdin");
            foreach (var destination in destinations)
            {
                if (reads.Contains(destination.Url, StringComparer.Ordinal)) await Stage(destination.Url, destination.Name);
                files.AddNode(new JsonObject { ["url"] = destination.Url, ["name"] = destination.Name, ["write"] = true });
            }
            foreach (var url in reads.Except(writes, StringComparer.Ordinal))
            {
                var name = "read-" + files.Count;
                await Stage(url, name);
                files.AddNode(new JsonObject { ["url"] = url, ["name"] = name, ["write"] = false });
            }

            var reply = await port.CallHost(media ? "ffmpeg_run" : "curl_download", new()
            {
                ["tool"] = tool, ["run"] = id, ["args"] = arguments.DeepClone(),
                ["seconds"] = args.GetInt32("seconds"), ["files"] = files
            }, ct);
            var exit = reply.GetInt32("exit_code", -1);
            var written = new JsonArray();
            if (exit == 0)
            {
                if (media)
                    foreach (var destination in destinations)
                        ValidateOutput(Path.Combine(directory, destination.Name));
                foreach (var destination in destinations)
                {
                    var file = Path.Combine(directory, destination.Name);
                    if (!File.Exists(file)) continue;
                    await using var input = Read(file);
                    await destination.Write(input, ct);
                    written.AddNode(destination.Url);
                }
            }
            var output = Path.Combine(directory, "stdout");
            await using var contents = Read(output);
            if (writeStdout is null && (contents.Length > 0 || exit == 0 && written.Count == 0) || exit != 0 && contents.Length > 0)
            {
                var stored = await assets.Output.StoreStream(tool + "-output", tool == "ffprobe" ? "text/plain" : "application/octet-stream", null,
                    (stream, token) => contents.CopyToAsync(stream, token), ct);
                stdout = stored.GetString("asset_url");
            }
            else if (writeStdout is not null && exit == 0) await writeStdout(contents, ct);
            else if (exit != 0) stdout = "";
            if (tool == "ffprobe" && contents.Length is > 0 and <= 12 * 1024)
            {
                contents.Position = 0;
                using var reader = new StreamReader(contents, leaveOpen: true);
                reply["output"] = await reader.ReadToEndAsync(ct);
            }
            reply["asset_url"] = stdout.Length == 0 ? null : stdout;
            reply["written_assets"] = written;
            reply["bytes"] = contents.Length;
            return reply;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        async Task Stage(string url, string name)
        {
            var staged = await AssetCodecs.Stage(directory, assets.Open(url, new()), "stage-", MessageAttachments.MaxAssetBytes, ct);
            File.Move(staged, Path.Combine(directory, name));
        }
    }

    private static FileStream Read(string path) => ValidateOutput(path).OpenRead();

    private static FileInfo ValidateOutput(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > MessageAttachments.MaxAssetBytes)
            throw new IOException("Missing or invalid command output file.");
        return file;
    }

    private static string[] Urls(JsonObject args, string key)
    {
        var urls = args.GetArray(key).Select(item => JsonValues.GetText(item)).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length > 8 || urls.Any(url => PluginUrls.Route(url, "assets").Length == 0))
            throw new ArgumentException("Supply up to eight Asset URLs per access list.");
        return urls;
    }
}
