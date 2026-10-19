using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker.Signing;

/// <summary>The parts of a CMS SignedData (RFC 5652) that signature verification needs.</summary>
internal sealed class CmsSignedData
{
    public string ContentType = "";
    public byte[]? Content;
    public List<byte[]> Certificates = new();
    public CmsSignerInfo Signer = null!;
}

/// <summary>One SignerInfo, with its signed attributes kept in the DER form the signature covers.</summary>
internal sealed class CmsSignerInfo
{
    public byte[]? IssuerName;
    public BigInteger Serial;
    public byte[]? SubjectKeyId;
    public string DigestOid = "";
    public byte[]? SignedAttributesDer;
    public List<(string Oid, byte[] Value)> SignedAttributes = new();
    public string SignatureOid = "";
    public byte[]? SignatureParameters;
    public byte[] Signature = Array.Empty<byte>();
    public List<(string Oid, byte[] Value)> UnsignedAttributes = new();

    public byte[]? SignedAttribute(string oid) => SignedAttributes.FirstOrDefault(a => a.Oid == oid).Value;
}

/// <summary>
/// Reads and checks CMS signatures with System.Formats.Asn1 and the platform's RSA and ECDSA classes, since the library may
/// not depend on System.Security.Cryptography.Pkcs. Every input is untrusted: malformed data surfaces as
/// <see cref="CryptographicException"/> (including <see cref="AsnContentException"/>), never as anything else.
/// </summary>
internal static class CmsReader
{
    internal const string IdMessageDigest = "1.2.840.113549.1.9.4";
    internal const string IdSigningTime = "1.2.840.113549.1.9.5";
    internal const string IdTimestampToken = "1.2.840.113549.1.9.16.2.14";
    internal const string IdTstInfo = "1.2.840.113549.1.9.16.1.4";
    private const int MaxCertificates = 64;
    private const int MaxAttributes = 64;

