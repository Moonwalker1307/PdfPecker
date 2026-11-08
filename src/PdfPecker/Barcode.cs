using System.Globalization;
using System.Text;
using PdfPecker.Barcodes;

namespace PdfPecker;

/// <summary>
/// Draws QR codes, Swiss QR-bill codes, Data Matrix codes and Code 128 and EAN-13 barcodes as SVG markup, to put in an
/// HTML template as an inline <c>&lt;svg&gt;</c> or, through <see cref="ToDataUri"/>, as an <c>&lt;img&gt;</c>. The
/// SVG stays vector graphics in the PDF, so it scans at any print size. <c>Read</c> reads the same symbologies back from
/// images, and <see cref="PdfBarcodes"/> from PDF pages. All methods are static and thread-safe.
/// </summary>
public static partial class Barcode
{
    /// <summary>
    /// A QR code holding <paramref name="text"/>, in the smallest version (size) that fits at the chosen
    /// <see cref="BarcodeOptions.ErrorCorrection"/>. Digits only and upper-case alphanumeric text are packed densely;
    /// anything else is stored as UTF-8.
    /// </summary>
    /// <param name="text">The text, such as a URL or a payment string. Up to 2,953 bytes of UTF-8 at the lowest level.</param>
    /// <param name="options">Size, colours and error correction; null for the defaults.</param>
    /// <returns>An <c>&lt;svg&gt;</c> element.</returns>
    /// <exception cref="ArgumentException">The text does not fit in a version 40 QR code.</exception>
    public static string QrCode(string text, BarcodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new BarcodeOptions();
        return QrSvg(QrEncoder.Encode(text, options.ErrorCorrection), options);
    }

    /// <summary>
    /// An EPC payment QR code (the European Payments Council's "SEPA credit transfer" QR, also called GiroCode), which
    /// banking apps read to fill in a euro transfer. It always uses error correction level M, as the EPC guideline
    /// requires; <see cref="BarcodeOptions.ErrorCorrection"/> is ignored.
    /// </summary>
    /// <param name="payment">The beneficiary, amount and reference.</param>
    /// <param name="options">Size and colours; null for the defaults.</param>
    /// <returns>An <c>&lt;svg&gt;</c> element.</returns>
    /// <exception cref="ArgumentException">A field of the payment is missing, too long or invalid.</exception>
    public static string EpcQrCode(EpcPayment payment, BarcodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(payment);
        options ??= new BarcodeOptions();
        return QrSvg(QrEncoder.Encode(payment.ToPayload(), QrErrorCorrection.Medium, maxVersion: 13), options);
    }

    /// <summary>
    /// The Swiss QR code of a QR-bill, with the Swiss cross in the middle, which Swiss banking apps read to pay the
    /// bill. It uses error correction level M and at most version 25, as the QR-bill guidelines require;
    /// <see cref="BarcodeOptions.ErrorCorrection"/> is ignored. On a printed payment part the code is 46 x 46 mm
    /// without its quiet zone; set CSS <c>width</c> on the SVG or image for that.
    /// </summary>
    /// <param name="bill">The account, creditor, amount, debtor and reference.</param>
    /// <param name="options">Size and colours; null for the defaults.</param>
    /// <returns>An <c>&lt;svg&gt;</c> element.</returns>
    /// <exception cref="ArgumentException">A field of the bill is missing, too long or invalid.</exception>
    public static string SwissQrCode(SwissQrBill bill, BarcodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        options ??= new BarcodeOptions();
        var modules = QrEncoder.Encode(bill.ToPayload(), QrErrorCorrection.Medium, maxVersion: 25);
        return MatrixSvg(modules, quietZone: 4, options, SwissCross);
    }

