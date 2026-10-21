using System.IO.Compression;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>The file format <see cref="PdfToImage"/> and <see cref="RenderedImage.Encode"/> produce.</summary>
public enum PdfImageFormat
{
    /// <summary>Lossless PNG: RGB when the page is opaque, RGBA otherwise. The default.</summary>
    Png,

    /// <summary>
    /// Baseline JPEG (JFIF). Lossy and without transparency: transparent areas are flattened onto
    /// <see cref="ImageOptions.BackgroundColor"/> (composited over white when that colour is itself translucent).
    /// </summary>
    Jpeg,

    /// <summary>Lossless WebP (VP8L), keeping alpha exactly. Usually smaller than PNG.</summary>
    WebP,

    /// <summary>
    /// TIFF, one page per file here; <see cref="PdfToImage.ConvertToTiff(byte[], ImageOptions?)"/> writes every page into one multi-page file.
    /// Colour, greyscale or black and white after <see cref="ImageOptions.TiffColorMode"/>, compressed as
    /// <see cref="ImageOptions.TiffCompression"/> says.
    /// </summary>
    Tiff,
}

/// <summary>The pixel format of TIFF output.</summary>
public enum TiffColorMode
{
    /// <summary>8-bit RGB, with an alpha channel when the page has transparency. The default.</summary>
    Color,

    /// <summary>8-bit greyscale. Transparency is flattened onto <see cref="ImageOptions.BackgroundColor"/>.</summary>
    Grayscale,

    /// <summary>
    /// 1 bit per pixel: pixels darker than <see cref="ImageOptions.TiffThreshold"/> are black. The usual choice for fax and
    /// document archives. Transparency is flattened onto <see cref="ImageOptions.BackgroundColor"/>.
    /// </summary>
    BlackAndWhite,
}

/// <summary>The compression of TIFF output. Every option is lossless.</summary>
public enum TiffCompression
{
    /// <summary>No compression: the largest files, readable by anything.</summary>
    None,

    /// <summary>LZW with the horizontal predictor. Read by every TIFF reader; the default for colour and greyscale.</summary>
    Lzw,

    /// <summary>Deflate (zlib) with the horizontal predictor. Usually smaller than LZW; not read by some older software.</summary>
    Deflate,

    /// <summary>PackBits run-length coding: fast, and fine for flat colours, but weak on anything else.</summary>
    PackBits,

    /// <summary>
    /// CCITT Group 4 fax coding, the smallest by far for text pages and what fax and archive systems expect. It is bilevel
    /// only, so it always writes black and white whatever <see cref="ImageOptions.TiffColorMode"/> says. The default for
    /// <see cref="TiffColorMode.BlackAndWhite"/>.
    /// </summary>
    CcittGroup4,
}

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

    /// <summary>The encoded format of <see cref="PdfToImage"/> results and of <see cref="RenderedImage.Encode"/>. Default <see cref="PdfImageFormat.Png"/>.</summary>
    public PdfImageFormat Format { get; set; } = PdfImageFormat.Png;

    private int _quality = 85;

    /// <summary>
    /// Quality from 1 to 100, default 85. For <see cref="PdfImageFormat.Jpeg"/> it scales the standard quantisation
    /// tables as libjpeg does (90 and up also keep full-resolution colour instead of 4:2:0 subsampling). WebP output is
    /// always lossless, so for <see cref="PdfImageFormat.WebP"/> it is the compression effort: higher values search
    /// harder for smaller files and take longer, but never change a pixel. PNG ignores it (see <see cref="PngCompression"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and 100.</exception>
    public int Quality
    {
        get => _quality;
        set => _quality = value is >= 1 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Quality must be between 1 and 100.");
    }

    /// <summary>Upper limit for either image dimension in pixels, protecting against huge pages. Default 20000.</summary>
    public int MaxDimension { get; set; } = 20000;

    /// <summary>The pixel format of TIFF output. Default <see cref="PdfPecker.TiffColorMode.Color"/>.</summary>
    public TiffColorMode TiffColorMode { get; set; } = TiffColorMode.Color;

    /// <summary>
    /// The compression of TIFF output. Null, the default, picks <see cref="PdfPecker.TiffCompression.CcittGroup4"/> for
    /// black and white and <see cref="PdfPecker.TiffCompression.Lzw"/> otherwise.
    /// </summary>
    public TiffCompression? TiffCompression { get; set; }

    private int _tiffThreshold = 128;

    /// <summary>
    /// For black and white TIFF output: pixels whose brightness (0 to 255) is below this become black. Default 128;
    /// raise it to keep light grey text and thin lines, lower it to drop pale backgrounds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and 255.</exception>
    public int TiffThreshold
    {
        get => _tiffThreshold;
        set => _tiffThreshold = value is >= 1 and <= 255 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "TiffThreshold must be between 1 and 255.");
    }
}

