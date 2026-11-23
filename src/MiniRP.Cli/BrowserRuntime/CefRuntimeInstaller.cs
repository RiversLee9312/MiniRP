using System.Buffers;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace MiniRP.Cli;

internal sealed record CefInstallProgress(string Phase, long BytesReceived, long TotalBytes);

internal sealed class CefRuntimeInstaller(string runtimeRoot, string bundledHelper, HttpClient? downloadClient = null)
{
    private const long MaximumExpandedSize = 3L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 8192;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public CefRuntimeRelease? Release { get; } = CefRuntimeRelease.ForCurrentProcess();
    private string? BundledModule => OperatingSystem.IsWindows() ? Path.ChangeExtension(bundledHelper, ".dll") : null;
    public bool IsAvailable => Release is not null && File.Exists(bundledHelper) &&
        (BundledModule is not { } module || File.Exists(module));

    public async Task<string?> FindAsync(CancellationToken token)
    {
        if (!IsAvailable || !Directory.Exists(runtimeRoot)) return null;
        await using var gate = await Lock(token);
        return await FindInstalled(Release!, token);
    }

    public async Task<string> InstallAsync(Action<CefInstallProgress> report, Func<string, CancellationToken, Task> validate,
        CancellationToken token)
    {
        if (!IsAvailable || Release is not { } release)
            throw new InvalidOperationException("This MiniRP build does not provide a compatible CEF host.");
        CreatePrivateDirectory(runtimeRoot);
        await using var gate = await Lock(token);
        if (await FindInstalled(release, token) is { } installed)
        {
            report(new("checking", release.DownloadBytes, release.DownloadBytes));
            await validate(installed, token);
            return installed;
        }

        var stage = Path.Combine(runtimeRoot, ".cef-stage-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(stage);
        try
        {
            var archivePath = Path.Combine(stage, "runtime.tar.bz2");
            await DownloadAsync(release, downloadClient ?? DownloadClient, archivePath, report, token);
            var files = Path.Combine(stage, "files");
            CreatePrivateDirectory(files);
            report(new("extracting", release.DownloadBytes, release.DownloadBytes));
            await using (var archive = File.OpenRead(archivePath))
            using (var expanded = new BZip2InputStream(archive))
                await ExtractAsync(expanded, release.ArchiveRoot, files, token);
            if (!await CheckRuntime(release, files, token))
                throw new InvalidDataException("The extracted CEF runtime did not match the supported release.");

            var host = await CopyHelper(files, token);
            report(new("checking", release.DownloadBytes, release.DownloadBytes));
            await validate(host, token);
            token.ThrowIfCancellationRequested();

            var destination = RuntimeDirectory(release);
            // A previous damaged installation remains in place until the replacement has passed validation.
            if (Directory.Exists(destination)) Directory.Move(destination, Path.Combine(stage, "previous"));
            try { Directory.Move(files, destination); }
            catch
            {
                var previous = Path.Combine(stage, "previous");
                if (Directory.Exists(previous)) Directory.Move(previous, destination);
                throw;
            }
            return Path.Combine(destination, Path.GetFileName(host));
        }
        finally { CleanStage(stage); }
    }

    private string RuntimeDirectory(CefRuntimeRelease release) => Path.Combine(runtimeRoot, release.ArchiveRoot);

    private async Task<string?> FindInstalled(CefRuntimeRelease release, CancellationToken token)
    {
        var directory = RuntimeDirectory(release);
        if (!Directory.Exists(directory)) return null;
        try
        {
            return await CheckRuntime(release, directory, token) ? await CopyHelper(directory, token) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private async Task<FileStream> Lock(CancellationToken token)
    {
        var path = Path.Combine(runtimeRoot, ".install-lock");
        for (;;)
        {
            token.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 11 or 32 or 33)
            {
                await Task.Delay(250, token);
            }
        }
    }

    internal static async Task DownloadAsync(CefRuntimeRelease release, HttpClient client, string archivePath,
        Action<CefInstallProgress> report, CancellationToken token)
    {
        const int rangeSize = 8 * 1024 * 1024;
        const int reportInterval = 1024 * 1024;
        var transferTimeout = TimeSpan.FromSeconds(45);
        report(new("downloading", 0, release.DownloadBytes));
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(131072);
        long total = 0;
        long lastReported = 0;
        var failures = 0;
        try
        {
            while (total < release.DownloadBytes)
            {
                var start = total;
                var end = Math.Min(start + rangeSize, release.DownloadBytes);
                using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
                request.Headers.Range = new RangeHeaderValue(start, end - 1);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                try
                {
                    timeout.CancelAfter(transferTimeout);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.OK && start == 0) end = release.DownloadBytes;
                    else
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (response.StatusCode != HttpStatusCode.PartialContent || range is null ||
                            range.Unit != "bytes" || range.From != start || range.To != end - 1 || range.Length != release.DownloadBytes)
                            throw new InvalidDataException("The server refused a verified range of the CEF archive.");
                    }
                    if (response.Content.Headers.ContentLength is long length && length != end - start)
                        throw new InvalidDataException("The server returned a different CEF archive size than expected.");
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    while (true)
                    {
                        timeout.CancelAfter(transferTimeout);
                        var count = await input.ReadAsync(buffer.AsMemory(), timeout.Token);
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        if (count == 0) break;
                        total += count;
                        if (total > end) throw new InvalidDataException("The CEF archive exceeded the expected size.");
                        digest.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        if (total - lastReported < reportInterval && total != release.DownloadBytes) continue;
                        lastReported = total;
                        report(new("downloading", total, release.DownloadBytes));
                    }
                    if (total != end) throw new EndOfStreamException("The CEF server stopped sending data.");
                    failures = 0;
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    error is (IOException or HttpRequestException or OperationCanceledException) &&
                    error is not InvalidDataException && failures++ < 2)
                {
                    // Preserve bytes already received. Accept resumed data only at its requested offset.
                    await Task.Delay(500, token);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("The CEF download timed out.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        report(new("verifying", total, release.DownloadBytes));
        if (total != release.DownloadBytes || Convert.ToHexStringLower(digest.GetHashAndReset()) != release.ArchiveSha256)
            throw new InvalidDataException("The CEF download does not match the published archive.");
    }

    internal static async Task ExtractAsync(Stream expanded, string archiveRoot, string destination, CancellationToken token)
    {
        using var reader = new TarReader(expanded, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = archiveRoot + "/";
        long expandedBytes = 0;
        var entries = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            if (++entries > MaximumEntries || entry.Length > MaximumExpandedSize - expandedBytes)
                throw new InvalidDataException("The CEF archive exceeds its extraction limits.");
            expandedBytes += entry.Length;
            if (entry.Name.TrimEnd('/') == archiveRoot && entry.EntryType == TarEntryType.Directory) continue;
            if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected CEF archive root.");
            var path = entry.Name[prefix.Length..].TrimEnd('/');
            if (!SafeRelativePath(path))
                throw new InvalidDataException("The CEF archive contains an unsafe name.");
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("The CEF archive contains a non-regular file.");

            var relative = RuntimePath(path);
            if (relative is null) continue;
            if (IsHostName(relative) || !names.Add(relative))
                throw new InvalidDataException("The CEF archive contains a conflicting runtime name.");
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous))
            {
                if (entry.DataStream is { } data) await data.CopyToAsync(file, token);
                if (file.Length != entry.Length)
                    throw new InvalidDataException("The CEF archive contains an incomplete file.");
            }
            if (!OperatingSystem.IsWindows())
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                if ((entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    mode |= UnixFileMode.UserExecute;
                File.SetUnixFileMode(target, mode);
            }
        }
    }

    // The SDK and runtime use the same archive. Only the runtime payload is extracted.
    private static string? RuntimePath(string path)
    {
        if (path == "LICENSE.txt") return path;
        var split = path.IndexOf('/');
        if (split < 0 || path[..split] is not ("Release" or "Resources")) return null;
        var relative = path[(split + 1)..];
        return relative is "bootstrap.exe" or "bootstrapc.exe"
            || Path.GetExtension(relative) is ".a" or ".lib" or ".pdb" or ".debug" ? null : relative;
    }

    private static bool SafeRelativePath(string path) => path.Split('/').All(segment =>
        segment.Length > 0 && segment[^1] != '.' &&
        segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '+') &&
        !IsDeviceName(segment.Split('.', 2)[0]));

    private static bool IsDeviceName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL" ||
            upper.Length == 4 && upper[..3] is ("COM" or "LPT") && upper[3] is >= '0' and <= '9';
    }

    private static bool IsHostName(string name)
    {
        const string prefix = "minirp-cef-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var hash = name[prefix.Length..];
        if (Path.GetExtension(hash) is ".exe" or ".dll") hash = hash[..^4];
        return CefRuntimeRelease.IsSha256(hash);
    }

    private static async Task<bool> CheckRuntime(CefRuntimeRelease release, string directory, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(directory, release.LibraryName))) return false;
        return await RuntimeDigestAsync(directory, token) == release.RuntimeSha256;
    }

    internal static async Task<string> RuntimeDigestAsync(string directory, CancellationToken token)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The CEF runtime directory is a link.");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(directory);
        var entries = 0;
        long totalBytes = 0;
        while (pending.TryPop(out var subdirectory))
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(subdirectory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaximumEntries) throw new InvalidDataException("The CEF runtime contains too many files.");
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The CEF runtime contains a link.");
                if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(file); continue; }
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (!SafeRelativePath(relative))
                    throw new InvalidDataException("The CEF runtime contains an unsafe name.");
                if (IsHostName(relative)) continue;
                var length = new FileInfo(file).Length;
                if (length > MaximumExpandedSize - totalBytes)
                    throw new InvalidDataException("The CEF runtime exceeds its size limit.");
                totalBytes += length;
                files.Add(relative, file);
            }
        }
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
            digest.AppendData(Encoding.UTF8.GetBytes(await FileSha256(file.Value, token) + "\t" + file.Key + "\n"));
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private async Task<string> CopyHelper(string directory, CancellationToken token)
    {
        var executableHash = await FileSha256(bundledHelper, token);
        var extension = "";
        var target = Path.Combine(directory, "minirp-cef-" + executableHash);
        if (BundledModule is { } module)
        {
            var moduleHash = await FileSha256(module, token);
            // The Windows sandbox bootstrap loads a DLL with the same basename as its executable.
            var combinedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(executableHash + moduleHash)));
            target = Path.Combine(directory, "minirp-cef-" + combinedHash);
            await CopyHostFile(directory, module, target + ".dll", moduleHash, token);
            extension = ".exe";
        }
        await CopyHostFile(directory, bundledHelper, target + extension, executableHash, token);
        return target + extension;
    }

    private static async Task CopyHostFile(string directory, string sourcePath, string target, string hash,
        CancellationToken token)
    {
        if (File.Exists(target) && await FileSha256(target, token) == hash) return;
        var temporary = Path.Combine(directory, ".host-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            await using (var copy = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous))
                await source.CopyToAsync(copy, token);
            if (await FileSha256(temporary, token) != hash)
                throw new InvalidDataException("The bundled CEF host changed during setup.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, PrivateDirectory);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<string> FileSha256(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, PrivateDirectory);
    }

    private void CleanStage(string stage)
    {
        stage = Path.GetFullPath(stage);
        if (Path.GetDirectoryName(stage) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot)) ||
            !Path.GetFileName(stage).StartsWith(".cef-stage-", StringComparison.Ordinal))
            return;
        try { Directory.Delete(stage, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
