using System.Text.RegularExpressions;
using PdfPecker.Pdf;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>
/// A rectangle on one page whose content <see cref="PdfRedactor"/> removes, in the coordinates <see cref="PdfText"/>
/// gives words: points from the top left corner of the page as it is displayed (after its crop box and rotation).
/// </summary>
public sealed class PdfRedactionArea
{
    /// <summary>Creates an area.</summary>
    /// <param name="pageNumber">The page, from 1.</param>
    /// <param name="x">The left edge, in points from the left of the page.</param>
    /// <param name="y">The top edge, in points from the top of the page.</param>
    /// <param name="width">The width in points.</param>
    /// <param name="height">The height in points.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page is below 1, or a size is negative or not a number.</exception>
    public PdfRedactionArea(int pageNumber, double x, double y, double width, double height)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber), "Pages are numbered from 1.");
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        PageNumber = pageNumber;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The area of a word that <see cref="PdfText.ExtractPages(byte[], PdfTextOptions?, CancellationToken)"/> found.</summary>
    /// <param name="pageNumber">The page the word is on, from 1 (<see cref="PdfTextPage.PageNumber"/>).</param>
    /// <param name="word">The word.</param>
    public static PdfRedactionArea Of(int pageNumber, PdfTextWord word)
    {
        if (word == null) throw new ArgumentNullException(nameof(word));
        return new PdfRedactionArea(pageNumber, word.X, word.Y, word.Width, word.Height);
    }

    /// <summary>The page, from 1.</summary>
    public int PageNumber { get; }

    /// <summary>The left edge, in points from the left of the page.</summary>
    public double X { get; }

    /// <summary>The top edge, in points from the top of the page.</summary>
    public double Y { get; }

    /// <summary>The width in points.</summary>
    public double Width { get; }

    /// <summary>The height in points.</summary>
    public double Height { get; }
}

/// <summary>Options for <see cref="PdfRedactor"/>.</summary>
public sealed class PdfRedactOptions
{
    /// <summary>
    /// The colour of the box drawn over each area once its content is gone, as a CSS colour (e.g. "#000", "black",
    /// "rgb(0,0,0)"); null or "transparent" draws no box and leaves the area blank. Default: "#000000".
    /// </summary>
    public string? FillColor { get; set; } = "#000000";
}

/// <summary>
/// Redacts PDFs: removes what is drawn in given areas of their pages from the file itself, not only from sight. Text in
/// an area is taken out of the content streams, so neither text extraction nor copying finds it, the pixels of images
/// under an area are overwritten, and vector shapes inside an area, links, form fields and other annotations that touch
/// one go too. A box is drawn over each area afterwards (<see cref="PdfRedactOptions.FillColor"/>).
/// </summary>
/// <remarks>
/// <para>
/// A glyph goes when an area covers a fifth or more of its width and height; the text around it keeps its place. Shapes
/// go when they lie wholly inside an area, while a shape that only crosses one, such as a table border, stays and is
/// covered by the box. An image partly under an area is written again with its pixels there painted over (as RGB, with
/// a soft mask when it has transparency), and one wholly under an area is removed. Forms drawn on a page are redacted
/// in a copy, so other pages that draw them are not changed. Marked content and structure elements that held removed
/// text lose their replacement and alternative texts (/ActualText, /Alt, /E), and redacted pages lose their thumbnail.
/// </para>
/// <para>
/// The document is written out again with only what its catalog still uses, so earlier revisions (where an incremental
/// update could keep the old page) and unused objects are gone, and the same input always gives the same bytes. Digital
/// signatures no longer verify. Text the document keeps elsewhere, such as its title and metadata, bookmarks and
/// attachments, is not looked at: <see cref="PdfDocumentProperties"/>, <see cref="PdfSanitizer"/> and
/// <see cref="PdfAttachment"/> deal with those. Encrypted PDFs are refused: decrypt with
/// <see cref="PdfProtector.Decrypt(byte[], string?)"/>, redact, and encrypt the result again.
/// </para>
/// </remarks>
public static class PdfRedactor
{
    /// <summary>Removes the content of <paramref name="areas"/> from <paramref name="pdf"/>.</summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="areas">The areas to redact, on any pages.</param>
    /// <param name="options">The box drawn over each area; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work between pages and objects.</param>
    /// <returns>The redacted PDF.</returns>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is encrypted, or the fill colour is not a CSS colour.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An area is on a page past the last one.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    public static byte[] Redact(byte[] pdf, IEnumerable<PdfRedactionArea> areas, PdfRedactOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (areas == null) throw new ArgumentNullException(nameof(areas));
        var list = areas.ToList();
        if (list.Any(a => a == null)) throw new ArgumentException("An area is null.", nameof(areas));
        uint? fill = Fill(options ?? new PdfRedactOptions());
        cancellationToken.ThrowIfCancellationRequested();

