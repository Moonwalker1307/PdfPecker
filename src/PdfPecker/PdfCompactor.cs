using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfCompactor"/>. The defaults are lossless: every page looks exactly as before.</summary>
public sealed class PdfCompactOptions
{
    /// <summary>
    /// Re-encodes 8-bit greyscale and RGB images as JPEG at this quality (1 to 100) where that makes them smaller. Lower
    /// is smaller and blurrier; 75 to 85 suits most documents. This is lossy, also for images that were not JPEGs, such as
    /// screenshots and logos. Default: null, images keep their exact pixels.
    /// </summary>
    public int? ImageQuality { get; set; }

    /// <summary>
    /// Downsamples 8-bit greyscale and RGB images drawn at a higher resolution than this, in pixels per inch, to this
    /// resolution, judged by the largest size each image is drawn at on any page. A downsampled JPEG is re-encoded as JPEG
    /// (at <see cref="ImageQuality"/>, or 85), other images stay lossless unless <see cref="ImageQuality"/> is set. 150
    /// suits the screen, 300 print. Default: null, no downsampling.
    /// </summary>
    public int? MaxImageDpi { get; set; }

    /// <summary>
    /// Removes the document information (title, author, dates) and XMP metadata. They are kept regardless when the PDF
    /// claims PDF/A or PDF/UA, which require them. Default: false.
    /// </summary>
    public bool RemoveMetadata { get; set; }

    /// <summary>
    /// Removes page thumbnails, which viewers draw themselves, and the private data editing applications keep in the
    /// file (<c>/PieceInfo</c>, such as Illustrator's editable copy), which no viewer shows. Default: true.
    /// </summary>
    public bool RemoveApplicationData { get; set; } = true;

    /// <summary>
    /// Packs objects into compressed object streams with a compressed cross-reference stream (PDF 1.5, which every current
    /// reader supports). Turn it off for readers from before 2003. PDF/A-1 files never use them, as PDF/A-1 does not allow
    /// them. Default: true.
    /// </summary>
    public bool UseObjectStreams { get; set; } = true;
}

/// <summary>
/// Makes existing PDFs smaller: any PDF, not only those this library creates. By default nothing visible changes: the
/// pages, bookmarks, links, forms, tags, attachments and PDF/A and PDF/UA conformance are kept, and every image keeps
/// its pixels. <see cref="PdfCompactOptions.ImageQuality"/> and <see cref="PdfCompactOptions.MaxImageDpi"/>
/// shrink images further, with some loss.
/// </summary>
/// <remarks>
/// The document is written out again with only the objects its pages and catalog still use, so earlier revisions kept
/// by incremental updates and orphaned objects go. Identical objects are written once (the same font or image embedded
/// many times, as merging documents often leaves them); streams that use lossless filters are recompressed with the
/// strongest Flate level, with PNG predictors for images, whenever that is smaller; JPEG, JPEG 2000, JBIG2 and CCITT
/// images keep their data; and objects are written in minimal syntax and packed into compressed object streams. The
/// result is a pure function of the input and options: the same PDF always gives the same bytes. When rewriting does not
/// make the file smaller, the input is returned unchanged.
/// <para>
/// Digitally signed PDFs are refused, because rewriting them breaks the signatures, and so are encrypted ones, whose
/// protection a rewrite would drop: decrypt with <see cref="PdfProtector.Decrypt(byte[], string?)"/>, compact, and
/// encrypt the result again with <see cref="PdfProtector.Encrypt(byte[], PdfSecurity, string?)"/>.
/// </para>
/// </remarks>
public static class PdfCompactor
{
    /// <summary>Makes <paramref name="pdf"/> as small as the options allow.</summary>
    /// <param name="pdf">The PDF to compact.</param>
    /// <param name="options">What may change; null for the lossless defaults.</param>
    /// <returns>The compacted PDF, or a copy of <paramref name="pdf"/> when compacting does not make it smaller.</returns>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is digitally signed or encrypted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PdfCompactOptions.ImageQuality"/> is not between 1 and 100,
    /// or <see cref="PdfCompactOptions.MaxImageDpi"/> is not positive.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    public static byte[] Compact(byte[] pdf, PdfCompactOptions? options = null) => Compact(pdf, options, CancellationToken.None);

    /// <summary>
    /// Makes <paramref name="pdf"/> as small as the options allow, like <see cref="Compact(byte[], PdfCompactOptions?)"/>,
    /// stopping with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF to compact.</param>
    /// <param name="options">What may change; null for the lossless defaults.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <returns>The compacted PDF, or a copy of <paramref name="pdf"/> when compacting does not make it smaller.</returns>
    public static byte[] Compact(byte[] pdf, PdfCompactOptions? options, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var result = CompactCore(pdf, options ?? new PdfCompactOptions(), cancellationToken);
        return result == null ? (byte[])pdf.Clone() : result.Span.ToArray();
    }

    /// <summary>Compacts the PDF read from <paramref name="input"/> (from its current position) and writes the result to <paramref name="output"/>.</summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be compacted.</remarks>
    /// <inheritdoc cref="Compact(byte[], PdfCompactOptions?)"/>
    public static void Compact(Stream input, Stream output, PdfCompactOptions? options = null)
        => Compact(input, output, options, CancellationToken.None);

    /// <summary>
    /// Compacts the PDF read from <paramref name="input"/> (from its current position) and writes the result to
    /// <paramref name="output"/>, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be compacted or the work is cancelled.</remarks>
    /// <param name="input">The PDF to compact.</param>
    /// <param name="output">Where the compacted PDF is written.</param>
    /// <param name="options">What may change; null for the lossless defaults.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    public static void Compact(Stream input, Stream output, PdfCompactOptions? options, CancellationToken cancellationToken)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        var pdf = PdfMerger.ReadAll(input);
        var result = CompactCore(pdf, options ?? new PdfCompactOptions(), cancellationToken);
        if (result == null) output.Write(pdf);
        else output.Write(result.RawBuffer, 0, result.Length);
    }

    /// <summary>The compacted file, or null when it is not smaller than the input.</summary>
    private static ByteBuffer? CompactCore(byte[] pdf, PdfCompactOptions options, CancellationToken cancellationToken)
    {
        if (options.ImageQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options), options.ImageQuality, "ImageQuality must be between 1 and 100.");
        if (options.MaxImageDpi is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxImageDpi, "MaxImageDpi must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        // Every stream is decoded once and some twice, so the decoding budget grows with the file, from the budget of a
        // rendered page up; a stream that would exceed it is kept as it is rather than failing the whole file.
        long budget = Math.Max(PdfFile.MaxDecodedPerPage, Math.Min(pdf.Length * 64L, 16L * PdfFile.MaxDecodedPerPage));
        using (PdfFile.BeginRender(cancellationToken, budget))
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
                throw new ArgumentException("The PDF is encrypted: decrypt it with PdfProtector.Decrypt, compact it, and encrypt the result again.", nameof(pdf), ex);
            }
            if (file.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
            if (file.Decryptor != null)
                throw new ArgumentException("The PDF is encrypted: decrypt it with PdfProtector.Decrypt, compact it, and encrypt the result again.", nameof(pdf));
            PdfProtector.CheckNotSigned(file);
            var result = PdfCompactWriter.Write(file, options);
            return result.Length < pdf.Length ? result : null;
        }
    }
}
