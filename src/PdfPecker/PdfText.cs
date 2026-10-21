using PdfPecker.Rendering;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfText"/>.</summary>
public sealed class PdfTextOptions
{
    /// <summary>
    /// The password of an encrypted PDF: its user or owner password. Not needed for a PDF that only restricts permissions.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The pages to read, numbered from 1, such as <c>PdfPageRange.Parse("1-3")</c>. Default: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>
    /// Whether the text of annotation appearances is included, such as the values shown in form fields and the text of
    /// stamps. Default: true, as for <see cref="ImageOptions.RenderAnnotations"/>.
    /// </summary>
    public bool IncludeAnnotations { get; set; } = true;

    /// <summary>Whether the Latin ligature characters U+FB00 to U+FB06 come out as their letters, such as "fi". Default: true.</summary>
    public bool ExpandLigatures { get; set; } = true;

    /// <summary>The most pages read; more throws <see cref="PdfLimitException"/>. Default: 100,000.</summary>
    public int MaxPages { get; set; } = 100_000;

    /// <summary>The most pages read at the same time. Default: the processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
}

/// <summary>The text of one page, with its lines and words and where they are.</summary>
/// <remarks>
/// Positions and sizes are in points (1/72 inch) on the page as displayed, after its rotation, measured from the top-left
/// corner with y growing downward. Boxes span the font's usual ascent and descent, so they are close to, not exactly,
/// the inked area.
/// </remarks>
public sealed class PdfTextPage
{
    internal PdfTextPage(int pageNumber, double width, double height, string text, IReadOnlyList<PdfTextLine> lines)
    {
        PageNumber = pageNumber;
        Width = width;
        Height = height;
        Text = text;
        Lines = lines;
    }

    /// <summary>The page number, from 1.</summary>
    public int PageNumber { get; }

    /// <summary>The page width in points, as displayed.</summary>
    public double Width { get; }

    /// <summary>The page height in points, as displayed.</summary>
    public double Height { get; }

    /// <summary>The page's text: lines separated by a line feed, and paragraphs by an empty line.</summary>
    public string Text { get; }

    /// <summary>The lines from top to bottom; text in other directions follows the page's upright text.</summary>
    public IReadOnlyList<PdfTextLine> Lines { get; }

    /// <summary>The page's text.</summary>
    public override string ToString() => Text;
}

/// <summary>A line of text on a page.</summary>
public sealed class PdfTextLine
{
    internal PdfTextLine(string text, IReadOnlyList<PdfTextWord> words, double x, double y, double width, double height, double angle, bool rightToLeft)
    {
        Text = text;
        Words = words;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Angle = angle;
        RightToLeft = rightToLeft;
    }

    /// <summary>The line's text: its words separated by single spaces, in reading order.</summary>
    public string Text { get; }

    /// <summary>The words in reading order (right to left for a right-to-left line).</summary>
    public IReadOnlyList<PdfTextWord> Words { get; }

    /// <summary>The left edge of the line's bounding box, in points.</summary>
    public double X { get; }

    /// <summary>The top edge of the line's bounding box, in points from the top of the page.</summary>
    public double Y { get; }

    /// <summary>The width of the line's bounding box, in points.</summary>
    public double Width { get; }

    /// <summary>The height of the line's bounding box, in points.</summary>
    public double Height { get; }

    /// <summary>The direction of the baseline in degrees counterclockwise: 0 for upright text, 90 for text running up the page.</summary>
    public double Angle { get; }

    /// <summary>Whether most of the line's letters are right-to-left (such as Arabic or Hebrew).</summary>
    public bool RightToLeft { get; }

    /// <summary>The line's text.</summary>
    public override string ToString() => Text;
}

/// <summary>A word: the text between two spaces on a line.</summary>
public sealed class PdfTextWord
{
    internal PdfTextWord(string text, double x, double y, double width, double height, double fontSize, string fontName)
    {
        Text = text;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        FontSize = fontSize;
        FontName = fontName;
    }

    /// <summary>The word.</summary>
    public string Text { get; }

    /// <summary>The left edge of the word's bounding box, in points.</summary>
    public double X { get; }

    /// <summary>The top edge of the word's bounding box, in points from the top of the page.</summary>
    public double Y { get; }

    /// <summary>The width of the word's bounding box, in points.</summary>
    public double Width { get; }

    /// <summary>The height of the word's bounding box, in points.</summary>
    public double Height { get; }

    /// <summary>The font size as drawn on the page, in points (the largest in the word).</summary>
    public double FontSize { get; }

    /// <summary>The PostScript name of the font of the word's first letter, without a subset tag, such as "Helvetica-Bold".</summary>
    public string FontName { get; }

    /// <summary>Where the word's baseline starts on the left of its glyphs, in points on the page as displayed.</summary>
    internal double OriginX { get; init; }

    /// <summary>See <see cref="OriginX"/>.</summary>
    internal double OriginY { get; init; }

    /// <summary>The length of the word along its baseline, in points.</summary>
    internal double Advance { get; init; }

    /// <summary>The word.</summary>
    public override string ToString() => Text;
}

