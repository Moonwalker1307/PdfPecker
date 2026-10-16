using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;

namespace PdfPecker.Signing;

/// <summary>Requests an RFC 3161 timestamp token over HTTP.</summary>
internal static class TimestampClient
{
    private const int MaxResponseSize = 1024 * 1024;

    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    /// <summary>Returns the TimeStampToken (a CMS ContentInfo) for the hash of <paramref name="data"/>.</summary>
    public static byte[] GetToken(string url, HttpClient? client, byte[] data, HashAlgorithmName hash)
        => GetTokenForImprint(url, client, Cms.HashData(hash, data), hash);

    /// <summary>Returns the TimeStampToken for an already computed hash, as a document timestamp needs.</summary>
    public static byte[] GetTokenForImprint(string url, HttpClient? client, byte[] imprint, HashAlgorithmName hash)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] &= 0x7F; // positive
        if (nonce[0] == 0) nonce[0] = 1;

        // TimeStampReq ::= SEQUENCE { version, messageImprint, nonce, certReq }
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteInteger(1);
            using (w.PushSequence())
            {
                Cms.WriteAlgorithm(w, Cms.HashOid(hash), nullParams: true);
                w.WriteOctetString(imprint);
            }
            w.WriteInteger(nonce);
            w.WriteBoolean(true);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(w.Encode()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        byte[] body;
        try
        {
            using var response = (client ?? Shared.Value).Send(request);
            if (!response.IsSuccessStatusCode)
                throw new PdfSignatureException($"The timestamp server returned HTTP {(int)response.StatusCode}.");
            using var stream = response.Content.ReadAsStream();
            using var ms = new MemoryStream();
            // A timestamp response is a few kilobytes; do not let a misbehaving server fill memory.
            var buffer = new byte[16384];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ms.Length + n > MaxResponseSize) throw new PdfSignatureException("The timestamp server's answer is too large.");
                ms.Write(buffer, 0, n);
            }
            body = ms.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new PdfSignatureException("The timestamp server could not be reached: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new PdfSignatureException("The timestamp server did not answer in time.", ex);
        }
        return ReadResponse(body, imprint, new BigInteger(nonce, isUnsigned: true, isBigEndian: true));
    }

    // TimeStampResp ::= SEQUENCE { status PKIStatusInfo, timeStampToken ContentInfo OPTIONAL }
    private static byte[] ReadResponse(byte[] body, byte[] imprint, BigInteger nonce)
    {
        try
        {
            var resp = new AsnReader(body, AsnEncodingRules.BER).ReadSequence();
            var status = resp.ReadSequence();
            int code = (int)status.ReadInteger();
            if (code > 1)
            {
                string text = status.HasData && status.PeekTag().TagValue == (int)UniversalTagNumber.Sequence
                    ? string.Join(" ", ReadStrings(status.ReadSequence()))
                    : "";
                throw new PdfSignatureException($"The timestamp server refused the request (status {code}). {text}".Trim());
            }
            if (!resp.HasData) throw new PdfSignatureException("The timestamp server sent no token.");
            byte[] token = resp.ReadEncodedValue().ToArray();
            CheckToken(token, imprint, nonce);
            return token;
        }
        catch (AsnContentException ex)
        {
            throw new PdfSignatureException("The timestamp server's answer is not a valid RFC 3161 response.", ex);
        }
    }

    private static IEnumerable<string> ReadStrings(AsnReader seq)
    {
        while (seq.HasData) yield return seq.ReadCharacterString(UniversalTagNumber.UTF8String);
    }

    /// <summary>Checks that the token's TSTInfo covers our hash and nonce.</summary>
    private static void CheckToken(byte[] token, byte[] imprint, BigInteger nonce)
    {
        var ci = new AsnReader(token, AsnEncodingRules.BER).ReadSequence();
        if (ci.ReadObjectIdentifier() != Cms.IdSignedData) throw new PdfSignatureException("The timestamp token is not CMS signed data.");
        var sd = ci.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        sd.ReadInteger();
        sd.ReadSetOf();
        var encap = sd.ReadSequence();
        encap.ReadObjectIdentifier();
        var content = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        byte[] tstDer = content.ReadOctetString();

        var tst = new AsnReader(tstDer, AsnEncodingRules.BER).ReadSequence();
        tst.ReadInteger(); // version
        tst.ReadObjectIdentifier(); // policy
        var mi = tst.ReadSequence();
        mi.ReadSequence();
        if (!mi.ReadOctetString().AsSpan().SequenceEqual(imprint))
            throw new PdfSignatureException("The timestamp token is for different data.");
        tst.ReadInteger(); // serial
        tst.ReadGeneralizedTime();
        while (tst.HasData)
        {
            var tag = tst.PeekTag();
            if (tag.TagClass == TagClass.Universal && tag.TagValue == (int)UniversalTagNumber.Integer)
            {
                if (tst.ReadInteger() != nonce) throw new PdfSignatureException("The timestamp token's nonce does not match the request.");
                return;
            }
            tst.ReadEncodedValue();
        }
        // RFC 3161 2.4.2: the nonce must be echoed when the request has one; without it the token could be a replay.
        throw new PdfSignatureException("The timestamp token does not carry the request's nonce.");
    }
}
