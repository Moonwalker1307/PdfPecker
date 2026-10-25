using System.Globalization;
using System.Text;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Html;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>The kind of HTML <see cref="PdfToHtml"/> writes.</summary>
public enum PdfHtmlMode
{
    /// <summary>
    /// Pages that look like the PDF: an image of each page with its text laid invisibly over it, every line at its
    /// place on the page. For people to read and search.
    /// </summary>
    Visual,

    /// <summary>
    /// The text of the pages as headings, paragraphs, lists and tables, in reading order, with nothing placed at a
    /// position: a tagged PDF's structure tree says what each piece of text is, and otherwise tables come from the
    /// borders and rules the PDF draws and from text that lines up in columns. For
    /// programs and language models that read the text, and for reflowing on small screens. Images are left out unless
    /// <see cref="PdfHtmlOptions.IncludeImages"/> is set, and the text is plain unless <see cref="PdfHtmlOptions.IncludeStyles"/> is.
    /// </summary>
    Structured,
}

/// <summary>Options for <see cref="PdfToHtml"/>.</summary>
public sealed class PdfHtmlOptions
{
    /// <summary>
    /// Whether the HTML looks like the PDF (<see cref="PdfHtmlMode.Visual"/>, the default) or gives its structure as
    /// flowing text and tables (<see cref="PdfHtmlMode.Structured"/>). The image options do not apply to structured HTML.
    /// </summary>
    public PdfHtmlMode Mode { get; set; } = PdfHtmlMode.Visual;

    /// <summary>
    /// The password of an encrypted PDF: its user or owner password. Not needed for a PDF that only restricts permissions.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The pages to convert, numbered from 1, such as <c>PdfPageRange.Parse("1-3")</c>. Default: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>
    /// The resolution of the page images in dots per inch (a PDF point is 1/72 inch). Default 150: sharp on ordinary
    /// screens and readable when zoomed. Higher values look better on high-density screens and make larger files.
    /// </summary>
    public float Dpi { get; set; } = 150;

    /// <summary>
    /// The format of the page images: <see cref="PdfImageFormat.WebP"/> (lossless, the default),
    /// <see cref="PdfImageFormat.Png"/> or <see cref="PdfImageFormat.Jpeg"/> (smaller for photos and scans, but lossy).
    /// <see cref="PdfImageFormat.Tiff"/> is not shown by browsers and is not accepted.
    /// </summary>
    public PdfImageFormat ImageFormat { get; set; } = PdfImageFormat.WebP;

    private int? _quality;

    /// <summary>
    /// The JPEG quality, or the WebP compression effort, from 1 to 100, as <see cref="ImageOptions.Quality"/>. Default
    /// 85 for JPEG and 29 for WebP; until it is set, it reads as the default of <see cref="ImageFormat"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and 100.</exception>
    public int Quality
    {
        get => _quality ?? (ImageFormat == PdfImageFormat.WebP ? ImageOptions.DefaultWebPEffort : ImageOptions.DefaultJpegQuality);
        set => _quality = value is >= 1 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Quality must be between 1 and 100.");
    }

    /// <summary>
    /// Whether the page text is laid invisibly over the images, so it can be selected, copied, searched and read by
    /// screen readers. Default true.
    /// </summary>
    public bool IncludeText { get; set; } = true;

    /// <summary>
    /// Whether link annotations become links: web, mail and phone addresses, and links to other converted pages.
    /// Other kinds of links, such as <c>javascript:</c> and local files, are always left out. Default true.
    /// </summary>
    public bool IncludeLinks { get; set; } = true;

    /// <summary>Whether annotation appearances (form fields, stamps, comments) are drawn and their text included. Default true.</summary>
    public bool RenderAnnotations { get; set; } = true;

    /// <summary>
    /// For <see cref="PdfHtmlMode.Structured"/> HTML: whether the text and tables keep how they look in the PDF, as CSS
    /// classes of each page: fonts, sizes and colours of the text, the backgrounds and border lines of table cells and
    /// coloured boxes, where text sits in its cell or on the line, the widths of tables and columns, and the width of the
    /// page. The text still flows, with nothing placed at a position. Default false: plain text and tables in one
    /// readable style. Visual HTML always looks like the PDF and ignores it.
    /// </summary>
    public bool IncludeStyles { get; set; }

