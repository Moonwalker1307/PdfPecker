using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker.Signing;

/// <summary>An OCSP response, reduced to what the document security store needs.</summary>
internal sealed class OcspInfo
{
    /// <summary>The full DER OCSPResponse, as stored in the DSS.</summary>
    public byte[] Encoded = Array.Empty<byte>();

    /// <summary>The certificates the response covers: SHA-1 of the issuer's name and the serial number.</summary>
    public List<(byte[] IssuerNameHash, BigInteger Serial)> Covers = new();

    /// <summary>Certificates embedded in the response, typically the OCSP responder's.</summary>
    public List<byte[]> Certificates = new();
}

/// <summary>
/// Reads, requests and matches certificate revocation data: OCSP responses (RFC 6960) and CRLs (RFC 5280). Network access
/// happens only in the Fetch methods, which the caller must opt into.
/// </summary>
internal static class Revocation
{
    private const string IdOcspBasic = "1.3.6.1.5.5.7.48.1.1";
    private const string IdAdOcsp = "1.3.6.1.5.5.7.48.1";
    private const string IdSha1 = "1.3.14.3.2.26";
    private const int MaxOcspSize = 1024 * 1024;

    // CRLs of large public CAs reach tens of megabytes; beyond this a CRL is unusable in a PDF anyway.
    private const int MaxCrlSize = 32 * 1024 * 1024;

    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    /// <summary>
    /// Parses an OCSP response. A bare BasicOCSPResponse (what some tools save) is wrapped into a full OCSPResponse, so the
    /// store always holds the same structure. Returns null for anything that is not a successful response.
    /// </summary>
    public static OcspInfo? ReadOcsp(byte[] data)
    {
        try
        {
            var outer = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
            byte[] basic;
            byte[] encoded;
            if (outer.PeekTag().TagValue == (int)UniversalTagNumber.Enumerated && outer.PeekTag().TagClass == TagClass.Universal)
            {
                if (outer.ReadEnumeratedBytes().Span is not [0]) return null; // not "successful"
                var rb = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
                if (rb.ReadObjectIdentifier() != IdOcspBasic) return null;
                basic = rb.ReadOctetString();
                AsnDecoder.ReadEncodedValue(data, AsnEncodingRules.BER, out _, out _, out int used);
                encoded = used == data.Length ? data : data[..used];
            }
            else
            {
                basic = data;
                encoded = WrapBasic(data);
            }
            var info = new OcspInfo { Encoded = encoded };
            var b = new AsnReader(basic, AsnEncodingRules.BER).ReadSequence();
            var tbs = b.ReadSequence();
            if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) tbs.ReadEncodedValue(); // version
            tbs.ReadEncodedValue(); // responderID
            tbs.ReadGeneralizedTime(); // producedAt
            var responses = tbs.ReadSequence();
            while (responses.HasData && info.Covers.Count < 1000)
            {
                var single = responses.ReadSequence();
                var certId = single.ReadSequence();
                certId.ReadSequence(); // hash algorithm
                byte[] nameHash = certId.ReadOctetString();
                certId.ReadOctetString(); // key hash
                info.Covers.Add((nameHash, certId.ReadInteger()));
            }
            b.ReadSequence(); // signature algorithm
            b.ReadBitString(out _);
            if (b.HasData && b.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var certs = b.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
                while (certs.HasData && info.Certificates.Count < 16) info.Certificates.Add(certs.ReadEncodedValue().ToArray());
            }
            return info;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static byte[] WrapBasic(byte[] basic)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteEncodedValue(new byte[] { 0x0A, 0x01, 0x00 }); // responseStatus ENUMERATED successful
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(IdOcspBasic);
                w.WriteOctetString(basic);
            }
        }
        return w.Encode();
    }

    /// <summary>Whether <paramref name="ocsp"/> answers for <paramref name="cert"/>.</summary>
    public static bool Covers(OcspInfo ocsp, X509Certificate2 cert)
    {
        var serial = new BigInteger(cert.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true);
#pragma warning disable CA5350 // OCSP CertIDs identify the issuer by SHA-1 (RFC 6960 and RFC 5019); nothing is secured by it.
        byte[] nameHash = SHA1.HashData(cert.IssuerName.RawData);
#pragma warning restore CA5350
        return ocsp.Covers.Any(c => c.Serial == serial && c.IssuerNameHash.AsSpan().SequenceEqual(nameHash));
    }

    /// <summary>The DER issuer name of a CRL, or null when <paramref name="crl"/> is not one.</summary>
    public static byte[]? CrlIssuer(byte[] crl)
    {
        try
        {
            var tbs = new AsnReader(crl, AsnEncodingRules.BER).ReadSequence().ReadSequence();
            if (tbs.PeekTag().TagValue == (int)UniversalTagNumber.Integer) tbs.ReadInteger(); // version
            tbs.ReadSequence(); // signature algorithm
            return tbs.ReadEncodedValue().ToArray();
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    /// <summary>The http(s) OCSP responder URLs in the certificate's Authority Information Access extension.</summary>
    public static List<string> OcspUrls(X509Certificate2 cert)
    {
        var urls = new List<string>();
        var ext = cert.Extensions["1.3.6.1.5.5.7.1.1"];
        if (ext == null) return urls;
        try
        {
            var seq = new AsnReader(ext.RawData, AsnEncodingRules.BER).ReadSequence();
            while (seq.HasData)
            {
                var ad = seq.ReadSequence();
                string method = ad.ReadObjectIdentifier();
                if (method == IdAdOcsp && ReadUri(ad) is { } uri) urls.Add(uri);
            }
        }
        catch (AsnContentException) { }
        return urls;
    }

    /// <summary>The http(s) URLs in the certificate's CRL Distribution Points extension.</summary>
    public static List<string> CrlUrls(X509Certificate2 cert)
    {
        var urls = new List<string>();
        var ext = cert.Extensions["2.5.29.31"];
        if (ext == null) return urls;
        try
        {
            var points = new AsnReader(ext.RawData, AsnEncodingRules.BER).ReadSequence();
            while (points.HasData)
            {
                var dp = points.ReadSequence();
                if (!dp.HasData || !dp.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) continue;
                var name = dp.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                if (!name.HasData || !name.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) continue;
                var fullName = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                while (fullName.HasData)
                    if (ReadUri(fullName) is { } uri) urls.Add(uri);
            }
        }
        catch (AsnContentException) { }
        return urls;
    }

    // GeneralName uniformResourceIdentifier [6] IA5String; other choices are skipped. Only http and https are fetched:
    // ldap and file URLs would reach places the caller did not intend.
    private static string? ReadUri(AsnReader r)
    {
        if (!r.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
        {
            r.ReadEncodedValue();
            return null;
        }
        string uri = r.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
        return uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? uri : null;
    }

    /// <summary>Builds an OCSP request for <paramref name="cert"/> issued by <paramref name="issuer"/> (SHA-1 CertID, as responders expect).</summary>
    public static byte[] OcspRequest(X509Certificate2 cert, X509Certificate2 issuer)
    {
#pragma warning disable CA5350 // RFC 5019 responders only accept SHA-1 CertIDs.
        byte[] nameHash = SHA1.HashData(issuer.SubjectName.RawData);
        byte[] keyHash = SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData);
#pragma warning restore CA5350
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence()) // OCSPRequest
        using (w.PushSequence()) // TBSRequest
        using (w.PushSequence()) // requestList
        using (w.PushSequence()) // Request
        using (w.PushSequence()) // CertID
        {
            Cms.WriteAlgorithm(w, IdSha1, nullParams: true);
            w.WriteOctetString(nameHash);
            w.WriteOctetString(keyHash);
            w.WriteInteger(cert.SerialNumberBytes.Span);
        }
        return w.Encode();
    }

    /// <summary>Asks the certificate's OCSP responders for its status; null when it names none.</summary>
    public static OcspInfo? FetchOcsp(X509Certificate2 cert, X509Certificate2 issuer, HttpClient? client, List<string> errors)
    {
        foreach (var url in OcspUrls(cert))
        {
            try
            {
                var body = Send(client, new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(OcspRequest(cert, issuer)) { Headers = { ContentType = new MediaTypeHeaderValue("application/ocsp-request") } },
                }, MaxOcspSize);
                var info = ReadOcsp(body);
                if (info != null && Covers(info, cert)) return info;
                errors.Add($"The OCSP responder {url} sent no usable answer for {cert.Subject}.");
            }
            catch (PdfSignatureException ex)
            {
                errors.Add($"OCSP {url}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Downloads the certificate's CRL; null when it names none.</summary>
    public static byte[]? FetchCrl(X509Certificate2 cert, HttpClient? client, List<string> errors)
    {
        foreach (var url in CrlUrls(cert))
        {
            try
            {
                var body = Send(client, new HttpRequestMessage(HttpMethod.Get, url), MaxCrlSize);
                if (CrlIssuer(body) is { } issuer && issuer.AsSpan().SequenceEqual(cert.IssuerName.RawData)) return body;
                errors.Add($"The CRL at {url} is not a CRL from {cert.Issuer}.");
            }
            catch (PdfSignatureException ex)
            {
                errors.Add($"CRL {url}: {ex.Message}");
            }
        }
        return null;
    }

    private static byte[] Send(HttpClient? client, HttpRequestMessage request, int maxSize)
    {
        using (request)
        {
            try
            {
                using var response = (client ?? Shared.Value).Send(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) throw new PdfSignatureException($"HTTP {(int)response.StatusCode}.");
                using var stream = response.Content.ReadAsStream();
                using var ms = new MemoryStream();
                var buffer = new byte[16384];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + n > maxSize) throw new PdfSignatureException("The answer is too large.");
                    ms.Write(buffer, 0, n);
                }
                return ms.ToArray();
            }
            catch (HttpRequestException ex)
            {
                throw new PdfSignatureException("The server could not be reached: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new PdfSignatureException("The server did not answer in time.", ex);
            }
        }
    }
}
