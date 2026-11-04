namespace PdfPecker.Fluent;

/// <summary>
/// An image loaded once and drawn wherever it is passed to <see cref="ContentExtensions.Image(IContainer, Image)"/>,
/// such as a logo on every page or in every document of a batch. The PDF holds it once for each size and quality it is
/// written at. It is safe to share between threads and documents.
/// </summary>
/// <remarks>
/// As in QuestPDF, an image is created with <see cref="FromFile"/>, <see cref="FromBinaryData"/> or
/// <see cref="FromStream"/>. It keeps only the file's bytes, so disposing it frees nothing and is optional.
/// </remarks>
public sealed class Image : IDisposable
{
    private Image(byte[] data) => Data = data;

    internal byte[] Data { get; }

    /// <summary>An image of PNG, JPEG, GIF, WebP, BMP, TIFF or SVG data. The array is used as it is, not copied.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="imageData"/> is null.</exception>
    public static Image FromBinaryData(byte[] imageData)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        return new Image(imageData);
    }

    /// <summary>An image read from a file now, by your code; the conversion itself never reads files.</summary>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
    public static Image FromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return new Image(File.ReadAllBytes(filePath));
    }

    /// <summary>An image read from a stream, from its current position to its end. The stream is not closed.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static Image FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new Image(copy.ToArray());
    }

    /// <summary>Does nothing: the image holds no native resources. It exists so that QuestPDF code compiles unchanged.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// An SVG drawing loaded once and drawn wherever it is passed to
/// <see cref="ContentExtensions.Svg(IContainer, SvgImage)"/>. It is drawn as an image, so it cannot change the document
/// around it, and it loads nothing from outside except data URIs.
/// </summary>
public sealed class SvgImage : IDisposable
{
    private SvgImage(byte[] data) => Data = data;

    internal byte[] Data { get; }

    /// <summary>An SVG drawing given as markup.</summary>
    /// <exception cref="ArgumentException"><paramref name="svg"/> is null or empty.</exception>
    public static SvgImage FromText(string svg)
    {
        ArgumentException.ThrowIfNullOrEmpty(svg);
        return new SvgImage(System.Text.Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>An SVG file read now, by your code; the conversion itself never reads files.</summary>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
    public static SvgImage FromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return new SvgImage(File.ReadAllBytes(filePath));
    }

    /// <summary>Does nothing: the drawing holds no native resources. It exists so that QuestPDF code compiles unchanged.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// How much an image may be compressed, as in QuestPDF. Each one writes the image as a JPEG at that quality, which suits
/// photos; an image whose JPEG would be larger, such as a small flat logo, is kept as it
/// is, and transparency stays lossless.
/// </summary>
public enum ImageCompressionQuality
{
    /// <summary>JPEG quality 100: nearly lossless, and still much smaller than PNG for photos.</summary>
    Best,

    /// <summary>JPEG quality 90.</summary>
    VeryHigh,

    /// <summary>JPEG quality 75, QuestPDF's default.</summary>
    High,

    /// <summary>JPEG quality 50.</summary>
    Medium,

    /// <summary>JPEG quality 25.</summary>
    Low,

    /// <summary>JPEG quality 10.</summary>
    VeryLow,
}

internal static class ImageCompression
{
    /// <summary>The JPEG quality QuestPDF writes for each setting.</summary>
    public static int Quality(ImageCompressionQuality quality) => quality switch
    {
        ImageCompressionQuality.Best => 100,
        ImageCompressionQuality.VeryHigh => 90,
        ImageCompressionQuality.High => 75,
        ImageCompressionQuality.Medium => 50,
        ImageCompressionQuality.Low => 25,
        ImageCompressionQuality.VeryLow => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Not an ImageCompressionQuality value."),
    };
}