    /// <summary>
    /// A Data Matrix code (ECC 200) holding <paramref name="text"/>, in the smallest square size that fits, from 10 x
    /// 10 to 144 x 144 modules. Data Matrix packs more into a small square than a QR code, so it is the usual choice
    /// for small labels. Digits are stored two to a codeword. Text outside ISO 8859-1 is stored as UTF-8 with the ECI
    /// that says so, which most current scanners read. For GS1 data use <see cref="Gs1DataMatrix"/>.
    /// <see cref="BarcodeOptions.ErrorCorrection"/> is ignored: each size has a fixed amount of error correction.
    /// </summary>
    /// <param name="text">The text. Up to 3,116 digits, or 1,556 other ASCII characters.</param>
    /// <param name="options">Size and colours; null for the defaults.</param>
    /// <returns>An <c>&lt;svg&gt;</c> element.</returns>
    /// <exception cref="ArgumentException">The text is empty or does not fit in a 144 x 144 symbol.</exception>
    public static string DataMatrix(string text, BarcodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new BarcodeOptions();
        return MatrixSvg(DataMatrixEncoder.Encode(text), quietZone: 2, options);
    }

    /// <summary>
    /// A GS1 Data Matrix code, as used on shipping, healthcare and pharmaceutical labels, from an element string with
    /// the application identifiers in parentheses: <c>"(01)09506000134352(17)270131(10)AB-123"</c>. The code starts
    /// with FNC1, and a field of variable length is ended with the FNC1 separator, as the GS1 General Specifications
    /// require. The fields with a fixed length (such as GTIN (01) and dates (11) to (17)) are checked for their number
    /// of digits.
    /// </summary>
    /// <param name="elementString">The application identifiers and their data.</param>
    /// <param name="options">Size and colours; null for the defaults.</param>
    /// <returns>An <c>&lt;svg&gt;</c> element.</returns>
    /// <exception cref="ArgumentException">
    /// The element string is malformed, a field has the wrong length or a character GS1 does not allow, or the data
    /// does not fit in a 144 x 144 symbol.
    /// </exception>
    public static string Gs1DataMatrix(string elementString, BarcodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(elementString);
        options ??= new BarcodeOptions();
        return MatrixSvg(DataMatrixEncoder.EncodeGs1(elementString), quietZone: 2, options);
    }

    /// <summary>
    /// A Code 128 barcode holding ASCII <paramref name="text"/>, with the text printed below it unless
    /// <see cref="BarcodeOptions.ShowText"/> is false. Runs of digits are packed two to a symbol.
    /// </summary>
    /// <param name="text">1 to 1,000 ASCII characters.</param>
    /// <param name="options">Size, colours and text; null for the defaults.</param>
    /// <returns>An <c>&lt;svg&gt;</c> element.</returns>
    /// <exception cref="ArgumentException">The text is empty, too long or has a character outside ASCII.</exception>
    public static string Code128(string text, BarcodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new BarcodeOptions();
        var modules = LinearEncoder.Code128(text);
        return LinearSvg(modules, quietLeft: 10, quietRight: 10, options, (sb, height, left, bars) =>
        {
            var printable = new StringBuilder(text.Length);
            foreach (var c in text)
                printable.Append(c < ' ' || c == 127 ? ' ' : c);
            Text(sb, printable.ToString(), left + bars / 2f, height + 10, options, anchor: "middle");
        }, guardDepth: 0);
    }

    /// <summary>
    /// An EAN-13 barcode, as used on retail products, with the digits printed below it unless
    /// <see cref="BarcodeOptions.ShowText"/> is false.
    /// </summary>
    /// <param name="digits">12 digits, to which the check digit is added, or 13 digits including a correct check digit.</param>
    /// <param name="options">Size, colours and text; null for the defaults.</param>
    /// <returns>An <c>&lt;svg&gt;</c> element.</returns>
    /// <exception cref="ArgumentException">The input is not 12 or 13 digits, or the check digit is wrong.</exception>
    public static string Ean13(string digits, BarcodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(digits);
        options ??= new BarcodeOptions();
        var full = LinearEncoder.Ean13Digits(digits);
        var modules = LinearEncoder.Ean13(full);
        return LinearSvg(modules, quietLeft: 11, quietRight: 7, options, (sb, height, left, _) =>
        {
            // The first digit sits in the left quiet zone; the other two groups of six are centred under their halves.
            Text(sb, full[..1], left - 1, height + 9, options, anchor: "end");
            Text(sb, full[1..7], left + 3 + 21, height + 9, options, anchor: "middle");
            Text(sb, full[7..], left + 50 + 21, height + 9, options, anchor: "middle");
        }, guardDepth: 5, guards: new[] { (0, 3), (45, 5), (92, 3) });
    }