/// <summary>
/// Extracts the text of PDF pages, any PDFs and not only those this library makes, in reading order, with the position of
/// every line and word.
/// </summary>
/// <remarks>
/// <para>
/// Text is read from what the PDF draws: the characters come from the fonts' ToUnicode maps, encodings and glyph names
/// (standard, embedded TrueType, CFF, Type 1 and Type 3 fonts), and marked content with /ActualText stands for its
/// glyphs. Glyphs on one baseline make a line; where a PDF draws no space between words, a gap wider than a fraction of
/// the font size becomes one. Right-to-left text such as Arabic and Hebrew comes out in logical order, and invisible text
/// (the recognised text layer of a scanned page) is included. Text hidden by optional content that is off is not.
/// </para>
/// <para>
/// Columns side by side are read across, line by line, not one column after the other. Text a PDF draws only as
/// outlines or images has no characters to extract, nor has a font that gives no way to its characters.
/// </para>
/// </remarks>
public static class PdfText
{
    /// <summary>
    /// The text of a PDF, its pages separated by a form feed (U+000C) as pdftotext does.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Password, pages and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or a page is too complex.</exception>
    /// <exception cref="PdfLimitException">More pages than <see cref="PdfTextOptions.MaxPages"/> would be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page of <see cref="PdfTextOptions.Pages"/> is past the last page.</exception>
    public static string Extract(byte[] pdf, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
        => Join(ExtractPages(pdf, options, cancellationToken));

    /// <summary>Reads a PDF from <paramref name="pdf"/> (from its current position to the end) and returns its text. The stream is not disposed.</summary>
    /// <inheritdoc cref="Extract(byte[], PdfTextOptions?, CancellationToken)"/>
    public static string Extract(Stream pdf, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
        => Extract(ReadAll(pdf), options, cancellationToken);

    /// <summary>The text of the PDF file at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Extract(byte[], PdfTextOptions?, CancellationToken)"/>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public static string ExtractFile(string path, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
        => Extract(File.ReadAllBytes(path ?? throw new ArgumentNullException(nameof(path))), options, cancellationToken);

    /// <summary>The text of each page, with its lines and words and their positions, in page order.</summary>
    /// <inheritdoc cref="Extract(byte[], PdfTextOptions?, CancellationToken)"/>
    public static IReadOnlyList<PdfTextPage> ExtractPages(byte[] pdf, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfTextOptions();
        var doc = Open(pdf, options.Password);
        int max = Math.Max(0, options.MaxPages);
        // Without a page selection a PDF with no pages simply has no text, as PdfRenderer.RenderAllPages returns no
        // images for it; only a selection the PDF cannot satisfy is the caller's error.
        var indices = options.Pages is { } range
            ? range.Resolve(doc.Pages.Count, "the PDF", max)
            : Enumerable.Range(0, Math.Min(doc.Pages.Count, max + 1)).ToList();
        if (indices.Count > max)
            throw new PdfLimitException($"The text of more than {max:N0} pages would be read (PdfTextOptions.MaxPages).");
        var result = new PdfTextPage[indices.Count];
        try
        {
            Parallel.For(0, indices.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            }, i => result[i] = Page(doc, indices[i], options, cancellationToken));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Report a damaged page the same way a single page does, not wrapped.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
        return result;
    }

    /// <summary>Reads a PDF from <paramref name="pdf"/> (from its current position to the end) and returns the text of each page. The stream is not disposed.</summary>
    /// <inheritdoc cref="Extract(byte[], PdfTextOptions?, CancellationToken)"/>
    public static IReadOnlyList<PdfTextPage> ExtractPages(Stream pdf, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
        => ExtractPages(ReadAll(pdf), options, cancellationToken);

    /// <summary>The text of each page of the PDF file at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Extract(byte[], PdfTextOptions?, CancellationToken)"/>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public static IReadOnlyList<PdfTextPage> ExtractPagesFromFile(string path, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
        => ExtractPages(File.ReadAllBytes(path ?? throw new ArgumentNullException(nameof(path))), options, cancellationToken);

    /// <summary>The text of one page, with its lines and words. <see cref="PdfTextOptions.Pages"/> is not used.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="pageNumber">The page, numbered from 1.</param>
    /// <param name="options">Password and other options; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageNumber"/> is not a page of the PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or the page is too complex.</exception>
    public static PdfTextPage ExtractPage(byte[] pdf, int pageNumber, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfTextOptions();
        var doc = Open(pdf, options.Password);
        if (pageNumber < 1 || pageNumber > doc.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, $"The PDF has {doc.Pages.Count} page(s); page numbers start at 1.");
        return Page(doc, pageNumber - 1, options, cancellationToken);
    }

    /// <summary>Reads a PDF from <paramref name="pdf"/> (from its current position to the end) and returns the text of one page. The stream is not disposed.</summary>
    /// <inheritdoc cref="ExtractPage(byte[], int, PdfTextOptions?, CancellationToken)"/>
    public static PdfTextPage ExtractPage(Stream pdf, int pageNumber, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
        => ExtractPage(ReadAll(pdf), pageNumber, options, cancellationToken);

    private static string Join(IReadOnlyList<PdfTextPage> pages) => string.Join("\f", pages.Select(p => p.Text));

    private static byte[] ReadAll(Stream pdf)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var ms = new MemoryStream();
        pdf.CopyTo(ms);
        return ms.ToArray();
    }

    private static RenderDocument Open(byte[] pdf, string? password)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        try
        {
            return new RenderDocument(pdf, password);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static PdfTextPage Page(RenderDocument doc, int index, PdfTextOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var glyphs = doc.CollectText(index, options.IncludeAnnotations, cancellationToken);
            var (w, h) = doc.PageSize(index);
            return TextLayout.Build(glyphs, index + 1, w, h, options.ExpandLigatures, cancellationToken);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }
}
