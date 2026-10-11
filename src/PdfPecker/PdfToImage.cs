using System.IO.Compression;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Options for rendering PDF pages to images.</summary>
public sealed class ImageOptions
{
    /// <summary>Resolution in dots per inch (a PDF point is 1/72 inch). Default 150.</summary>
    public float Dpi { get; set; } = 150;

    /// <summary>If set, the image is scaled to this width in pixels (keeping the aspect ratio unless <see cref="Height"/> is also set; then the page fits inside both).</summary>
    public int? Width { get; set; }

    /// <summary>If set, the image is scaled to this height in pixels (see <see cref="Width"/>).</summary>
    public int? Height { get; set; }

    /// <summary>Background color as 0xAARRGGBB. Default opaque white. Use 0 (or <see cref="Transparent"/>) for a transparent background.</summary>
    public uint BackgroundColor { get; set; } = 0xFFFFFFFF;

    /// <summary>Renders on a transparent background instead of <see cref="BackgroundColor"/>.</summary>
    public bool Transparent { get; set; }

    /// <summary>Whether annotation appearances (form fields, stamps, comments) are drawn. Default true.</summary>
    public bool RenderAnnotations { get; set; } = true;

    /// <summary>Password for encrypted PDFs (user or owner password). Not needed for PDFs that only restrict permissions.</summary>
    public string? Password { get; set; }

    /// <summary>Maximum number of pages rendered at the same time when converting several pages. Default: the processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>PNG compression. Default <see cref="CompressionLevel.Fastest"/>.</summary>
    public CompressionLevel PngCompression { get; set; } = CompressionLevel.Fastest;

    /// <summary>Upper limit for either image dimension in pixels, protecting against huge pages. Default 20000.</summary>
    public int MaxDimension { get; set; } = 20000;
}

/// <summary>A rendered page image.</summary>
public sealed class RenderedImage
{
    private readonly uint[] _premultiplied; // 0xAABBGGRR, premultiplied alpha
    private readonly CompressionLevel _compression;
    private byte[]? _rgba;

    internal RenderedImage(int width, int height, uint[] premultiplied, CompressionLevel compression)
    {
        Width = width;
        Height = height;
        _premultiplied = premultiplied;
        _compression = compression;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Pixels as R, G, B, A bytes (straight, not premultiplied alpha), <c>Width * 4</c> bytes per row from the top.</summary>
    public byte[] Rgba => _rgba ??= PngEncoder.ToStraightRgba(_premultiplied);

    /// <summary>Encodes the image as PNG (RGB when fully opaque, else RGBA).</summary>
    public byte[] ToPng() => PngEncoder.Encode(_premultiplied, Width, Height, _compression);

    /// <summary>Writes the image as a PNG file.</summary>
    public void SavePng(string path) => File.WriteAllBytes(path, ToPng());
}

/// <summary>
/// An opened PDF document that renders pages to images. Parsing, fonts (TrueType, CFF, Type 1, Type 3), images
/// (including JPEG and CCITT fax decoding), rasterization and PNG encoding are implemented in this library.
/// </summary>
/// <remarks>Instances are thread-safe: several pages can be rendered in parallel. Fonts and images are shared between pages.</remarks>
public sealed class PdfRenderer
{
    private readonly RenderDocument _doc;

    private PdfRenderer(RenderDocument doc) => _doc = doc;

    /// <summary>Opens a PDF from bytes.</summary>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <paramref name="password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    public static PdfRenderer Open(byte[] pdf, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        try
        {
            return new PdfRenderer(new RenderDocument(pdf, password));
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>Opens a PDF from a stream (read to the end).</summary>
    public static PdfRenderer Open(Stream pdf, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var ms = new MemoryStream();
        pdf.CopyTo(ms);
        return Open(ms.ToArray(), password);
    }

    /// <summary>Opens a PDF file.</summary>
    public static PdfRenderer OpenFile(string path, string? password = null) => Open(File.ReadAllBytes(path), password);

    /// <summary>Number of pages.</summary>
    public int PageCount => _doc.Pages.Count;

    /// <summary>Displayed page size in points (after page rotation).</summary>
    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        CheckIndex(pageIndex);
        return _doc.PageSize(pageIndex);
    }

