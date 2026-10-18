using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfPages"/>.</summary>
public sealed class PdfPagesOptions
{
    /// <summary>
    /// The password of an encrypted PDF: its user or owner password. Not needed for a PDF that only restricts permissions.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Keep the bookmarks that go to the pages taken. Default: true.</summary>
    public bool Bookmarks { get; set; } = true;

    /// <summary>The most pages a result may have; more throws <see cref="PdfLimitException"/>. Default: 100,000.</summary>
    public int MaxPages { get; set; } = 100_000;

    /// <summary>Document title. Default: the PDF's own.</summary>
    public string? Title { get; set; }

    /// <summary>Document author. Default: the PDF's own.</summary>
    public string? Author { get; set; }

    /// <summary>Document subject. Default: the PDF's own.</summary>
    public string? Subject { get; set; }

    /// <summary>Document keywords. Default: the PDF's own.</summary>
    public string? Keywords { get; set; }

    /// <summary>The application that created the original document. Default: the PDF's own.</summary>
    public string? Creator { get; set; }

    /// <summary>The application that produced the PDF. Default: "PdfPecker".</summary>
    public string? Producer { get; set; }

    internal PdfMergeOptions ToMergeOptions() => new()
    {
        Passwords = new[] { Password },
        Bookmarks = Bookmarks,
        MaxPages = MaxPages,
        Title = Title,
        Author = Author,
        Subject = Subject,
        Keywords = Keywords,
        Creator = Creator,
        Producer = Producer,
    };
}

/// <summary>
/// Page tools for existing PDFs, any PDFs and not only those this library makes: take some of a PDF's pages (in any
/// order, and more than once), leave pages out, turn pages, and split a PDF into several. Pages are numbered from 1; see
/// <see cref="PdfPageRange"/> for the range syntax, such as <c>"1-3,5,8-"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The result keeps, for the pages taken: their content, annotations and links; form fields with a widget on them; the
/// bookmarks that go to them; the structure tree (tags) of their content; page labels; the document properties and
/// language. Links and bookmarks to pages left out are dropped (a bookmark with a kept bookmark below it stays, without a
/// destination), and links between pages taken go to the new pages. Structure elements are kept when some of their
/// content is on a page taken, so a tagged PDF stays tagged, and a PDF/UA-1 input gives a PDF/UA-1 result.
/// </para>
/// <para>
/// A page taken more than once is copied: its links and other annotations are copied with it, but form fields and
/// popups stay on the first copy, and the later copies are not tagged, so the result no longer claims PDF/UA. As with
/// <see cref="PdfMerger"/>, the result is not encrypted, signatures are removed, and PDF/A conformance and attachments are
/// not carried over. Objects only the pages left out use are not read.
/// </para>
/// </remarks>
public static class PdfPages
{
    /// <summary>The number of pages of a PDF.</summary>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    public static int Count(byte[] pdf, string? password = null) => PdfToImage.GetPageCount(pdf, password);

    /// <summary>The number of pages of a PDF read from <paramref name="pdf"/> (from its current position to its end). The stream is not disposed.</summary>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    public static int Count(Stream pdf, string? password = null) => PdfToImage.GetPageCount(pdf, password);