    /// <summary>Parses a ContentInfo holding SignedData; bytes after the first DER value (a PDF's zero padding) are ignored.</summary>
    public static CmsSignedData Parse(ReadOnlyMemory<byte> data)
    {
        try
        {
            var ci = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
            if (ci.ReadObjectIdentifier() != Cms.IdSignedData) throw new CryptographicException("The signature is not CMS signed data.");
            var sd = ci.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            var result = new CmsSignedData();
            sd.ReadInteger();
            sd.ReadSetOf(); // digest algorithms, repeated in the SignerInfo
            var encap = sd.ReadSequence();
            result.ContentType = encap.ReadObjectIdentifier();
            if (encap.HasData)
            {
                var explicitContent = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                result.Content = explicitContent.ReadOctetString();
            }
            if (sd.HasData && sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var certs = sd.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
                while (certs.HasData)
                {
                    var encoded = certs.ReadEncodedValue();
                    // Attribute certificates and other choices carry a context tag; only plain certificates matter here.
                    if (encoded.Span[0] == 0x30 && result.Certificates.Count < MaxCertificates) result.Certificates.Add(encoded.ToArray());
                }
            }
            if (sd.HasData && sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
                sd.ReadEncodedValue(); // CRLs
            var signers = sd.ReadSetOf();
            result.Signer = ReadSignerInfo(signers.ReadSequence());
            return result;
        }
        catch (AsnContentException ex)
        {
            throw new CryptographicException("The signature is not valid CMS: " + ex.Message, ex);
        }
    }

    private static CmsSignerInfo ReadSignerInfo(AsnReader si)
    {
        var s = new CmsSignerInfo();
        si.ReadInteger();
        if (si.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            s.SubjectKeyId = si.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
        else
        {
            var ias = si.ReadSequence();
            s.IssuerName = ias.ReadEncodedValue().ToArray();
            s.Serial = ias.ReadInteger();
        }
        s.DigestOid = si.ReadSequence().ReadObjectIdentifier();
        if (si.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            // The signature covers the attributes encoded as a universal SET, not with the [0] tag they are stored with.
            var raw = si.ReadEncodedValue();
            s.SignedAttributesDer = raw.ToArray();
            s.SignedAttributesDer[0] = 0x31;
            s.SignedAttributes = ReadAttributes(new AsnReader(s.SignedAttributesDer, AsnEncodingRules.BER).ReadSetOf());
        }
        var alg = si.ReadSequence();
        s.SignatureOid = alg.ReadObjectIdentifier();
        if (alg.HasData) s.SignatureParameters = alg.ReadEncodedValue().ToArray();
        s.Signature = si.ReadOctetString();
        if (si.HasData && si.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
            s.UnsignedAttributes = ReadAttributes(si.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 1)));
        return s;
    }

    private static List<(string, byte[])> ReadAttributes(AsnReader set)
    {
        var list = new List<(string, byte[])>();
        while (set.HasData && list.Count < MaxAttributes)
        {
            var attr = set.ReadSequence();
            string oid = attr.ReadObjectIdentifier();
            var values = attr.ReadSetOf();
            if (values.HasData) list.Add((oid, values.ReadEncodedValue().ToArray()));
        }
        return list;
    }

    /// <summary>The hash algorithm of a digest OID, or of a signature OID that names its hash.</summary>
    public static HashAlgorithmName? HashFromOid(string oid) => oid switch
    {
        "1.3.14.3.2.26" or "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
        "2.16.840.1.101.3.4.2.1" or "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" or "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" or "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
        _ => null,
    };

    public static byte[] Hash(HashAlgorithmName hash, ReadOnlySpan<byte> data) => hash.Name == "SHA1"
#pragma warning disable CA5350 // SHA-1 only identifies old signatures and DSS entries; it secures nothing new.
        ? SHA1.HashData(data)
#pragma warning restore CA5350
        : Cms.HashData(hash, data);

    /// <summary>The certificate that made the signature, found by issuer and serial number or by subject key identifier.</summary>
    public static X509Certificate2? FindSigner(CmsSignerInfo s, IEnumerable<X509Certificate2> certs)
    {
        foreach (var c in certs)
        {
            if (s.SubjectKeyId != null)
            {
                var ski = c.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
                if (ski != null && ski.SubjectKeyIdentifierBytes.Span.SequenceEqual(s.SubjectKeyId)) return c;
            }
            else if (s.IssuerName != null && c.IssuerName.RawData.AsSpan().SequenceEqual(s.IssuerName)
                && new BigInteger(c.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true) == s.Serial)
                return c;
        }
        return null;
    }

    /// <summary>
    /// Checks a SignerInfo against the hash of the signed content. Returns whether the message-digest attribute matches
    /// (or, without signed attributes, whether the signature itself covers the content) and whether the signature value
    /// verifies with <paramref name="signer"/>'s public key.
    /// </summary>
    public static (bool DigestMatches, bool SignatureValid) Verify(CmsSignerInfo s, byte[] contentDigest, X509Certificate2? signer)
    {
        var digestHash = HashFromOid(s.DigestOid) ?? throw new CryptographicException("Unsupported digest algorithm " + s.DigestOid + ".");
        bool digestMatches;
        byte[] signedHash;
        if (s.SignedAttributesDer != null)
        {
            var md = s.SignedAttribute(IdMessageDigest);
            digestMatches = md != null && new AsnReader(md, AsnEncodingRules.BER).ReadOctetString().AsSpan().SequenceEqual(contentDigest);
            signedHash = Hash(SignatureHash(s, digestHash), s.SignedAttributesDer);
        }
        else
        {
            digestMatches = true; // no attributes: the signature itself is over the content's hash
            signedHash = contentDigest;
        }
        if (signer == null) return (digestMatches, false);
        bool valid = VerifyHash(s, signer, signedHash, SignatureHash(s, digestHash));
        if (s.SignedAttributesDer == null) digestMatches = valid;
        return (digestMatches, valid);
    }

    // Signature OIDs such as sha256WithRSAEncryption name their own hash; plain rsaEncryption uses the digest algorithm.
    private static HashAlgorithmName SignatureHash(CmsSignerInfo s, HashAlgorithmName digestHash)
    {
        if (s.SignatureOid == "1.2.840.113549.1.1.10") return PssHash(s.SignatureParameters) ?? digestHash;
        return s.SignatureOid is "1.2.840.113549.1.1.1" or "1.2.840.10045.2.1" ? digestHash : HashFromOid(s.SignatureOid) ?? digestHash;
    }

    private static HashAlgorithmName? PssHash(byte[]? parameters)
    {
        if (parameters == null) return HashAlgorithmName.SHA1;
        var p = new AsnReader(parameters, AsnEncodingRules.BER).ReadSequence();
        if (!p.HasData || !p.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) return HashAlgorithmName.SHA1;
        return HashFromOid(p.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence().ReadObjectIdentifier());
    }

    private static bool VerifyHash(CmsSignerInfo s, X509Certificate2 signer, byte[] hash, HashAlgorithmName hashName)
    {
        switch (s.SignatureOid)
        {
            case "1.2.840.113549.1.1.1":
            case "1.2.840.113549.1.1.5":
            case "1.2.840.113549.1.1.11":
            case "1.2.840.113549.1.1.12":
            case "1.2.840.113549.1.1.13":
            case "1.2.840.113549.1.1.10":
                {
                    using var rsa = PublicKey(signer.GetRSAPublicKey);
                    if (rsa == null) return false;
                    var padding = s.SignatureOid == "1.2.840.113549.1.1.10" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
                    return rsa.VerifyHash(hash, s.Signature, hashName, padding);
                }
            case "1.2.840.10045.2.1":
            case "1.2.840.10045.4.1":
            case "1.2.840.10045.4.3.2":
            case "1.2.840.10045.4.3.3":
            case "1.2.840.10045.4.3.4":
                {
                    using var ec = PublicKey(signer.GetECDsaPublicKey);
                    return ec != null && ec.VerifyHash(hash, s.Signature, DSASignatureFormat.Rfc3279DerSequence);
                }
            default:
                throw new CryptographicException("Unsupported signature algorithm " + s.SignatureOid + ".");
        }
    }

    // A certificate from the PDF can hold a key the platform cannot decode. On Windows, .NET 8 and 9 throw
    // OverflowException for a negative RSA modulus instead of CryptographicException, so every failure to read the key
    // becomes the exception the verifier reports as a problem.
    internal static T? PublicKey<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or FormatException)
        {
            throw new CryptographicException("The signer's public key could not be read.", ex);
        }
    }

    /// <summary>Reads a signing-time attribute value (UTCTime or GeneralizedTime).</summary>
    public static DateTimeOffset? ReadTime(byte[]? value)
    {
        if (value == null) return null;
        var r = new AsnReader(value, AsnEncodingRules.BER);
        var tag = r.PeekTag();
        return tag.TagValue == (int)UniversalTagNumber.UtcTime ? r.ReadUtcTime() : r.ReadGeneralizedTime();
    }

    /// <summary>A timestamp token's TSTInfo: the hash it covers and the time it states.</summary>
    public static (HashAlgorithmName Hash, byte[] Imprint, DateTimeOffset Time) ReadTstInfo(byte[] tstInfo)
    {
        var tst = new AsnReader(tstInfo, AsnEncodingRules.BER).ReadSequence();
        tst.ReadInteger();
        tst.ReadObjectIdentifier();
        var mi = tst.ReadSequence();
        string oid = mi.ReadSequence().ReadObjectIdentifier();
        var hash = HashFromOid(oid) ?? throw new CryptographicException("Unsupported timestamp hash " + oid + ".");
        byte[] imprint = mi.ReadOctetString();
        tst.ReadInteger();
        return (hash, imprint, tst.ReadGeneralizedTime());
    }

    /// <summary>Loads a DER certificate; null when it is not one.</summary>
    public static X509Certificate2? LoadCertificate(byte[] der)
    {
        try
        {
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadCertificate(der);
#else
            // The constructor also accepts PKCS#12 and other containers, which have no place in a signature and would
            // let a PDF make the platform import keys; only a plain certificate is loaded.
            if (X509Certificate2.GetCertContentType(der) != X509ContentType.Cert) return null;
            return new X509Certificate2(der);
#endif
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
