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
    public PdfSignatureOptions(X509Certificate2 certificate)
    {
        Certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
    }

    /// <summary>The signing certificate. It must have a private key (RSA or ECDSA), e.g. loaded from a .pfx file or the certificate store.</summary>
    public X509Certificate2 Certificate { get; }

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
