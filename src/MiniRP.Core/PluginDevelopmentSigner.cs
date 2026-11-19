#if !MINIRP_ANDROID_BIONIC
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
#endif

namespace MiniRP.Core;

internal static class PluginDevelopmentSigner
{
#if MINIRP_ANDROID_BIONIC
    public static async Task<byte[]> Sign(string path, byte[] payload, HostPort port, CancellationToken ct)
    {
        var reply = await port.CallHost("plugin_sign", new()
        {
            ["path"] = path,
            ["payload"] = Convert.ToBase64String(payload)
        }, ct, TimeSpan.FromSeconds(15));
        var signature = Convert.FromBase64String(reply.GetString("signature"));
        if (signature.Length is < 1 or > 65536) throw new InvalidDataException("Invalid plugin signature size.");
        return signature;
    }
#else
    private const string Algorithm = "SHA256WITHECDSA";

    public static Task<byte[]> Sign(string path, byte[] payload, HostPort port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (key, certificate) = Open(path);
        return Task.FromResult(PluginSignature.Sign(payload, key, certificate, Algorithm));
    }

    private static (AsymmetricKeyParameter Key, X509Certificate Certificate) Open(string path)
    {
        if (File.Exists(path))
        {
            var document = JsonValues.ParseObject(File.ReadAllText(path));
            return (PrivateKeyFactory.CreateKey(Convert.FromBase64String(document.GetString("key"))),
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
        return (pair.Private, certificate);
    }
#endif
}