    /// <summary>
    /// Turns SVG markup into a <c>data:image/svg+xml;base64,...</c> URI for an <c>&lt;img src&gt;</c>, a CSS
    /// <c>background-image</c> or a Markdown image.
    /// </summary>
    /// <param name="svg">SVG markup, such as the result of <see cref="QrCode"/>.</param>
    /// <returns>The data URI.</returns>
    public static string ToDataUri(string svg)
    {
        ArgumentNullException.ThrowIfNull(svg);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    private static string QrSvg(bool[,] modules, BarcodeOptions options) => MatrixSvg(modules, quietZone: 4, options);

    private static string MatrixSvg(bool[,] modules, int quietZone, BarcodeOptions options,
        Action<StringBuilder, int, int>? overlay = null)
    {
        var size = modules.GetLength(0);
        var quiet = options.QuietZone ? quietZone : 0;
        var total = size + 2 * quiet;
        var moduleSize = options.ModuleSize > 0 ? options.ModuleSize : 3;
        // About a quarter of the modules start a run, and each run is about 14 characters of path data.
        var sb = new StringBuilder(size * size * 4 + 256);
        Open(sb, total, total, moduleSize, options);
        sb.Append("<path fill=\"").Append(Escape(options.Color)).Append("\" d=\"");
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size;)
            {
                if (!modules[y, x])
                {
                    x++;
                    continue;
                }
                var start = x;
                while (x < size && modules[y, x])
                    x++;
                sb.Append(CultureInfo.InvariantCulture, $"M{start + quiet} {y + quiet}h{x - start}v1h-{x - start}z");
            }
        }
        sb.Append("\"/>");
        overlay?.Invoke(sb, size, quiet);
        return sb.Append("</svg>").ToString();
    }

    /// <summary>
    /// The Swiss cross of the QR-bill: 7 mm on a 46 mm code, a black square with a white border and a white cross in
    /// the proportions of the official graphic. It is drawn in black and white whatever the options' colours, as the
    /// style guide requires.
    /// </summary>
    private static void SwissCross(StringBuilder sb, int size, int quiet)
    {
        var unit = size * 7f / 46 / 19.8f;
        var origin = quiet + (size - 19.8f * unit) / 2;
        void Rect(float x, float y, float width, float height, string fill) =>
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{Number(origin + x * unit)}\" y=\"{Number(origin + y * unit)}\" width=\"{Number(width * unit)}\" height=\"{Number(height * unit)}\" fill=\"{fill}\"/>");
        Rect(0, 0, 19.8f, 19.8f, "#fff");
        Rect(1.4f, 1.4f, 17f, 17f, "#000");
        Rect(8.25f, 4.5f, 3.3f, 10.8f, "#fff");
        Rect(4.5f, 8.25f, 10.8f, 3.3f, "#fff");
    }

    private static string LinearSvg(bool[] modules, int quietLeft, int quietRight, BarcodeOptions options,
        Action<StringBuilder, float, int, int> drawText, int guardDepth, (int Start, int Length)[]? guards = null)
    {
        if (!options.QuietZone)
            quietLeft = quietRight = 0;
        var moduleSize = options.ModuleSize > 0 ? options.ModuleSize : 1.5f;
        var height = options.Height > 0 ? options.Height / moduleSize : 60;
        var width = modules.Length + quietLeft + quietRight;
        var textHeight = options.ShowText ? 11 : 0;
        var sb = new StringBuilder(modules.Length * 24 + 256);
        Open(sb, width, height + textHeight, moduleSize, options);
        sb.Append("<path fill=\"").Append(Escape(options.Color)).Append("\" d=\"");
        for (var x = 0; x < modules.Length;)
        {
            if (!modules[x])
            {
                x++;
                continue;
            }
            var start = x;
            while (x < modules.Length && modules[x])
                x++;
            // Guard bars reach down between the digits, as on printed EAN codes.
            var bar = height;
            if (options.ShowText && guards is not null && Array.Exists(guards, g => start >= g.Start && start < g.Start + g.Length))
                bar += guardDepth;
            sb.Append(CultureInfo.InvariantCulture, $"M{start + quietLeft} 0h{x - start}v{Number(bar)}h-{x - start}z");
        }
        sb.Append("\"/>");
        if (options.ShowText)
            drawText(sb, height, quietLeft, modules.Length);
        return sb.Append("</svg>").ToString();
    }

    private static void Open(StringBuilder sb, float width, float height, float moduleSize, BarcodeOptions options)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Number(width * moduleSize)}\" height=\"{Number(height * moduleSize)}\" viewBox=\"0 0 {Number(width)} {Number(height)}\" shape-rendering=\"crispEdges\">");
        if (!string.IsNullOrEmpty(options.Background))
            sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{Number(width)}\" height=\"{Number(height)}\" fill=\"{Escape(options.Background)}\"/>");
    }

    private static void Text(StringBuilder sb, string text, float x, float y, BarcodeOptions options, string anchor) =>
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{Number(x)}\" y=\"{Number(y)}\" font-family=\"monospace\" font-size=\"10\" text-anchor=\"{anchor}\" fill=\"{Escape(options.Color)}\">{Escape(text)}</text>");

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>How much of a QR code can be damaged or covered and still read; more needs a larger code.</summary>
public enum QrErrorCorrection
{
    /// <summary>About 7 percent.</summary>
    Low,
    /// <summary>About 15 percent. The default.</summary>
    Medium,
    /// <summary>About 25 percent.</summary>
    Quartile,
    /// <summary>About 30 percent, for codes printed small or with a logo on top.</summary>
    High,
}

