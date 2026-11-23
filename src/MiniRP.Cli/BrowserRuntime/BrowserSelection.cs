using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MiniRP.Cli;

internal enum BrowserFamily { Chromium, WebView2, Cef }

internal sealed partial record BrowserSelection(BrowserFamily Family, string Executable, bool Snap = false, string? RuntimeBinary = null)
{
    public const int MinimumMajor = 135;
    public string Name => Family switch
    {
        BrowserFamily.WebView2 => "WebView2 Evergreen",
        BrowserFamily.Cef => "CEF",
        _ => Snap ? "Chromium (Snap)" : "Chromium"
    };

    public DirectoryInfo NewProfile()
    {
        if (!Snap) return Directory.CreateTempSubdirectory("minirp-browser-profile-");
        // Strictly confined Chromium cannot see the host's /tmp or arbitrary hidden home directories.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parent = Path.Combine(home, "snap", "chromium", "common", "MiniRP");
        return Directory.CreateDirectory(Path.Combine(parent, "profile-" + Guid.NewGuid().ToString("N")));
    }

    public BrowserProcess StartWindow(string profile, string url, bool probe = false)
    {
        if (Family != BrowserFamily.Chromium)
            return StartEmbedded("window", profile, url);
        var args = new List<string>
        {
            "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
            "--disable-background-mode", "--disable-extensions", "--app=" + url
        };
        if (probe) args.AddRange(["--window-position=-20000,-20000", "--window-size=320,200", "--remote-debugging-port=0"]);
        return BrowserProcess.Start(Executable, args.ToArray());
    }

    public BrowserProcess StartEmbedded(params string[] arguments) => RuntimeBinary is { } webView
        ? BrowserProcess.StartWebView2(Executable, webView, arguments)
        : BrowserProcess.Start(Executable, arguments);

    public static bool HasDisplay => !OperatingSystem.IsLinux() ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static async Task<IReadOnlyList<BrowserSelection>> Discover(string root, string preferred, CancellationToken token)
    {
        var found = new List<(string Channel, BrowserSelection Browser)>();
        void Add(string channel, BrowserFamily family, string? file, bool snap = false)
        {
            if (file is null || !Path.IsPathFullyQualified(file) || !File.Exists(file)) return;
            if (Path.GetFileName(file).Contains("headless_shell", StringComparison.OrdinalIgnoreCase)) return;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!found.Any(item => item.Browser.Executable.Equals(file, comparison))) found.Add((channel, new(family, file, snap)));
        }
        if (OperatingSystem.IsWindows())
        {
            Add("webview2", BrowserFamily.WebView2, Helper("minirp-webview2"));
            foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86 })
            {
                var basePath = Environment.GetFolderPath(folder);
                if (basePath.Length == 0) continue;
                Add("chrome", BrowserFamily.Chromium, Path.Combine(basePath, "Google", "Chrome", "Application", "chrome.exe"));
                Add("msedge", BrowserFamily.Chromium, Path.Combine(basePath, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            try
            {
                using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
                lookup.CancelAfter(TimeSpan.FromSeconds(8));
                var node = await BrowserProcess.Read(Node, lookup.Token, "-e",
                    "console.log(require(process.argv[1]).chromium.executablePath())", PlaywrightPath(root, "playwright"));
                Add("chromium", BrowserFamily.Chromium, node.Trim());
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
        }
        if (OperatingSystem.IsLinux())
        {
            foreach (var name in new[] { "chromium", "chromium-browser", "google-chrome-stable", "google-chrome", "microsoft-edge" })
            {
                var file = FindOnPath(name);
                var snap = file is not null && (file.StartsWith("/snap/bin/", StringComparison.Ordinal) || IsSnapWrapper(file));
                Add(name.StartsWith("chromium", StringComparison.Ordinal) ? "chromium" : name.StartsWith("google", StringComparison.Ordinal)
                    ? "chrome" : "msedge", BrowserFamily.Chromium, file, snap);
            }
            Add("chromium", BrowserFamily.Chromium, "/snap/bin/chromium", snap: true);
        }
        return found.OrderBy(item => item.Channel == preferred ? 0 : item.Channel == "webview2" ? 1 : 2)
            .ThenBy(item => OperatingSystem.IsWindows() && item.Channel == "chromium" ? 1 : 0)
            .Select(item => item.Browser).ToArray();
    }

    public async Task<bool> IsRecentChromium(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
            return FileVersionInfo.GetVersionInfo(Executable).FileMajorPart >= MinimumMajor;
        var output = await BrowserProcess.Read(Executable, token, "--version");
        var version = ChromiumVersion().Match(output);
        return version.Success && int.TryParse(version.Groups[1].Value, out var major) && major >= MinimumMajor;
    }

    private static bool IsSnapWrapper(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            if (stream.Length > 32 * 1024) return false;
            using var reader = new StreamReader(stream);
            var source = reader.ReadToEnd();
            return source.StartsWith("#!", StringComparison.Ordinal) && source.Contains("/snap/bin/chromium", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    public static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified).Select(entry => Path.Combine(entry, name)).FirstOrDefault(File.Exists);

    public static string Helper(string name) =>
        Path.Combine(AppContext.BaseDirectory, "browser", OperatingSystem.IsWindows() ? name + ".exe" : name);

    public static string Node => File.Exists(Helper("node")) ? Helper("node") : "node";
    public static string PlaywrightPath(string root, string path)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "node_modules", path);
        return Path.Exists(bundled) ? bundled : Path.Combine(root, "node_modules", path);
    }

    [GeneratedRegex(@"\b(?:Chromium|Chrome(?: for Testing)?|Edge)\s+(\d+)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChromiumVersion();
}
