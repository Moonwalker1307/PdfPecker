using System.Globalization;
using PdfPecker.Images;

namespace PdfPecker;

/// <summary>
/// The byte layout of pixels from <see cref="ImageDecoder"/> and
/// <see cref="PdfRenderer.RenderPage(int, Span{byte}, ImagePixelFormat, ImageOptions?, CancellationToken)"/>: 8 bits
/// per channel, rows from the top without padding.
/// </summary>
public enum ImagePixelFormat
{
    /// <summary>One byte per pixel, 0 black to 255 white. Colour becomes Rec. 601 luma; transparency is flattened onto the background.</summary>
    Gray,

    /// <summary>Three bytes per pixel: red, green, blue. Transparency is flattened onto the background.</summary>
    Rgb,

    /// <summary>Four bytes per pixel: red, green, blue and alpha, with straight (not premultiplied) alpha.</summary>
    Rgba,
}

/// <summary>Options for <see cref="ImageDecoder.Decode(byte[], ImageDecodeOptions?, CancellationToken)"/>.</summary>
public sealed class ImageDecodeOptions
{
    /// <summary>The most pixels an image may have, which is also the library's own limit. 100 million.</summary>
    public const long MaxPixelLimit = 100_000_000;

    private int _frame;
    private long _maxPixels = MaxPixelLimit;

    /// <summary>
    /// The frame to decode, counted from 0: a page of a multi-page TIFF file (see <see cref="ImageDecoder.GetFrameCount"/>).
    /// Other formats have one frame; an animated GIF or WebP gives its first. Default 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Frame
    {
        get => _frame;
        set => _frame = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Frame cannot be negative.");
    }

    /// <summary>
    /// The pixel format to return. Null, the default, keeps the image's own: <see cref="ImagePixelFormat.Gray"/> for
    /// a grey image, <see cref="ImagePixelFormat.Rgb"/> for a colour one and <see cref="ImagePixelFormat.Rgba"/> when
    /// some pixels are not opaque.
    /// </summary>
    public ImagePixelFormat? Format { get; set; }

    /// <summary>
    /// What transparent and translucent pixels are flattened onto for <see cref="ImagePixelFormat.Gray"/> and
    /// <see cref="ImagePixelFormat.Rgb"/>, as 0xAARRGGBB (composited over white when it is itself translucent), with
    /// the renderer's arithmetic: the pixels are those of the image drawn on a page of that colour. Default opaque white.
    /// </summary>
    public uint BackgroundColor { get; set; } = 0xFFFFFFFF;

    /// <summary>
    /// Whether a JPEG photo is turned and flipped as its EXIF orientation says, as browsers and
    /// <see cref="ImageToPdf"/> show it. The turn is lossless. Default true.
    /// </summary>
    public bool ApplyOrientation { get; set; } = true;

    /// <summary>
    /// The most pixels (width × height) an image may have; a larger one fails before its pixels are allocated.
    /// Default and largest value <see cref="MaxPixelLimit"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or more than <see cref="MaxPixelLimit"/>.</exception>
    public long MaxPixels
    {
        get => _maxPixels;
        set => _maxPixels = value is >= 1 and <= MaxPixelLimit
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxPixels must be between 1 and 100,000,000.");
    }
}

/// <summary>Decoded pixels from <see cref="ImageDecoder"/>.</summary>
public sealed class ImagePixels
{
    internal ImagePixels(int width, int height, ImagePixelFormat format, byte[] pixels)
    {
        Width = width;
        Height = height;
        Format = format;
        Pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The layout of <see cref="Pixels"/>.</summary>
    public ImagePixelFormat Format { get; }

    /// <summary>The bytes of one row: <see cref="Width"/> times 1, 3 or 4 after <see cref="Format"/>.</summary>
    public int Stride => Width * PixelConversion.BytesPerPixel(Format);

    /// <summary>The pixels, <see cref="Stride"/> bytes per row from the top, without padding. The array is the caller's to keep or change.</summary>
    public byte[] Pixels { get; }
}

/// <summary>
/// Decodes PNG, JPEG, GIF, WebP, BMP and TIFF files (every page of a multi-page TIFF) to pixels, without making a PDF
/// of them first. The pixels are those <see cref="ImageToPdf"/> embeds and <see cref="PdfRenderer"/> draws at one
/// pixel per image pixel: the same colour conversions (CMYK, palettes, 16-bit and fewer-bit samples, the EXIF
/// orientation of photos) and the same limits on damaged and oversized files. All methods are static and thread-safe.
/// </summary>
public static class ImageDecoder
{
    /// <summary>
    /// The number of frames <see cref="Decode(byte[], ImageDecodeOptions?, CancellationToken)"/> can read: the pages of
    /// a TIFF file (up to 65,536), and 1 for the other formats.
    /// </summary>
    /// <param name="image">The image file's bytes.</param>
    /// <returns>The number of frames.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="InvalidDataException">The data is not a PNG, JPEG, GIF, WebP, BMP or TIFF file.</exception>
    public static int GetFrameCount(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int count = PixelDecoder.FrameCount(image);
        return count > 0 || TiffIdentified(image) ? count : throw new InvalidDataException(NotAnImage);
    }

