using System.Globalization;
using System.Text;
using PdfPecker.Images;

namespace PdfPecker;

/// <summary>
/// Turns images into a PDF with one image per page, such as scanned receipts or photos for an archive. PNG, JPEG, GIF,
/// WebP, BMP and TIFF are read (the first frame of an animated GIF or a multi-page TIFF). All methods are static and
/// thread-safe.
/// </summary>
public static class ImageToPdf
{
    /// <summary>The most images one call converts; more would only be held in memory at once.</summary>
    public const int MaxImages = 10_000;

    /// <summary>Converts images to a PDF, one per page, in the given order.</summary>
    /// <param name="images">The image files' bytes.</param>
    /// <param name="pdfOptions">
    /// Metadata, PDF/A, encryption and other output settings; null for the defaults. <see cref="PdfOptions.PageSize"/>
    /// and <see cref="PdfOptions.Margins"/> apply only when <see cref="ImageToPdfOptions.FitPageToImage"/> is false.
    /// </param>
    /// <param name="imageOptions">How the images are placed; null for pages the size of each image.</param>
    /// <returns>The PDF.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> or one of the images is null.</exception>
    /// <exception cref="ArgumentException">There are no images or more than <see cref="MaxImages"/>, or an option is out of range.</exception>
    /// <exception cref="InvalidDataException">An image is not in a supported format or is damaged.</exception>
    /// <exception cref="PdfLimitException">There are more images than <see cref="PdfOptions.MaxPages"/>.</exception>
    public static byte[] Convert(IEnumerable<byte[]> images, PdfOptions? pdfOptions = null, ImageToPdfOptions? imageOptions = null)
        => Convert(images, pdfOptions, imageOptions, CancellationToken.None);

    /// <summary>
    /// Converts images to a PDF, one per page, in the given order, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="images">The image files' bytes.</param>
    /// <param name="pdfOptions">Metadata, PDF/A, encryption and other output settings; null for the defaults.</param>
    /// <param name="imageOptions">How the images are placed; null for pages the size of each image.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <returns>The PDF.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> or one of the images is null.</exception>
    /// <exception cref="ArgumentException">There are no images or more than <see cref="MaxImages"/>, or an option is out of range.</exception>
    /// <exception cref="InvalidDataException">An image is not in a supported format or is damaged.</exception>
    /// <exception cref="PdfLimitException">There are more images than <see cref="PdfOptions.MaxPages"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static byte[] Convert(IEnumerable<byte[]> images, PdfOptions? pdfOptions, ImageToPdfOptions? imageOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        imageOptions ??= new ImageToPdfOptions();
        if (!(imageOptions.Dpi > 0) || float.IsInfinity(imageOptions.Dpi))
            throw new ArgumentException("Dpi must be a positive number.", nameof(imageOptions));

        var list = new List<byte[]>();
        foreach (var image in images)
        {
            ArgumentNullException.ThrowIfNull(image, nameof(images));
            if (list.Count == MaxImages)
                throw new ArgumentException($"At most {MaxImages} images can be converted at once.", nameof(images));
            list.Add(image);
        }
        if (list.Count == 0)
            throw new ArgumentException("There are no images to convert.", nameof(images));

