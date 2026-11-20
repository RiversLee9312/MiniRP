using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal static class AgentMediaSampler
{
    private const string MediaFormats = "mov,matroska,webm,mp3,wav,aac,ogg,flac,mpeg,mpegts,avi,flv,asf";
    private const string ImageFormats = "image2,png_pipe,jpeg_pipe,webp_pipe,bmp_pipe,tiff_pipe,gif,mov";
    public static async Task<JsonObject> Sample(string directory, JsonObject request, CancellationToken ct)
    {
        var input = PathFor(directory, request.GetString("source"), "asset-source-");
        var output = PathFor(directory, request.GetString("output"), "asset-sample-");
        if (!File.Exists(input) || File.Exists(output)) throw new IOException("Invalid media sample files.");
        var kind = request.GetString("mode");
        if (kind == "pdf_metadata") return await PdfMetadata(input, ct);
        if (kind is "image_metadata" or "video_metadata") return await MediaMetadata(kind, input, ct);
        if (kind is "image_decode" or "image_encode") return await Image(kind, input, output, request, ct);
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
        else if (kind == "pdf_create_page")
        {
            await Run("img2pdf", ["--output", output, input], ct);
        }
        else if (kind is "video_frame" or "audio_clip" or "video_encode")
        {
            string[] common = ["-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe",
                "-format_whitelist", MediaFormats, "-ss", start, "-i", input];
            string[] sample = kind switch
            {
                "video_frame" => ["-map", "0:v:0", "-frames:v", "1", "-vf", "scale=1400:1400:force_original_aspect_ratio=decrease", "-f", "image2", "-update", "1", "-c:v", "png"],
                "video_encode" => ["-map", "0:v:0", "-map", "0:a:0?", "-t", length, "-c:v", "libx264", "-pix_fmt", "yuv420p",
                    "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:a", "aac", "-movflags", "+faststart", "-f", "mp4"],
                _ => ["-map", "0:a:0", "-t", length, "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav"]
            };
            await Run("ffmpeg", [.. common, .. sample, output], ct);
        }
        else throw new ArgumentException("Unknown asset sample mode.");
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("No media at the requested position.");
        return new() { ["mime"] = kind switch
        {
            "audio_clip" => "audio/wav", "video_encode" => "video/mp4", "pdf_create_page" => "application/pdf", _ => "image/png"
        } };
    }

    private static async Task<JsonObject> Image(string kind, string input, string output, JsonObject request, CancellationToken ct)
    {
        var metadata = await MediaMetadata("image_metadata", input, ct);
        var width = metadata.GetInt32("width");
        var height = metadata.GetInt32("height");
        if (width < 1 || height < 1) throw new IOException("Unable to decode image.");
        var scale = Math.Min(1d, 2048d / Math.Max(width, height));
        width = Math.Max(1, (int)Math.Round(width * scale));
        height = Math.Max(1, (int)Math.Round(height * scale));
        string[] options = ["-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe",
            "-format_whitelist", ImageFormats, "-noautorotate", "-i", input, "-frames:v", "1", "-vf", $"scale={width}:{height}"];
        if (kind == "image_decode")
        {
            await Run("ffmpeg", [.. options, "-pix_fmt", "rgba", "-f", "rawvideo", output], ct);
            return new() { ["mime"] = "application/octet-stream", ["width"] = width, ["height"] = height, ["pixel_format"] = "rgba8" };
        }
        var format = request.GetString("format");
        var codec = format switch { "png" => "png", "jpeg" => "mjpeg", "webp" => "libwebp", _ => throw new ArgumentException("Unsupported image format.") };
        var quality = Math.Clamp(request.GetInt32("quality", 90), 1, 100);
        string[] tuning = format switch
        {
            "jpeg" => ["-q:v", (31 - 29 * quality / 100).ToString(CultureInfo.InvariantCulture)],
            "webp" => ["-quality", quality.ToString(CultureInfo.InvariantCulture)],
            _ => []
        };
        await Run("ffmpeg", [.. options, "-c:v", codec, .. tuning, "-f", "image2", "-update", "1", output], ct);
        return new() { ["mime"] = "image/" + format };
    }

    private static string Seconds(int milliseconds) => (milliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<JsonObject> PdfMetadata(string input, CancellationToken ct)
    {
        var details = await Run("pdfinfo", ["-enc", "UTF-8", input], ct);
        var result = new JsonObject();
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Title"] = "title", ["Author"] = "author", ["Subject"] = "subject", ["Creator"] = "creator",
            ["Producer"] = "producer", ["Pages"] = "page_count", ["Page size"] = "page_size",
            ["PDF version"] = "pdf_version", ["Encrypted"] = "encrypted", ["Optimized"] = "linearized",
        };
        foreach (var line in details.Split('\n'))
        {
            var split = line.IndexOf(':');
            if (split < 0 || !keys.TryGetValue(line[..split], out var key)) continue;
            var value = line[(split + 1)..].Trim();
            result[key] = int.TryParse(value, CultureInfo.InvariantCulture, out var number) && key == "page_count"
                ? JsonValue.Create(number) : JsonValue.Create(value[..Math.Min(512, value.Length)]);
        }
        return result;
    }

    private static async Task<JsonObject> MediaMetadata(string kind, string input, CancellationToken ct)
    {
        var formats = kind == "image_metadata" ? ImageFormats : MediaFormats;
        var json = await Run("ffprobe", ["-v", "error", "-protocol_whitelist", "file,pipe", "-format_whitelist", formats,
            "-show_entries", "stream=codec_type,codec_name,width,height,r_frame_rate:stream_side_data=rotation:format=duration,bit_rate,format_name",
            "-of", "json", input], ct);
        var metadata = JsonValues.ParseObject(json);
        var video = metadata.GetArray("streams").FirstOrDefault(stream => stream.GetString("codec_type") == "video")
            ?? throw new IOException("Image or video metadata is unavailable.");
        var result = new JsonObject
        {
            ["width"] = video.GetInt32("width"), ["height"] = video.GetInt32("height"), ["codec"] = video.GetString("codec_name"),
            ["format"] = metadata.GetObject("format").GetString("format_name")
        };
        if (kind == "video_metadata")
        {
            var format = metadata.GetObject("format");
            result["has_audio"] = metadata.GetArray("streams").Any(stream => stream.GetString("codec_type") == "audio");
            if (double.TryParse(format.GetString("duration"), CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
                result["duration_ms"] = Math.Round(seconds * 1000);
            if (long.TryParse(format.GetString("bit_rate"), CultureInfo.InvariantCulture, out var bitRate))
                result["bit_rate"] = bitRate;
            result["frame_rate"] = video.GetString("r_frame_rate");
        }
        if (video.GetArray("side_data_list").FirstOrDefault() is { } rotation)
            result["rotation_degrees"] = rotation.GetInt32("rotation");
        return result;
    }

    private static string PathFor(string directory, string id, string prefix)
    {
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length <= prefix.Length ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid media sample file.");
        return Path.Combine(directory, id);
    }

    private static async Task<string> Run(string program, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Media sampler did not start."); }
        catch (Win32Exception error) { throw new IOException("Install " + program + " to sample this asset on the desktop.", error); }
        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var details = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            if (process.ExitCode != 0) throw new IOException((await details).Trim());
            return await output;
        }
    }
}
