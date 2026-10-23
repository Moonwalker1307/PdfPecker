using System.Security.Cryptography;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>
/// Linearizes existing PDFs for Fast Web View: any PDF, not only those this library creates, such as the result of
/// <see cref="PdfMerger"/>. A viewer that loads a linearized PDF over the web shows the first page as soon as it has
/// arrived and fetches later pages as they are needed, instead of waiting for the whole file. To linearize while
/// converting, set <see cref="PdfOptions.Linearize"/> instead.
/// </summary>
/// <remarks>
/// The document is written out again as a whole, in the order of the PDF specification's annex F: the first page and the
/// objects it needs come first, with a cross-reference table of their own and a hint stream that tells the viewer where
/// every later page's objects lie. Pages, bookmarks, links, forms, the logical structure (tags), metadata, attachments,
/// PDF/A and PDF/UA conformance and every other object reachable from the catalog are kept; earlier revisions kept by
/// incremental updates and unused objects are not. The result uses classic cross-reference tables and no object streams,
/// so it can be a little larger than a compacted file. It is a pure function of the input: the same PDF always gives the
/// same bytes.
/// <para>
/// Any later change written as an incremental update, such as a signature from <see cref="PdfSigner"/>, keeps the file
/// valid but ends its linearization, so viewers load it as a whole again. Digitally signed PDFs are refused, because
/// rewriting them breaks the signatures, and so are encrypted ones: decrypt with
/// <see cref="PdfProtector.Decrypt(byte[], string?)"/> first, or set <see cref="PdfOptions.Linearize"/> together with
/// <see cref="PdfOptions.Security"/> when converting.
/// </para>
/// </remarks>
public static class PdfLinearizer
{
    /// <summary>Linearizes <paramref name="pdf"/> for Fast Web View.</summary>
    /// <param name="pdf">The PDF to linearize.</param>
    /// <returns>The linearized PDF.</returns>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is digitally signed or encrypted.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF, or has no pages.</exception>
    public static byte[] Linearize(byte[] pdf) => Linearize(pdf, CancellationToken.None);

    /// <summary>
    /// Linearizes <paramref name="pdf"/> for Fast Web View, like <see cref="Linearize(byte[])"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF to linearize.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <returns>The linearized PDF.</returns>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is digitally signed or encrypted.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF, or has no pages.</exception>
    public static byte[] Linearize(byte[] pdf, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return LinearizeCore(pdf, cancellationToken).Span.ToArray();
    }

    /// <summary>
    /// Linearizes the PDF read from <paramref name="input"/> (from its current position) and writes the result to
    /// <paramref name="output"/>.
    /// </summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be linearized.</remarks>
    /// <param name="input">The PDF to linearize.</param>
    /// <param name="output">Where the linearized PDF is written.</param>
    /// <exception cref="ArgumentException">The PDF is digitally signed or encrypted, or <paramref name="output"/> is not writable.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or has no pages.</exception>
    public static void Linearize(Stream input, Stream output) => Linearize(input, output, CancellationToken.None);

    /// <summary>
    /// Linearizes the PDF read from <paramref name="input"/> (from its current position) and writes the result to
    /// <paramref name="output"/>, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be linearized or the work is cancelled.</remarks>
    /// <param name="input">The PDF to linearize.</param>
    /// <param name="output">Where the linearized PDF is written.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <exception cref="ArgumentException">The PDF is digitally signed or encrypted, or <paramref name="output"/> is not writable.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or has no pages.</exception>
    public static void Linearize(Stream input, Stream output, CancellationToken cancellationToken)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        var result = LinearizeCore(PdfMerger.ReadAll(input), cancellationToken);
        output.Write(result.RawBuffer, 0, result.Length);
    }

    private static ByteBuffer LinearizeCore(byte[] pdf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (PdfFile.BeginRender(cancellationToken))
        {
            PdfFile file;
            try
            {
                file = PdfFile.Open(pdf, null);
            }
            catch (PdfFormatException ex)
            {
                throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
            }
            catch (Exception ex) when (ex is PdfPasswordException or NotSupportedException)
            {
                throw new ArgumentException(EncryptedMessage, nameof(pdf), ex);
            }
            if (file.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
            if (file.Decryptor != null) throw new ArgumentException(EncryptedMessage, nameof(pdf));
            PdfProtector.CheckNotSigned(file);
            // A file without an identifier gets one made from its bytes, so that the result stays a function of the input.
            return PdfLinearWriter.Write(file, null, MD5.HashData(pdf));
        }
    }

    private const string EncryptedMessage =
        "The PDF is encrypted: decrypt it with PdfProtector.Decrypt first, or set PdfOptions.Linearize with PdfOptions.Security when converting.";

    /// <summary>
    /// Linearizes a file <see cref="PdfDocumentWriter"/> has just written, opened with <paramref name="password"/> and
    /// encrypted again with the writer's own <paramref name="crypt"/>, so the encryption dictionary stays the same.
    /// </summary>
    internal static byte[] LinearizeOwn(byte[] pdf, PdfEncryptor? crypt, string? password, CancellationToken cancellationToken)
    {
        // Nothing is decoded: the objects are copied as they are, so no decoding budget applies.
        bool scoped = !PdfFile.InRenderScope;
        var scope = scoped ? PdfFile.BeginRender(cancellationToken, long.MaxValue) : default;
        try
        {
            var file = PdfFile.Open(pdf, password);
            return PdfLinearWriter.Write(file, crypt, MD5.HashData(pdf)).Span.ToArray();
        }
        finally
        {
            if (scoped) scope.Dispose();
        }
    }
}
