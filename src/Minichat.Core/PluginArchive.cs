using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;

namespace Minichat.Core;

/// <summary>Packages a plugin with the same parser and signature contract used by both hosts.</summary>
public static class PluginArchive
{
    public static void Pack(string sourceDirectory, string destinationFile, X509Certificate2 certificate)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationFile);
        var root = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destination.EndsWith(".plugin", StringComparison.OrdinalIgnoreCase) || destination.StartsWith(root, comparer))
            throw new ArgumentException("Choose a .plugin destination outside the source directory.");
        if (!certificate.HasPrivateKey) throw new ArgumentException("The certificate has no private signing key.");
        if (!File.Exists(Path.Combine(source, "manifest.json"))) throw new FileNotFoundException("The source needs manifest.json.");

        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                long total = 0;
                var count = 0;
                foreach (var file in Directory.EnumerateFiles(source, "*", options))
                {
                    var name = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (!PluginPackage.SafePath(name) || name.Equals(PluginSignature.EntryName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid package entry: " + name);
                    if (++count >= 256 || (total += new FileInfo(file).Length) > PluginPackage.MaxPackageBytes - 65536)
                        throw new InvalidDataException("The plugin exceeds the archive size or entry limit.");
                    archive.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
                }
            }
            byte[] signature;
            using (var archive = ZipFile.OpenRead(temp)) signature = PluginSignature.Sign(archive, certificate);
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Update))
            using (var output = archive.CreateEntry(PluginSignature.EntryName).Open()) output.Write(signature);
            Verify(temp);
            File.Move(temp, destination, overwrite: false);
        }
        finally { File.Delete(temp); }
    }

    public static (string PackageName, string Signer) Verify(string path)
    {
        using var input = File.OpenRead(path);
        var package = PluginPackage.Read(input);
        return (package.PackageName, package.Signer);
    }
}