    /// <summary>A new PDF with the given pages, in the given order, such as <c>"1-3,5"</c> or <c>"3,1,2"</c>.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="pages">The pages to take, numbered from 1 (see <see cref="PdfPageRange.Parse"/>).</param>
    /// <param name="options">Password, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="FormatException"><paramref name="pages"/> is not a page range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable.</exception>
    public static byte[] Extract(byte[] pdf, string pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
        => Extract(pdf, PdfPageRange.Parse(pages ?? throw new ArgumentNullException(nameof(pages))), options, cancellationToken);

    /// <summary>A new PDF with the given pages (numbered from 1), in the given order.</summary>
    /// <inheritdoc cref="Extract(byte[], string, PdfPagesOptions?, CancellationToken)"/>
    public static byte[] Extract(byte[] pdf, IEnumerable<int> pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
        => Extract(pdf, PdfPageRange.FromPages(pages), options, cancellationToken);

    /// <summary>A new PDF with the given pages, in the given order.</summary>
    /// <inheritdoc cref="Extract(byte[], string, PdfPagesOptions?, CancellationToken)"/>
    public static byte[] Extract(byte[] pdf, PdfPageRange pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pages == null) throw new ArgumentNullException(nameof(pages));
        return Run(pdf, new PdfMergeWriter.PageSpec { Pages = pages }, options, cancellationToken);
    }

    /// <summary>
    /// Reads a PDF from <paramref name="input"/> (from its current position) and writes the given pages to
    /// <paramref name="output"/>. The streams are not disposed.
    /// </summary>
    /// <inheritdoc cref="Extract(byte[], string, PdfPagesOptions?, CancellationToken)"/>
    public static void Extract(Stream input, Stream output, PdfPageRange pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pages == null) throw new ArgumentNullException(nameof(pages));
        RunStream(input, output, new PdfMergeWriter.PageSpec { Pages = pages }, options, cancellationToken);
    }

    /// <summary>
    /// Writes the given pages of the PDF file <paramref name="inputPath"/> to <paramref name="outputPath"/>. The output
    /// file appears only once it is complete, and may be the input.
    /// </summary>
    /// <inheritdoc cref="Extract(byte[], string, PdfPagesOptions?, CancellationToken)"/>
    public static void ExtractFile(string inputPath, string outputPath, string pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
        var range = PdfPageRange.Parse(pages ?? throw new ArgumentNullException(nameof(pages)));
        var pdf = File.ReadAllBytes(inputPath);
        AtomicFile.Write(outputPath, output => Extract(new MemoryStream(pdf, writable: false), output, range, options, cancellationToken));
    }

    /// <summary>A new PDF without the given pages, such as <c>"2,5-"</c>.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="pages">The pages to leave out, numbered from 1 (see <see cref="PdfPageRange.Parse"/>).</param>
    /// <param name="options">Password, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentException">Every page would be left out.</exception>
    /// <exception cref="FormatException"><paramref name="pages"/> is not a page range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable.</exception>
    public static byte[] Remove(byte[] pdf, string pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
        => Remove(pdf, PdfPageRange.Parse(pages ?? throw new ArgumentNullException(nameof(pages))), options, cancellationToken);

    /// <summary>A new PDF without the given pages.</summary>
    /// <inheritdoc cref="Remove(byte[], string, PdfPagesOptions?, CancellationToken)"/>
    public static byte[] Remove(byte[] pdf, PdfPageRange pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pages == null) throw new ArgumentNullException(nameof(pages));
        return Run(pdf, new PdfMergeWriter.PageSpec { Pages = pages, Invert = true }, options, cancellationToken);
    }