/// <summary>A rendered page image.</summary>
public sealed class RenderedImage
{
    private readonly uint[] _premultiplied; // 0xAABBGGRR, premultiplied alpha
    private readonly CompressionLevel _compression;
    private readonly int _quality;
    private readonly uint _matte; // 0xRRGGBB that JPEG, greyscale and black and white TIFF flatten transparency onto
    private readonly TiffColorMode _tiffColorMode;
    private readonly TiffCompression? _tiffCompression;
    private readonly int _tiffThreshold;
    private readonly double _dpiX, _dpiY;
    private byte[]? _rgba;

    internal RenderedImage(int width, int height, uint[] premultiplied, ImageOptions options, double dpiX = 72, double dpiY = 72)
    {
        Width = width;
        Height = height;
        _dpiX = dpiX;
        _dpiY = dpiY;
        _tiffColorMode = options.TiffColorMode;
        _tiffCompression = options.TiffCompression;
        _tiffThreshold = options.TiffThreshold;
        _premultiplied = premultiplied;
        _compression = options.PngCompression;
        Format = options.Format;
        _quality = options.Quality;
        // BackgroundColor over white, so a transparent or translucent background still gives a defined matte.
        uint bg = options.BackgroundColor, a = bg >> 24;
        uint Channel(int shift) => (((bg >> shift) & 255) * a + 255 * (255 - a) + 127) / 255;
        _matte = Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    /// <summary>The format <see cref="Encode"/> and <see cref="Save"/> write, from <see cref="ImageOptions.Format"/>.</summary>
    public PdfImageFormat Format { get; }

    /// <summary>The pixels, premultiplied, as 0xAABBGGRR.</summary>
    internal uint[] Premultiplied => _premultiplied;

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Pixels as R, G, B, A bytes (straight, not premultiplied alpha), <c>Width * 4</c> bytes per row from the top.</summary>
    public byte[] Rgba => _rgba ??= PngEncoder.ToStraightRgba(_premultiplied);

    /// <summary>Encodes the image as PNG (RGB when fully opaque, else RGBA).</summary>
    public byte[] ToPng() => PngEncoder.Encode(_premultiplied, Width, Height, _compression);

    /// <summary>Writes the image as a PNG file.</summary>
    public void SavePng(string path) => AtomicFile.WriteAllBytes(path, ToPng());

    /// <summary>
    /// Encodes the image as baseline JPEG. JPEG has no alpha: transparent and translucent pixels are flattened onto
    /// <see cref="ImageOptions.BackgroundColor"/> (composited over white).
    /// </summary>
    /// <param name="quality">1 to 100; null uses <see cref="ImageOptions.Quality"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is not between 1 and 100.</exception>
    /// <exception cref="NotSupportedException">A dimension exceeds 65,535 pixels.</exception>
    public byte[] ToJpeg(int? quality = null) => JpegEncoder.Encode(_premultiplied, Width, Height, quality ?? _quality, _matte);

    /// <summary>Encodes the image as lossless WebP (VP8L), alpha included.</summary>
    /// <param name="quality">The compression effort, 1 to 100; null uses <see cref="ImageOptions.Quality"/>. It never changes pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is not between 1 and 100.</exception>
    /// <exception cref="NotSupportedException">A dimension exceeds 16,384 pixels, the WebP limit.</exception>
    public byte[] ToWebP(int? quality = null) => WebPEncoder.Encode(_premultiplied, Width, Height, quality ?? _quality);

    /// <summary>
    /// Encodes the image as a single-page TIFF, with its resolution recorded. Colour mode, compression and threshold
    /// come from <see cref="ImageOptions.TiffColorMode"/>, <see cref="ImageOptions.TiffCompression"/> and
    /// <see cref="ImageOptions.TiffThreshold"/>.
    /// </summary>
    /// <param name="compression">The compression; null uses <see cref="ImageOptions.TiffCompression"/>.</param>
    public byte[] ToTiff(TiffCompression? compression = null) => TiffEncoder.Write(new[] { EncodeTiffPage(compression) });

    internal TiffEncoder.Page EncodeTiffPage(TiffCompression? compression = null)
    {
        var mode = _tiffColorMode;
        var c = compression ?? _tiffCompression ?? (mode == TiffColorMode.BlackAndWhite ? TiffCompression.CcittGroup4 : TiffCompression.Lzw);
        return TiffEncoder.EncodePage(_premultiplied, Width, Height, _dpiX, _dpiY, c, mode, _matte, _tiffThreshold);
    }

    /// <summary>Encodes the image in <see cref="Format"/>.</summary>
    public byte[] Encode() => Format switch
    {
        PdfImageFormat.Jpeg => ToJpeg(),
        PdfImageFormat.WebP => ToWebP(),
        PdfImageFormat.Tiff => ToTiff(),
        _ => ToPng(),
    };

    /// <summary>Writes the image to a file in <see cref="Format"/> (the path is used as given, whatever its extension).</summary>
    public void Save(string path) => AtomicFile.WriteAllBytes(path, Encode());

    /// <summary>The usual file extension of a format, with the dot: ".png", ".jpg", ".webp" or ".tif".</summary>
    public static string GetFileExtension(PdfImageFormat format) => format switch
    {
        PdfImageFormat.Jpeg => ".jpg",
        PdfImageFormat.WebP => ".webp",
        PdfImageFormat.Tiff => ".tif",
        _ => ".png",
    };
}

/// <summary>
/// An opened PDF document that renders pages to images. Parsing, fonts (TrueType, CFF, Type 1, Type 3), images
/// (including JPEG and CCITT fax decoding), rasterization and PNG, JPEG, WebP and TIFF encoding are implemented in this library.
/// </summary>
/// <remarks>Instances are thread-safe: several pages can be rendered in parallel. Fonts and images are shared between pages.</remarks>
public sealed class PdfRenderer
{
    private readonly RenderDocument _doc;

