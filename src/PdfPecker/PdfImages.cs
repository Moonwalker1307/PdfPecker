using System.Text;
using PdfPecker.Images;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfImages"/>.</summary>
public sealed class PdfImagesOptions
{
    /// <summary>
    /// The password of an encrypted PDF: its user or owner password. Not needed for a PDF that only restricts permissions.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The pages to read, numbered from 1, such as <c>PdfPageRange.Parse("1-3")</c>. Default: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>
    /// Whether images in annotation appearances are included, such as the picture of a signature or a stamp. Default:
    /// true, as for <see cref="PdfTextOptions.IncludeAnnotations"/>.
    /// </summary>
    public bool IncludeAnnotations { get; set; } = true;

    /// <summary>
    /// Whether a JPEG image that a viewer shows as it is comes out as its original JPEG bytes, with no loss and with its
    /// EXIF and other metadata. Default: true. When false, or for a JPEG in CMYK or with a mask, the image is decoded
    /// and written as PNG.
    /// </summary>
    public bool KeepJpeg { get; set; } = true;

    /// <summary>The most distinct images returned; more throws <see cref="PdfLimitException"/>. Default: 10,000.</summary>
    public int MaxImages { get; set; } = 10_000;

    /// <summary>The most pages read; more throws <see cref="PdfLimitException"/>. Default: 100,000.</summary>
    public int MaxPages { get; set; } = 100_000;

    /// <summary>The most pages read, and images decoded, at the same time. Default: the processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
}

/// <summary>An image taken out of a PDF, with every place it is drawn.</summary>
public sealed class PdfExtractedImage
{
    internal PdfExtractedImage(byte[] data, PdfImageFormat format, int width, int height, bool isMask,
        IReadOnlyList<PdfImagePlacement> placements, PdfImageExif? exif, string? xmpMetadata)
    {
        Data = data;
        Format = format;
        Width = width;
        Height = height;
        IsMask = isMask;
        Placements = placements;
        Exif = exif;
        XmpMetadata = xmpMetadata;
    }

    /// <summary>The image file: PNG, or the original JPEG (see <see cref="PdfImagesOptions.KeepJpeg"/>).</summary>
    public byte[] Data { get; }

    /// <summary>The format of <see cref="Data"/>: <see cref="PdfImageFormat.Png"/> or <see cref="PdfImageFormat.Jpeg"/>.</summary>
    public PdfImageFormat Format { get; }

    /// <summary>The file name extension of <see cref="Format"/>, with its dot: ".png" or ".jpg".</summary>
    public string FileExtension => Format == PdfImageFormat.Jpeg ? ".jpg" : ".png";

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Whether the PDF uses the image as a stencil, painting its shape in the current colour. Such an image comes out
    /// black on a transparent background.
    /// </summary>
    public bool IsMask { get; }

    /// <summary>Where the image is drawn, in page order: one entry per draw, so a logo on every page has one per page.</summary>
    public IReadOnlyList<PdfImagePlacement> Placements { get; }

    /// <summary>The EXIF data of a JPEG image, such as the camera and the time it was taken; null when it has none.</summary>
    public PdfImageExif? Exif { get; }

    /// <summary>The XMP metadata packet the PDF attaches to the image, as text; null when it has none.</summary>
    public string? XmpMetadata { get; }

    /// <summary>Writes <see cref="Data"/> to a file.</summary>
    /// <param name="path">The file to write; an existing file is replaced.</param>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not write the file.</exception>
    public void Save(string path) => File.WriteAllBytes(path ?? throw new ArgumentNullException(nameof(path)), Data);
}

