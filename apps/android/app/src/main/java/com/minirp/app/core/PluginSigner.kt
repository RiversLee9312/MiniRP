package com.minirp.app.core

import android.util.AtomicFile
import java.io.File
import java.io.FileNotFoundException
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.PKCS8EncodedKeySpec
import java.time.Instant
import java.time.ZoneOffset
import java.util.Base64
import java.util.Date
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.X509CertificateHolder
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.cms.CMSProcessableByteArray
import org.bouncycastle.cms.CMSSignedDataGenerator
import org.bouncycastle.cms.jcajce.JcaSignerInfoGeneratorBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import org.bouncycastle.operator.jcajce.JcaDigestCalculatorProviderBuilder
import org.json.JSONObject

internal object PluginSigner {
    private const val algorithm = "SHA256withECDSA"

    fun sign(workspace: File, request: JSONObject): JSONObject {
        val path = File(request.getString("path")).canonicalFile
        val projects = workspace.resolve("preset_sessions").canonicalFile
        val segments = path.relativeTo(projects).path.split(File.separatorChar)
        require(
            segments.size == 4 && ".." !in segments && segments[1] == "plugin_agents" &&
                segments[3] == "signer.json" && path.parentFile?.isDirectory == true
        ) { "Invalid plugin signer path." }

        val decoder = Base64.getDecoder()
        val payload = decoder.decode(request.getString("payload"))
        val (key, certificate) = open(path)
        val signature = CMSSignedDataGenerator().apply {
            val signer = JcaContentSignerBuilder(algorithm).build(key)
            addSignerInfoGenerator(
                JcaSignerInfoGeneratorBuilder(JcaDigestCalculatorProviderBuilder().build())
                    .build(signer, certificate)
            )
            addCertificate(certificate)
        }.generate(CMSProcessableByteArray(payload), false).encoded
        return JSONObject().put("signature", Base64.getEncoder().encodeToString(signature))
    }

    private fun open(path: File): Pair<PrivateKey, X509CertificateHolder> = synchronized(this) {
        val storage = AtomicFile(path)
        val record = try {
            JSONObject(storage.readFully().toString(Charsets.UTF_8))
        } catch (missing: FileNotFoundException) {
            if (path.exists()) throw missing
            create(storage)
            JSONObject(storage.readFully().toString(Charsets.UTF_8))
        }
        val decoder = Base64.getDecoder()
        val key = KeyFactory.getInstance("EC").generatePrivate(
            PKCS8EncodedKeySpec(decoder.decode(record.getString("key")))
        )
        key to X509CertificateHolder(decoder.decode(record.getString("certificate")))
    }

    private fun create(storage: AtomicFile) {
        val random = SecureRandom()
        val keys = KeyPairGenerator.getInstance("EC").apply {
            initialize(ECGenParameterSpec("secp256r1"), random)
        }.generateKeyPair()
        val name = X500Name("CN=MiniRP development plugin")
        val now = Instant.now()
        val certificate = JcaX509v3CertificateBuilder(
            name,
            BigInteger(128, random).add(BigInteger.ONE),
            Date.from(now.minusSeconds(300)),
            Date.from(now.atOffset(ZoneOffset.UTC).plusYears(10).toInstant()),
            name,
            keys.public,
        ).build(JcaContentSignerBuilder(algorithm).setSecureRandom(random).build(keys.private))
        val encoder = Base64.getEncoder()
        val record = JSONObject()
            .put("key", encoder.encodeToString(keys.private.encoded))
            .put("certificate", encoder.encodeToString(certificate.encoded))
        val output = storage.startWrite()
        try {
            output.write(record.toString().toByteArray(Charsets.UTF_8))
            storage.finishWrite(output)
        } catch (error: Exception) {
            storage.failWrite(output)
            throw error
        }
    }
}
