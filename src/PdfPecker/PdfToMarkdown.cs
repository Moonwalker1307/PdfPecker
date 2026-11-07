using System.Globalization;
using System.Text;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Html;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfToMarkdown"/>.</summary>
public sealed class PdfMarkdownOptions
{
    /// <summary>
    /// The password of an encrypted PDF: its user or owner password. Not needed for a PDF that only restricts permissions.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The pages to convert, numbered from 1, such as <c>PdfPageRange.Parse("1-3")</c>. Default: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>Whether text that links to a web address or an email address is a Markdown link. Default true.</summary>
    public bool IncludeLinks { get; set; } = true;

    /// <summary>Whether the text of annotations, such as the values of form fields, is included. Default true.</summary>
    public bool RenderAnnotations { get; set; } = true;

    /// <summary>
    /// Whether the pictures the pages draw, such as a logo, are included as images with the picture inside the address
    /// (a <c>data:</c> URI), where they sit in the text. Pictures in table cells, pictures that fill most of the page,
    /// and pictures too small to see are left out. Default false: many Markdown viewers do not show <c>data:</c> images,
    /// and they make the text much longer.
    /// </summary>
    public bool IncludeImages { get; set; }

    /// <summary>
    /// Whether each page starts with an HTML comment that gives its page number, such as <c>&lt;!-- page 3 --&gt;</c>,
    /// so a search index or language model can say which page a passage comes from. Markdown viewers do not show it.
    /// Default false.
    /// </summary>
    public bool IncludePageComments { get; set; }

    /// <summary>The most pages converted; more throws <see cref="PdfLimitException"/>. Default: 10,000.</summary>
    public int MaxPages { get; set; } = 10_000;

    /// <summary>The most pages converted at the same time. Default: the processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
}

/// <summary>
/// Converts PDFs, any PDFs and not only those this library makes, to Markdown: the text of their pages as headings,
/// paragraphs, lists and tables in reading order, for search indexes, language models and anything else that reads text.
/// </summary>
/// <remarks>
/// <para>
/// The output is GitHub Flavored Markdown, which CommonMark readers also read, apart from its tables. It has the same
/// structure as <see cref="PdfToHtml"/> in <see cref="PdfHtmlMode.Structured"/> mode, so what that finds and misses
/// applies here too: a tagged PDF, such as one written for PDF/UA, is read from its structure tree, and other PDFs from
/// their layout, where tables come from the borders and rules the PDF draws and from text that lines up in columns.
/// Bold and italic text, links and line breaks the PDF made on purpose are kept.
/// </para>
/// <para>
/// Markdown tables have one header row and no spanned cells: several header rows are joined column by column, a
/// spanned cell's text is in its first row and column, a table without a header row has an empty one, and a line break
/// in a cell is a <c>&lt;br&gt;</c>. Lists counted in letters or Roman numerals keep their markers as text in a
/// bulleted list. Text that would read as Markdown, such as an asterisk or a number at the start of a line, is escaped
/// with a backslash. Drawings and the text of scanned pages are not part of the output.
/// </para>
/// </remarks>
public static class PdfToMarkdown
{
    /// <summary>Converts a PDF to Markdown.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Password, pages, links and images; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <returns>The Markdown, with lines ending in <c>\n</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page of <see cref="PdfMarkdownOptions.Pages"/> is past the last page.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or a page is too complex.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    /// <exception cref="PdfLimitException">More pages than <see cref="PdfMarkdownOptions.MaxPages"/> would be converted.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static string Convert(byte[] pdf, PdfMarkdownOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        Write(pdf, options, cancellationToken, chunk => sb.Append(chunk));
        return sb.ToString();
    }

    /// <summary>Reads a PDF from <paramref name="pdf"/> (from its current position to the end) and converts it to Markdown. The stream is not disposed.</summary>
    /// <inheritdoc cref="Convert(byte[], PdfMarkdownOptions?, CancellationToken)"/>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    public static string Convert(Stream pdf, PdfMarkdownOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var ms = new MemoryStream();
        pdf.CopyTo(ms);
        return Convert(ms.ToArray(), options, cancellationToken);
    }

    /// <summary>
    /// Converts a PDF to Markdown written to <paramref name="output"/> as UTF-8, a few pages at a time, so a long document
    /// is never held in memory whole. The stream is not disposed.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="output">Receives the Markdown.</param>
    /// <param name="options">Password, pages, links and images; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <inheritdoc cref="Convert(byte[], PdfMarkdownOptions?, CancellationToken)"/>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="IOException">Writing to the stream fails.</exception>
    public static void Convert(byte[] pdf, Stream output, PdfMarkdownOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        Write(pdf, options, cancellationToken, chunk =>
        {
            writer.Write(chunk);
            writer.Flush();
        });
    }

    /// <summary>Converts the PDF file at <paramref name="pdfPath"/> to the Markdown file at <paramref name="markdownPath"/>, which is replaced only once the conversion succeeds.</summary>
    /// <param name="pdfPath">The PDF file.</param>
    /// <param name="markdownPath">The Markdown file to write.</param>
    /// <param name="options">Password, pages, links and images; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <inheritdoc cref="Convert(byte[], PdfMarkdownOptions?, CancellationToken)"/>
    /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> or <paramref name="markdownPath"/> is null.</exception>
    /// <exception cref="IOException">A file cannot be read or written, for example because the PDF does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read or write a file.</exception>
    public static void ConvertFile(string pdfPath, string markdownPath, PdfMarkdownOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
        if (markdownPath == null) throw new ArgumentNullException(nameof(markdownPath));
        var pdf = File.ReadAllBytes(pdfPath);
        AtomicFile.Write(markdownPath, output => Convert(pdf, output, options, cancellationToken));
    }

    private static void Write(byte[] pdf, PdfMarkdownOptions? options, CancellationToken cancellationToken, Action<string> write)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        options ??= new PdfMarkdownOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var doc = PdfRenderer.Open(pdf, options.Password).Document;
        int max = Math.Max(0, options.MaxPages);
        var indices = options.Pages is { } range
            ? range.Resolve(doc.Pages.Count, "the PDF", max)
            : Enumerable.Range(0, Math.Min(doc.Pages.Count, max + 1)).ToList();
        if (indices.Count > max)
            throw new PdfLimitException($"More than {max:N0} pages would be converted (PdfMarkdownOptions.MaxPages).");

        var links = options.IncludeLinks ? new PageLinks(doc) : null;
        var tree = PdfToHtml.Tree(doc);
        bool written = false;
        PdfToHtml.WritePages(indices.Count, options.MaxDegreeOfParallelism, cancellationToken, i =>
        {
            int index = indices[i];
            var blocks = PdfToHtml.StructuredBlocks(doc, tree, links, index, options.RenderAnnotations, options.IncludeImages, out _, out _, cancellationToken);
            var sb = new StringBuilder(4096);
            if (options.IncludePageComments) sb.Append("<!-- page ").Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(" -->\n\n");
            MarkdownPageWriter.WritePage(sb, blocks);
            return sb.ToString();
        }, chunk =>
        {
            // Every block ends with a blank line; the document ends with one line break.
            if (chunk.Length == 0) return;
            if (written) write("\n\n");
            write(chunk.TrimEnd('\n'));
            written = true;
        });
        if (written) write("\n");
    }
}