/// <summary>Where an image is drawn on a page.</summary>
/// <remarks>
/// Positions and sizes are in points (1/72 inch) on the page as displayed, after its rotation, measured from the top-left
/// corner with y growing downward, as for <see cref="PdfTextPage"/>. The box bounds the image as drawn; a rotated or
/// skewed image fills only part of it, and a clip may hide part of the image.
/// </remarks>
public sealed class PdfImagePlacement
{
    internal PdfImagePlacement(int pageNumber, double x, double y, double width, double height)
    {
        PageNumber = pageNumber;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The page number, from 1.</summary>
    public int PageNumber { get; }

    /// <summary>The left edge of the image's bounding box, in points.</summary>
    public double X { get; }

    /// <summary>The top edge of the image's bounding box, in points from the top of the page.</summary>
    public double Y { get; }

    /// <summary>The width of the image's bounding box, in points.</summary>
    public double Width { get; }

    /// <summary>The height of the image's bounding box, in points.</summary>
    public double Height { get; }

    /// <summary>The box the clips leave visible, where they were collected; null for none.</summary>
    internal (double X0, double Y0, double X1, double Y1)? Clip { get; init; }
}

/// <summary>
/// Extracts the images of PDF pages, any PDFs and not only those this library makes: photos, logos, scans and the
/// pictures inside forms and annotations, each once with every place it is drawn.
/// </summary>
/// <remarks>
/// <para>
/// Images are found by running the pages' content, so only images a page draws are returned, in the order they are
/// first drawn, and images hidden by optional content that is off are left out. Images a PDF uses as soft masks, inside
/// tiling patterns or inside Type 3 font glyphs are not pictures of their own and are not returned.
/// </para>
/// <para>
/// A JPEG comes out unchanged when <see cref="PdfImagesOptions.KeepJpeg"/> allows it. Every other image (Flate, LZW,
/// JPEG 2000, CCITT fax, JBIG2, run-length or raw samples in any colour space) is decoded to 8-bit RGB with its soft mask
/// or colour key mask as alpha and written as PNG. An image that cannot be decoded is left out.
/// </para>
/// </remarks>
public static class PdfImages
{
    /// <summary>
    /// The images of a PDF, each once, in the order they are first drawn, with the pages and places they are drawn.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Password, pages and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or a page is too complex.</exception>
    /// <exception cref="PdfLimitException">
    /// More pages than <see cref="PdfImagesOptions.MaxPages"/> would be read, or more images than
    /// <see cref="PdfImagesOptions.MaxImages"/> found.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A page of <see cref="PdfImagesOptions.Pages"/> is past the last page.</exception>
    public static IReadOnlyList<PdfExtractedImage> Extract(byte[] pdf, PdfImagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfImagesOptions();
        var doc = Open(pdf, options.Password);
        int maxPages = Math.Max(0, options.MaxPages);
        var indices = options.Pages is { } range
            ? range.Resolve(doc.Pages.Count, "the PDF", maxPages)
            : Enumerable.Range(0, Math.Min(doc.Pages.Count, maxPages + 1)).ToList();
        if (indices.Count > maxPages)
            throw new PdfLimitException($"The images of more than {maxPages:N0} pages would be read (PdfImagesOptions.MaxPages).");
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
            CancellationToken = cancellationToken,
        };

        var draws = new List<ImageDraw>[indices.Count];
        Run(() => Parallel.For(0, indices.Count, parallel, i => draws[i] = Collect(doc, indices[i], options, cancellationToken)));

        // Group the draws by image in page order, so that the result does not depend on which page finished first.
        var found = new List<Found>();
        var byObject = new Dictionary<PdfStream, Found>(ReferenceEqualityComparer.Instance);
        int maxImages = Math.Max(0, options.MaxImages);
        for (int i = 0; i < indices.Count; i++)
        {
            int page = indices[i] + 1;
            foreach (var draw in draws[i])
            {
                if (!Bounds(draw.Ctm, out var placement, page)) continue;
                if (draw.XObject is { } xobject && byObject.TryGetValue(xobject, out var known))
                {
                    known.Placements.Add(placement);
                    continue;
                }
                if (found.Count == maxImages)
                    throw new PdfLimitException($"The PDF has more than {maxImages:N0} images (PdfImagesOptions.MaxImages).");
                var image = new Found(draw);
                image.Placements.Add(placement);
                found.Add(image);
                if (draw.XObject is { } x) byObject[x] = image;
            }
        }

        var images = new PdfExtractedImage?[found.Count];
        Run(() => Parallel.For(0, found.Count, parallel, i => images[i] = Decode(doc.File, found[i], options.KeepJpeg, cancellationToken)));
        return images.Where(image => image != null).ToList()!;
    }

    /// <summary>Reads a PDF from <paramref name="pdf"/> (from its current position to the end) and returns its images. The stream is not disposed.</summary>
    /// <inheritdoc cref="Extract(byte[], PdfImagesOptions?, CancellationToken)"/>
    public static IReadOnlyList<PdfExtractedImage> Extract(Stream pdf, PdfImagesOptions? options = null, CancellationToken cancellationToken = default)
        => Extract(ReadAll(pdf), options, cancellationToken);

