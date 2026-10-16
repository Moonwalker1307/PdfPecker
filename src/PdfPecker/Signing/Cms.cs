using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker.Signing;

/// <summary>
/// Builds the detached CMS SignedData (RFC 5652) that goes into a PDF signature's /Contents, with the CAdES signed
/// attributes PAdES requires. Encoded with System.Formats.Asn1 and signed with the certificate's RSA or ECDSA key, or by
/// an external signer that holds the key elsewhere.
/// </summary>
internal static class Cms
{
    internal const string IdData = "1.2.840.113549.1.7.1";
    internal const string IdSignedData = "1.2.840.113549.1.7.2";
    private const string IdContentType = "1.2.840.113549.1.9.3";
    private const string IdMessageDigest = "1.2.840.113549.1.9.4";
    private const string IdSigningTime = "1.2.840.113549.1.9.5";
    private const string IdSigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    private const string IdTimestampToken = "1.2.840.113549.1.9.16.2.14";
    private const string IdRsaEncryption = "1.2.840.113549.1.1.1";
    private const string IdRsaPss = "1.2.840.113549.1.1.10";
    private const string IdMgf1 = "1.2.840.113549.1.1.8";

    internal static string HashOid(HashAlgorithmName h) => h.Name switch
    {
        "SHA256" => "2.16.840.1.101.3.4.2.1",
        "SHA384" => "2.16.840.1.101.3.4.2.2",
        "SHA512" => "2.16.840.1.101.3.4.2.3",
        _ => throw new NotSupportedException("Signatures support SHA-256, SHA-384 and SHA-512, not " + h.Name + "."),
    };

    private static string EcdsaOid(HashAlgorithmName h) => h.Name switch
    {
        "SHA256" => "1.2.840.10045.4.3.2",
        "SHA384" => "1.2.840.10045.4.3.3",
        _ => "1.2.840.10045.4.3.4",
    };

    /// <summary>
    /// The DER-encoded signed attributes (a SET) for <paramref name="digest"/>, the hash of the PDF's signed byte ranges.
    /// These bytes are what the signer's key signs.
    /// </summary>
    public static byte[] SignedAttributes(byte[] digest, X509Certificate2 cert, HashAlgorithmName hash, bool pades, DateTimeOffset signingTime)
    {
        var attrs = new AsnWriter(AsnEncodingRules.DER);
        using (attrs.PushSetOf())
        {
            WriteAttribute(attrs, IdContentType, w => w.WriteObjectIdentifier(IdData));
            if (!pades)
                WriteAttribute(attrs, IdSigningTime, w => w.WriteUtcTime(signingTime.ToUniversalTime()));
            WriteAttribute(attrs, IdMessageDigest, w => w.WriteOctetString(digest));
            WriteAttribute(attrs, IdSigningCertificateV2, w => WriteSigningCertificateV2(w, cert, hash));
        }
        return attrs.Encode();
    }

    /// <summary>Signs <paramref name="signedAttrs"/> with the certificate's own RSA (PKCS#1 v1.5) or ECDSA private key.</summary>
    public static (byte[] Signature, PdfSignatureAlgorithm Algorithm) SignWithPrivateKey(X509Certificate2 cert, byte[] signedAttrs, HashAlgorithmName hash)
    {
        using var rsa = cert.GetRSAPrivateKey();
        if (rsa != null) return (rsa.SignData(signedAttrs, hash, RSASignaturePadding.Pkcs1), PdfSignatureAlgorithm.RsaPkcs1);
        using var ecdsa = cert.GetECDsaPrivateKey();
        if (ecdsa != null) return (ecdsa.SignData(signedAttrs, hash, DSASignatureFormat.Rfc3279DerSequence), PdfSignatureAlgorithm.Ecdsa);
        throw new ArgumentException("The signing certificate has no RSA or ECDSA private key. Load it from a .pfx file or a certificate store entry that includes the key.");
    }

