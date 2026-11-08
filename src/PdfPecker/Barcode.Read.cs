using PdfPecker.Barcodes.Reading;
using PdfPecker.Images;

namespace PdfPecker;

public static partial class Barcode
{
    /// <summary>
    /// Reads the QR codes, Data Matrix codes and Code 128 and EAN-13 barcodes in an image, such as a scan, a photo or a
    /// screenshot. Codes may be at any angle (barcodes upright, upside down or turned a quarter), several to an image, and
    /// light on dark as well as dark on light. For PDF pages use <see cref="PdfBarcodes"/>.
    /// </summary>
    /// <param name="image">A PNG, JPEG, GIF, WebP, BMP or TIFF file.</param>
    /// <param name="options">The formats to look for and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The codes found, top to bottom and then left to right, with positions in pixels; empty for none.</returns>
    /// <exception cref="InvalidDataException">The data is not a readable image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<BarcodeResult> Read(byte[] image, BarcodeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var gray = DecodeImage(image) ?? throw new InvalidDataException("The data is not a readable PNG, JPEG, GIF, WebP, BMP or TIFF image.");
        return Read(gray, options, 0, 1, 1, cancellationToken);
    }

    /// <summary>Reads the codes in an image from a stream, read to its end; see <see cref="Read(byte[], BarcodeReaderOptions?, CancellationToken)"/>.</summary>
    /// <param name="image">A stream with a PNG, JPEG, GIF, WebP, BMP or TIFF file.</param>
    /// <param name="options">The formats to look for and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The codes found, with positions in pixels; empty for none.</returns>
    /// <exception cref="InvalidDataException">The data is not a readable image.</exception>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<BarcodeResult> Read(Stream image, BarcodeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var ms = new MemoryStream();
        image.CopyTo(ms);
        return Read(ms.ToArray(), options, cancellationToken);
    }

    /// <summary>Reads the codes in an image file; see <see cref="Read(byte[], BarcodeReaderOptions?, CancellationToken)"/>.</summary>
    /// <param name="path">A PNG, JPEG, GIF, WebP, BMP or TIFF file.</param>
    /// <param name="options">The formats to look for and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The codes found, with positions in pixels; empty for none.</returns>
    /// <exception cref="InvalidDataException">The file is not a readable image.</exception>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<BarcodeResult> ReadFile(string path, BarcodeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllBytes(path), options, cancellationToken);
    }

    /// <summary>
    /// Reads the codes in a page image that <see cref="PdfRenderer.RenderPage(int, ImageOptions?, CancellationToken)"/>
    /// rendered, with positions in its pixels. <see cref="PdfBarcodes"/> does the rendering itself and gives positions in
    /// points.
    /// </summary>
    /// <param name="image">The rendered page.</param>
    /// <param name="options">The formats to look for and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The codes found, with positions in pixels; empty for none.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<BarcodeResult> Read(RenderedImage image, BarcodeReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Read(GrayImage.FromPremultiplied(image.Premultiplied, image.Width, image.Height), options, 0, 1, 1, cancellationToken);
    }

    /// <summary>Reads a grey image; positions are multiplied by the scale, for page points.</summary>
    internal static List<BarcodeResult> Read(GrayImage gray, BarcodeReaderOptions? options, int pageNumber, double scaleX, double scaleY,
        CancellationToken cancellationToken)
    {
        options ??= new BarcodeReaderOptions();
        var detections = BarcodeReader.Read(gray, options.Formats, options.TryInverted, Math.Max(0, options.MaxBarcodes), cancellationToken);
        var results = new List<BarcodeResult>(detections.Count);
        foreach (var d in detections)
        {
            var corners = Array.ConvertAll(d.Corners, p => new BarcodePoint(p.X * scaleX, p.Y * scaleY));
            results.Add(new BarcodeResult(d.Format, d.Content.Text, d.Content.Bytes, d.Content.Gs1, corners, pageNumber));
        }
        return results;
    }

    /// <summary>The image's grey levels as drawn on white; null when it cannot be decoded.</summary>
    private static GrayImage? DecodeImage(byte[] data)
    {
        if (PdfImage.FromBytes(data) is not { } image || image.DecodeRgb() is not { } color) return null;
        byte[]? alpha = null;
        if (image.SMask?.DecodeRgb() is { } mask && mask.Width == color.Width && mask.Height == color.Height)
        {
            alpha = new byte[mask.Width * mask.Height];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = mask.Rgb[i * 3];
        }
        return GrayImage.FromRgb(color.Rgb, alpha, color.Width, color.Height);
    }
}