    /// <summary>The images of the PDF file at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Extract(byte[], PdfImagesOptions?, CancellationToken)"/>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public static IReadOnlyList<PdfExtractedImage> ExtractFile(string path, PdfImagesOptions? options = null, CancellationToken cancellationToken = default)
        => Extract(File.ReadAllBytes(path ?? throw new ArgumentNullException(nameof(path))), options, cancellationToken);

    /// <summary>
    /// The images one page draws, each decoded once, with every place on the page it is drawn, for structured HTML;
    /// images that cannot be decoded are left out.
    /// </summary>
    internal static List<PdfExtractedImage> PageImages(RenderDocument doc, int index, bool annotations, int max, CancellationToken cancellationToken)
    {
        var found = new List<Found>();
        var byObject = new Dictionary<PdfStream, Found>(ReferenceEqualityComparer.Instance);
        foreach (var draw in doc.CollectImages(index, annotations, cancellationToken, clips: true))
        {
            if (!Bounds(draw.Ctm, out var placement, index + 1)) continue;
            if (draw.Clip != null) placement = new PdfImagePlacement(index + 1, placement.X, placement.Y, placement.Width, placement.Height) { Clip = draw.Clip };
            if (draw.XObject is { } xobject && byObject.TryGetValue(xobject, out var known))
            {
                known.Placements.Add(placement);
                continue;
            }
            if (found.Count == max) break;
            var image = new Found(draw);
            image.Placements.Add(placement);
            found.Add(image);
            if (draw.XObject is { } x) byObject[x] = image;
        }
        var images = new List<PdfExtractedImage>(found.Count);
        foreach (var f in found)
            if (Decode(doc.File, f, true, cancellationToken) is { } image) images.Add(image);
        return images;
    }

    /// <summary>An image found on the pages, with the first draw of it and every place it is drawn.</summary>
    private sealed class Found(ImageDraw draw)
    {
        public readonly ImageDraw Draw = draw;
        public readonly List<PdfImagePlacement> Placements = new();
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Report a damaged page the same way a single page does, not wrapped.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
    }

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