    /// <summary>Renders one page (0-based index).</summary>
    public RenderedImage RenderPage(int pageIndex, ImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckIndex(pageIndex);
        options ??= new ImageOptions();
        var (pw, ph) = _doc.PageSize(pageIndex);
        double sx, sy;
        int w, h;
        if (options.Width is int ow && options.Height is int oh)
        {
            double s = Math.Min(ow / pw, oh / ph);
            sx = sy = s;
        }
        else if (options.Width is int ow2) sx = sy = ow2 / pw;
        else if (options.Height is int oh2) sx = sy = oh2 / ph;
        else
        {
            if (!(options.Dpi > 0)) throw new ArgumentOutOfRangeException(nameof(options), "Dpi must be positive.");
            sx = sy = options.Dpi / 72.0;
        }
        int max = Math.Max(1, options.MaxDimension);
        double limit = Math.Min(max / Math.Max(pw * sx, 1e-9), max / Math.Max(ph * sy, 1e-9));
        if (limit < 1) { sx *= limit; sy *= limit; }
        w = Math.Max(1, (int)Math.Round(pw * sx));
        h = Math.Max(1, (int)Math.Round(ph * sy));
        // Stretch slightly so the page fills the rounded pixel size exactly.
        sx = w / pw;
        sy = h / ph;

        uint bg = options.Transparent ? 0 : options.BackgroundColor;
        uint a = bg >> 24;
        uint r = (bg >> 16) & 255, g = (bg >> 8) & 255, b = bg & 255;
        uint premul = Canvas.Pack((int)(r * a / 255), (int)(g * a / 255), (int)(b * a / 255), (int)a);
        Canvas canvas;
        try
        {
            canvas = _doc.Render(pageIndex, sx, sy, w, h, premul, options.RenderAnnotations, cancellationToken);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        return new RenderedImage(w, h, canvas.Pixels, options.PngCompression);
    }

    /// <summary>Renders one page and encodes it as PNG.</summary>
    public byte[] RenderPageToPng(int pageIndex, ImageOptions? options = null, CancellationToken cancellationToken = default)
        => RenderPage(pageIndex, options, cancellationToken).ToPng();

    /// <summary>Renders all pages (in parallel) and encodes each as PNG, in page order.</summary>
    public byte[][] RenderAllToPng(ImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ImageOptions();
        var result = new byte[PageCount][];
        try
        {
            Parallel.For(0, PageCount, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            }, i => result[i] = RenderPageToPng(i, options, cancellationToken));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Report a damaged page the same way RenderPage does, not wrapped.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
        return result;
    }

    private void CheckIndex(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)_doc.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"The document has {_doc.Pages.Count} page(s); page indexes are 0-based.");
    }
}

/// <summary>Converts PDF pages to PNG images. Everything is implemented in this library, with no dependencies beyond the .NET base class library.</summary>
/// <remarks>All methods are static and thread-safe. For rendering several pages of one document, <see cref="PdfRenderer"/> avoids re-parsing.</remarks>
public static class PdfToImage
{
    /// <summary>Returns the number of pages in a PDF.</summary>
    public static int GetPageCount(byte[] pdf, string? password = null) => PdfRenderer.Open(pdf, password).PageCount;

    /// <summary>Renders one page (0-based index) to PNG.</summary>
    public static byte[] ConvertPage(byte[] pdf, int pageIndex = 0, ImageOptions? options = null)
        => PdfRenderer.Open(pdf, options?.Password).RenderPageToPng(pageIndex, options);

    /// <summary>Renders every page to PNG, in page order. Pages are rendered in parallel.</summary>
    public static byte[][] Convert(byte[] pdf, ImageOptions? options = null)
        => PdfRenderer.Open(pdf, options?.Password).RenderAllToPng(options);

    /// <summary>Renders every page asynchronously on the thread pool.</summary>
    public static Task<byte[][]> ConvertAsync(byte[] pdf, ImageOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() => PdfRenderer.Open(pdf, options?.Password).RenderAllToPng(options, cancellationToken), cancellationToken);

    /// <summary>
    /// Renders a PDF file's pages to PNG files in <paramref name="outputDirectory"/>, named
    /// <c>{file name}-{page number}.png</c> (page numbers start at 1). Returns the written paths.
    /// </summary>
    public static IReadOnlyList<string> ConvertFile(string pdfPath, string outputDirectory, ImageOptions? options = null)
    {
        var renderer = PdfRenderer.OpenFile(pdfPath, options?.Password);
        Directory.CreateDirectory(outputDirectory);
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        var pngs = renderer.RenderAllToPng(options);
        var paths = new string[pngs.Length];
        for (int i = 0; i < pngs.Length; i++)
        {
            paths[i] = Path.Combine(outputDirectory, $"{name}-{i + 1}.png");
            File.WriteAllBytes(paths[i], pngs[i]);
        }
        return paths;
    }
}
