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
    /// Allow images and stylesheets to be read from the local file system. Default: true. Set it to false when converting
    /// untrusted HTML (for example in a web service), so a document cannot pull files from the server into its output.
    /// <c>data:</c> URIs and <see cref="ResourceResolver"/> keep working.
    /// </summary>
    public bool AllowFileAccess { get; set; } = true;

    /// <summary>Extra CSS applied after the document's own styles.</summary>
    public string? UserStyleSheet { get; set; }

    /// <summary>
    /// Add bookmarks (the PDF outline) built from the h1-h6 headings, nested by level. Default: true.
    /// Links (<c>&lt;a href&gt;</c>) are always clickable: external URLs open in the browser and <c>#id</c> links jump to the element.
    /// </summary>
    public bool Bookmarks { get; set; } = true;

    /// <summary>Compress page content streams. Default: true.</summary>
    public bool Compress { get; set; } = true;

    /// <summary>Compression level for content streams. Default: <see cref="CompressionLevel.Fastest"/>.</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;

    /// <summary>Document title. Defaults to the HTML &lt;title&gt;.</summary>
    public string? Title { get; set; }

    public string? Author { get; set; }

    public string? Subject { get; set; }

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
    /// Digitally sign the PDF with a certificate. Default: null, unsigned. Works together with <see cref="Security"/>
    /// and <see cref="Conformance"/>.
    /// </summary>
    public PdfSignatureOptions? Signature { get; set; }
}
