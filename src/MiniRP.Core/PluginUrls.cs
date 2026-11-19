namespace MiniRP.Core;

internal static class PluginUrls
{
    public const string Scheme = "minirp";
    private const string PreviousScheme = "minichat";

    public static bool IsScheme(string scheme) => scheme is Scheme or PreviousScheme;

    public static bool IsReservedPackage(string package) =>
        package.StartsWith(Scheme + ".", StringComparison.Ordinal) ||
        package.StartsWith(PreviousScheme + ".", StringComparison.Ordinal);

    // Third-party package names are signed identities. Only built-in names have aliases.
    public static string CanonicalPackage(string package) => package switch
    {
        "minichat.app" => "minirp.app",
        "minichat.base" => "minirp.base",
        "minichat.licenses" => "minirp.licenses",
        "minichat.legacy" => "minirp.legacy",
        _ => package
    };

    public static string Route(string value, string host)
    {
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        var prefix = host + "/";
        return separator > 0 && IsScheme(value[..separator]) &&
            value.AsSpan(separator + 3).StartsWith(prefix, StringComparison.Ordinal)
            ? value[(separator + 3 + prefix.Length)..] : "";
    }

    public static bool IsLegacyThumbnail(string url) => Route(url, "assets").Split('/') is [var package, "attachment-thumbnail"] &&
        CanonicalPackage(package) == "minirp.legacy";
}
