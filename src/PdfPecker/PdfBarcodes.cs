using PdfPecker.Barcodes.Reading;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfBarcodes"/>.</summary>
public sealed class PdfBarcodeOptions
{
    /// <summary>
    /// The password of an encrypted PDF: its user or owner password. Not needed for a PDF that only restricts permissions.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The pages to read, numbered from 1, such as <c>PdfPageRange.Parse("1-3")</c>. Default: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>
    /// The resolution pages are rendered at for reading, in dots per inch. Default: 300, which reads modules down to
    /// about a quarter of a millimetre; lower is faster, higher finds smaller codes.
    /// </summary>
    public float Dpi { get; set; } = 300;

    /// <summary>The formats to look for. Default: <see cref="BarcodeFormat.All"/>; fewer formats read faster.</summary>
    public BarcodeFormat Formats { get; set; } = BarcodeFormat.All;

    /// <summary>Whether QR and Data Matrix codes printed light on a dark ground are looked for too. Default: true.</summary>
    public bool TryInverted { get; set; } = true;

    /// <summary>The most barcodes returned from one page; reading the page stops there. Default: 1,000.</summary>
    public int MaxBarcodesPerPage { get; set; } = 1_000;

    /// <summary>The most pages read; more throws <see cref="PdfLimitException"/>. Default: 10,000.</summary>
    public int MaxPages { get; set; } = 10_000;

    /// <summary>The most pages read at the same time. Default: the processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
}

/// <summary>
/// Finds and reads the QR codes, Data Matrix codes and Code 128 and EAN-13 barcodes on PDF pages: drawn as vector
/// graphics, embedded as images or scanned. Each page is rendered at <see cref="PdfBarcodeOptions.Dpi"/> and read as
/// <see cref="Barcode.Read(byte[], BarcodeReaderOptions?, CancellationToken)"/> reads an image, so a code is found
/// whatever produced the PDF.
/// </summary>
/// <remarks>
/// Positions are in points (1/72 inch) on the page as displayed, after its rotation, measured from the top-left corner
/// with y growing downward, as for <see cref="PdfTextPage"/> and <see cref="PdfImagePlacement"/>.
/// </remarks>
public static class PdfBarcodes
{
    /// <summary>The barcodes of a PDF, page by page and on each page top to bottom.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Password, pages, resolution, formats and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The codes found, each with its page number and corners in points; empty for none.</returns>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    /// <exception cref="PdfLimitException">More pages than <see cref="PdfBarcodeOptions.MaxPages"/> would be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A page of <see cref="PdfBarcodeOptions.Pages"/> is past the last page, or <see cref="PdfBarcodeOptions.Dpi"/> is
    /// not between 1 and 2,400.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<BarcodeResult> Find(byte[] pdf, PdfBarcodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= new PdfBarcodeOptions();
        if (!(options.Dpi >= 1 && options.Dpi <= 2400))
            throw new ArgumentOutOfRangeException(nameof(options), options.Dpi, "PdfBarcodeOptions.Dpi must be between 1 and 2,400.");
        var renderer = PdfRenderer.Open(pdf, options.Password);
        int maxPages = Math.Max(0, options.MaxPages);
        var indices = options.Pages is { } range
            ? range.Resolve(renderer.PageCount, "the PDF", maxPages)
            : Enumerable.Range(0, Math.Min(renderer.PageCount, maxPages + 1)).ToList();
        if (indices.Count > maxPages)
            throw new PdfLimitException($"The barcodes of more than {maxPages:N0} pages would be read (PdfBarcodeOptions.MaxPages).");

        var readerOptions = new BarcodeReaderOptions
        {
            Formats = options.Formats,
            TryInverted = options.TryInverted,
            MaxBarcodes = options.MaxBarcodesPerPage,
        };
        var perPage = new List<BarcodeResult>[indices.Count];
        var imageOptions = new ImageOptions { Dpi = options.Dpi };
        try
        {
            Parallel.For(0, indices.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            }, i =>
            {
                int index = indices[i];
                var image = renderer.RenderPage(index, imageOptions, cancellationToken);
                var (width, height) = renderer.GetPageSize(index);
                var gray = GrayImage.FromPremultiplied(image.Premultiplied, image.Width, image.Height);
                perPage[i] = Barcode.Read(gray, readerOptions, index + 1, width / image.Width, height / image.Height, cancellationToken);
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Report a damaged page the same way a single page does, not wrapped.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
        return perPage.SelectMany(p => p).ToList();
    }

    /// <summary>The barcodes of a PDF read from a stream, read to its end; see <see cref="Find(byte[], PdfBarcodeOptions?, CancellationToken)"/>.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="options">Password, pages, resolution, formats and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The codes found, each with its page number and corners in points; empty for none.</returns>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    /// <exception cref="PdfLimitException">More pages than <see cref="PdfBarcodeOptions.MaxPages"/> would be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page of <see cref="PdfBarcodeOptions.Pages"/> is past the last page, or the Dpi is out of range.</exception>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<BarcodeResult> Find(Stream pdf, PdfBarcodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var ms = new MemoryStream();
        pdf.CopyTo(ms);
        return Find(ms.ToArray(), options, cancellationToken);
    }

    /// <summary>The barcodes of a PDF file; see <see cref="Find(byte[], PdfBarcodeOptions?, CancellationToken)"/>.</summary>
    /// <param name="path">The PDF file.</param>
    /// <param name="options">Password, pages, resolution, formats and limits; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The codes found, each with its page number and corners in points; empty for none.</returns>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF, or a page cannot be rendered.</exception>
    /// <exception cref="NotSupportedException">The PDF uses an encryption handler other than the standard one.</exception>
    /// <exception cref="PdfLimitException">More pages than <see cref="PdfBarcodeOptions.MaxPages"/> would be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page of <see cref="PdfBarcodeOptions.Pages"/> is past the last page, or the Dpi is out of range.</exception>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<BarcodeResult> FindFile(string path, PdfBarcodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Find(File.ReadAllBytes(path), options, cancellationToken);
    }
}