        var options = pdfOptions?.Clone() ?? new PdfOptions();
        var html = BuildHtml(list, imageOptions, options, cancellationToken);
        // The page rules of the generated document carry the layout.
        options.IgnoreCssPageRules = false;
        return HtmlToPdf.Convert(html, options, cancellationToken);
    }

    /// <summary>Converts image files to a PDF file, one image per page, writing through a temporary file.</summary>
    /// <param name="imagePaths">The image files, in page order.</param>
    /// <param name="pdfPath">The PDF file to write.</param>
    /// <param name="pdfOptions">Metadata, PDF/A, encryption and other output settings; null for the defaults.</param>
    /// <param name="imageOptions">How the images are placed; null for pages the size of each image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="imagePaths"/> or <paramref name="pdfPath"/> is null.</exception>
    /// <exception cref="ArgumentException">There are no images or more than <see cref="MaxImages"/>, or an option is out of range.</exception>
    /// <exception cref="InvalidDataException">An image is not in a supported format or is damaged.</exception>
    /// <exception cref="PdfLimitException">There are more images than <see cref="PdfOptions.MaxPages"/>.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static void ConvertFiles(IEnumerable<string> imagePaths, string pdfPath, PdfOptions? pdfOptions = null, ImageToPdfOptions? imageOptions = null)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentNullException.ThrowIfNull(pdfPath);
        var pdf = Convert(imagePaths.Select(File.ReadAllBytes), pdfOptions, imageOptions);
        AtomicFile.WriteAllBytes(pdfPath, pdf);
    }

    private static string BuildHtml(List<byte[]> images, ImageToPdfOptions options, PdfOptions pdfOptions, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(1024);
        var body = new StringBuilder(1024);
        sb.Append("<!doctype html><html><head><style>html,body{margin:0;padding:0}img{display:block}");
        if (!options.FitPageToImage)
            sb.Append("section{height:100vh;overflow:hidden;display:flex;align-items:center;justify-content:center}");
        var pxPerPixel = 96f / options.Dpi;
        // The page's content box in CSS pixels, which an image larger than it is scaled down to.
        var pageSize = pdfOptions.PageSize;
        var margins = pdfOptions.Margins;
        var contentWidth = (pageSize.Width - margins.Left - margins.Right) / 0.75f;
        var contentHeight = (pageSize.Height - margins.Top - margins.Bottom) / 0.75f;
        for (var i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = PdfImage.FromBytes(images[i])
                ?? throw new InvalidDataException($"Image {i + 1} is not a readable PNG, JPEG, GIF, WebP, BMP or TIFF file.");
            var breakAfter = i + 1 < images.Count ? " style=\"break-after:page\"" : "";
            var src = "data:" + MimeType(images[i]) + ";base64," + System.Convert.ToBase64String(images[i]);
            float width = image.Width * pxPerPixel, height = image.Height * pxPerPixel;
            if (options.FitPageToImage)
            {
                sb.Append(CultureInfo.InvariantCulture, $"@page p{i}{{size:{width:0.###}px {height:0.###}px;margin:0}}.p{i}{{page:p{i}}}");
                body.Append(CultureInfo.InvariantCulture,
                    $"<section class=\"p{i}\"{breakAfter}><img src=\"{src}\" style=\"width:{width:0.###}px;height:{height:0.###}px\"></section>");
            }
            else
            {
                var scale = Math.Min(1, Math.Min(contentWidth / width, contentHeight / height));
                body.Append(CultureInfo.InvariantCulture,
                    $"<section{breakAfter}><img src=\"{src}\" style=\"width:{RoundDown(width * scale)}px;height:{RoundDown(height * scale)}px\"></section>");
            }
        }
        return sb.Append("</style></head><body>").Append(body).Append("</body></html>").ToString();
    }

    // Rounding down keeps an image that fills the page from spilling a thousandth of a pixel onto a blank next page.
    private static string RoundDown(float value) => (MathF.Floor(value * 1000) / 1000).ToString("0.###", CultureInfo.InvariantCulture);

    private static string MimeType(byte[] data) => data switch
    {
        [0xFF, 0xD8, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0x47, 0x49, 0x46, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        [0x42, 0x4D, ..] => "image/bmp",
        _ => "image/tiff",
    };
}

/// <summary>How <see cref="ImageToPdf"/> places images on pages.</summary>
public sealed class ImageToPdfOptions
{
    /// <summary>
    /// Make each page exactly the size of its image, with no margins. Default: true. When false, every page has the
    /// <see cref="PdfOptions.PageSize"/> and <see cref="PdfOptions.Margins"/>, and each image is centred, scaled down to
    /// fit when it is larger than the space inside the margins; smaller images keep their size.
    /// </summary>
    public bool FitPageToImage { get; set; } = true;

    /// <summary>
    /// The resolution the images were made at, which sets their printed size: an image 2,480 pixels wide at 300 dpi is
    /// 8.27 inches (A4) wide. Default: 96, one image pixel per CSS pixel.
    /// </summary>
    public float Dpi { get; set; } = 96;
}
