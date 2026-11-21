using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class ContainerHost(string workspace)
{
    private readonly SemaphoreSlim gate = new(1);
    private Runtime? runtime;
    private sealed record Runtime(string Qemu, string Image, string ImageId);

    public async Task<JsonObject> Handle(JsonObject request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            runtime ??= await Find(ct);
            if (request.GetString("kind") == "container_runtime") return new()
            {
                ["image"] = runtime.ImageId, ["room_vm"] = File.Exists(Path.Combine(runtime.Image, "ROOM_VM"))
            };
            if (request.GetString("image") != runtime.ImageId) throw new IOException("The container image changed.");
            var id = request.GetString("run");
            if (id.Length != 12 || !id.All(Uri.IsHexDigit)) throw new IOException("Invalid VM request.");
            var run = Path.Combine(workspace, "container-runs", id);
            var info = new ProcessStartInfo(runtime.Qemu) { WorkingDirectory = run, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (OperatingSystem.IsWindows())
            {
                info.ArgumentList.Add("-accel");
                info.ArgumentList.Add("whpx");
            }
            var firmware = Path.Combine(Path.GetDirectoryName(runtime.Qemu)!, "firmware");
            if (Directory.Exists(firmware))
            {
                info.ArgumentList.Add("-L");
                info.ArgumentList.Add(firmware);
            }
            info.ArgumentList.Add("-accel");
            info.ArgumentList.Add("tcg,tb-size=32");
            foreach (var value in request.GetArray("arguments"))
            {
                var argument = JsonValues.GetText(value).Replace("{kernel}", Path.Combine(runtime.Image, "kernel"), StringComparison.Ordinal)
                    .Replace("{initrd}", Path.Combine(runtime.Image, "initrd.gz"), StringComparison.Ordinal)
                    .Replace("{rootfs}", Path.Combine(runtime.Image, "root.squashfs").Replace(",", ",,"), StringComparison.Ordinal);
                info.ArgumentList.Add(argument);
            }
            using var process = Process.Start(info) ?? throw new IOException("Could not start QEMU.");
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
                await output;
                var detail = await errors;
                if (process.ExitCode != 0)
                    throw new IOException($"QEMU exited with code {process.ExitCode}: " + detail[..Math.Min(detail.Length, 1000)].Trim());
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                }
            }
            return new();
        }
        finally { gate.Release(); }
    }

    private static async Task<Runtime> Find(CancellationToken ct)
    {
        var image = Path.GetFullPath(Environment.GetEnvironmentVariable("MINIRP_CONTAINER_IMAGE") ??
            Path.Combine(AppContext.BaseDirectory, "container-image"));
        var exe = OperatingSystem.IsWindows() ? "qemu-system-x86_64.exe" : "qemu-system-x86_64";
        var qemu = Environment.GetEnvironmentVariable("MINIRP_QEMU");
        if (string.IsNullOrWhiteSpace(qemu))
            qemu = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Prepend(Path.Combine(AppContext.BaseDirectory, "qemu"))
                .Select(directory => Path.Combine(directory, exe)).FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(qemu) || !File.Exists(qemu) || !File.Exists(Path.Combine(image, "SHA256SUMS")))
            throw new IOException("Install the MiniRP x86-64 container image and QEMU to use the container skill.");
        var sums = await File.ReadAllBytesAsync(Path.Combine(image, "SHA256SUMS"), ct);
        var expected = System.Text.Encoding.ASCII.GetString(sums).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = new[] { "kernel", "initrd.gz", "root.squashfs" };
        if (expected.Length != files.Length) throw new IOException("Invalid container image manifest.");
        for (var index = 0; index < files.Length; index++)
        {
            await using var stream = File.OpenRead(Path.Combine(image, files[index]));
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (expected[index].TrimEnd('\r') != hash + "  " + files[index])
                throw new IOException("Container image checksum mismatch.");
        }
        return new(Path.GetFullPath(qemu), image, Convert.ToHexStringLower(SHA256.HashData(sums)));
    }
}
