package com.minichat.app.core

import java.security.MessageDigest
import java.util.Base64
import org.bouncycastle.asn1.nist.NISTObjectIdentifiers
import org.bouncycastle.cert.X509CertificateHolder
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cms.CMSProcessableByteArray
import org.bouncycastle.cms.CMSSignedData
import org.bouncycastle.cms.jcajce.JcaSimpleSignerInfoVerifierBuilder
import org.bouncycastle.util.Selector
import org.json.JSONObject

internal object PluginVerifier {
    fun verify(request: JSONObject): JSONObject {
        val decoder = Base64.getDecoder()
        val content = CMSProcessableByteArray(decoder.decode(request.getString("payload")))
        val signed = CMSSignedData(content, decoder.decode(request.getString("signature")))
        val signer = signed.signerInfos.signers.singleOrNull()
        check(
            signed.isDetachedSignature &&
                signer != null &&
                signer.digestAlgOID == NISTObjectIdentifiers.id_sha256.id
        ) {
            "The plugin requires a detached SHA-256 signature and one signer."
        }
        @Suppress("UNCHECKED_CAST") val selector = signer.sid as Selector<X509CertificateHolder>
        val certificate =
            signed.certificates.getMatches(selector).singleOrNull()
                ?: error("The signer certificate is missing.")
        val key = JcaX509CertificateConverter().getCertificate(certificate).publicKey
        check(signer.verify(JcaSimpleSignerInfoVerifierBuilder().build(key))) {
            "The plugin signature is invalid."
        }
        val fingerprint = MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
        return JSONObject().put("signer", fingerprint.joinToString("") { "%02X".format(it) })
    }
}
