namespace PdfPecker;

/// <summary>
/// The barcode symbologies <see cref="Barcode.Read(byte[], BarcodeReaderOptions?, CancellationToken)"/> and
/// <see cref="PdfBarcodes"/> read. The values are flags, so <see cref="BarcodeReaderOptions.Formats"/> can name several;
/// a <see cref="BarcodeResult"/> has exactly one.
/// </summary>
[Flags]
public enum BarcodeFormat
{
    /// <summary>No format.</summary>
    None = 0,

    /// <summary>A QR code (ISO/IEC 18004), including EPC payment codes and the Swiss QR-bill code.</summary>
    QrCode = 1,

    /// <summary>A Data Matrix code (ECC 200, ISO/IEC 16022), square or rectangular, including GS1 Data Matrix.</summary>
    DataMatrix = 2,

    /// <summary>A Code 128 barcode (ISO/IEC 15417), including GS1-128.</summary>
    Code128 = 4,

    /// <summary>An EAN-13 barcode (ISO/IEC 15420); a UPC-A code reads as EAN-13 with a leading zero.</summary>
    Ean13 = 8,

    /// <summary>Every format.</summary>
    All = QrCode | DataMatrix | Code128 | Ean13,
}

/// <summary>A corner of a barcode, in pixels of an image or in points on a PDF page, from the top left, y downward.</summary>
/// <param name="X">The distance from the left edge.</param>
/// <param name="Y">The distance from the top edge.</param>
public readonly record struct BarcodePoint(double X, double Y);

/// <summary>Options for <see cref="Barcode.Read(byte[], BarcodeReaderOptions?, CancellationToken)"/>.</summary>
public sealed class BarcodeReaderOptions
{
    /// <summary>The formats to look for. Default: <see cref="BarcodeFormat.All"/>; fewer formats read faster.</summary>
    public BarcodeFormat Formats { get; set; } = BarcodeFormat.All;

    /// <summary>
    /// Whether QR and Data Matrix codes printed light on a dark ground are looked for too. Default: true. Turning it off
    /// saves about a third of the time on images with no such codes.
    /// </summary>
    public bool TryInverted { get; set; } = true;

    /// <summary>The most barcodes returned from one image or page; reading stops there. Default: 1,000.</summary>
    public int MaxBarcodes { get; set; } = 1_000;
}

/// <summary>A barcode that was read, with what it holds and where it is.</summary>
public sealed class BarcodeResult
{
    internal BarcodeResult(BarcodeFormat format, string text, byte[] bytes, bool isGs1, BarcodePoint[] corners, int pageNumber)
    {
        Format = format;
        Text = text;
        Bytes = bytes;
        IsGs1 = isGs1;
        Corners = corners;
        PageNumber = pageNumber;
        X = corners.Min(c => c.X);
        Y = corners.Min(c => c.Y);
        Width = corners.Max(c => c.X) - X;
        Height = corners.Max(c => c.Y) - Y;
    }

    /// <summary>The symbology.</summary>
    public BarcodeFormat Format { get; }

    /// <summary>
    /// The text the code holds. Bytes are decoded in the character set an ECI in the code names; without one, QR code
    /// bytes that are valid UTF-8 are read as UTF-8 and other bytes as ISO 8859-1, the standards' default. In GS1 data
    /// (<see cref="IsGs1"/>) the FNC1 separators between fields are the group separator character, U+001D.
    /// </summary>
    public string Text { get; }

    /// <summary>The content as the code stores it, before decoding to text: one byte per character or byte.</summary>
    public byte[] Bytes { get; }

    /// <summary>
    /// Whether the code starts with FNC1, which marks GS1 data such as GS1 Data Matrix, GS1-128 or a GS1 QR code. The
    /// text then holds the application identifiers and their data, such as <c>"0109506000134352"</c>.
    /// </summary>
    public bool IsGs1 { get; }

    /// <summary>
    /// The corners of the code in its own reading order: top left, top right, bottom right and bottom left as the code
    /// is meant to be read, so a code printed upside down has its first corner at the bottom right. For a barcode the
    /// box covers the bars and the lines it was read on.
    /// </summary>
    public IReadOnlyList<BarcodePoint> Corners { get; }

    /// <summary>The left edge of the bounding box of <see cref="Corners"/>.</summary>
    public double X { get; }

    /// <summary>The top edge of the bounding box of <see cref="Corners"/>.</summary>
    public double Y { get; }

    /// <summary>The width of the bounding box of <see cref="Corners"/>.</summary>
    public double Width { get; }

    /// <summary>The height of the bounding box of <see cref="Corners"/>.</summary>
    public double Height { get; }

    /// <summary>
    /// The page number, from 1, for a code found by <see cref="PdfBarcodes"/>, whose positions are in points on that
    /// page; 0 for a code read from an image, whose positions are in pixels.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// The euro credit transfer of an EPC payment QR code (the text starts with <c>BCD</c>), with its fields as the code
    /// gives them; null for any other code. <see cref="Barcode.EpcQrCode"/> writes such codes.
    /// </summary>
    public EpcPayment? EpcPayment => Format == BarcodeFormat.QrCode ? Barcodes.Reading.PaymentPayloads.Epc(Text) : null;

    /// <summary>
    /// The payment of a Swiss QR-bill code (the text starts with <c>SPC</c>), with its fields as the code gives them;
    /// null for any other code. <see cref="Barcode.SwissQrCode"/> writes such codes.
    /// </summary>
    public SwissQrBill? SwissQrBill => Format == BarcodeFormat.QrCode ? Barcodes.Reading.PaymentPayloads.Swiss(Text) : null;

    /// <summary>The format and text, for debugging.</summary>
    public override string ToString() => $"{Format}: {Text}";
}
