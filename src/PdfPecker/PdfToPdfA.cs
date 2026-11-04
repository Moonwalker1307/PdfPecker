using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfToPdfA"/>.</summary>
public sealed class PdfAConversionOptions
{
    /// <summary>
    /// The PDF/A level to convert to: <see cref="PdfConformance.PdfA2b"/> (the default),
    /// <see cref="PdfConformance.PdfA2u"/>, <see cref="PdfConformance.PdfA3b"/> or <see cref="PdfConformance.PdfA3u"/>.
    /// The U levels also need every character to map to Unicode, so that the text can be searched and copied. PDF/A-2
    /// keeps attachments only when they are PDF/A files themselves; PDF/A-3 keeps every attachment.
    /// </summary>
    public PdfConformance Conformance { get; set; } = PdfConformance.PdfA2b;
}

/// <summary>The outcome of <see cref="PdfToPdfA.Convert(byte[], PdfAConversionOptions?)"/>.</summary>
public sealed class PdfAConversionResult
{
    internal PdfAConversionResult(byte[] pdf, IReadOnlyList<string> changes, IReadOnlyList<string> problems)
    {
        Pdf = pdf;
        Changes = changes;
        Problems = problems;
    }

    /// <summary>The converted PDF. It claims the PDF/A level asked for, also when <see cref="Problems"/> is not empty.</summary>
    public byte[] Pdf { get; }

    /// <summary>
    /// Whether the conversion found nothing left that breaks the PDF/A rules it checks: <see cref="Problems"/> is
    /// empty. A validator such as veraPDF gives the final word.
    /// </summary>
    public bool IsConformant => Problems.Count == 0;

    /// <summary>
    /// What the conversion changed, one sentence each, such as fonts it embedded, colours it made device independent or
    /// actions it removed. Changes that alter how the document looks or behaves say so.
    /// </summary>
    public IReadOnlyList<string> Changes { get; }

    /// <summary>
    /// What still breaks the PDF/A rules after the conversion, one sentence each, such as a glyph an embedded font lacks
    /// or an image format PDF/A does not allow. Empty when the result conforms as far as the conversion can tell.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Converts existing PDFs, any PDF and not only those this library makes, to PDF/A-2 or PDF/A-3 (level B or U) for
/// long-term archiving, and reports what it changed and what it could not fix.
/// </summary>
/// <remarks>
/// <para>
/// The conversion embeds fonts that are not embedded (with the bundled Liberation and DejaVu glyphs, at the document's own
/// character widths, so the text keeps its place), adds an sRGB output intent and makes other device colours device
/// independent, writes the XMP metadata with the PDF/A identification, removes JavaScript and the other actions and
/// annotations PDF/A forbids, makes annotations printable with an appearance of their own, and removes what else PDF/A
/// does not allow, such as transfer functions, image alternates and external streams. The pages are not rasterized:
/// text stays text.
/// </para>
/// <para>
/// The result is checked again after it is written, so <see cref="PdfAConversionResult.Problems"/> lists what is still
/// wrong, such as an embedded font that lacks a glyph the text uses. The same input always gives the same bytes.
/// Encrypted PDFs are refused (decrypt them with <see cref="PdfProtector.Decrypt(byte[], string?)"/> first: PDF/A
/// forbids encryption), and so are signed ones, whose signatures the conversion would break.
/// </para>
/// </remarks>
public static class PdfToPdfA
{
    /// <summary>The most levels of PDF attachments inside attachments that are converted too.</summary>
    internal const int MaxAttachmentDepth = 2;

    /// <summary>Converts <paramref name="pdf"/> to PDF/A.</summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">The PDF/A level; null for PDF/A-2b.</param>
    /// <returns>The converted PDF, what changed and what could not be fixed.</returns>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is encrypted or digitally signed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PdfAConversionOptions.Conformance"/> is not a level this
    /// conversion makes.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    public static PdfAConversionResult Convert(byte[] pdf, PdfAConversionOptions? options = null) => Convert(pdf, options, CancellationToken.None);

    /// <summary>
    /// Converts <paramref name="pdf"/> to PDF/A, like <see cref="Convert(byte[], PdfAConversionOptions?)"/>, stopping
    /// with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">The PDF/A level; null for PDF/A-2b.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <returns>The converted PDF, what changed and what could not be fixed.</returns>
    public static PdfAConversionResult Convert(byte[] pdf, PdfAConversionOptions? options, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var conformance = (options ?? new PdfAConversionOptions()).Conformance;
        if (conformance is not (PdfConformance.PdfA2b or PdfConformance.PdfA2u or PdfConformance.PdfA3b or PdfConformance.PdfA3u))
            throw new ArgumentOutOfRangeException(nameof(options), conformance, "The conversion makes PDF/A-2b, PDF/A-2u, PDF/A-3b and PDF/A-3u.");
        return ConvertCore(pdf, conformance, 0, cancellationToken);
    }