/// <summary>Size, colours and text of a barcode drawn by <see cref="Barcode"/>.</summary>
public sealed class BarcodeOptions
{
    /// <summary>
    /// The width of one module (the narrowest bar, or one square of a QR or Data Matrix code) in CSS pixels, which sets
    /// the SVG's width and height. Default 0: 3 for QR and Data Matrix codes and 1.5 for barcodes. CSS <c>width</c> on
    /// the SVG or image scales it too.
    /// </summary>
    public float ModuleSize { get; set; }

    /// <summary>Height of the bars of a Code 128 or EAN-13 barcode in CSS pixels. Default 0: 60 modules.</summary>
    public float Height { get; set; }

    /// <summary>Error correction of a QR code. Default: <see cref="QrErrorCorrection.Medium"/>.</summary>
    public QrErrorCorrection ErrorCorrection { get; set; } = QrErrorCorrection.Medium;

    /// <summary>
    /// Leave the blank margin scanners need around the code: 4 modules for QR codes, 2 for Data Matrix codes, 10 for
    /// Code 128, 11 and 7 for EAN-13. Default: true. Turn it off only when the layout leaves that space around the
    /// code.
    /// </summary>
    public bool QuietZone { get; set; } = true;

    /// <summary>Print the text or digits below a Code 128 or EAN-13 barcode. Default: true.</summary>
    public bool ShowText { get; set; } = true;

    /// <summary>Colour of the bars and text, as a CSS colour. Default: "#000".</summary>
    public string Color { get; set; } = "#000";

    /// <summary>
    /// Colour behind the code, as a CSS colour, or null or empty for none. Default: "#fff", since scanners need a light
    /// background and a coloured page could otherwise show through.
    /// </summary>
    public string? Background { get; set; } = "#fff";
}
