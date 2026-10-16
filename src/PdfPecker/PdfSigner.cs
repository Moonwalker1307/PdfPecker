using PdfPecker.Signing;

namespace PdfPecker;

/// <summary>Thrown when a PDF cannot be signed, e.g. because the timestamp server failed.</summary>
public sealed class PdfSignatureException : Exception
{
    public PdfSignatureException(string message) : base(message) { }
    public PdfSignatureException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Adds a digital signature to a PDF: any PDF, not only those this library creates. To sign while converting, set
/// <see cref="PdfOptions.Signature"/> instead.
/// </summary>
public static class PdfSigner
{
    /// <summary>
    /// Signs <paramref name="pdf"/> and returns the signed file. The signature is appended as an incremental update, so the
    /// original bytes are kept and signatures already in the file stay valid.
    /// </summary>
    /// <param name="pdf">The PDF to sign.</param>
    /// <param name="options">The certificate and signature settings.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="PdfSignatureException">The timestamp server failed.</exception>
    public static byte[] Sign(byte[] pdf, PdfSignatureOptions options, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return SignatureWriter.Sign(pdf, options, password);
    }

    /// <summary>
    /// Makes the signatures in <paramref name="pdf"/> long-term verifiable: adds a Document Security Store with every
    /// certificate in their chains (including their timestamp authorities') and the OCSP responses and CRLs that show the
    /// certificates were not revoked, so the signatures can be checked after the certificates expire (PAdES B-LT). With
    /// <see cref="PdfLtvOptions.TimestampUrl"/> a document timestamp follows (PAdES B-LTA). Everything is appended as
    /// incremental updates, so the existing signatures stay valid.
    /// </summary>
    /// <param name="pdf">A signed PDF.</param>
    /// <param name="options">Extra certificates, revocation data, and whether to fetch it. Default: store the chains only, offline.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <exception cref="PdfSignatureException">The PDF has no signatures, or fetching revocation data or the timestamp failed.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    public static byte[] AddValidationData(byte[] pdf, PdfLtvOptions? options = null, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return LtvWriter.AddValidationData(pdf, options ?? new PdfLtvOptions(), password);
    }

    /// <summary>Signs a PDF file and writes the signed copy to <paramref name="outputPath"/> (which may be the same path).</summary>
    public static void SignFile(string inputPath, string outputPath, PdfSignatureOptions options, string? password = null)
        => File.WriteAllBytes(outputPath, Sign(File.ReadAllBytes(inputPath), options, password));
}