    /// <summary>
    /// Checks that the certificate's public key suits <paramref name="algorithm"/>, so a mismatch fails before the external
    /// signer is called (which may cost money or a PIN prompt).
    /// </summary>
    public static void CheckKeyType(X509Certificate2 cert, PdfSignatureAlgorithm algorithm)
    {
        bool rsa = algorithm is PdfSignatureAlgorithm.RsaPkcs1 or PdfSignatureAlgorithm.RsaPss;
        string oid = cert.PublicKey.Oid.Value ?? "";
        bool isRsa = oid == IdRsaEncryption;
        bool isEc = oid == "1.2.840.10045.2.1";
        if (rsa && !isRsa) throw new ArgumentException($"The external signer uses {algorithm}, but the certificate's public key is not an RSA key.");
        if (!rsa && !isEc) throw new ArgumentException("The external signer uses ECDSA, but the certificate's public key is not an EC key.");
    }

    /// <summary>
    /// Verifies a signature returned by an external signer against the certificate's public key and returns it in the
    /// form CMS stores. An external signer that uses the wrong key, the wrong padding or the wrong hash would otherwise
    /// produce a PDF that every viewer reports as broken, long after the mistake was made. ECDSA signatures are accepted
    /// both DER-encoded (as AWS KMS and PKCS#11 wrappers often return them) and as raw r||s (as Azure Key Vault and
    /// .NET's own <see cref="ECDsa.SignHash(byte[])"/> return them); CMS needs DER.
    /// </summary>
    public static byte[] CheckExternalSignature(X509Certificate2 cert, PdfSignatureAlgorithm algorithm, byte[] signedAttrs, HashAlgorithmName hash, byte[]? signature)
    {
        if (signature == null || signature.Length == 0) throw new PdfSignatureException("The external signer returned no signature.");
        if (algorithm == PdfSignatureAlgorithm.Ecdsa)
        {
            using var ec = cert.GetECDsaPublicKey() ?? throw new PdfSignatureException("The certificate has no EC public key.");
            if (ec.VerifyData(signedAttrs, signature, hash, DSASignatureFormat.Rfc3279DerSequence)) return signature;
            if (ec.VerifyData(signedAttrs, signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return P1363ToDer(signature);
        }
        else
        {
            using var rsa = cert.GetRSAPublicKey() ?? throw new PdfSignatureException("The certificate has no RSA public key.");
            var padding = algorithm == PdfSignatureAlgorithm.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
            if (rsa.VerifyData(signedAttrs, signature, hash, padding)) return signature;
        }
        throw new PdfSignatureException("The external signer's signature does not verify with the certificate's public key. "
            + "Check that the signer uses the certificate's key, the declared algorithm and the hash algorithm of the request.");
    }

    private static byte[] P1363ToDer(byte[] signature)
    {
        int half = signature.Length / 2;
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteIntegerUnsigned(Minimal(signature.AsSpan(0, half)));
            w.WriteIntegerUnsigned(Minimal(signature.AsSpan(half)));
        }
        return w.Encode();

        // r and s are fixed-width in P1363, so about one signature in 128 has a leading zero byte, which DER forbids.
        static ReadOnlySpan<byte> Minimal(ReadOnlySpan<byte> value)
        {
            int i = 0;
            while (i < value.Length - 1 && value[i] == 0) i++;
            return value.Slice(i);
        }
    }

    /// <summary>
    /// The detached CMS SignedData around a finished signature. <paramref name="token"/>, when given, is an RFC 3161
    /// timestamp token over the signature value, stored as an unsigned attribute.
    /// </summary>
    public static byte[] Assemble(byte[] signedAttrs, byte[] signature, PdfSignatureAlgorithm algorithm, byte[]? token,
        X509Certificate2 cert, IReadOnlyList<X509Certificate2> certs, HashAlgorithmName hash)
    {
        string hashOid = HashOid(hash);
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence()) // ContentInfo
        {
            w.WriteObjectIdentifier(IdSignedData);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            using (w.PushSequence()) // SignedData
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) WriteAlgorithm(w, hashOid, nullParams: false);
                using (w.PushSequence()) w.WriteObjectIdentifier(IdData); // detached: no content
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                    foreach (var c in certs) w.WriteEncodedValue(c.RawData);
                using (w.PushSetOf())
                using (w.PushSequence()) // SignerInfo
                {
                    w.WriteInteger(1);
                    WriteIssuerAndSerial(w, cert, wrapIssuerInGeneralNames: false);
                    WriteAlgorithm(w, hashOid, nullParams: false);
                    // Re-tag the SET as [0] IMPLICIT.
                    var tagged = (byte[])signedAttrs.Clone();
                    tagged[0] = 0xA0;
                    w.WriteEncodedValue(tagged);
                    switch (algorithm)
                    {
                        case PdfSignatureAlgorithm.RsaPkcs1: WriteAlgorithm(w, IdRsaEncryption, nullParams: true); break;
                        case PdfSignatureAlgorithm.RsaPss: WritePssAlgorithm(w, hash); break;
                        default: WriteAlgorithm(w, EcdsaOid(hash), nullParams: false); break;
                    }
                    w.WriteOctetString(signature);
                    if (token != null)
                    {
                        using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true)))
                            WriteAttribute(w, IdTimestampToken, v => v.WriteEncodedValue(token));
                    }
                }
            }
        }
        return w.Encode();
    }

    // RSASSA-PSS-params (RFC 4055): the hash, MGF1 with the same hash, and a salt as long as the hash, which is what .NET,
    // Azure Key Vault (PS256/384/512) and AWS KMS (RSASSA_PSS_SHA_*) produce. Every field is written because the defaults
    // are SHA-1 and a 20-byte salt.
    private static void WritePssAlgorithm(AsnWriter w, HashAlgorithmName hash)
    {
        string hashOid = HashOid(hash);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(IdRsaPss);
            using (w.PushSequence())
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                    WriteAlgorithm(w, hashOid, nullParams: true);
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true)))
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier(IdMgf1);
                    WriteAlgorithm(w, hashOid, nullParams: true);
                }
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true)))
                    w.WriteInteger(HashLength(hash));
            }
        }
    }

    internal static int HashLength(HashAlgorithmName hash) => hash.Name switch
    {
        "SHA384" => 48,
        "SHA512" => 64,
        _ => 32,
    };

    private static void WriteAttribute(AsnWriter w, string oid, Action<AsnWriter> value)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            using (w.PushSetOf()) value(w);
        }
    }

    internal static void WriteAlgorithm(AsnWriter w, string oid, bool nullParams)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            if (nullParams) w.WriteNull();
        }
    }

    // SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 }
    // ESSCertIDv2 ::= SEQUENCE { hashAlgorithm DEFAULT sha256, certHash OCTET STRING, issuerSerial IssuerSerial }
    private static void WriteSigningCertificateV2(AsnWriter w, X509Certificate2 cert, HashAlgorithmName hash)
    {
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
        {
            if (hash != HashAlgorithmName.SHA256) WriteAlgorithm(w, HashOid(hash), nullParams: false);
            w.WriteOctetString(HashData(hash, cert.RawData));
            WriteIssuerAndSerial(w, cert, wrapIssuerInGeneralNames: true);
        }
    }

    private static void WriteIssuerAndSerial(AsnWriter w, X509Certificate2 cert, bool wrapIssuerInGeneralNames)
    {
        using (w.PushSequence())
        {
            if (wrapIssuerInGeneralNames)
            {
                // GeneralNames ::= SEQUENCE OF GeneralName; directoryName [4] Name (explicit: Name is a CHOICE).
                using (w.PushSequence())
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
                    w.WriteEncodedValue(cert.IssuerName.RawData);
            }
            else w.WriteEncodedValue(cert.IssuerName.RawData);
            w.WriteInteger(Convert.FromHexString(cert.SerialNumber));
        }
    }

    internal static byte[] HashData(HashAlgorithmName hash, ReadOnlySpan<byte> data) => hash.Name switch
    {
        "SHA256" => SHA256.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => throw new NotSupportedException(hash.Name),
    };

    /// <summary>The signing certificate followed by its chain up to (not including) a self-signed root.</summary>
    public static List<X509Certificate2> CertificateChain(X509Certificate2 cert, IEnumerable<X509Certificate2>? extra)
    {
        var list = new List<X509Certificate2> { cert };
        if (extra != null)
        {
            foreach (var c in extra)
                if (!list.Any(x => x.RawData.AsSpan().SequenceEqual(c.RawData))) list.Add(c);
            return list;
        }
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            chain.Build(cert);
            foreach (var el in chain.ChainElements)
            {
                var c = el.Certificate;
                if (c.RawData.AsSpan().SequenceEqual(cert.RawData)) continue;
                if (c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) continue; // root
                list.Add(c);
            }
        }
        catch (CryptographicException)
        {
            // No chain: embed the signing certificate alone.
        }
        return list;
    }
}
