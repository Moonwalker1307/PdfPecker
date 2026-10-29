using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfSanitizer"/>.</summary>
public sealed class PdfSanitizeOptions
{
    /// <summary>
    /// Removes embedded files too: the document's attachments, its associated files and file attachment annotations.
    /// PdfPecker never opens them, but a reader may, and an attachment can be a program. This also removes the XML of an
    /// e-invoice (ZUGFeRD, Factur-X). Default: false.
    /// </summary>
    public bool RemoveAttachments { get; set; }
}

/// <summary>
/// Removes active content from PDFs that come from others, so that passing them on, merging them or showing them is
/// safe: JavaScript (document, page, field and link scripts), actions that launch programs, open other files, submit or
/// import form data or play media, additional actions that run when a page or field is opened or changed, XFA forms,
/// media and 3D annotations, and links with <c>javascript:</c>, <c>vbscript:</c>, <c>data:</c> or <c>file:</c>
/// addresses. Pages, text, images, bookmarks and links within the document, web and e-mail links, form fields and their
/// values stay.
/// </summary>
/// <remarks>
/// PdfPecker itself never runs any of this; it matters for whoever opens the PDF in a viewer. The document is written out
/// again with only what its catalog still uses, so earlier revisions and unused objects go too, and the same input always
/// gives the same bytes. Digital signatures no
/// longer verify after sanitizing, as the signed bytes change. Encrypted PDFs are refused: decrypt with
/// <see cref="PdfProtector.Decrypt(byte[], string?)"/>, sanitize, and encrypt the result again.
/// </remarks>
public static class PdfSanitizer
{
    /// <summary>Removes the active content of <paramref name="pdf"/>.</summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">What to remove besides scripts and actions; null for the defaults.</param>
    /// <returns>The PDF without active content, or a copy of <paramref name="pdf"/> when it has none.</returns>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is encrypted.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    public static byte[] RemoveActiveContent(byte[] pdf, PdfSanitizeOptions? options = null)
        => RemoveActiveContent(pdf, options, CancellationToken.None);

    /// <summary>
    /// Removes the active content of <paramref name="pdf"/>, like
    /// <see cref="RemoveActiveContent(byte[], PdfSanitizeOptions?)"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">What to remove besides scripts and actions; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <returns>The PDF without active content, or a copy of <paramref name="pdf"/> when it has none.</returns>
    public static byte[] RemoveActiveContent(byte[] pdf, PdfSanitizeOptions? options, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var result = Sanitize(pdf, options ?? new PdfSanitizeOptions(), cancellationToken);
        return result ?? (byte[])pdf.Clone();
    }

    /// <summary>
    /// Removes the active content of the PDF read from <paramref name="input"/> (from its current position) and writes the
    /// result to <paramref name="output"/>. The streams are not closed.
    /// </summary>
    /// <param name="input">The PDF; it is treated as untrusted input.</param>
    /// <param name="output">Where the sanitized PDF is written: the input unchanged when it has no active content.</param>
    /// <param name="options">What to remove besides scripts and actions; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work between objects; nothing is written then.</param>
    /// <exception cref="ArgumentException"><paramref name="input"/> is encrypted, or <paramref name="output"/> is not writable.</exception>
    /// <exception cref="InvalidDataException">The input is not a readable PDF.</exception>
    public static void RemoveActiveContent(Stream input, Stream output, PdfSanitizeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        var pdf = PdfMerger.ReadAll(input);
        output.Write(Sanitize(pdf, options ?? new PdfSanitizeOptions(), cancellationToken) ?? pdf);
    }

    /// <summary>
    /// Whether <paramref name="pdf"/> has active content that <see cref="RemoveActiveContent(byte[], PdfSanitizeOptions?)"/>
    /// would remove, for example to reject such uploads instead.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">What counts besides scripts and actions; null for the defaults.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    public static bool HasActiveContent(byte[] pdf, PdfSanitizeOptions? options = null, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        using (PdfFile.BeginRender(CancellationToken.None))
        {
            var file = Open(pdf, password);
            return ActiveContent.Process(file, remove: false, (options ?? new PdfSanitizeOptions()).RemoveAttachments, CancellationToken.None) > 0;
        }
    }

    /// <summary>The sanitized file, or null when the input has no active content.</summary>
    private static byte[]? Sanitize(byte[] pdf, PdfSanitizeOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (PdfFile.BeginRender(cancellationToken))
        {
            PdfFile file;
            try
            {
                file = Open(pdf, null);
            }
            catch (Exception ex) when (ex is PdfPasswordException or NotSupportedException)
            {
                throw Encrypted(pdf, ex);
            }
            if (file.Decryptor != null) throw Encrypted(pdf, null);
            if (ActiveContent.Process(file, remove: true, options.RemoveAttachments, cancellationToken) == 0) return null;
            var result = PdfRewriter.Write(file, null, reproducible: true).Span.ToArray();

            // A damaged file may have had its cross-reference table rebuilt while it was written, which parses objects
            // again from the file, unchanged. The result is a clean file, so one more pass over it settles that.
            var check = PdfFile.Open(result, null);
            if (ActiveContent.Process(check, remove: true, options.RemoveAttachments, cancellationToken) > 0)
            {
                result = PdfRewriter.Write(check, null, reproducible: true).Span.ToArray();
                if (ActiveContent.Process(PdfFile.Open(result, null), remove: false, options.RemoveAttachments, cancellationToken) > 0)
                    throw new InvalidDataException("The PDF is too damaged to remove its active content reliably.");
            }
            return result;
        }
    }

    private static PdfFile Open(byte[] pdf, string? password)
    {
        try
        {
            var file = PdfFile.Open(pdf, password);
            if (file.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
            return file;
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
    }

    private static ArgumentException Encrypted(byte[] pdf, Exception? inner)
        => new("The PDF is encrypted: decrypt it with PdfProtector.Decrypt, remove its active content, and encrypt the result again.", nameof(pdf), inner);
}
