using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker.Signing;

/// <summary>
/// Builds the detached CMS SignedData (RFC 5652) that goes into a PDF signature's /Contents, with the CAdES signed
/// attributes PAdES requires. Encoded with System.Formats.Asn1 and signed with the certificate's RSA or ECDSA key.
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
    /// Signs <paramref name="digest"/> (the hash of the PDF's signed byte ranges). <paramref name="timestamp"/>, when
    /// given, receives the signature value and returns an RFC 3161 timestamp token to embed.
    /// </summary>
    public static byte[] Sign(byte[] digest, X509Certificate2 cert, IReadOnlyList<X509Certificate2> certs, HashAlgorithmName hash,
        bool pades, DateTimeOffset signingTime, Func<byte[], byte[]>? timestamp)
    {
        string hashOid = HashOid(hash);
        using var rsa = cert.GetRSAPrivateKey();
        using var ecdsa = rsa == null ? cert.GetECDsaPrivateKey() : null;
        if (rsa == null && ecdsa == null)
            throw new ArgumentException("The signing certificate has no RSA or ECDSA private key. Load it from a .pfx file or a certificate store entry that includes the key.");

        // Signed attributes, DER-encoded as a SET for signing; the same content is stored with an implicit [0] tag.
        var attrs = new AsnWriter(AsnEncodingRules.DER);
        using (attrs.PushSetOf())
        {
            WriteAttribute(attrs, IdContentType, w => w.WriteObjectIdentifier(IdData));
            if (!pades)
                WriteAttribute(attrs, IdSigningTime, w => w.WriteUtcTime(signingTime.ToUniversalTime()));
            WriteAttribute(attrs, IdMessageDigest, w => w.WriteOctetString(digest));
            WriteAttribute(attrs, IdSigningCertificateV2, w => WriteSigningCertificateV2(w, cert, hash));
        }
        byte[] signedAttrs = attrs.Encode();

        byte[] signature = rsa != null
            ? rsa.SignData(signedAttrs, hash, RSASignaturePadding.Pkcs1)
            : ecdsa!.SignData(signedAttrs, hash, DSASignatureFormat.Rfc3279DerSequence);
        byte[]? token = timestamp?.Invoke(signature);

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
                    if (rsa != null) WriteAlgorithm(w, IdRsaEncryption, nullParams: true);
                    else WriteAlgorithm(w, EcdsaOid(hash), nullParams: false);
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
