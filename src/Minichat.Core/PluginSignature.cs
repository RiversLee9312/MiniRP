using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;

namespace Minichat.Core;

internal static class PluginSignature
{
    internal const string EntryName = "META-INF/minichat.p7s";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    internal static (string Signer, Dictionary<string, string> Hashes) Verify(ZipArchive archive,
        Func<byte[], byte[], string>? platformVerifier)
    {
        var signature = archive.GetEntry(EntryName) ?? throw new InvalidDataException("The plugin archive must be signed.");
        if (signature.Length is < 1 or > 65536) throw new InvalidDataException("Invalid plugin signature size.");
        using var bytes = new MemoryStream();
        using (var input = signature.Open())
        {
            var buffer = new byte[8192];
            for (var count = input.Read(buffer); count > 0; count = input.Read(buffer))
            {
                if (bytes.Length + count > 65536) throw new InvalidDataException("Plugin signature is too large.");
                bytes.Write(buffer, 0, count);
            }
        }
        var (payload, hashes) = Payload(archive);
        if (platformVerifier is not null) return (platformVerifier(payload, bytes.ToArray()), hashes);
        return (VerifyCms(payload, bytes.ToArray()), hashes);
    }

    private static string VerifyCms(byte[] payload, byte[] signature)
    {
        try
        {
            var signed = new CmsSignedData(new CmsProcessableByteArray(payload), signature);
            var signers = signed.GetSignerInfos().GetSigners().ToArray();
            if (!signed.IsDetachedSignature || signers.Length != 1 || signers[0].DigestAlgorithmID.Algorithm.Id != Sha256Oid)
                throw new InvalidDataException("The plugin requires a detached SHA-256 signature and one signer.");
            var signer = signers[0];
            var certificate = signed.GetCertificates().EnumerateMatches(signer.SignerID).SingleOrDefault() ??
                throw new InvalidDataException("The signer certificate is missing.");
            if (!signer.Verify(certificate.GetPublicKey())) throw new CryptographicException("The plugin signature is invalid.");
            var digest = new Sha256Digest();
            var raw = certificate.GetEncoded();
            digest.BlockUpdate(raw, 0, raw.Length);
            return Finish(digest);
        }
        catch (CmsException error) { throw new CryptographicException("The plugin signature is invalid.", error); }
    }

    internal static (byte[] Bytes, Dictionary<string, string> Hashes) Payload(ZipArchive archive)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var content = new StringBuilder("MCPLUGIN/2\n");
        foreach (var entry in archive.Entries.OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            if (entry.FullName == EntryName) continue;
            var hash = Hash(entry);
            hashes.Add(entry.FullName, hash);
            content.Append(hash).Append('\t').Append(entry.FullName).Append('\n');
        }
        return (Encoding.UTF8.GetBytes(content.ToString()), hashes);
    }

    internal static byte[] Sign(ZipArchive archive, X509Certificate2 certificate)
    {
        var (payload, _) = Payload(archive);
        using var key = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey() ??
            throw new ArgumentException("The certificate must have an RSA or ECDSA private key.");
        var encodedKey = key.ExportPkcs8PrivateKey();
        try
        {
            var signingCertificate = new X509CertificateParser().ReadCertificate(certificate.RawData);
            var signer = new Asn1SignatureFactory(key is RSA ? "SHA256WITHRSA" : "SHA256WITHECDSA", PrivateKeyFactory.CreateKey(encodedKey));
            var generator = new CmsSignedDataGenerator();
            generator.AddSignerInfoGenerator(new SignerInfoGeneratorBuilder().Build(signer, signingCertificate));
            generator.AddCertificates(CollectionUtilities.CreateStore(new[] { signingCertificate }));
            return generator.Generate(new CmsProcessableByteArray(payload), encapsulate: false).GetEncoded();
        }
        finally { CryptographicOperations.ZeroMemory(encodedKey); }
    }

    internal static string Hash(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        var hash = new Sha256Digest();
        var buffer = new byte[65536];
        long size = 0;
        for (var count = input.Read(buffer); count > 0; count = input.Read(buffer))
        {
            size += count;
            if (size > entry.Length) throw new InvalidDataException("A ZIP entry exceeds its declared size.");
            hash.BlockUpdate(buffer, 0, count);
        }
        if (size != entry.Length) throw new InvalidDataException("A ZIP entry is truncated.");
        return Finish(hash);
    }

    private static string Finish(Sha256Digest hash)
    {
        var buffer = new byte[hash.GetDigestSize()];
        hash.DoFinal(buffer, 0);
        return Convert.ToHexString(buffer);
    }
}
