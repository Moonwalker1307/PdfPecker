using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker;

/// <summary>How the signature is encoded in the PDF.</summary>
public enum PdfSignatureFormat
{
    /// <summary>
    /// PAdES baseline (<c>/ETSI.CAdES.detached</c>): a CAdES signature with the signing certificate bound into the signed
    /// attributes. The default; what EU eIDAS and current Adobe Reader versions expect.
    /// </summary>
    PAdES,

    /// <summary>Classic PDF signature (<c>/adbe.pkcs7.detached</c>), for older validators.</summary>
    Pkcs7Detached,
}

/// <summary>The public-key algorithm an <see cref="PdfExternalSigner"/> signs with.</summary>
public enum PdfSignatureAlgorithm
{
    /// <summary>RSA with PKCS#1 v1.5 padding (RS256/RS384/RS512, AWS KMS RSASSA_PKCS1_V1_5_SHA_*). The most widely accepted.</summary>
    RsaPkcs1,

    /// <summary>
    /// RSA with PSS padding, MGF1 with the same hash and a salt as long as the hash (PS256/PS384/PS512, AWS KMS
    /// RSASSA_PSS_SHA_*, .NET's <see cref="RSASignaturePadding.Pss"/>).
    /// </summary>
    RsaPss,

    /// <summary>ECDSA (ES256/ES384/ES512, AWS KMS ECDSA_SHA_*). The signature may be DER-encoded or raw r||s.</summary>
    Ecdsa,
}

/// <summary>What an <see cref="PdfExternalSigner"/> is asked to sign.</summary>
/// <remarks>
/// Sign either <see cref="Digest"/> with a "sign hash" API (Azure Key Vault <c>SignAsync</c>, AWS KMS <c>Sign</c> with
/// <c>MessageType.DIGEST</c>, <see cref="RSA.SignHash(byte[], HashAlgorithmName, RSASignaturePadding)"/>, PKCS#11
/// <c>CKM_RSA_PKCS</c> with a DigestInfo) or <see cref="Data"/> with a "sign data" API that hashes itself; both give the
/// same signature.
/// </remarks>
public sealed class PdfSignatureRequest
{
    internal PdfSignatureRequest(X509Certificate2 certificate, PdfSignatureAlgorithm algorithm, HashAlgorithmName hashAlgorithm, byte[] data, byte[] digest)
    {
        Certificate = certificate;
        Algorithm = algorithm;
        HashAlgorithm = hashAlgorithm;
        Data = data;
        Digest = digest;
    }

    /// <summary>The signing certificate; its key is the one that must sign.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The algorithm declared on the <see cref="PdfExternalSigner"/>.</summary>
    public PdfSignatureAlgorithm Algorithm { get; }

    /// <summary>The hash algorithm, from <see cref="PdfSignatureOptions.HashAlgorithm"/>.</summary>
    public HashAlgorithmName HashAlgorithm { get; }

    /// <summary>The bytes to sign: the DER-encoded CMS signed attributes, which hold the hash of the PDF.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The hash of <see cref="Data"/> with <see cref="HashAlgorithm"/>, for signers that take a precomputed digest.</summary>
    public ReadOnlyMemory<byte> Digest { get; }
}

/// <summary>
/// Signs with a private key that stays outside this process: a hardware security module, a PKCS#11 token, a smart card,
/// or a cloud key vault such as Azure Key Vault or AWS KMS. PdfPecker builds the PDF and the CMS structure and asks the
/// callback only for the raw signature value.
/// </summary>
/// <remarks>
/// The returned signature is checked against the certificate's public key before it goes into the PDF, so a signer that
/// uses the wrong key or padding fails with a <see cref="PdfSignatureException"/> instead of producing a broken file. The
/// callback is normally called once per signature; in the rare case the signature does not fit its reserved space, it is
/// called a second time.
/// </remarks>
public sealed class PdfExternalSigner
{
    private readonly Func<PdfSignatureRequest, byte[]>? _sign;
    private readonly Func<PdfSignatureRequest, CancellationToken, ValueTask<byte[]>>? _signAsync;

    /// <summary>A signer with a synchronous callback, e.g. a PKCS#11 library or a local smart card.</summary>
    /// <param name="algorithm">The algorithm the callback signs with; it must match the certificate's key type.</param>
    /// <param name="sign">Returns the raw signature over the request.</param>
    public PdfExternalSigner(PdfSignatureAlgorithm algorithm, Func<PdfSignatureRequest, byte[]> sign)
    {
        Algorithm = algorithm;
        _sign = sign ?? throw new ArgumentNullException(nameof(sign));
    }

    /// <summary>
    /// A signer with an asynchronous callback, e.g. a cloud key vault. Use it with <see cref="PdfSigner.SignAsync"/> or
    /// <see cref="HtmlToPdf.ConvertAsync"/>, which await it; the synchronous
    /// <see cref="PdfSigner.Sign(byte[], PdfSignatureOptions, string?)"/> (and signing during
    /// <see cref="HtmlToPdf.Convert(string, PdfOptions?)"/>) blocks on it.
    /// </summary>
    /// <param name="algorithm">The algorithm the callback signs with; it must match the certificate's key type.</param>
    /// <param name="signAsync">Returns the raw signature over the request.</param>
    public PdfExternalSigner(PdfSignatureAlgorithm algorithm, Func<PdfSignatureRequest, CancellationToken, ValueTask<byte[]>> signAsync)
    {
        Algorithm = algorithm;
        _signAsync = signAsync ?? throw new ArgumentNullException(nameof(signAsync));
    }

