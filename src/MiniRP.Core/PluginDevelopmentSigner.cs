using System.IO.Compression;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace MiniRP.Core;

internal sealed record PluginDevelopmentSigner(AsymmetricKeyParameter Key, X509Certificate Certificate)
{
    private const string Algorithm = "SHA256WITHECDSA";

    public byte[] Sign(ZipArchive archive) => PluginSignature.Sign(archive, Key, Certificate, Algorithm);

    public static PluginDevelopmentSigner Open(string path)
    {
        if (File.Exists(path))
        {
            var document = JsonValues.ParseObject(File.ReadAllText(path));
            return new(PrivateKeyFactory.CreateKey(Convert.FromBase64String(document.GetString("key"))),
                new X509CertificateParser().ReadCertificate(Convert.FromBase64String(document.GetString("certificate"))));
        }

        var random = new SecureRandom();
        var keys = new ECKeyPairGenerator();
        keys.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, random));
        var pair = keys.GenerateKeyPair();
        var name = new X509Name("CN=MiniRP development plugin");
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(new BigInteger(128, random).Add(BigInteger.One));
        generator.SetIssuerDN(name);
        generator.SetSubjectDN(name);
        generator.SetNotBefore(DateTime.UtcNow.AddMinutes(-5));
        generator.SetNotAfter(DateTime.UtcNow.AddYears(10));
        generator.SetPublicKey(pair.Public);
        var certificate = generator.Generate(new Asn1SignatureFactory(Algorithm, pair.Private, random));
        AtomicFile.Write(path, JsonValues.Serialize(new JsonObject
        {
            ["key"] = Convert.ToBase64String(PrivateKeyInfoFactory.CreatePrivateKeyInfo(pair.Private).GetEncoded()),
            ["certificate"] = Convert.ToBase64String(certificate.GetEncoded()),
        }));
        return new(pair.Private, certificate);
    }
}
