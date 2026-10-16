using PdfPecker.Signing;

namespace PdfPecker;

/// <summary>Thrown when a PDF cannot be signed, e.g. because the timestamp server or the external signer failed.</summary>
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
    /// <exception cref="PdfSignatureException">The timestamp server failed, or the external signer's signature does not verify.</exception>
    public static byte[] Sign(byte[] pdf, PdfSignatureOptions options, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return SignatureWriter.Sign(pdf, options, password);
    }

    /// <summary>
    /// Signs <paramref name="pdf"/> asynchronously: an asynchronous <see cref="PdfExternalSigner"/> (a cloud key vault)
    /// and the timestamp server are awaited instead of blocking a thread. Otherwise the same as <see cref="Sign"/>.
    /// </summary>
    /// <param name="pdf">The PDF to sign.</param>
    /// <param name="options">The certificate and signature settings.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the external signer and the timestamp request.</param>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="PdfSignatureException">The timestamp server failed, or the external signer's signature does not verify.</exception>
    public static Task<byte[]> SignAsync(byte[] pdf, PdfSignatureOptions options, string? password = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return SignatureWriter.SignAsync(pdf, options, password, async: true, cancellationToken).AsTask();
    }

    /// <summary>Signs a PDF file and writes the signed copy to <paramref name="outputPath"/> (which may be the same path).</summary>
    public static void SignFile(string inputPath, string outputPath, PdfSignatureOptions options, string? password = null)
        => File.WriteAllBytes(outputPath, Sign(File.ReadAllBytes(inputPath), options, password));
}