    private static List<ImageDraw> Collect(RenderDocument doc, int index, PdfImagesOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return doc.CollectImages(index, options.IncludeAnnotations, cancellationToken);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>The box the unit square of an image's space covers on the page; false when it covers nothing finite.</summary>
    private static bool Bounds(in Matrix ctm, out PdfImagePlacement placement, int page)
    {
        var (x0, y0) = ctm.Apply(0, 0);
        var (x1, y1) = ctm.Apply(1, 0);
        var (x2, y2) = ctm.Apply(1, 1);
        var (x3, y3) = ctm.Apply(0, 1);
        double left = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)), right = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        double top = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)), bottom = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        placement = new PdfImagePlacement(page, left, top, right - left, bottom - top);
        return double.IsFinite(left) && double.IsFinite(right) && double.IsFinite(top) && double.IsFinite(bottom);
    }

    /// <summary>Encodes a found image, or null when it cannot be decoded.</summary>
    private static PdfExtractedImage? Decode(PdfFile file, Found found, bool keepJpeg, CancellationToken cancellationToken)
    {
        var draw = found.Draw;
        // Each image has the decode budget of a page of its own, as the renderer decodes it within one.
        using var scope = PdfFile.BeginRender(cancellationToken);
        try
        {
            string? xmp = draw.XObject is { } xobject ? Xmp(file, xobject) : null;
            byte[]? jpeg = JpegData(file, draw);
            var exif = jpeg != null ? ExifReader.Read(jpeg) : null;
            if (keepJpeg && jpeg != null && PassThrough(file, draw, jpeg) is var (width, height))
                return new PdfExtractedImage(jpeg, PdfImageFormat.Jpeg, width, height, false, found.Placements, exif, xmp);

            var data = draw.InlineData ?? (draw.Dict as PdfStream)?.RawData ?? Array.Empty<byte>();
            var image = PdfImageDecoder.Decode(file, draw.Dict, data, false, draw.Resources, draw.XObject == null);
            if (image == null) return null;
            bool isMask = image.Stencil != null;
            var pixels = image.Pixels ?? StencilPixels(image.Stencil!);
            var png = PngEncoder.Encode(pixels, image.Width, image.Height, System.IO.Compression.CompressionLevel.Optimal);
            return new PdfExtractedImage(png, PdfImageFormat.Png, image.Width, image.Height, isMask, found.Placements, exif, xmp);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException
                                       or ArgumentException or OverflowException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>A stencil mask as black where it paints and transparent elsewhere, in premultiplied RGBA.</summary>
    private static uint[] StencilPixels(byte[] coverage)
    {
        var pixels = new uint[coverage.Length];
        for (int i = 0; i < coverage.Length; i++) pixels[i] = (uint)coverage[i] << 24;
        return pixels;
    }

    /// <summary>The JPEG file of a DCT-encoded image, after any filters before the DCT one; null for other images.</summary>
    private static byte[]? JpegData(PdfFile file, in ImageDraw draw)
    {
        byte[] data;
        string? filter;
        if (draw.XObject is { } xobject) data = file.DecodeStream(xobject, stopAtImageFilter: true, out filter, out _);
        else if (draw.InlineData is { } inline && file.Resolve(draw.Dict["Filter"] ?? draw.Dict["F"]) is PdfName name)
            (data, filter) = (inline, Filters.Canonical(name.Value));
        else return null;
        return filter == "DCTDecode" && data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 ? data : null;
    }

    /// <summary>
    /// The size of a JPEG a viewer shows as the PDF does, or null when the PDF changes how it looks: a colour space of
    /// other than one or three components (a CMYK JPEG alone is shown inverted by many viewers), a Decode array, or a
    /// mask that a JPEG file cannot carry.
    /// </summary>
    private static (int Width, int Height)? PassThrough(PdfFile file, in ImageDraw draw, byte[] jpeg)
    {
        var dict = draw.Dict;
        if (dict["SMask"] != null || dict["Mask"] != null || dict["Decode"] != null || dict["D"] != null) return null;
        if (file.Resolve(dict["ImageMask"] ?? dict["IM"]) is PdfBool { Value: true }) return null;
        if ((dict["ColorSpace"] ?? dict["CS"]) is { } cs
            && ColorSpace.Parse(file, cs, draw.Resources) is not (DeviceGray or DeviceRgb or CalSpace)) return null;
        if (PdfImage.FromBytes(jpeg) is not { } header || header.Decode != null) return null;
        if (header.ColorSpace is not (PdfImage.DeviceGray or PdfImage.DeviceRgb)) return null;
        return (header.Width, header.Height);
    }

    /// <summary>The image's /Metadata stream as text, or null.</summary>
    private static string? Xmp(PdfFile file, PdfStream xobject)
    {
        if (file.GetStream(xobject, "Metadata") is not { } metadata) return null;
        try
        {
            var bytes = file.DecodeStream(metadata);
            return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>The EXIF data of a JPEG image: the camera, when the photo was taken and where.</summary>
/// <remarks>
/// The common tags are read here. <see cref="Data"/> holds the whole EXIF block for anything else, such as exposure
/// settings or a thumbnail, to read with an EXIF library.
/// </remarks>
public sealed class PdfImageExif
{
    internal PdfImageExif(byte[] data, string? make, string? model, string? software, DateTime? dateTaken, DateTime? dateModified,
        int? orientation, double? latitude, double? longitude, double? altitude)
    {
        Data = data;
        Make = make;
        Model = model;
        Software = software;
        DateTaken = dateTaken;
        DateModified = dateModified;
        Orientation = orientation;
        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
    }

    /// <summary>The EXIF block as stored in the JPEG: a TIFF header and its directories, without the "Exif" prefix.</summary>
    public byte[] Data { get; }

    /// <summary>The camera maker, such as "Canon"; null when not recorded.</summary>
    public string? Make { get; }

    /// <summary>The camera model; null when not recorded.</summary>
    public string? Model { get; }

    /// <summary>The software that wrote the image; null when not recorded.</summary>
    public string? Software { get; }

    /// <summary>When the photo was taken (DateTimeOriginal), in the camera's local time; null when not recorded.</summary>
    public DateTime? DateTaken { get; }

    /// <summary>When the image file was last changed (DateTime), in local time; null when not recorded.</summary>
    public DateTime? DateModified { get; }

    /// <summary>
    /// The EXIF orientation from 1 to 8: 1 is upright, 6 means the image is shown turned 90 degrees clockwise. Null when
    /// not recorded. The PDF draws the image as its page shows it, whatever this says.
    /// </summary>
    public int? Orientation { get; }

    /// <summary>The GPS latitude in degrees, negative south of the equator; null when not recorded.</summary>
    public double? Latitude { get; }

    /// <summary>The GPS longitude in degrees, negative west of Greenwich; null when not recorded.</summary>
    public double? Longitude { get; }

    /// <summary>The GPS altitude in metres, negative below sea level; null when not recorded.</summary>
    public double? Altitude { get; }
}