    /// <summary>
    /// Converts the PDF read from <paramref name="input"/> (from its current position) to PDF/A and writes it to
    /// <paramref name="output"/>. The streams are not closed.
    /// </summary>
    /// <param name="input">The PDF; it is treated as untrusted input.</param>
    /// <param name="output">Where the converted PDF is written; nothing is written when the conversion fails or is cancelled.</param>
    /// <param name="options">The PDF/A level; null for PDF/A-2b.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <returns>The converted PDF, what changed and what could not be fixed.</returns>
    /// <exception cref="ArgumentException">The input is encrypted or digitally signed, or <paramref name="output"/> is not writable.</exception>
    /// <exception cref="InvalidDataException">The input is not a readable PDF.</exception>
    public static PdfAConversionResult Convert(Stream input, Stream output, PdfAConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        var result = Convert(PdfMerger.ReadAll(input), options, cancellationToken);
        output.Write(result.Pdf);
        return result;
    }

    /// <summary>
    /// The conversion; <paramref name="depth"/> counts the attachments it is inside, which are converted too when they
    /// are PDFs.
    /// </summary>
    internal static PdfAConversionResult ConvertCore(byte[] pdf, PdfConformance conformance, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Every stream may be decoded a few times (fonts, content, images), so the budget grows with the file.
        long budget = Math.Max(PdfFile.MaxDecodedPerPage, Math.Min(pdf.Length * 64L, 16L * PdfFile.MaxDecodedPerPage));
        byte[] result;
        var changes = new PdfAReport();
        PdfFile file;
        PdfAConverter converter;
        bool drawFields;
        using (PdfFile.BeginRender(cancellationToken, budget))
        {
            file = Open(pdf);
            PdfProtector.CheckNotSigned(file);
            drawFields = PdfAConverter.NeedsAppearances(file);
        }
        if (drawFields && DrawFields(pdf, cancellationToken) is { } drawn)
        {
            // The form asked viewers to draw its fields, which PDF/A forbids: the form filler draws them now.
            pdf = drawn;
            changes.Add("Drew the appearances of the form's fields, which the form asked viewers to draw.");
            using (PdfFile.BeginRender(cancellationToken, budget)) file = Open(pdf);
        }
        using (PdfFile.BeginRender(cancellationToken, budget))
        {
            converter = new PdfAConverter(file, conformance, changes, fix: true, depth);
            converter.Run();
        }
        // Each attached PDF is converted with its own decoding scope, so not inside the one above.
        converter.ConvertAttachments(data => ConvertAttachment(data, depth + 1, cancellationToken));
        using (PdfFile.BeginRender(cancellationToken, budget))
            result = PdfRewriter.Write(file, null, reproducible: true, (1, 7)).Span.ToArray();
        var problems = new PdfAReport();
        using (PdfFile.BeginRender(cancellationToken, budget))
            new PdfAConverter(PdfFile.Open(result, null), conformance, problems, fix: false, depth).Run();
        return new PdfAConversionResult(result, changes.Messages(), problems.Messages());
    }

    /// <summary>An attached PDF converted to PDF/A-2b, or null when it cannot be (it is damaged, encrypted or signed).</summary>
    private static PdfAConversionResult? ConvertAttachment(byte[] data, int depth, CancellationToken cancellationToken)
    {
        try
        {
            return ConvertCore(data, PdfConformance.PdfA2b, depth, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or PdfPeckerException)
        {
            return null;
        }
    }

    /// <summary>The PDF with its text and choice fields drawn by the form filler, or null when the form cannot be filled.</summary>
    private static byte[]? DrawFields(byte[] pdf, CancellationToken cancellationToken)
    {
        try
        {
            return PdfForm.Fill(pdf, new Dictionary<string, string>(), null, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or PdfPeckerException)
        {
            return null;
        }
    }

    private static PdfFile Open(byte[] pdf)
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
            throw Encrypted(ex);
        }
        if (file.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
        if (file.Decryptor != null) throw Encrypted(null);
        return file;
    }

    private static ArgumentException Encrypted(Exception? inner)
        => new("The PDF is encrypted, which PDF/A forbids: decrypt it with PdfProtector.Decrypt and convert the result.", "pdf", inner);
}