    /// <summary>Decodes an image file to pixels.</summary>
    /// <param name="image">A PNG, JPEG, GIF, WebP, BMP or TIFF file.</param>
    /// <param name="options">The frame, pixel format, background and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Checked before and after decoding, which itself runs to the end.</param>
    /// <returns>The pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageDecodeOptions.Frame"/> is not a frame of the image, or <see cref="ImageDecodeOptions.Format"/> is not a defined value.</exception>
    /// <exception cref="InvalidDataException">
    /// The data is not a supported image, is damaged, or has more pixels than <see cref="ImageDecodeOptions.MaxPixels"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static ImagePixels Decode(byte[] image, ImageDecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new ImageDecodeOptions();
        if (options.Format is { } requested) _ = PixelConversion.BytesPerPixel(requested);
        cancellationToken.ThrowIfCancellationRequested();
        int frames = PixelDecoder.FrameCount(image);
        if (frames == 0) throw new InvalidDataException(TiffIdentified(image) ? "The TIFF file has no readable page." : NotAnImage);
        if (options.Frame >= frames)
            throw new ArgumentOutOfRangeException(nameof(options), options.Frame,
                string.Create(CultureInfo.InvariantCulture, $"The image has {frames} frame(s); Frame counts from 0."));

        ImagePlanes? planes;
        try
        {
            planes = PixelDecoder.Decode(image, options.Frame, options.ApplyOrientation, options.MaxPixels);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or IOException
                                       or OverflowException or FormatException)
        {
            throw new InvalidDataException("The image is damaged: " + ex.Message, ex);
        }
        if (planes == null)
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The image is damaged, in a form that is not supported, or larger than {options.MaxPixels} pixels."));
        cancellationToken.ThrowIfCancellationRequested();

        var format = options.Format ?? (planes.HasTransparency ? ImagePixelFormat.Rgba : planes.Colors == 1 ? ImagePixelFormat.Gray : ImagePixelFormat.Rgb);
        var pixels = PixelConversion.FromPlanes(planes, format, PixelConversion.Matte(options.BackgroundColor));
        return new ImagePixels(planes.Width, planes.Height, format, pixels);
    }

    /// <summary>Decodes an image from a stream, read to its end; see <see cref="Decode(byte[], ImageDecodeOptions?, CancellationToken)"/>.</summary>
    /// <param name="image">A stream with a PNG, JPEG, GIF, WebP, BMP or TIFF file.</param>
    /// <param name="options">The frame, pixel format, background and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Checked before and after decoding.</param>
    /// <returns>The pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageDecodeOptions.Frame"/> is not a frame of the image, or <see cref="ImageDecodeOptions.Format"/> is not a defined value.</exception>
    /// <exception cref="InvalidDataException">The data is not a supported image, is damaged, or is too large.</exception>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static ImagePixels Decode(Stream image, ImageDecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var ms = new MemoryStream();
        image.CopyTo(ms);
        return Decode(ms.ToArray(), options, cancellationToken);
    }

    /// <summary>Decodes an image file; see <see cref="Decode(byte[], ImageDecodeOptions?, CancellationToken)"/>.</summary>
    /// <param name="path">A PNG, JPEG, GIF, WebP, BMP or TIFF file.</param>
    /// <param name="options">The frame, pixel format, background and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Checked before and after decoding.</param>
    /// <returns>The pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageDecodeOptions.Frame"/> is not a frame of the image, or <see cref="ImageDecodeOptions.Format"/> is not a defined value.</exception>
    /// <exception cref="InvalidDataException">The file is not a supported image, is damaged, or is too large.</exception>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static ImagePixels DecodeFile(string path, ImageDecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Decode(File.ReadAllBytes(path), options, cancellationToken);
    }

    private const string NotAnImage = "The data is not a PNG, JPEG, GIF, WebP, BMP or TIFF image.";

    /// <summary>A TIFF header whose directory chain is empty or broken still names the format, which then has no frames.</summary>
    private static bool TiffIdentified(byte[] image) => Images.TiffDecoder.IsTiff(image);
}
