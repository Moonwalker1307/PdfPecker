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

    /// <summary>Asks the certificate's OCSP responders for its status; null when it names none or none answers.</summary>
    public static async ValueTask<OcspInfo?> FetchOcspAsync(X509Certificate2 cert, X509Certificate2 issuer, RevocationFetcher fetcher, List<string> errors)
    {
        foreach (var url in OcspUrls(cert))
        {
            try
            {
                var body = await fetcher.SendAsync(url, () => new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(OcspRequest(cert, issuer)) { Headers = { ContentType = new MediaTypeHeaderValue("application/ocsp-request") } },
                }, MaxOcspSize, errors).ConfigureAwait(false);
                if (body == null) continue;
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

    /// <summary>Downloads the certificate's CRL; null when it names none or none can be fetched.</summary>
    public static async ValueTask<byte[]?> FetchCrlAsync(X509Certificate2 cert, RevocationFetcher fetcher, List<string> errors)
    {
        foreach (var url in CrlUrls(cert))
        {
            try
            {
                var body = await fetcher.GetCrlAsync(url, MaxCrlSize, errors).ConfigureAwait(false);
                if (body == null) continue;
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
}

/// <summary>
/// Makes the OCSP and CRL requests of one <see cref="PdfSigner.AddValidationData(byte[], PdfLtvOptions?, string?)"/> call.
/// The URLs come from certificates in the PDF, which may be hostile, so the number of URLs and requests and the total
/// time are capped, redirects are followed only a few times and within the same scheme, and the caller's filter sees
/// every URL. With <c>async</c> false every request is sent synchronously and the returned tasks are already complete.
/// </summary>
internal sealed class RevocationFetcher : IDisposable
{
    /// <summary>The most distinct URLs, redirect targets included, one call may contact.</summary>
    public const int MaxUrls = 32;

    // OCSP requests differ per certificate, so one responder can be asked several times; this bounds the repeats.
    private const int MaxRequests = 64;
    private const int MaxRedirects = 3;
    private static readonly TimeSpan PerRequest = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Total = TimeSpan.FromMinutes(2);

    // Redirects are followed here rather than by the handler, so each hop is checked like any other URL.
    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = PerRequest });

    private readonly HttpClient _client;
    private readonly Func<Uri, bool>? _filter;
    private readonly HashSet<string> _urls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _crls = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _deadline = new(Total);
    private readonly bool _async;
    private readonly CancellationToken _cancellation;
    private int _requests;
    private bool _limitReported;

    public RevocationFetcher(HttpClient? client, Func<Uri, bool>? filter, bool async, CancellationToken cancellationToken)
    {
        _client = client ?? Shared.Value;
        _filter = filter;
        _async = async;
        _cancellation = cancellationToken;
    }

    public void Dispose() => _deadline.Dispose();

    /// <summary>Downloads a CRL, reusing the answer when several certificates share a distribution point.</summary>
    public async ValueTask<byte[]?> GetCrlAsync(string url, int maxSize, List<string> errors)
    {
        if (_crls.TryGetValue(url, out var cached)) return cached;
        var body = await SendAsync(url, () => new HttpRequestMessage(HttpMethod.Get, url), maxSize, errors).ConfigureAwait(false);
        if (body != null) _crls[url] = body;
        return body;
    }

    /// <summary>
    /// Sends the request <paramref name="create"/> makes for <paramref name="url"/>. Returns null when the URL is not to
    /// be contacted (adding the reason to <paramref name="errors"/> when it is a limit), and throws
    /// <see cref="PdfSignatureException"/> when the server fails, or <see cref="OperationCanceledException"/> when the
    /// caller cancels.
    /// </summary>
    public async ValueTask<byte[]?> SendAsync(string url, Func<HttpRequestMessage> create, int maxSize, List<string> errors)
    {
        var uri = new Uri(url);
        for (int hop = 0; ; hop++)
        {
            if (!Allowed(uri, errors)) return null;
            using var request = create();
            request.RequestUri = uri;
            _cancellation.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token, _cancellation);
            timeout.CancelAfter(PerRequest);
            var token = timeout.Token;
            try
            {
                using var response = _async
                    ? await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)
                    : _client.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    // Only a GET may be repeated elsewhere, and only within its scheme: a redirect must not turn an
                    // https request into an http one, nor send a POST body somewhere the certificate does not name.
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (request.Method != HttpMethod.Get || hop >= MaxRedirects || !string.Equals(next.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase))
                        throw new PdfSignatureException($"HTTP {status}: the redirect to {next} is not followed.");
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new PdfSignatureException($"HTTP {status}.");
                // HttpClient's own timeout ends once the headers are in, so a server that sends the body slowly is
                // stopped by closing the response when the time is up.
                using var stop = token.Register(static r => ((IDisposable)r!).Dispose(), response);
                using var stream = _async
                    ? await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false)
                    : response.Content.ReadAsStream(token);
                using var ms = new MemoryStream();
                var buffer = new byte[16384];
                int n;
                while ((n = _async ? await stream.ReadAsync(buffer, token).ConfigureAwait(false) : stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + n > maxSize) throw new PdfSignatureException("The answer is too large.");
                    ms.Write(buffer, 0, n);
                }
                token.ThrowIfCancellationRequested();
                return ms.ToArray();
            }
            catch (Exception ex) when (_cancellation.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException)
            {
                throw new OperationCanceledException("Fetching revocation data was cancelled.", ex, _cancellation);
            }
            catch (Exception ex) when (ex is OperationCanceledException || (token.IsCancellationRequested && ex is IOException or ObjectDisposedException or HttpRequestException))
            {
                if (_deadline.IsCancellationRequested) return Limit(errors, TimeLimitMessage);
                throw new PdfSignatureException("The server did not answer in time.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new PdfSignatureException("The server could not be reached: " + ex.Message, ex);
            }
        }
    }

    private static string TimeLimitMessage => $"Fetching revocation data took longer than the limit of {Total.TotalMinutes:0} minutes.";

    private bool Allowed(Uri uri, List<string> errors)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (_deadline.IsCancellationRequested)
        {
            Limit(errors, TimeLimitMessage);
            return false;
        }
        string key = uri.AbsoluteUri;
        if ((!_urls.Contains(key) && _urls.Count >= MaxUrls) || _requests >= MaxRequests)
        {
            Limit(errors, $"The certificates name more OCSP and CRL addresses than the limit of {MaxUrls} per call; the rest were not contacted.");
            return false;
        }
        // A vetoed URL is skipped as if the certificate did not name it.
        if (_filter != null && !_filter(uri)) return false;
        _urls.Add(key);
        _requests++;
        return true;
    }

    private byte[]? Limit(List<string> errors, string message)
    {
        if (!_limitReported) errors.Add(message);
        _limitReported = true;
        return null;
    }
}
