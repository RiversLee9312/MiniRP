using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal static class AgentMediaSampler
{
    public static async Task<JsonObject> Sample(string directory, JsonObject request, CancellationToken ct)
    {
        var input = PathFor(directory, request.GetString("source"), "asset-source-");
        var output = PathFor(directory, request.GetString("output"), "asset-sample-");
        if (!File.Exists(input) || File.Exists(output)) throw new IOException("Invalid media sample files.");
        var kind = request.GetString("mode");
        var start = Seconds(Math.Max(0, request.GetInt32("start_ms")));
        var length = Seconds(Math.Clamp(request.GetInt32("duration_ms", 10000), 100, 30000));
        if (kind == "pdf_page")
        {
            var page = Math.Max(1, request.GetInt32("page", 1)).ToString(CultureInfo.InvariantCulture);
            var png = output + ".png";
            try
            {
                await Run("pdftoppm", ["-f", page, "-l", page, "-scale-to", "1400", "-singlefile", "-png", input, output], ct);
                File.Move(png, output);
            }
            finally { File.Delete(png); }
        }
        else if (kind is "video_frame" or "audio_clip")
        {
            string[] common = ["-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe",
                "-format_whitelist", "mov,matroska,webm,mp3,wav,aac,ogg,flac,mpeg,mpegts,avi,flv,asf", "-ss", start, "-i", input];
            string[] sample = kind == "video_frame"
                ? ["-map", "0:v:0", "-frames:v", "1", "-vf", "scale=1400:1400:force_original_aspect_ratio=decrease", "-f", "image2", "-update", "1", "-c:v", "png"]
                : ["-map", "0:a:0", "-t", length, "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav"];
            await Run("ffmpeg", [.. common, .. sample, output], ct);
        }
        else throw new ArgumentException("Unknown asset sample mode.");
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("No media at the requested position.");
        return new() { ["mime"] = kind == "audio_clip" ? "audio/wav" : "image/png" };
    }

    private static string Seconds(int milliseconds) => (milliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

    private static string PathFor(string directory, string id, string prefix)
    {
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length <= prefix.Length ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid media sample file.");
        return Path.Combine(directory, id);
    }

    private static async Task Run(string program, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Media sampler did not start."); }
        catch (Win32Exception error) { throw new IOException("Install " + program + " to sample this asset on the desktop.", error); }
        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var details = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            if (process.ExitCode != 0) throw new IOException((await details).Trim());
        }
    }
}
