using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker;

/// <summary>
/// What <see cref="PdfSigner.AddValidationData"/> stores so that signatures can still be verified after their
/// certificates expire or their CA goes offline (long-term validation, PAdES B-LT and B-LTA).
/// </summary>
/// <remarks>
/// Nothing goes over the network unless <see cref="FetchRevocationData"/> is true or <see cref="TimestampUrl"/> is set.
/// Without either, the store holds the certificate chains plus the OCSP responses and CRLs you supply.
/// </remarks>
public sealed class PdfLtvOptions
{
    /// <summary>
    /// Extra certificates: intermediates and roots that complete the signers' chains, and OCSP responder certificates.
    /// They are used to build the chains and stored with them.
    /// </summary>
    public IList<X509Certificate2>? Certificates { get; set; }

    /// <summary>DER-encoded OCSP responses (an OCSPResponse or a BasicOCSPResponse) to store, e.g. from your own responder.</summary>
    public IList<byte[]>? OcspResponses { get; set; }

    /// <summary>DER-encoded certificate revocation lists to store.</summary>
    public IList<byte[]>? Crls { get; set; }

    /// <summary>
    /// Fetch an OCSP response for every certificate in each chain from the responder in its Authority Information Access
    /// extension, falling back to the CRL from its CRL Distribution Points. Only http and https URLs are used. Default:
    /// false, so nothing is fetched.
    /// </summary>
    public bool FetchRevocationData { get; set; }

    /// <summary>HTTP client for OCSP and CRL requests, e.g. with a proxy. Default: a shared client.</summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Decides which OCSP and CRL URLs may be contacted when <see cref="FetchRevocationData"/> is on. It is called with
    /// each URL, redirect targets included, before the request; return false to skip it, as if the certificate did not
    /// name it. The URLs come from certificates in the PDF, so use this to keep requests away from internal hosts.
    /// Default: null, so every http and https URL is allowed. Whatever the filter says, one call contacts at most 32
    /// distinct URLs, follows at most 3 redirects per CRL download (never from https to http) and stops fetching after
    /// two minutes in total. A supplied <see cref="HttpClient"/> whose handler follows redirects itself does so without
    /// asking the filter.
    /// </summary>
    public Func<Uri, bool>? UrlFilter { get; set; }

    /// <summary>
    /// URL of an RFC 3161 timestamp server. When set, a document timestamp is added after the validation data, which
    /// seals it and the signatures with a trusted time (PAdES B-LTA). To make that timestamp long-term verifiable as
    /// well, call <see cref="PdfSigner.AddValidationData"/> again later without a timestamp URL.
    /// </summary>
    public string? TimestampUrl { get; set; }

    /// <summary>HTTP client for the timestamp server. Default: a shared client.</summary>
    public HttpClient? TimestampHttpClient { get; set; }

    /// <summary>Digest algorithm of the document timestamp: SHA-256 (default), SHA-384 or SHA-512.</summary>
    public HashAlgorithmName TimestampHashAlgorithm { get; set; } = HashAlgorithmName.SHA256;
}