    /// <summary>
    /// For <see cref="PdfHtmlMode.Structured"/> HTML: whether the pictures the pages draw, such as a logo, are included
    /// where they sit in the flow, as JPEG or PNG images in the HTML file. A picture that fills most of a page, such as a
    /// scanned page under its text, is left out, as are image masks, pictures drawn turned and pictures too small to see.
    /// Default false. Visual HTML always draws the whole page and ignores it.
    /// </summary>
    public bool IncludeImages { get; set; }

    /// <summary>The document title. Default: the PDF's own title, or "PDF document" when it has none.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// The most pixels on either side of a page image; larger pages get a lower resolution. Default 16,384, the most
    /// WebP allows.
    /// </summary>
    public int MaxDimension { get; set; } = 16_384;

    /// <summary>The most pages converted; more throws <see cref="PdfLimitException"/>. Default: 10,000.</summary>
    public int MaxPages { get; set; } = 10_000;

    /// <summary>The most pages converted at the same time. Default: the processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
}

/// <summary>
/// Converts PDFs, any PDFs and not only those this library makes, to HTML: by default pages that look like the PDF, an
/// image of each page with its text laid invisibly over it, so the text can be selected, copied, searched and read by
/// screen readers, and with its links; or, with <see cref="PdfHtmlMode.Structured"/>, the text as headings,
/// paragraphs, lists and tables in reading order.
/// </summary>
/// <remarks>
/// <para>
/// The output is one self-contained HTML file with no scripts: the page images, the style sheet and the small web font
/// of the text layer are all inside it. Every page is an element with the class <c>pdf-page</c> and the id
/// <c>page-</c> followed by its page number.
/// </para>
/// <para>
/// The text comes from <see cref="PdfText"/>, so what it can and cannot extract applies here too. In the visual mode
/// the layout is that of the PDF: pages scale to the width of the window, down to a phone screen, and print on paper of
/// their own size, but the HTML does not reflow into paragraphs and tables, and text is not drawn in the PDF's fonts
/// but shown in the image, so it zooms as an image does.
/// </para>
/// <para>
/// Structured HTML flows like any web page, with nothing placed at a position on the page: each page is a
/// <c>section</c> of headings, paragraphs, lists and <c>table</c> elements, for search indexes, language models and
/// small screens. A tagged PDF, such as one written for PDF/UA, has a structure tree that says what each piece of text
/// is, and its headings, paragraphs, lists and tables, with their header rows and spanned cells, are read from it;
/// text marked as an artifact, such as a running header, comes from the layout at its height on the page. For a PDF
/// without tags, or a page whose tags leave much of its text out, tables come from the borders and rules the PDF draws
/// and from text that lines up in columns; this is inferred from the layout, so a table can be missed, or read as text
/// columns, where the layout is unusual, and what is not certain stays plain paragraphs. Images, drawings and the text
/// of scanned pages are not part of it.
/// </para>
/// </remarks>
public static class PdfToHtml
{
    /// <summary>Converts a PDF to an HTML document.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Password, pages, image resolution and format; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <returns>The HTML document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="PdfHtmlOptions.ImageFormat"/> is TIFF in the visual mode.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PdfHtmlOptions.Dpi"/> is not positive in the visual mode, or a page of <see cref="PdfHtmlOptions.Pages"/> is past the last page.
    /// </exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or a page is too complex.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    /// <exception cref="PdfLimitException">More pages than <see cref="PdfHtmlOptions.MaxPages"/> would be converted.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static string Convert(byte[] pdf, PdfHtmlOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        Write(pdf, options, cancellationToken, chunk => sb.Append(chunk));
        return sb.ToString();
    }

    /// <summary>Reads a PDF from <paramref name="pdf"/> (from its current position to the end) and converts it to an HTML document. The stream is not disposed.</summary>
    /// <inheritdoc cref="Convert(byte[], PdfHtmlOptions?, CancellationToken)"/>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    public static string Convert(Stream pdf, PdfHtmlOptions? options = null, CancellationToken cancellationToken = default)
        => Convert(ReadAll(pdf), options, cancellationToken);

    /// <summary>
    /// Converts a PDF to an HTML document written to <paramref name="output"/> as UTF-8, a few pages at a time, so a long
    /// document is never held in memory whole. The stream is not disposed.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="output">Receives the HTML.</param>
    /// <param name="options">Password, pages, image resolution and format; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <inheritdoc cref="Convert(byte[], PdfHtmlOptions?, CancellationToken)"/>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="IOException">Writing to the stream fails.</exception>
    public static void Convert(byte[] pdf, Stream output, PdfHtmlOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        Write(pdf, options, cancellationToken, chunk =>
        {
            writer.Write(chunk);
            writer.Flush();
        });
    }

    /// <summary>Converts the PDF file at <paramref name="pdfPath"/> to the HTML file at <paramref name="htmlPath"/>, which is replaced only once the conversion succeeds.</summary>
    /// <param name="pdfPath">The PDF file.</param>
    /// <param name="htmlPath">The HTML file to write.</param>
    /// <param name="options">Password, pages, image resolution and format; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <inheritdoc cref="Convert(byte[], PdfHtmlOptions?, CancellationToken)"/>
    /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> or <paramref name="htmlPath"/> is null.</exception>
    /// <exception cref="IOException">A file cannot be read or written, for example because the PDF does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read or write a file.</exception>
    public static void ConvertFile(string pdfPath, string htmlPath, PdfHtmlOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
        if (htmlPath == null) throw new ArgumentNullException(nameof(htmlPath));
        var pdf = File.ReadAllBytes(pdfPath);
        AtomicFile.Write(htmlPath, output => Convert(pdf, output, options, cancellationToken));
    }

    private static byte[] ReadAll(Stream pdf)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var ms = new MemoryStream();
        pdf.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Write(byte[] pdf, PdfHtmlOptions? options, CancellationToken cancellationToken, Action<string> write)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        options ??= new PdfHtmlOptions();
        bool structured = options.Mode == PdfHtmlMode.Structured;
        if (!structured && options.ImageFormat == PdfImageFormat.Tiff)
            throw new ArgumentException("Browsers do not show TIFF images; use WebP, PNG or JPEG.", nameof(options));
        if (!structured && !(options.Dpi > 0)) throw new ArgumentOutOfRangeException(nameof(options), options.Dpi, "Dpi must be positive.");
        cancellationToken.ThrowIfCancellationRequested();

        var renderer = PdfRenderer.Open(pdf, options.Password);
        var doc = renderer.Document;
        int max = Math.Max(0, options.MaxPages);
        var indices = options.Pages is { } range
            ? range.Resolve(doc.Pages.Count, "the PDF", max)
            : Enumerable.Range(0, Math.Min(doc.Pages.Count, max + 1)).ToList();
        if (indices.Count > max)
            throw new PdfLimitException($"More than {max:N0} pages would be converted (PdfHtmlOptions.MaxPages).");

        // Links go to the first copy of a page the output holds, as a page range can name a page twice.
        var pageNumbers = new Dictionary<int, int>();
        var firstCopy = new bool[indices.Count];
        for (int i = 0; i < indices.Count; i++) firstCopy[i] = pageNumbers.TryAdd(indices[i], indices[i] + 1);
        int PageId(int index) => pageNumbers.TryGetValue(index, out int n) ? n : 0;

        // Each paper size becomes a CSS named page, so a printout keeps the size of every page.
        var sizes = new Dictionary<(double, double), string>();
        var pageNames = new string[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            var (w, h) = doc.PageSize(indices[i]);
            var key = (Math.Round(w, 2), Math.Round(h, 2));
            if (!sizes.TryGetValue(key, out var name)) sizes[key] = name = "p" + (sizes.Count + 1).ToString(CultureInfo.InvariantCulture);
            pageNames[i] = name;
        }

        var head = new StringBuilder();
        head.Append("<!DOCTYPE html>\n<html");
        if (Language(doc.File) is { } lang) head.Append(" lang=\"").Append(lang).Append('"');
        head.Append(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        head.Append("<meta name=\"generator\" content=\"PdfPecker\">\n<title>");
        HtmlPageWriter.AppendEscaped(head, options.Title ?? DocumentTitle(doc.File) ?? "PDF document", attribute: false);
        head.Append("</title>\n<style>\n");
        if (structured) FlowHtmlWriter.WriteStyles(head, options.IncludeStyles);
        else
        {
            HtmlPageWriter.WriteStyles(head);
            foreach (var ((w, h), name) in sizes)
                head.Append("@page ").Append(name).Append("{size:").Append(HtmlPageWriter.Number(w)).Append("pt ").Append(HtmlPageWriter.Number(h)).Append("pt}\n");
        }
        head.Append("</style>\n</head>\n<body>\n");
        write(head.ToString());

        var links = options.IncludeLinks ? new PageLinks(doc) : null;
        var tree = structured ? Tree(doc) : null;
        var imageOptions = new ImageOptions
        {
            Dpi = options.Dpi,
            Format = options.ImageFormat,
            Quality = options.Quality,
            MaxDimension = options.MaxDimension,
            RenderAnnotations = options.RenderAnnotations,
        };
        string mediaType = options.ImageFormat switch
        {
            PdfImageFormat.Jpeg => "image/jpeg",
            PdfImageFormat.Png => "image/png",
            _ => "image/webp",
        };

        // Pages are converted in parallel a batch at a time and written in order, so memory holds one batch, not the document.
        int parallel = Math.Max(1, options.MaxDegreeOfParallelism);
        var batch = new string[Math.Min(parallel, Math.Max(1, indices.Count))];
        for (int start = 0; start < indices.Count; start += batch.Length)
        {
            int count = Math.Min(batch.Length, indices.Count - start);
            try
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, k =>
                {
                    int i = start + k, index = indices[i];
                    // Only the first copy of a page has the id that links go to.
                    int id = firstCopy[i] ? index + 1 : 0;
                    batch[k] = structured
                        ? StructuredPage(doc, tree, links, index, id, options, PageId, cancellationToken)
                        : Page(renderer, links, index, id, pageNames[i], imageOptions, mediaType, options, PageId, cancellationToken);
                });
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                // Report a damaged page the same way a single page does, not wrapped.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
            }
            for (int k = 0; k < count; k++)
            {
                write(batch[k]);
                batch[k] = null!;
            }
        }
        write("</body>\n</html>\n");
    }

    private static string Page(PdfRenderer renderer, PageLinks? links, int index, int id, string pageName, ImageOptions imageOptions,
        string mediaType, PdfHtmlOptions options, Func<int, int> pageIds, CancellationToken cancellationToken)
    {
        var doc = renderer.Document;
        var image = renderer.RenderPage(index, imageOptions, cancellationToken);
        var encoded = image.Encode();
        var (w, h) = doc.PageSize(index);
        PdfTextPage? text = null;
        List<PageLink> pageLinks;
        try
        {
            if (options.IncludeText)
                text = TextLayout.Build(doc.CollectText(index, options.RenderAnnotations, cancellationToken), index + 1, w, h, true, cancellationToken);
            pageLinks = links?.Read(index) ?? new List<PageLink>();
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        var sb = new StringBuilder(encoded.Length * 4 / 3 + 4096);
        HtmlPageWriter.WritePage(sb, id, w, h, pageName, mediaType, encoded, image.Width, image.Height, text, pageLinks, pageIds);
        return sb.ToString();
    }

    /// <summary>The structure tree of a tagged PDF, or null for an untagged one or a tree too damaged to read.</summary>
    private static StructureTree? Tree(RenderDocument doc)
    {
        try
        {
            return StructureTree.Read(doc.File, doc.Pages);
        }
        catch (PdfFormatException)
        {
            return null;
        }
    }

    private static string StructuredPage(RenderDocument doc, StructureTree? tree, PageLinks? links, int index, int id, PdfHtmlOptions options,
        Func<int, int> pageIds, CancellationToken cancellationToken)
    {
        var (w, h) = doc.PageSize(index);
        List<FlowBlock> blocks;
        FlowStyles? styles = null;
        try
        {
            var collected = doc.CollectText(index, options.RenderAnnotations, rules: true, cancellationToken);
            var pageLinks = links?.Read(index) ?? new List<PageLink>();
            // A tagged page says what its text is, but writers tag some tables, such as a grid of labels and values, as
            // paragraphs; tables its layout shows where the tags have only paragraphs replace those paragraphs.
            var tagged = tree != null && tree.Root.OnPage(index)
                ? PageStructure.BuildTagged(tree, index, collected.Glyphs, w, h, pageLinks, cancellationToken) : null;
            var text = TextLayout.Build(collected.Glyphs, index + 1, w, h, true, cancellationToken);
            blocks = PageStructure.Build(text, collected.Rules, collected.Bullets, pageLinks, cancellationToken);
            if (tagged != null) blocks = PageStructure.AddLayoutTables(tagged, blocks);
            if (options.IncludeImages) AddImages(doc, index, w, h, options, blocks, cancellationToken);
            if (options.IncludeStyles) styles = new FlowStyles(index + 1, w, h, collected.Fills, collected.Rules, blocks);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        var sb = new StringBuilder(4096);
        FlowHtmlWriter.WritePage(sb, id, blocks, pageIds, styles);
        return sb.ToString();
    }

    /// <summary>The most pictures of one page put in structured HTML.</summary>
    private const int MaxPageImages = 100;

    /// <summary>
    /// Puts a page's pictures in its blocks where they sit, such as at the top of the table cell or text column under
    /// them: pictures that fill most of the page, image masks, pictures too small to see and pictures drawn turned are
    /// left out.
    /// </summary>
    private static void AddImages(RenderDocument doc, int index, double width, double height, PdfHtmlOptions options, List<FlowBlock> blocks,
        CancellationToken cancellationToken)
    {
        int placed = 0;
        foreach (var image in PdfImages.PageImages(doc, index, options.RenderAnnotations, MaxPageImages, cancellationToken))
        {
            if (image.IsMask) continue;
            string mediaType = image.Format == PdfImageFormat.Jpeg ? "image/jpeg" : "image/png";
            foreach (var at in image.Placements)
            {
                if (at.Width < 8 || at.Height < 8 || at.Width * at.Height > 0.5 * width * height) continue;
                // A picture whose box on the page is far out of its own shape is drawn turned or skewed, as a corner
                // ribbon is, and would show upright and out of place in the flow.
                double shape = at.Width / at.Height * image.Height / Math.Max(1, image.Width);
                if (shape is > 1.5 or < 1 / 1.5) continue;
                // The part a clip leaves, as of a photo cropped to its card.
                double x0 = at.X, y0 = at.Y, x1 = at.X + at.Width, y1 = at.Y + at.Height;
                if (at.Clip is var (cx0, cy0, cx1, cy1))
                    (x0, y0, x1, y1) = (Math.Max(x0, cx0), Math.Max(y0, cy0), Math.Min(x1, cx1), Math.Min(y1, cy1));
                if (x1 - x0 < 8 || y1 - y0 < 8) continue;
                // A picture drawn over and over, as a pattern tile can be, is in the file once per place it is drawn.
                if (++placed > MaxPageImages) return;
                var block = new ImageBlock
                {
                    Top = y0, Left = x0, X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, MediaType = mediaType, Data = image.Data,
                    Drawn = (at.X, at.Y, at.X + at.Width, at.Y + at.Height),
                };
                FlowStyles.Place(blocks, block);
            }
        }
    }

    private static string? DocumentTitle(PdfFile file)
    {
        var title = (file.Resolve(file.GetDict(file.Trailer, "Info")?["Title"]) as PdfString)?.Text.Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    /// <summary>The catalog's /Lang when it looks like a language tag, such as "da-DK"; anything else is left out.</summary>
    private static string? Language(PdfFile file)
    {
        var lang = (file.Resolve(file.Catalog?["Lang"]) as PdfString)?.Text.Trim();
        if (string.IsNullOrEmpty(lang) || lang.Length > 35) return null;
        foreach (char c in lang)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return null;
        return lang;
    }
}