    /// <summary>The algorithm the callback signs with.</summary>
    public PdfSignatureAlgorithm Algorithm { get; }

    internal ValueTask<byte[]> SignAsync(PdfSignatureRequest request, bool async, CancellationToken cancellationToken)
    {
        if (_sign != null) return new ValueTask<byte[]>(_sign(request));
        var task = _signAsync!(request, cancellationToken);
        // Synchronous signing has no choice but to wait; asynchronous signing awaits the callback's own task.
        return async || task.IsCompleted ? task : new ValueTask<byte[]>(task.AsTask().GetAwaiter().GetResult());
    }
}

/// <summary>A visible signature box on a page. Without one the signature is invisible (it still shows in the signature panel).</summary>
public sealed class PdfSignatureAppearance
{
    /// <summary>The page, 1-based. Negative numbers count from the end: -1 is the last page. Default: 1.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Distance from the left edge of the page to the box, in points.</summary>
    public float X { get; set; } = 36;

    /// <summary>Distance from the top edge of the page to the box, in points.</summary>
    public float Y { get; set; } = 36;

    /// <summary>Box width in points. Default: 200.</summary>
    public float Width { get; set; } = 200;

    /// <summary>Box height in points. Default: 60.</summary>
    public float Height { get; set; } = 60;

    /// <summary>
    /// Text in the box, one line per <c>\n</c>. Default: "Digitally signed by {name}", the date, and the reason and
    /// location when set. <c>{name}</c>, <c>{date}</c>, <c>{reason}</c> and <c>{location}</c> are replaced.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>Draw a border around the box. Default: true.</summary>
    public bool Border { get; set; } = true;
}

/// <summary>
/// Signs the PDF with a certificate. Viewers such as Adobe Reader then show who signed it and whether the file has been
/// changed since.
/// </summary>
/// <remarks>
/// The signature is added as an incremental update, so earlier signatures in an existing PDF stay valid. Signing uses the
/// .NET cryptography classes; no other library is involved.
/// </remarks>
public sealed class PdfSignatureOptions
{
    /// <summary>Signs with the certificate's own private key.</summary>
    /// <param name="certificate">A certificate with an RSA or ECDSA private key, e.g. loaded from a .pfx file or the certificate store.</param>
    public PdfSignatureOptions(X509Certificate2 certificate)
    {
        Certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
    }

    /// <summary>Signs with a key held elsewhere, such as an HSM, a smart card or a cloud key vault.</summary>
    /// <param name="certificate">The signer's certificate. Only its public part is needed.</param>
    /// <param name="externalSigner">Produces the signature value with the private key.</param>
    public PdfSignatureOptions(X509Certificate2 certificate, PdfExternalSigner externalSigner)
        : this(certificate)
    {
        ExternalSigner = externalSigner ?? throw new ArgumentNullException(nameof(externalSigner));
    }

    /// <summary>
    /// The signing certificate. Without an <see cref="ExternalSigner"/> it must have a private key (RSA or ECDSA), e.g.
    /// loaded from a .pfx file or the certificate store.
    /// </summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The signer that holds the private key outside this process, or null to use the certificate's own key.</summary>
    public PdfExternalSigner? ExternalSigner { get; }

    /// <summary>
    /// Intermediate certificates to embed. By default the chain is built from the certificate store (without network
    /// access), and every certificate found below the root is embedded.
    /// </summary>
    public IList<X509Certificate2>? ExtraCertificates { get; set; }

    /// <summary>Why the document is signed, e.g. "Approved".</summary>
    public string? Reason { get; set; }

    /// <summary>Where it was signed, e.g. "Copenhagen".</summary>
    public string? Location { get; set; }

    /// <summary>How to reach the signer, e.g. an e-mail address.</summary>
    public string? ContactInfo { get; set; }

    /// <summary>The signer's name. Default: the certificate's common name.</summary>
    public string? SignerName { get; set; }

    /// <summary>
    /// The name of the signature field. When the PDF already has an empty signature field with this (fully qualified) name,
    /// the signature goes into it, at the field's own place on its page; a field of another type, or one already signed,
    /// is an error. Otherwise a new field with this name is added. Default: "Signature1" (or the next free number).
    /// </summary>
    public string? FieldName { get; set; }

    /// <summary>Show a signature box on a page. Default: null, an invisible signature.</summary>
    public PdfSignatureAppearance? Appearance { get; set; }

    /// <summary>Signature encoding. Default: <see cref="PdfSignatureFormat.PAdES"/>.</summary>
    public PdfSignatureFormat Format { get; set; } = PdfSignatureFormat.PAdES;

    /// <summary>Digest algorithm: SHA-256 (default), SHA-384 or SHA-512.</summary>
    public HashAlgorithmName HashAlgorithm { get; set; } = HashAlgorithmName.SHA256;

    /// <summary>The signing time recorded in the PDF. Default: now. Without a timestamp server it is the computer's own clock.</summary>
    public DateTimeOffset? SigningTime { get; set; }

    /// <summary>
    /// URL of an RFC 3161 timestamp server (e.g. http://timestamp.digicert.com). When set, a trusted timestamp is added
    /// to the signature, which proves when it was made (PAdES B-T). This is the only option that uses the network.
    /// </summary>
    public string? TimestampUrl { get; set; }

    /// <summary>HTTP client for the timestamp server, e.g. with credentials or a proxy. Default: a shared client.</summary>
    public HttpClient? TimestampHttpClient { get; set; }
}
