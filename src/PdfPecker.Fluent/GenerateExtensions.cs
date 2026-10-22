using PdfPecker.Fluent.Html;
using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>Turns a document into a PDF, HTML or page images.</summary>
public static class GenerateExtensions
{
    /// <summary>
    /// The PDF. <paramref name="options"/> gives every other PdfPecker setting (fonts, signatures, encryption, ...);
    /// the document's own page size, margins, metadata and settings take their place in a copy, so the options object
    /// is left unchanged and can be shared.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document's structure is invalid, such as a table without columns.</exception>
    /// <exception cref="PdfPeckerException">PdfPecker could not convert the document, such as when it exceeds a limit.</exception>
    public static byte[] GeneratePdf(this IDocument document, PdfOptions? options = null)
        => DocumentGenerator.Pdf(document, options, CancellationToken.None);

    /// <summary>Writes the PDF to a file.</summary>
    public static void GeneratePdf(this IDocument document, string filePath, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        File.WriteAllBytes(filePath, GeneratePdf(document, options));
    }

    /// <summary>Writes the PDF to a stream.</summary>
    public static void GeneratePdf(this IDocument document, Stream stream, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(GeneratePdf(document, options));
    }

    /// <summary>The PDF, generated on the thread pool, with cancellation.</summary>
    public static Task<byte[]> GeneratePdfAsync(this IDocument document, PdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Task.Run(() => DocumentGenerator.Pdf(document, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The HTML that PdfPecker turns into the PDF, with images as data URIs, so it opens in any browser. It is the
    /// quickest way to see what a fluent call does, and the starting point for moving a document to an HTML template.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document has more than one page set; each would be its own HTML document.</exception>
    public static string GenerateHtml(this IDocument document)
    {
        var pages = DocumentGenerator.Html(document, dataUris: true, out _);
        if (pages.Count > 1)
            throw new InvalidOperationException("A document with several Page calls makes one HTML document per call; GenerateHtml needs a single Page call.");
        return pages[0];
    }

    /// <summary>
    /// Writes the PDF to a new file in the temporary folder and opens it in the system's PDF viewer, for trying a layout
    /// out. Returns the file's path.
    /// </summary>
    /// <exception cref="InvalidOperationException">No application is set up to open PDF files; the file is written all the same.</exception>
    public static string GeneratePdfAndShow(this IDocument document, PdfOptions? options = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "PdfPecker.Fluent-" + Guid.NewGuid().ToString("N") + ".pdf");
        GeneratePdf(document, path, options);
        Viewer.Open(path);
        return path;
    }

    /// <summary>
    /// Writes the HTML of <see cref="GenerateHtml"/> to a new file in the temporary folder and opens it in the browser,
    /// where its layout can be inspected. Returns the file's path.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document has more than one page set, or no application is set up to open HTML files.</exception>
    public static string GenerateHtmlAndShow(this IDocument document)
    {
        string path = Path.Combine(Path.GetTempPath(), "PdfPecker.Fluent-" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(path, GenerateHtml(document));
        Viewer.Open(path);
        return path;
    }

    /// <summary>Each page of the PDF as an image (PNG unless <paramref name="imageOptions"/> says otherwise).</summary>
    public static IReadOnlyList<byte[]> GenerateImages(this IDocument document, ImageOptions? imageOptions = null, PdfOptions? options = null)
    {
        byte[] pdf = GeneratePdf(document, options);
        int count = PdfToImage.GetPageCount(pdf);
        var images = new byte[count][];
        for (int i = 0; i < count; i++) images[i] = PdfToImage.ConvertPage(pdf, i, imageOptions);
        return images;
    }
}

internal static class DocumentGenerator
{
    private sealed record Composed(DocumentContainer Container, DocumentMetadata Metadata, DocumentSettings Settings);

    private static Composed Compose(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var container = new DocumentContainer();
        document.Compose(container);
        if (container.Pages.Count == 0) container.Pages.Add(new PageSet());
        return new Composed(container, document.GetMetadata() ?? DocumentMetadata.Default, document.GetSettings() ?? DocumentSettings.Default);
    }

    /// <summary>One HTML document per page set.</summary>
    public static List<string> Html(IDocument document, bool dataUris, out ImageTable images)
    {
        var composed = Compose(document);
        images = new ImageTable(dataUris);
        var html = new List<string>(composed.Container.Pages.Count);
        foreach (var page in composed.Container.Pages)
            html.Add(HtmlWriter.Write(page, composed.Container.Css, composed.Metadata, images));
        return html;
    }

    public static byte[] Pdf(IDocument document, PdfOptions? options, CancellationToken cancellationToken)
    {
        var composed = Compose(document);
        var pages = composed.Container.Pages;
        var images = new ImageTable(dataUris: false);
        if (pages.Count > 1 && (composed.Settings.PdfA || composed.Settings.PdfUA
            || options is { } o && (o.Conformance != PdfConformance.None || o.Accessibility != PdfAccessibility.None || o.Signature != null)))
            throw new InvalidOperationException("PDF/A, PDF/UA and signatures need a document with a single Page call.");

        var pdfs = new List<byte[]>(pages.Count);
        foreach (var page in pages)
        {
            string html = HtmlWriter.Write(page, composed.Container.Css, composed.Metadata, images);
            var pageOptions = Options(options, composed, page, images);
            if (pages.Count > 1)
            {
                // The page sets are merged, which sets these for the whole document.
                pageOptions.Viewer = null;
                pageOptions.PageLabels = new List<PdfPageLabel>();
            }
            pdfs.Add(HtmlToPdf.Convert(html, pageOptions, cancellationToken));
        }
        if (pdfs.Count == 1) return pdfs[0];

        var merge = new PdfMergeOptions
        {
            Title = options?.Title ?? composed.Metadata.Title,
            Author = options?.Author ?? composed.Metadata.Author,
            Subject = options?.Subject ?? composed.Metadata.Subject,
            Keywords = options?.Keywords ?? composed.Metadata.Keywords,
            Creator = options?.Creator ?? composed.Metadata.Creator,
            Producer = options?.Producer ?? composed.Metadata.Producer,
            CreationDate = options?.CreationDate ?? composed.Metadata.CreationDate,
            MaxPages = options?.MaxPages ?? new PdfOptions().MaxPages,
            Bookmarks = options?.Bookmarks ?? true,
        };
        if (options != null)
        {
            merge.CustomProperties = options.CustomProperties;
            merge.Viewer = options.Viewer;
            merge.PageLabels = options.PageLabels;
        }
        return PdfMerger.Merge(pdfs, merge, cancellationToken);
    }

    /// <summary>A copy of the caller's options with the document's page, metadata and settings applied.</summary>
    private static PdfOptions Options(PdfOptions? caller, Composed composed, PageSet page, ImageTable images)
    {
        var options = Copy(caller ?? new PdfOptions());
        var metadata = composed.Metadata;
        var settings = composed.Settings;

        options.PageSize = page.Size;
        options.Margins = new PageMargins(page.MarginTop, page.MarginRight, page.MarginBottom, page.MarginLeft);
        options.Title ??= metadata.Title;
        options.Author ??= metadata.Author;
        options.Subject ??= metadata.Subject;
        options.Keywords ??= metadata.Keywords;
        options.Creator ??= metadata.Creator;
        options.Producer ??= metadata.Producer;
        options.Language ??= metadata.Language;
        options.CreationDate ??= metadata.CreationDate;
        if (settings.PdfA && options.Conformance == PdfConformance.None) options.Conformance = PdfConformance.PdfA3b;
        if (settings.PdfUA && options.Accessibility == PdfAccessibility.None) options.Accessibility = PdfAccessibility.PdfUA1;
        options.Compress &= settings.CompressDocument;
        if (caller == null || caller.Fonts.Count == 0) options.Fonts = FontManager.Fonts;

        var resolver = caller?.ResourceResolver;
        options.ResourceResolver = source => images.Resolve(source) ?? resolver?.Invoke(source);
        return options;
    }

    /// <summary>A shallow copy of every setting. A test checks that this list covers every public property.</summary>
    internal static PdfOptions Copy(PdfOptions o) => new()
    {
        PageSize = o.PageSize,
        Margins = o.Margins,
        Scale = o.Scale,
        ShrinkToFit = o.ShrinkToFit,
        TruncateBandsToWholePoints = o.TruncateBandsToWholePoints,
        IgnoreCssPageRules = o.IgnoreCssPageRules,
        BaseDirectory = o.BaseDirectory,
        ResourceResolver = o.ResourceResolver,
        AllowFileAccess = o.AllowFileAccess,
        Fonts = o.Fonts,
        UseSystemFonts = o.UseSystemFonts,
        MissingFont = o.MissingFont,
        MissingCharacter = o.MissingCharacter,
        UserStyleSheet = o.UserStyleSheet,
        Bookmarks = o.Bookmarks,
        FormFields = o.FormFields,
        MaxPages = o.MaxPages,
        Compress = o.Compress,
        CompressionLevel = o.CompressionLevel,
        Title = o.Title,
        Author = o.Author,
        Subject = o.Subject,
        Keywords = o.Keywords,
        Creator = o.Creator,
        Producer = o.Producer,
        CustomProperties = o.CustomProperties,
        CreationDate = o.CreationDate,
        FooterText = o.FooterText,
        HeaderText = o.HeaderText,
        Watermark = o.Watermark,
        Security = o.Security,
        Conformance = o.Conformance,
        Accessibility = o.Accessibility,
        Language = o.Language,
        Hyphenation = o.Hyphenation,
        EInvoice = o.EInvoice,
        Attachments = o.Attachments,
        EmbedStandardFonts = o.EmbedStandardFonts,
        Signature = o.Signature,
        Viewer = o.Viewer,
        PageLabels = o.PageLabels,
    };
}

/// <summary>Opens a file with the application the system associates with it.</summary>
internal static class Viewer
{
    public static void Open(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("No application could open " + path + ". The file is written; open it yourself.", e);
        }
    }
}
