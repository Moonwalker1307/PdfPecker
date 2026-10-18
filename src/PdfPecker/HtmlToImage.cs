namespace PdfPecker;

/// <summary>
/// Converts HTML straight to images, one per page: the shortcut for <see cref="HtmlToPdf"/> followed by
/// <see cref="PdfToImage"/>, for previews, thumbnails and e-mails. All methods are static and thread-safe.
/// </summary>
public static class HtmlToImage
{
    /// <summary>Converts an HTML string to one image per page, in page order.</summary>
    /// <param name="html">The HTML document.</param>
    /// <param name="pdfOptions">
    /// Page, font and layout settings, as for <see cref="HtmlToPdf.Convert(string, PdfOptions?)"/>; null for the defaults.
    /// <see cref="PdfOptions.Security"/> and <see cref="PdfOptions.Signature"/> are ignored.
    /// </param>
    /// <param name="imageOptions">Resolution, size and format of the images; null for PNG at 150 dpi.</param>
    /// <returns>The encoded images, one per page.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pdfOptions"/> holds a value out of range or a combination that is not allowed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="PdfLimitException">The document would have more than <see cref="PdfOptions.MaxPages"/> pages.</exception>
    /// <exception cref="NotSupportedException">A page is larger than JPEG or WebP allows (65,535 and 16,384 pixels per side).</exception>
    public static byte[][] Convert(string html, PdfOptions? pdfOptions = null, ImageOptions? imageOptions = null)
        => Convert(html, pdfOptions, imageOptions, CancellationToken.None);

    /// <summary>
    /// Converts an HTML string to one image per page, in page order, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="html">The HTML document.</param>
    /// <param name="pdfOptions">Page, font and layout settings; null for the defaults.</param>
    /// <param name="imageOptions">Resolution, size and format of the images; null for PNG at 150 dpi.</param>
    /// <param name="cancellationToken">Stops the conversion and the rendering.</param>
    /// <returns>The encoded images, one per page.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pdfOptions"/> holds a value out of range or a combination that is not allowed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="PdfLimitException">The document would have more than <see cref="PdfOptions.MaxPages"/> pages.</exception>
    /// <exception cref="NotSupportedException">A page is larger than JPEG or WebP allows (65,535 and 16,384 pixels per side).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static byte[][] Convert(string html, PdfOptions? pdfOptions, ImageOptions? imageOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        var pdf = HtmlToPdf.Convert(html, WithoutSecurity(pdfOptions), cancellationToken);
        return PdfToImage.Convert(pdf, imageOptions, cancellationToken);
    }

    /// <summary>Converts an HTML string to one image per page on the thread pool.</summary>
    /// <param name="html">The HTML document.</param>
    /// <param name="pdfOptions">Page, font and layout settings; null for the defaults.</param>
    /// <param name="imageOptions">Resolution, size and format of the images; null for PNG at 150 dpi.</param>
    /// <param name="cancellationToken">Stops the conversion and the rendering.</param>
    /// <returns>The encoded images, one per page.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pdfOptions"/> holds a value out of range or a combination that is not allowed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ImageOptions.Dpi"/> is not positive.</exception>
    /// <exception cref="PdfLimitException">The document would have more than <see cref="PdfOptions.MaxPages"/> pages.</exception>
    /// <exception cref="NotSupportedException">A page is larger than JPEG or WebP allows (65,535 and 16,384 pixels per side).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<byte[][]> ConvertAsync(string html, PdfOptions? pdfOptions = null, ImageOptions? imageOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Task.Run(() => Convert(html, pdfOptions, imageOptions, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Encryption and signing change nothing on the page, so they are left out: a password would only have to be given
    /// again to render, and signing could contact a timestamp server for nothing.
    /// </summary>
    private static PdfOptions? WithoutSecurity(PdfOptions? options)
    {
        if (options?.Security is null && options?.Signature is null)
            return options;
        var copy = options.Clone();
        copy.Security = null;
        copy.Signature = null;
        return copy;
    }
}
