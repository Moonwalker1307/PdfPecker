using PdfPecker.Signing;

namespace PdfPecker;

/// <summary>Thrown when a PDF cannot be signed, e.g. because the timestamp server or the external signer failed.</summary>
public sealed class PdfSignatureException : PdfPeckerException
{
    /// <summary>Creates the exception with a message saying why signing failed.</summary>
    public PdfSignatureException(string message) : base(message) { }
    /// <summary>Creates the exception with a message and the error that caused it.</summary>
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
    /// <exception cref="PdfSignatureException">The timestamp server failed, the external signer's signature does not verify, or the PDF is certified in a way that does not allow this signature (see <see cref="PdfSignatureOptions.Certification"/>).</exception>
    public static byte[] Sign(byte[] pdf, PdfSignatureOptions options, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return SignatureWriter.Sign(pdf, options, password, CancellationToken.None);
    }

    /// <summary>
    /// Signs <paramref name="pdf"/> and returns the signed file, stopping with <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> is cancelled. Otherwise the same as
    /// <see cref="Sign(byte[], PdfSignatureOptions, string?)"/>.
    /// </summary>
    /// <param name="pdf">The PDF to sign.</param>
    /// <param name="options">The certificate and signature settings.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the external signer and the timestamp request.</param>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="PdfSignatureException">The timestamp server failed, the external signer's signature does not verify, or the PDF is certified in a way that does not allow this signature (see <see cref="PdfSignatureOptions.Certification"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static byte[] Sign(byte[] pdf, PdfSignatureOptions options, string? password, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return SignatureWriter.Sign(pdf, options, password, cancellationToken);
    }

    /// <summary>
    /// Signs <paramref name="pdf"/> asynchronously: an asynchronous <see cref="PdfExternalSigner"/> (a cloud key vault)
    /// and the timestamp server are awaited instead of blocking a thread. Otherwise the same as
    /// <see cref="Sign(byte[], PdfSignatureOptions, string?)"/>.
    /// </summary>
    /// <param name="pdf">The PDF to sign.</param>
    /// <param name="options">The certificate and signature settings.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the external signer and the timestamp request.</param>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="PdfSignatureException">The timestamp server failed, the external signer's signature does not verify, or the PDF is certified in a way that does not allow this signature (see <see cref="PdfSignatureOptions.Certification"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<byte[]> SignAsync(byte[] pdf, PdfSignatureOptions options, string? password = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return SignatureWriter.SignAsync(pdf, options, password, async: true, cancellationToken).AsTask();
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
        return LtvWriter.AddValidationData(pdf, options ?? new PdfLtvOptions(), password, CancellationToken.None);
    }

    /// <summary>
    /// Adds the validation data like <see cref="AddValidationData(byte[], PdfLtvOptions?, string?)"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled, also in the
    /// middle of an OCSP, CRL or timestamp request.
    /// </summary>
    /// <param name="pdf">A signed PDF.</param>
    /// <param name="options">Extra certificates, revocation data, and whether to fetch it. Default: store the chains only, offline.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the OCSP, CRL and timestamp requests.</param>
    /// <exception cref="PdfSignatureException">The PDF has no signatures, or fetching revocation data or the timestamp failed.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static byte[] AddValidationData(byte[] pdf, PdfLtvOptions? options, string? password, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return LtvWriter.AddValidationData(pdf, options ?? new PdfLtvOptions(), password, cancellationToken);
    }

    /// <summary>
    /// Adds the validation data asynchronously: the OCSP, CRL and timestamp requests are awaited instead of blocking a
    /// thread. Otherwise the same as <see cref="AddValidationData(byte[], PdfLtvOptions?, string?)"/>.
    /// </summary>
    /// <param name="pdf">A signed PDF.</param>
    /// <param name="options">Extra certificates, revocation data, and whether to fetch it. Default: store the chains only, offline.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the OCSP, CRL and timestamp requests.</param>
    /// <exception cref="PdfSignatureException">The PDF has no signatures, or fetching revocation data or the timestamp failed.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<byte[]> AddValidationDataAsync(byte[] pdf, PdfLtvOptions? options = null, string? password = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return LtvWriter.AddValidationDataAsync(pdf, options ?? new PdfLtvOptions(), password, async: true, cancellationToken).AsTask();
    }

    /// <summary>Signs a PDF file and writes the signed copy to <paramref name="outputPath"/> (which may be the same path).</summary>
    /// <exception cref="ArgumentNullException">A path or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The signing certificate has no private key, or its key does not suit the external signer.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="PdfSignatureException">The timestamp server failed, the external signer's signature does not verify, or the PDF is certified in a way that does not allow this signature (see <see cref="PdfSignatureOptions.Certification"/>).</exception>
    /// <exception cref="NotSupportedException"><see cref="PdfSignatureOptions.HashAlgorithm"/> is not SHA-256, SHA-384 or SHA-512.</exception>
    /// <exception cref="IOException">
    /// The input file cannot be read (it does not exist, for example) or the output file cannot be written.
    /// The output file is written only once it is complete, so a failure leaves any earlier file at that path unchanged.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the input file or write the output file.</exception>
    public static void SignFile(string inputPath, string outputPath, PdfSignatureOptions options, string? password = null)
        => AtomicFile.WriteAllBytes(outputPath, Sign(File.ReadAllBytes(inputPath), options, password));

    /// <summary>
    /// Signs a PDF file and writes the signed copy to <paramref name="outputPath"/> (which may be the same path), stopping
    /// with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled; the output file
    /// is then left as it was.
    /// </summary>
    /// <param name="inputPath">The PDF file to sign.</param>
    /// <param name="outputPath">The signed file; it appears only once signing has succeeded.</param>
    /// <param name="options">The certificate and signature settings.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the external signer and the timestamp request.</param>
    public static void SignFile(string inputPath, string outputPath, PdfSignatureOptions options, string? password, CancellationToken cancellationToken)
        => AtomicFile.WriteAllBytes(outputPath, Sign(File.ReadAllBytes(inputPath), options, password, cancellationToken));

    /// <summary>
    /// Signs the PDF read from <paramref name="input"/> (from its current position to its end) and writes the signed file
    /// to <paramref name="output"/>. Otherwise the same as
    /// <see cref="Sign(byte[], PdfSignatureOptions, string?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>The streams are not disposed. Nothing is written unless signing succeeds.</remarks>
    /// <param name="input">The PDF to sign.</param>
    /// <param name="output">Where the signed PDF is written.</param>
    /// <param name="options">The certificate and signature settings.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the external signer and the timestamp request.</param>
    public static void Sign(Stream input, Stream output, PdfSignatureOptions options, string? password = null, CancellationToken cancellationToken = default)
    {
        CheckStreams(input, output);
        var signed = Sign(PdfMerger.ReadAll(input), options, password, cancellationToken);
        output.Write(signed, 0, signed.Length);
    }

    /// <summary>
    /// Signs the PDF read from <paramref name="input"/> (from its current position to its end) and writes the signed file
    /// to <paramref name="output"/>, reading, signing and writing asynchronously. Otherwise the same as
    /// <see cref="SignAsync(byte[], PdfSignatureOptions, string?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>The streams are not disposed. Nothing is written unless signing succeeds.</remarks>
    /// <param name="input">The PDF to sign.</param>
    /// <param name="output">Where the signed PDF is written.</param>
    /// <param name="options">The certificate and signature settings.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the reads, the external signer, the timestamp request and the write.</param>
    public static async Task SignAsync(Stream input, Stream output, PdfSignatureOptions options, string? password = null,
        CancellationToken cancellationToken = default)
    {
        CheckStreams(input, output);
        if (options == null) throw new ArgumentNullException(nameof(options));
        var pdf = await PdfMerger.ReadAllAsync(input, cancellationToken).ConfigureAwait(false);
        var signed = await SignAsync(pdf, options, password, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(signed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds validation data to the PDF read from <paramref name="input"/> (from its current position to its end) and writes
    /// the result to <paramref name="output"/>. Otherwise the same as
    /// <see cref="AddValidationData(byte[], PdfLtvOptions?, string?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>The streams are not disposed. Nothing is written unless the validation data is added.</remarks>
    /// <param name="input">A signed PDF.</param>
    /// <param name="output">Where the result is written.</param>
    /// <param name="options">Extra certificates, revocation data, and whether to fetch it. Default: store the chains only, offline.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the OCSP, CRL and timestamp requests.</param>
    public static void AddValidationData(Stream input, Stream output, PdfLtvOptions? options = null, string? password = null,
        CancellationToken cancellationToken = default)
    {
        CheckStreams(input, output);
        var result = AddValidationData(PdfMerger.ReadAll(input), options, password, cancellationToken);
        output.Write(result, 0, result.Length);
    }

    /// <summary>
    /// Adds validation data to the PDF read from <paramref name="input"/> (from its current position to its end) and writes
    /// the result to <paramref name="output"/>, reading, fetching and writing asynchronously. Otherwise the same as
    /// <see cref="AddValidationDataAsync(byte[], PdfLtvOptions?, string?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>The streams are not disposed. Nothing is written unless the validation data is added.</remarks>
    /// <param name="input">A signed PDF.</param>
    /// <param name="output">Where the result is written.</param>
    /// <param name="options">Extra certificates, revocation data, and whether to fetch it. Default: store the chains only, offline.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Cancels the reads, the OCSP, CRL and timestamp requests and the write.</param>
    public static async Task AddValidationDataAsync(Stream input, Stream output, PdfLtvOptions? options = null, string? password = null,
        CancellationToken cancellationToken = default)
    {
        CheckStreams(input, output);
        var pdf = await PdfMerger.ReadAllAsync(input, cancellationToken).ConfigureAwait(false);
        var result = await AddValidationDataAsync(pdf, options, password, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private static void CheckStreams(Stream input, Stream output)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
    }
}
