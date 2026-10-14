using System.IO.Compression;

namespace PdfPecker;

/// <summary>A page size in PostScript points (1/72 inch).</summary>
public readonly struct PageSize
{
    public float Width { get; }
    public float Height { get; }

    public PageSize(float width, float height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Page dimensions must be positive.");
        Width = width;
        Height = height;
    }

    public static PageSize A3 => new(841.89f, 1190.55f);
    public static PageSize A4 => new(595.28f, 841.89f);
    public static PageSize A5 => new(419.53f, 595.28f);
    public static PageSize Letter => new(612f, 792f);
    public static PageSize Legal => new(612f, 1008f);

    /// <summary>The same size rotated to landscape orientation.</summary>
    public PageSize Landscape => Width >= Height ? this : new PageSize(Height, Width);

    /// <summary>Creates a page size from millimetres.</summary>
    public static PageSize FromMillimeters(float width, float height) => new(width * 72f / 25.4f, height * 72f / 25.4f);

    public override string ToString() => $"{Width}x{Height}pt";
}

/// <summary>Page margins in points.</summary>
public readonly struct PageMargins
{
    public float Top { get; }
    public float Right { get; }
    public float Bottom { get; }
    public float Left { get; }

    public PageMargins(float all) : this(all, all, all, all) { }

    public PageMargins(float top, float right, float bottom, float left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    /// <summary>Creates margins from millimetres.</summary>
    public static PageMargins FromMillimeters(float top, float right, float bottom, float left)
        => new(top * 72f / 25.4f, right * 72f / 25.4f, bottom * 72f / 25.4f, left * 72f / 25.4f);
}

/// <summary>An archival standard the generated PDF conforms to.</summary>
public enum PdfConformance
{
    /// <summary>A plain PDF.</summary>
    None,
    /// <summary>
    /// PDF/A-2b (ISO 19005-2, level B): fonts embedded, XMP metadata, sRGB output intent, no encryption.
    /// The usual choice for archiving invoices and payslips.
    /// </summary>
    PdfA2b,
    /// <summary>PDF/A-3b (ISO 19005-3, level B): as PDF/A-2b; the part that also allows attached files.</summary>
    PdfA3b,
}

/// <summary>Options for <see cref="HtmlToPdf"/>.</summary>
public sealed class PdfOptions
{
    /// <summary>Page size. Default: A4. A CSS <c>@page { size: ... }</c> rule overrides it unless <see cref="IgnoreCssPageRules"/> is set.</summary>
    public PageSize PageSize { get; set; } = PageSize.A4;

    /// <summary>Page margins in points. Default: 36pt (half an inch) on every side.</summary>
    public PageMargins Margins { get; set; } = new(36);

    /// <summary>
    /// Scales the page content, like the Scale setting in Chrome's print dialog: at 0.5 the page is laid out twice as
    /// wide and tall and drawn at half size. The margins keep their size on paper; header and footer text and
    /// watermarks are scaled with the content. Between 0.1 and 2. Default: 1.
    /// </summary>
    /// <remarks>
    /// Chrome, and so IronPDF, lays a page out at a whole number of CSS pixels and stretches it to the paper: an A4 page
    /// is written at 594.96 x 841.92 pt (whole points, then Chrome's 1/300 inch grid), which is 793.28 px wide, and laid
    /// out 793 px wide. To put every box and glyph where IronPDF puts them, set <see cref="PageSize"/> to
    /// <c>new PageSize(594.96f, 841.92f)</c> and this to <c>793.28f / 793f</c>, and turn on
    /// <see cref="TruncateBandsToWholePoints"/>. A scaled page is also written at the paper size Chrome gives it, so
    /// viewers such as pdfium snap thin lines the same way.
    /// </remarks>
    public float Scale { get; set; } = 1f;

    /// <summary>
    /// Round the height that a running header or footer reserves with its <c>height</c> attribute down to whole points on
    /// paper, as Chrome does with page margins: IronPDF turned <c>height="17mm"</c> (48.19 pt) into a 48 pt margin, so the
    /// flowing content started 0.19 pt higher than the attribute says. A band whose content is taller than its attribute
    /// still gets the room its content needs. Default: false.
    /// </summary>
    public bool TruncateBandsToWholePoints { get; set; }

    /// <summary>Ignore <c>@page</c> size and margin rules in the document's CSS.</summary>
    public bool IgnoreCssPageRules { get; set; }

    /// <summary>
    /// Directory used to resolve relative image and stylesheet paths. Defaults to the HTML file's folder
    /// when converting a file; when converting a string, relative paths are not resolved unless this is set.
    /// </summary>
    public string? BaseDirectory { get; set; }

    /// <summary>
    /// Optional callback that returns the bytes for a resource URL (images and stylesheets), or null to fall back to the
    /// default handling. Use it to serve http(s) resources, embedded resources or a cache: the converter never touches the network itself.
    /// </summary>
    public Func<string, byte[]?>? ResourceResolver { get; set; }

    /// <summary>
    /// Allow images and stylesheets to be read from anywhere on the local file system: absolute paths, <c>file:</c> URLs
    /// and relative paths that leave <see cref="BaseDirectory"/>. Default: false, so a document reads only files inside its
    /// base directory (the input file's folder for <c>ConvertFile</c>), and none when there is no base directory.
    /// Turn it on only for HTML you trust: otherwise a document can pull any file the process can read into its output.
    /// <c>data:</c> URIs and <see cref="ResourceResolver"/> always work.
    /// </summary>
    public bool AllowFileAccess { get; set; }

    /// <summary>
    /// TrueType/OpenType fonts that CSS <c>font-family</c> can use, in addition to the document's own <c>@font-face</c>
    /// rules. Used fonts are embedded as subsets, so text can use any Unicode character they cover. Without them, text
    /// uses the standard PDF fonts (Helvetica, Times, Courier), which cover Western European characters only.
    /// A collection can be shared by many options and conversions.
    /// </summary>
    /// <example><code>options.Fonts.AddFile("fonts/OpenSans-Regular.ttf").AddFile("fonts/OpenSans-Bold.ttf");</code></example>
    public FontCollection Fonts { get; set; } = new();

    /// <summary>
    /// Look up font families that neither <see cref="Fonts"/> nor the document's <c>@font-face</c> rules provide in the
    /// fonts installed on this machine (C:\Windows\Fonts, /Library/Fonts, /usr/share/fonts and the user's own font
    /// folders), and embed them like registered fonts. CSS <c>local()</c> sources can then name installed fonts too.
    /// Default: false, so output does not depend on the machine.
    /// </summary>
    /// <remarks>
    /// The folders are scanned once per process, the first time a document asks for a family that is not registered,
    /// which can take a moment on a machine with many fonts. Names that mean a standard PDF font (Arial, Helvetica,
    /// Times New Roman, Courier New and the generic families) keep using it, and installed fonts are not searched for
    /// characters the listed fonts lack. Documents can only choose installed fonts by name; no other files are read.
    /// </remarks>
    public bool UseSystemFonts { get; set; }

    /// <summary>
    /// Called with each font family that a document asks for in <c>font-family</c> but that no font provides, so its
    /// text is drawn in a later family of the list or a standard font instead. Each family is reported once per
    /// conversion; families listed after one that was found, and generic families such as <c>sans-serif</c>, are not
    /// reported. Use it to log the substitution, or throw from it to fail the conversion. Default: null.
    /// </summary>
    /// <example><code>options.MissingFont = family => logger.LogWarning("Font {Family} is not registered", family);</code></example>
    public Action<string>? MissingFont { get; set; }

    /// <summary>Extra CSS applied after the document's own styles.</summary>
    public string? UserStyleSheet { get; set; }

    /// <summary>
    /// Add bookmarks (the PDF outline) built from the h1-h6 headings, nested by level. Default: true.
    /// Links (<c>&lt;a href&gt;</c>) are always clickable: external URLs open in the browser and <c>#id</c> links jump to the element.
    /// </summary>
    public bool Bookmarks { get; set; } = true;

    /// <summary>
    /// Turn form controls into fillable PDF form fields (AcroForm). Default: true. Text inputs and textareas become
    /// text fields, check boxes and radio buttons become buttons (radio buttons with the same name are one field),
    /// and selects become drop-down or list boxes, named after their <c>name</c> attribute and filled with their
    /// current values. Disabled controls and buttons are drawn but not fillable. When false, every control is drawn
    /// as it is, with its value, as in a browser's print output.
    /// </summary>
    public bool FormFields { get; set; } = true;

    /// <summary>
    /// The most pages a document may have; a longer one throws <see cref="PdfLimitException"/>. Default: 100,000.
    /// Lower it when converting untrusted HTML, where a few bytes of CSS (a huge height, thousands of page breaks)
    /// can ask for millions of pages.
    /// </summary>
    public int MaxPages { get; set; } = 100_000;

    /// <summary>Compress page content streams. Default: true.</summary>
    public bool Compress { get; set; } = true;

    /// <summary>Compression level for content streams. Default: <see cref="CompressionLevel.Fastest"/>.</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;

    /// <summary>Document title. Defaults to the HTML &lt;title&gt;.</summary>
    public string? Title { get; set; }

    /// <summary>The document's author, shown in the PDF viewer's document properties. Default: none.</summary>
    public string? Author { get; set; }

    /// <summary>The document's subject or description, shown in the PDF viewer's document properties. Default: none.</summary>
    public string? Subject { get; set; }

    /// <summary>Keywords for the document properties, e.g. "invoice, 2026".</summary>
    public string? Keywords { get; set; }

    /// <summary>The application that created the original document, shown as "Application" in viewers.</summary>
    public string? Creator { get; set; }

    /// <summary>The application that produced the PDF. Default: "PdfPecker".</summary>
    public string? Producer { get; set; }

    /// <summary>
    /// Custom document properties, such as an order or employee number, written to the document information dictionary
    /// (and to the XMP metadata for PDF/A). Names cannot be the standard ones (Title, Author, Subject, Keywords, Creator,
    /// Producer, CreationDate, ModDate, Trapped); null values are skipped.
    /// </summary>
    public IDictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();

    /// <summary>Text centred in the bottom margin of every page. <c>{page}</c> and <c>{pages}</c> are replaced, e.g. "Page {page} of {pages}".</summary>
    public string? FooterText { get; set; }

    /// <summary>Text centred in the top margin of every page. Supports <c>{page}</c> and <c>{pages}</c>.</summary>
    public string? HeaderText { get; set; }

    /// <summary>A text or image stamped on every page, e.g. a diagonal "DRAFT".</summary>
    public PdfWatermark? Watermark { get; set; }

    /// <summary>Password protection and permissions (AES-256). Cannot be combined with PDF/A <see cref="Conformance"/>.</summary>
    public PdfSecurity? Security { get; set; }

    /// <summary>Produce an archival PDF/A file. Default: <see cref="PdfConformance.None"/>. Cannot be combined with <see cref="Security"/>.</summary>
    public PdfConformance Conformance { get; set; }

    /// <summary>
    /// Make a Factur-X / ZUGFeRD e-invoice: the invoice's CII XML is attached to the PDF with the metadata that tells
    /// software it is there. The file is PDF/A-3b, so <see cref="Conformance"/> must be None (it is then raised to
    /// PDF/A-3b) or <see cref="PdfConformance.PdfA3b"/>, and <see cref="Security"/> must be null. Default: null.
    /// </summary>
    public PdfEInvoice? EInvoice { get; set; }

    /// <summary>
    /// Embed the bundled Liberation fonts (metric-compatible with Helvetica, Times and Courier) instead of referring to
    /// the non-embedded standard PDF fonts. Text then looks the same in every viewer and can use any character those
    /// fonts cover, at the cost of a larger file. Default: false. Always on for PDF/A, which requires embedded fonts.
    /// </summary>
    public bool EmbedStandardFonts { get; set; }

    /// <summary>
    /// Digitally sign the PDF with a certificate. Default: null, unsigned. Works together with <see cref="Security"/>
    /// and <see cref="Conformance"/>.
    /// </summary>
    public PdfSignatureOptions? Signature { get; set; }
}