    private PdfRenderer(RenderDocument doc) => _doc = doc;

    /// <summary>The parsed document, for the converters that also read its text and links.</summary>
    internal RenderDocument Document => _doc;

    /// <summary>Opens a PDF from bytes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
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
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <paramref name="password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The stream is not a readable PDF.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    public static PdfRenderer Open(Stream pdf, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var ms = new MemoryStream();
        pdf.CopyTo(ms);
        return Open(ms.ToArray(), password);
    }

    /// <summary>Opens a PDF file.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <paramref name="password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public static PdfRenderer OpenFile(string path, string? password = null) => Open(File.ReadAllBytes(path), password);

    /// <summary>Number of pages.</summary>
    public int PageCount => _doc.Pages.Count;

    /// <summary>Displayed page size in points (after page rotation) of the page at <paramref name="pageIndex"/>, 0-based (the first page is 0).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        CheckIndex(pageIndex);
        return _doc.PageSize(pageIndex);
    }

    /// <summary>
    /// Renders one page. <paramref name="pageIndex"/> is 0-based (the first page is 0), unlike <see cref="PdfPageRange"/>
    /// and <see cref="PdfText"/>, which number pages from 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document, or <see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">The page cannot be rendered, for example because it is too complex.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
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
        // A requested Dpi is recorded as given, not as the slightly stretched scale below.
        double? dpi = options.Width == null && options.Height == null && limit >= 1 ? options.Dpi : null;
        if (limit < 1) { sx *= limit; sy *= limit; }
        w = Math.Max(1, (int)Math.Round(pw * sx));
        h = Math.Max(1, (int)Math.Round(ph * sy));
        return RenderSized(pageIndex, w, h, options, dpi, cancellationToken);
    }

    /// <summary>
    /// Renders one page onto exactly <paramref name="width"/> × <paramref name="height"/> pixels, stretching it a little
    /// where its shape differs from them. <see cref="RenderPage"/> keeps the page's shape, so a size it was given can come
    /// out a pixel short on one side; rasters that are drawn over a known rectangle need the size they asked for.
    /// </summary>
    internal RenderedImage RenderPageExact(int pageIndex, int width, int height, ImageOptions options, CancellationToken cancellationToken = default)
    {
        CheckIndex(pageIndex);
        return RenderSized(pageIndex, width, height, options, null, cancellationToken);
    }

    private RenderedImage RenderSized(int pageIndex, int w, int h, ImageOptions options, double? dpi, CancellationToken cancellationToken)
    {
        var (pw, ph) = _doc.PageSize(pageIndex);
        // Stretch slightly so the page fills the rounded pixel size exactly.
        double sx = w / pw, sy = h / ph;

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
        return new RenderedImage(w, h, canvas.Pixels, options, dpi ?? sx * 72, dpi ?? sy * 72);
    }

    /// <summary>Renders one page and encodes it as PNG. <paramref name="pageIndex"/> is 0-based (the first page is 0).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document, or <see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">The page cannot be rendered, for example because it is too complex.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public byte[] RenderPageToPng(int pageIndex, ImageOptions? options = null, CancellationToken cancellationToken = default)
        => RenderPage(pageIndex, options, cancellationToken).ToPng();

    /// <summary>Renders one page and encodes it in <see cref="ImageOptions.Format"/>. <paramref name="pageIndex"/> is 0-based (the first page is 0).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document, or <see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">The page cannot be rendered, for example because it is too complex.</exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ImageOptions.Format"/> is JPEG or WebP and the image is larger than the format allows (65,535 pixels
    /// per side for JPEG, 16,384 for WebP).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public byte[] RenderPageEncoded(int pageIndex, ImageOptions? options = null, CancellationToken cancellationToken = default)
        => RenderPage(pageIndex, options, cancellationToken).Encode();

    /// <summary>Renders all pages (in parallel) and encodes each as PNG, in page order.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">A page cannot be rendered, for example because it is too complex.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public byte[][] RenderAllToPng(ImageOptions? options = null, CancellationToken cancellationToken = default)
        => RenderAll(options, static img => img.ToPng(), cancellationToken);

    /// <summary>Renders all pages (in parallel) and encodes each in <see cref="ImageOptions.Format"/>, in page order.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">A page cannot be rendered, for example because it is too complex.</exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ImageOptions.Format"/> is JPEG or WebP and the image is larger than the format allows (65,535 pixels
    /// per side for JPEG, 16,384 for WebP).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public byte[][] RenderAllEncoded(ImageOptions? options = null, CancellationToken cancellationToken = default)
        => RenderAll(options, static img => img.Encode(), cancellationToken);

    /// <summary>
    /// Renders all pages (in parallel) into one multi-page TIFF, in page order, after
    /// <see cref="ImageOptions.TiffColorMode"/>, <see cref="ImageOptions.TiffCompression"/> and
    /// <see cref="ImageOptions.TiffThreshold"/>. <see cref="ImageOptions.Format"/> is not used.
    /// </summary>
    /// <exception cref="NotSupportedException">The file would pass 4 GB, the limit of the TIFF format.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">
    /// The PDF has no pages, which a TIFF file cannot hold, or a page cannot be rendered, for example because it is too
    /// complex.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public byte[] RenderAllToTiff(ImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        // A TIFF file needs at least one page, and a PDF without pages is malformed rather than a caller's mistake.
        if (PageCount == 0) throw new InvalidDataException("The PDF has no pages, and a TIFF file needs at least one.");
        return TiffEncoder.Write(RenderAll(options, static img => img.EncodeTiffPage(), cancellationToken));
    }

    private T[] RenderAll<T>(ImageOptions? options, Func<RenderedImage, T> encode, CancellationToken cancellationToken)
    {
        options ??= new ImageOptions();
        var result = new T[PageCount];
        try
        {
            Parallel.For(0, PageCount, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            }, i => result[i] = encode(RenderPage(i, options, cancellationToken)));
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

/// <summary>
/// Converts PDF pages to PNG, JPEG, WebP or TIFF images (<see cref="ImageOptions.Format"/>), or a whole PDF to one
/// multi-page TIFF (<see cref="ConvertToTiff(byte[], ImageOptions?)"/>). Everything is implemented in
/// this library, with no dependencies beyond the .NET base class library.
/// </summary>
/// <remarks>All methods are static and thread-safe. For rendering several pages of one document, <see cref="PdfRenderer"/> avoids re-parsing.</remarks>
public static class PdfToImage
{
    /// <summary>Returns the number of pages in a PDF.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <paramref name="password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    public static int GetPageCount(byte[] pdf, string? password = null) => PdfRenderer.Open(pdf, password).PageCount;

    /// <summary>Returns the number of pages in a PDF read from <paramref name="pdf"/> (from its current position to its end). The stream is not disposed.</summary>
    public static int GetPageCount(Stream pdf, string? password = null) => PdfRenderer.Open(pdf, password).PageCount;

    /// <summary>
    /// Renders one page to an image in <see cref="ImageOptions.Format"/> (PNG by default). <paramref name="pageIndex"/> is
    /// 0-based (the first page is 0), unlike <see cref="PdfPageRange"/> and <see cref="PdfText"/>, which number pages from 1.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <see cref="ImageOptions.Password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF, or the page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">
    /// The PDF uses an encryption handler other than the standard one, or the image is larger than JPEG or WebP allows
    /// (65,535 and 16,384 pixels per side).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document, or <see cref="ImageOptions.Dpi"/> is not positive.</exception>
    public static byte[] ConvertPage(byte[] pdf, int pageIndex = 0, ImageOptions? options = null)
        => PdfRenderer.Open(pdf, options?.Password).RenderPageEncoded(pageIndex, options);

    /// <summary>
    /// Renders one page to an image in <see cref="ImageOptions.Format"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="pageIndex">The page, 0-based: the first page is 0.</param>
    /// <param name="options">Size, format and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering.</param>
    public static byte[] ConvertPage(byte[] pdf, int pageIndex, ImageOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PdfRenderer.Open(pdf, options?.Password).RenderPageEncoded(pageIndex, options, cancellationToken);
    }

    /// <summary>
    /// Renders one page of a PDF read from <paramref name="pdf"/> (from its current position to its end) to an image in
    /// <see cref="ImageOptions.Format"/>. The stream is not disposed.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="pageIndex">The page, 0-based: the first page is 0.</param>
    /// <param name="options">Size, format and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering.</param>
    public static byte[] ConvertPage(Stream pdf, int pageIndex = 0, ImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PdfRenderer.Open(pdf, options?.Password).RenderPageEncoded(pageIndex, options, cancellationToken);
    }

    /// <summary>Renders every page to an image in <see cref="ImageOptions.Format"/>, in page order. Pages are rendered in parallel.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <see cref="ImageOptions.Password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">
    /// The PDF uses an encryption handler other than the standard one, or an image is larger than JPEG or WebP allows
    /// (65,535 and 16,384 pixels per side).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    public static byte[][] Convert(byte[] pdf, ImageOptions? options = null)
        => PdfRenderer.Open(pdf, options?.Password).RenderAllEncoded(options);

    /// <summary>
    /// Renders every page to an image in <see cref="ImageOptions.Format"/>, in page order, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled. Pages are rendered
    /// in parallel.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Size, format and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering; no further page is started once it is cancelled.</param>
    public static byte[][] Convert(byte[] pdf, ImageOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PdfRenderer.Open(pdf, options?.Password).RenderAllEncoded(options, cancellationToken);
    }

    /// <summary>
    /// Renders every page of a PDF read from <paramref name="pdf"/> (from its current position to its end) to an image in
    /// <see cref="ImageOptions.Format"/>, in page order. The stream is not disposed.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Size, format and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering; no further page is started once it is cancelled.</param>
    public static byte[][] Convert(Stream pdf, ImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PdfRenderer.Open(pdf, options?.Password).RenderAllEncoded(options, cancellationToken);
    }

    /// <summary>Renders every page asynchronously on the thread pool.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <see cref="ImageOptions.Password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">
    /// The PDF uses an encryption handler other than the standard one, or an image is larger than JPEG or WebP allows
    /// (65,535 and 16,384 pixels per side).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<byte[][]> ConvertAsync(byte[] pdf, ImageOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() => PdfRenderer.Open(pdf, options?.Password).RenderAllEncoded(options, cancellationToken), cancellationToken);

    /// <summary>
    /// Renders every page into one multi-page TIFF, in page order: colour, greyscale or black and white with
    /// <see cref="ImageOptions.TiffColorMode"/>, compressed after <see cref="ImageOptions.TiffCompression"/>, at
    /// <see cref="ImageOptions.Dpi"/>. Pages are rendered in parallel.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <see cref="ImageOptions.Password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF, has no pages, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">
    /// The PDF uses an encryption handler other than the standard one, or the file would pass 4 GB, the limit of the
    /// TIFF format.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    public static byte[] ConvertToTiff(byte[] pdf, ImageOptions? options = null)
        => PdfRenderer.Open(pdf, options?.Password).RenderAllToTiff(options);

    /// <summary>
    /// Renders every page into one multi-page TIFF (see <see cref="ConvertToTiff(byte[], ImageOptions?)"/>), stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Size, TIFF settings and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering; no further page is started once it is cancelled.</param>
    /// <exception cref="NotSupportedException">The file would pass 4 GB, the limit of the TIFF format.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF, has no pages, or a page cannot be rendered.</exception>
    public static byte[] ConvertToTiff(byte[] pdf, ImageOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PdfRenderer.Open(pdf, options?.Password).RenderAllToTiff(options, cancellationToken);
    }

    /// <summary>
    /// Renders every page of a PDF read from <paramref name="input"/> (from its current position to its end) into one
    /// multi-page TIFF (see <see cref="ConvertToTiff(byte[], ImageOptions?)"/>) and writes it to <paramref name="output"/>.
    /// The streams are not disposed, and nothing is written unless the rendering completes.
    /// </summary>
    /// <param name="input">The PDF.</param>
    /// <param name="output">Where the TIFF is written.</param>
    /// <param name="options">Size, TIFF settings and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering; no further page is started once it is cancelled.</param>
    /// <exception cref="NotSupportedException">The file would pass 4 GB, the limit of the TIFF format.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF, has no pages, or a page cannot be rendered.</exception>
    public static void ConvertToTiff(Stream input, Stream output, ImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        var tiff = PdfRenderer.Open(input, options?.Password).RenderAllToTiff(options, cancellationToken);
        output.Write(tiff, 0, tiff.Length);
    }

    /// <summary>Renders every page into one multi-page TIFF (see <see cref="ConvertToTiff(byte[], ImageOptions?)"/>) on the thread pool.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <see cref="ImageOptions.Password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable PDF, has no pages, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">
    /// The PDF uses an encryption handler other than the standard one, or the file would pass 4 GB, the limit of the
    /// TIFF format.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<byte[]> ConvertToTiffAsync(byte[] pdf, ImageOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() => PdfRenderer.Open(pdf, options?.Password).RenderAllToTiff(options, cancellationToken), cancellationToken);

    /// <summary>Renders a PDF file into one multi-page TIFF file (see <see cref="ConvertToTiff(byte[], ImageOptions?)"/>).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> or <paramref name="tiffPath"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <see cref="ImageOptions.Password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF, has no pages, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">
    /// The PDF uses an encryption handler other than the standard one, or the file would pass 4 GB, the limit of the
    /// TIFF format.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="IOException">
    /// The PDF file cannot be read (it does not exist, for example) or the TIFF file cannot be written. The TIFF file is
    /// written only once rendering has succeeded, so a failure leaves any earlier file at that path unchanged.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the PDF file or write the TIFF file.</exception>
    public static void ConvertFileToTiff(string pdfPath, string tiffPath, ImageOptions? options = null)
        => ConvertFileToTiff(pdfPath, tiffPath, options, CancellationToken.None);

    /// <summary>
    /// Renders a PDF file into one multi-page TIFF file (see <see cref="ConvertToTiff(byte[], ImageOptions?)"/>), stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled; the TIFF file is
    /// then left as it was.
    /// </summary>
    /// <param name="pdfPath">The PDF file.</param>
    /// <param name="tiffPath">The TIFF file to write; it appears only once it is complete.</param>
    /// <param name="options">Size, TIFF settings and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering; no further page is started once it is cancelled.</param>
    /// <exception cref="InvalidDataException">The file is not a readable PDF, has no pages, or a page cannot be rendered.</exception>
    public static void ConvertFileToTiff(string pdfPath, string tiffPath, ImageOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.WriteAllBytes(tiffPath, PdfRenderer.OpenFile(pdfPath, options?.Password).RenderAllToTiff(options, cancellationToken));
    }

    /// <summary>
    /// Renders a PDF file's pages to image files in <paramref name="outputDirectory"/>, named
    /// <c>{file name}-{page number}</c> plus <c>.png</c>, <c>.jpg</c>, <c>.webp</c> or <c>.tif</c> after <see cref="ImageOptions.Format"/>
    /// (page numbers start at 1). Returns the written paths.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> or <paramref name="outputDirectory"/> is null.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and <see cref="ImageOptions.Password"/> does not open it.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">
    /// The PDF uses an encryption handler other than the standard one, or an image is larger than JPEG or WebP allows
    /// (65,535 and 16,384 pixels per side).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="IOException">
    /// The PDF file cannot be read (it does not exist, for example), or the folder or an image file cannot be written.
    /// Every page is rendered before the first image is written, and each image is written whole or not at all.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the PDF file or write to the folder.</exception>
    public static IReadOnlyList<string> ConvertFile(string pdfPath, string outputDirectory, ImageOptions? options = null)
        => ConvertFile(pdfPath, outputDirectory, options, CancellationToken.None);

    /// <summary>
    /// Renders a PDF file's pages to image files in <paramref name="outputDirectory"/>, named
    /// <c>{file name}-{page number}</c> plus <c>.png</c>, <c>.jpg</c>, <c>.webp</c> or <c>.tif</c> after <see cref="ImageOptions.Format"/>
    /// (page numbers in file names start at 1), stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled. Every page is rendered before the first file is written, and
    /// cancelling while the files are written stops before the next one.
    /// </summary>
    /// <param name="pdfPath">The PDF file.</param>
    /// <param name="outputDirectory">The folder for the images; it is created when missing.</param>
    /// <param name="options">Size, format and password; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the rendering and the writing.</param>
    /// <returns>The written paths, in page order.</returns>
    public static IReadOnlyList<string> ConvertFile(string pdfPath, string outputDirectory, ImageOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var renderer = PdfRenderer.OpenFile(pdfPath, options?.Password);
        Directory.CreateDirectory(outputDirectory);
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        var images = renderer.RenderAllEncoded(options, cancellationToken);
        var extension = RenderedImage.GetFileExtension(options?.Format ?? PdfImageFormat.Png);
        var paths = new string[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths[i] = Path.Combine(outputDirectory, $"{name}-{i + 1}{extension}");
            AtomicFile.WriteAllBytes(paths[i], images[i]);
        }
        return paths;
    }
}