        RenderDocument doc;
        try
        {
            doc = new RenderDocument(pdf, null);
            if (doc.File.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is PdfPasswordException or NotSupportedException)
        {
            throw Encrypted(ex);
        }
        if (doc.File.Decryptor != null) throw Encrypted(null);
        foreach (var a in list)
            if (a.PageNumber > doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(areas), $"An area is on page {a.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}, but the PDF has {doc.Pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

        var redaction = new Redaction(doc.File, fill);
        foreach (var page in list.GroupBy(a => a.PageNumber).OrderBy(g => g.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (PdfFile.BeginRender(cancellationToken))
                redaction.RedactPage(doc, page.Key - 1, page.Select(a => new Redaction.Area(a.X, a.Y, a.X + a.Width, a.Y + a.Height)).ToList());
        }
        using (PdfFile.BeginRender(cancellationToken))
            return PdfRewriter.Write(doc.File, null, reproducible: true).Span.ToArray();
    }

    /// <summary>
    /// Redacts the PDF read from <paramref name="input"/> (from its current position) like
    /// <see cref="Redact(byte[], IEnumerable{PdfRedactionArea}, PdfRedactOptions?, CancellationToken)"/> and writes the
    /// result to <paramref name="output"/>. The streams are not closed.
    /// </summary>
    /// <param name="input">The PDF; it is treated as untrusted input.</param>
    /// <param name="output">Where the redacted PDF is written.</param>
    /// <param name="areas">The areas to redact, on any pages.</param>
    /// <param name="options">The box drawn over each area; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work between pages and objects; nothing is written then.</param>
    /// <exception cref="ArgumentException"><paramref name="input"/> is encrypted, or <paramref name="output"/> is not writable.</exception>
    /// <exception cref="InvalidDataException">The input is not a readable PDF.</exception>
    public static void Redact(Stream input, Stream output, IEnumerable<PdfRedactionArea> areas, PdfRedactOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        output.Write(Redact(PdfMerger.ReadAll(input), areas, options, cancellationToken));
    }

    /// <summary>
    /// Removes every occurrence of <paramref name="text"/> from <paramref name="pdf"/>: the words that contain it, found
    /// as <see cref="FindText(byte[], string, StringComparison, CancellationToken)"/> finds them, are redacted.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="text">The text to remove, which may span several words of a line.</param>
    /// <param name="options">The box drawn over each area; null for the defaults.</param>
    /// <param name="comparison">How text is compared; ordinal by default.</param>
    /// <param name="cancellationToken">Stops the work between pages and objects.</param>
    /// <returns>The redacted PDF, written out again even when the text does not occur.</returns>
    public static byte[] RedactText(byte[] pdf, string text, PdfRedactOptions? options = null, StringComparison comparison = StringComparison.Ordinal,
        CancellationToken cancellationToken = default)
        => Redact(pdf, FindText(pdf, text, comparison, cancellationToken), options, cancellationToken);

    /// <summary>Removes every match of <paramref name="pattern"/> from <paramref name="pdf"/>, like <see cref="RedactText(byte[], string, PdfRedactOptions?, StringComparison, CancellationToken)"/>.</summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="pattern">What to remove, matched against each line of text, its words separated by single spaces.</param>
    /// <param name="options">The box drawn over each area; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work between pages and objects.</param>
    /// <returns>The redacted PDF.</returns>
    public static byte[] RedactText(byte[] pdf, Regex pattern, PdfRedactOptions? options = null, CancellationToken cancellationToken = default)
        => Redact(pdf, FindText(pdf, pattern, cancellationToken), options, cancellationToken);

    /// <summary>
    /// The areas of the words that hold <paramref name="text"/>, one per word, for reviewing before
    /// <see cref="Redact(byte[], IEnumerable{PdfRedactionArea}, PdfRedactOptions?, CancellationToken)"/>. Each line is
    /// searched as its words separated by single spaces, so a phrase matches across words but not across lines, and a
    /// match inside a word (an account number in "No.12345") takes the whole word.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="text">The text to find.</param>
    /// <param name="comparison">How text is compared; ordinal by default.</param>
    /// <param name="cancellationToken">Stops the work between pages.</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    public static IReadOnlyList<PdfRedactionArea> FindText(byte[] pdf, string text, StringComparison comparison = StringComparison.Ordinal,
        CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (text.Length == 0) throw new ArgumentException("The text to find is empty.", nameof(text));
        return Find(pdf, line =>
        {
            var found = new List<(int, int)>();
            for (int at = line.IndexOf(text, comparison); at >= 0; at = line.IndexOf(text, at + Math.Max(1, text.Length), comparison))
                found.Add((at, text.Length));
            return found;
        }, cancellationToken);
    }

    /// <summary>The areas of the words that match <paramref name="pattern"/>, like <see cref="FindText(byte[], string, StringComparison, CancellationToken)"/>.</summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="pattern">What to find, matched against each line of text, its words separated by single spaces.</param>
    /// <param name="cancellationToken">Stops the work between pages.</param>
    public static IReadOnlyList<PdfRedactionArea> FindText(byte[] pdf, Regex pattern, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (pattern == null) throw new ArgumentNullException(nameof(pattern));
        return Find(pdf, line => pattern.Matches(line).Where(m => m.Length > 0).Select(m => (m.Index, m.Length)).ToList(), cancellationToken);
    }

    private static List<PdfRedactionArea> Find(byte[] pdf, Func<string, List<(int Start, int Length)>> match, CancellationToken cancellationToken)
    {
        var areas = new List<PdfRedactionArea>();
        var options = new PdfTextOptions { Order = PdfTextOrder.TopToBottom, IncludeAnnotations = false };
        foreach (var page in PdfText.ExtractPages(pdf, options, cancellationToken))
        {
            foreach (var line in page.Lines)
            {
                if (line.Words.Count == 0) continue;
                var starts = new int[line.Words.Count];
                var joined = new System.Text.StringBuilder();
                for (int i = 0; i < line.Words.Count; i++)
                {
                    if (i > 0) joined.Append(' ');
                    starts[i] = joined.Length;
                    joined.Append(line.Words[i].Text);
                }
                var taken = new bool[line.Words.Count];
                foreach (var (start, length) in match(joined.ToString()))
                {
                    int end = start + length;
                    for (int i = 0; i < starts.Length; i++)
                        if (starts[i] < end && starts[i] + line.Words[i].Text.Length > start) taken[i] = true;
                }
                for (int i = 0; i < taken.Length; i++)
                    if (taken[i]) areas.Add(PdfRedactionArea.Of(page.PageNumber, line.Words[i]));
            }
        }
        return areas;
    }

    private static uint? Fill(PdfRedactOptions options)
    {
        if (options.FillColor == null) return null;
        if (!Css.CssColor.TryParse(options.FillColor, Css.CssColor.Black, out uint color))
            throw new ArgumentException($"\"{options.FillColor}\" is not a CSS colour.", nameof(options));
        if (!Css.CssColor.IsVisible(color)) return null;
        return Css.CssColor.FlattenOnWhite(color);
    }

    private static ArgumentException Encrypted(Exception? inner)
        => new("The PDF is encrypted: decrypt it with PdfProtector.Decrypt, redact it, and encrypt the result again.", "pdf", inner);
}