    /// <summary>
    /// Reads a PDF from <paramref name="input"/> (from its current position) and writes it without the given pages
    /// (numbered from 1) to <paramref name="output"/>. The streams are not disposed.
    /// </summary>
    /// <param name="input">The PDF.</param>
    /// <param name="output">Where the result is written.</param>
    /// <param name="pages">The pages to leave out, numbered from 1.</param>
    /// <param name="options">Password, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentException">Every page would be left out.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable.</exception>
    public static void Remove(Stream input, Stream output, PdfPageRange pages, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pages == null) throw new ArgumentNullException(nameof(pages));
        RunStream(input, output, new PdfMergeWriter.PageSpec { Pages = pages, Invert = true }, options, cancellationToken);
    }

    /// <summary>A new PDF with pages turned clockwise by <paramref name="degrees"/> (on top of any rotation they have).</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="degrees">A multiple of 90; negative turns counter-clockwise.</param>
    /// <param name="pages">The pages to turn, numbered from 1, such as <c>"2,4"</c> (see <see cref="PdfPageRange.Parse"/>); null for every page.</param>
    /// <param name="options">Password, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentException"><paramref name="degrees"/> is not a multiple of 90.</exception>
    /// <exception cref="FormatException"><paramref name="pages"/> is not a page range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    public static byte[] Rotate(byte[] pdf, int degrees, string? pages = null, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
        => Run(pdf, RotateSpec(degrees, pages), options, cancellationToken);

    /// <summary>
    /// Reads a PDF from <paramref name="input"/> (from its current position) and writes it with pages turned clockwise by
    /// <paramref name="degrees"/> to <paramref name="output"/>. The streams are not disposed.
    /// </summary>
    /// <param name="input">The PDF.</param>
    /// <param name="output">Where the result is written.</param>
    /// <param name="degrees">A multiple of 90; negative turns counter-clockwise.</param>
    /// <param name="pages">The pages to turn, numbered from 1, such as <c>"2,4"</c> (see <see cref="PdfPageRange.Parse"/>); null for every page.</param>
    /// <param name="options">Password, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentException"><paramref name="degrees"/> is not a multiple of 90.</exception>
    /// <exception cref="FormatException"><paramref name="pages"/> is not a page range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    public static void Rotate(Stream input, Stream output, int degrees, string? pages = null, PdfPagesOptions? options = null,
        CancellationToken cancellationToken = default)
        => RunStream(input, output, RotateSpec(degrees, pages), options, cancellationToken);

    /// <summary>Splits a PDF into parts of <paramref name="pagesPerFile"/> pages each (the last may have fewer).</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="pagesPerFile">The pages in each part, at least 1.</param>
    /// <param name="options">Password, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The parts, in order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pagesPerFile"/> is below 1.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable or has no pages.</exception>
    public static byte[][] Split(byte[] pdf, int pagesPerFile, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (pagesPerFile < 1) throw new ArgumentOutOfRangeException(nameof(pagesPerFile), pagesPerFile, "Each part needs at least one page.");
        var file = Open(pdf, options, cancellationToken);
        return SplitOpened(file, Chunks(file, pagesPerFile), options, cancellationToken);
    }

    /// <summary>
    /// Splits a PDF read from <paramref name="pdf"/> (from its current position to its end) into parts of
    /// <paramref name="pagesPerFile"/> pages each (the last may have fewer). The stream is not disposed.
    /// </summary>
    /// <inheritdoc cref="Split(byte[], int, PdfPagesOptions?, CancellationToken)"/>
    public static byte[][] Split(Stream pdf, int pagesPerFile, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
        => Split(ReadInput(pdf), pagesPerFile, options, cancellationToken);

    /// <summary>Splits a PDF into one part per range, such as <c>Split(pdf, "1-3", "4-")</c>. Ranges may overlap.</summary>
    /// <exception cref="ArgumentException">No ranges are given.</exception>
    /// <exception cref="FormatException">A range is not a page range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    public static byte[][] Split(byte[] pdf, params string[] ranges)
        => Split(pdf, (ranges ?? throw new ArgumentNullException(nameof(ranges))).Select(r => PdfPageRange.Parse(r ?? throw new ArgumentException("A range is null.", nameof(ranges)))), null);

    /// <summary>Splits a PDF into one part per range. Ranges may overlap.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="ranges">The pages of each part.</param>
    /// <param name="options">Password, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The parts, in the order of <paramref name="ranges"/>.</returns>
    /// <exception cref="ArgumentException">No ranges are given.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable.</exception>
    public static byte[][] Split(byte[] pdf, IEnumerable<PdfPageRange> ranges, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (ranges == null) throw new ArgumentNullException(nameof(ranges));
        var list = ranges.ToList();
        if (list.Count == 0) throw new ArgumentException("Give at least one range.", nameof(ranges));
        if (list.Any(r => r == null)) throw new ArgumentException("A range is null.", nameof(ranges));
        return SplitOpened(Open(pdf, options, cancellationToken), list, options, cancellationToken);
    }

    /// <summary>
    /// Splits a PDF read from <paramref name="pdf"/> (from its current position to its end) into one part per range.
    /// Ranges may overlap. The stream is not disposed.
    /// </summary>
    /// <inheritdoc cref="Split(byte[], IEnumerable{PdfPageRange}, PdfPagesOptions?, CancellationToken)"/>
    public static byte[][] Split(Stream pdf, IEnumerable<PdfPageRange> ranges, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
        => Split(ReadInput(pdf), ranges, options, cancellationToken);

    /// <summary>
    /// Splits the PDF file <paramref name="inputPath"/> into parts of <paramref name="pagesPerFile"/> pages and writes part
    /// <c>n</c> (from 1) to <c>outputPath(n)</c>, one part at a time. Returns the number of parts.
    /// </summary>
    /// <example><c>PdfPages.SplitFile("report.pdf", 1, n => $"report-{n}.pdf");</c></example>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pagesPerFile"/> is below 1.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable or has no pages.</exception>
    public static int SplitFile(string inputPath, int pagesPerFile, Func<int, string> outputPath, PdfPagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
        if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
        if (pagesPerFile < 1) throw new ArgumentOutOfRangeException(nameof(pagesPerFile), pagesPerFile, "Each part needs at least one page.");
        var file = Open(File.ReadAllBytes(inputPath), options, cancellationToken);
        var parts = Chunks(file, pagesPerFile);
        for (int i = 0; i < parts.Count; i++)
        {
            var path = outputPath(i + 1) ?? throw new ArgumentException($"No output path for part {i + 1}.", nameof(outputPath));
            AtomicFile.Write(path, output =>
            {
                var writer = Writer(options, cancellationToken, output);
                writer.Add(file, new PdfMergeWriter.PageSpec { Pages = parts[i] });
                writer.Finish();
                writer.Flush(output);
            });
        }
        return parts.Count;
    }

    private static PdfMergeWriter Writer(PdfPagesOptions? options, CancellationToken cancellationToken, Stream? sink = null)
        => new((options ?? new PdfPagesOptions()).ToMergeOptions(), cancellationToken, sink, single: true);

    private static PdfMergeWriter.PageSpec RotateSpec(int degrees, string? pages)
    {
        if (degrees % 90 != 0) throw new ArgumentException($"Pages turn by multiples of 90 degrees, not {degrees}.", nameof(degrees));
        return new PdfMergeWriter.PageSpec { Rotate = degrees % 360, RotatePages = pages == null ? null : PdfPageRange.Parse(pages) };
    }

    private static byte[] ReadInput(Stream pdf) => PdfMerger.ReadAll(pdf ?? throw new ArgumentNullException(nameof(pdf)));

    /// <summary>Reads the whole input, then writes the chosen pages to <paramref name="output"/> as they are made.</summary>
    private static void RunStream(Stream input, Stream output, PdfMergeWriter.PageSpec spec, PdfPagesOptions? options, CancellationToken cancellationToken)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        var writer = Writer(options, cancellationToken, output);
        writer.Add(PdfMerger.ReadAll(input), spec);
        writer.Finish();
        writer.Flush(output);
        output.Flush();
    }

    private static byte[] Run(byte[] pdf, PdfMergeWriter.PageSpec spec, PdfPagesOptions? options, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var writer = Writer(options, cancellationToken);
        writer.Add(pdf, spec);
        writer.Finish();
        return writer.ToArray();
    }

    private static PdfFile Open(byte[] pdf, PdfPagesOptions? options, CancellationToken cancellationToken)
    {
        using (PdfFile.BeginRender(cancellationToken))
            return PdfMergeWriter.Open(pdf, options?.Password, null);
    }

    private static List<PdfPageRange> Chunks(PdfFile file, int pagesPerFile)
    {
        int count = PdfMergeWriter.CountPages(file);
        if (count == 0) throw new InvalidDataException("The PDF has no pages.");
        var parts = new List<PdfPageRange>();
        for (int first = 1; first <= count; first += pagesPerFile)
            parts.Add(PdfPageRange.Span(first, (int)Math.Min(count, (long)first + pagesPerFile - 1)));
        return parts;
    }

    private static byte[][] SplitOpened(PdfFile file, List<PdfPageRange> parts, PdfPagesOptions? options, CancellationToken cancellationToken)
    {
        var result = new byte[parts.Count][];
        for (int i = 0; i < parts.Count; i++)
        {
            var writer = Writer(options, cancellationToken);
            writer.Add(file, new PdfMergeWriter.PageSpec { Pages = parts[i] });
            writer.Finish();
            result[i] = writer.ToArray();
        }
        return result;
    }
}
