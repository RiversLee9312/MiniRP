using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Minichat.Core;

try
{
    switch (args)
    {
        case ["certificate", var path]:
        {
            var password = Password();
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Minichat Plugin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
            using var file = File.Open(path, FileMode.CreateNew);
            file.Write(certificate.Export(X509ContentType.Pfx, password));
            break;
        }
        case ["pack", var directory, var certificatePath, var output]:
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, Password(),
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            PluginArchive.Pack(directory, output, certificate);
            PrintIdentity(output);
            break;
        }
        case ["verify", var path]:
            PrintIdentity(path);
            break;
        default:
            Console.Error.WriteLine("Usage: certificate <output.pfx> | pack <source-dir> <certificate.pfx> <output.plugin> | verify <file.plugin>");
            return 2;
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static string Password() => Environment.GetEnvironmentVariable("MINICHAT_SIGNING_PASSWORD") is { Length: > 0 } password
    ? password
    : throw new InvalidOperationException("Set MINICHAT_SIGNING_PASSWORD for the signing key.");

static void PrintIdentity(string path)
{
    var (package, signer) = PluginArchive.Verify(path);
    Console.WriteLine(package);
    Console.WriteLine(signer);
}
