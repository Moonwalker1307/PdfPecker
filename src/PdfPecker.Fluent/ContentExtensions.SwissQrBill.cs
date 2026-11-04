using System.Globalization;
using System.Text;

namespace PdfPecker.Fluent;

public static partial class ContentExtensions
{
    // The Swiss QR-bill payment part, laid out after the Swiss Payment Standards' style guide for the QR-bill: a
    // 210 x 105 mm slip with the receipt (62 mm) on the left and the payment part (148 mm) on the right. QuestPDF has
    // nothing like it. The library builds and checks the payload; this file only places the text around the code.

    private const Unit Mm = Unit.Millimetre;

    /// <summary>
    /// The payment part of a Swiss QR-bill with its receipt, 210 x 105 mm, as printed at the bottom of an A4 invoice:
    /// the Swiss QR code with the Swiss cross, the account, reference, additional information and debtor, and the
    /// currency and amount, in the sizes and positions of the QR-bill style guide. A missing amount or debtor gets the
    /// blank field with corner marks to fill in by hand. Dashed lines above it and between the receipt and the payment
    /// part show where to cut. Put it in the page's footer, or in the content of a page with no bottom margin, so it
    /// sits at the bottom edge of the paper.
    /// </summary>
    /// <param name="container">The slot, which must be at least 210 x 105 mm.</param>
    /// <param name="bill">The account, creditor, amount, debtor and reference.</param>
    /// <param name="language">The language of the headings. Default: English.</param>
    /// <exception cref="ArgumentException">A field of the bill is missing, too long or invalid.</exception>
    public static void SwissQrBill(this IContainer container, SwissQrBill bill, SwissQrBillLanguage language = SwissQrBillLanguage.English)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (!Enum.IsDefined(language))
            throw new ArgumentOutOfRangeException(nameof(language));
        // The library checks every field while it builds the code, so the text below can trust them.
        var qrCode = Barcode.SwissQrCode(bill, new BarcodeOptions { QuietZone = false });
        var labels = SwissLabels[(int)language];
        var slip = new SwissSlip(bill);

        // The dashed lines take half a point of the 105 and 62 mm, so the slip keeps its size.
        const float line = 0.5f;
        float[] dashes = [3, 2];
        container.Width(210, Mm).Height(105, Mm)
            .DefaultTextStyle(s => s.FontFamily("Liberation Sans", "Arial", "Helvetica").FontColor(Colors.Black).NormalWeight().Italic(false))
            .Column(column =>
            {
                column.Item().LineHorizontal(line).LineColor(Colors.Black).LineDashPattern(dashes);
                column.Item().Height(Length.ToPoints(105, Mm) - line).Row(row =>
                {
                    row.ConstantItem(Length.ToPoints(62, Mm) - line).Padding(5, Mm).Element(c => Receipt(c, labels, slip));
                    row.AutoItem().LineVertical(line).LineColor(Colors.Black).LineDashPattern(dashes);
                    row.RelativeItem().Padding(5, Mm).Element(c => PaymentPart(c, labels, slip, qrCode));
                });
            });
    }

    private static void Receipt(IContainer container, string[] labels, SwissSlip slip)
    {
        // Headings are 6 pt bold and values 8 pt on 9 pt lines, as the style guide sets for the receipt.
        container.Column(column =>
        {
            column.Item().Height(7, Mm).Text(labels[0]).FontSize(11).Bold();
            column.Item().Height(56, Mm).Column(info =>
            {
                SwissSection(info, labels[2], slip.Creditor, 6, 8, last: false);
                if (slip.Reference.Length != 0)
                    SwissSection(info, labels[3], [slip.Reference], 6, 8, last: false);
                if (slip.Debtor is { } debtor)
                {
                    SwissSection(info, labels[5], debtor, 6, 8, last: true);
                }
                else
                {
                    SwissHeading(info, labels[6], 6);
                    info.Item().PaddingTop(1, Mm).Element(c => CornerMarks(c, 52, 20));
                }
            });
            column.Item().Height(14, Mm).Row(row =>
            {
                row.ConstantItem(13, Mm).Column(currency =>
                {
                    SwissHeading(currency, labels[7], 6);
                    currency.Item().PaddingTop(1).Text(slip.Currency).FontSize(8);
                });
                row.RelativeItem().Column(amount =>
                {
                    SwissHeading(amount, labels[8], 6);
                    if (slip.Amount.Length != 0)
                        amount.Item().PaddingTop(1).Text(slip.Amount).FontSize(8);
                    else
                        amount.Item().AlignRight().PaddingTop(0.5f, Mm).Element(c => CornerMarks(c, 30, 10));
                });
            });
            column.Item().Height(18, Mm).AlignRight().Text(labels[9]).FontSize(6).Bold();
        });
    }

    private static void PaymentPart(IContainer container, string[] labels, SwissSlip slip, string qrCode)
    {
        // Headings are 8 pt bold and values 10 pt on 11 pt lines in the payment part.
        container.Row(row =>
        {
            row.ConstantItem(51, Mm).Column(column =>
            {
                column.Item().Height(7, Mm).Text(labels[1]).FontSize(11).Bold();
                column.Item().Height(56, Mm).PaddingVertical(5, Mm).Width(46, Mm).Height(46, Mm).Svg(qrCode).FitArea();
                column.Item().Height(22, Mm).Row(amountRow =>
                {
                    amountRow.ConstantItem(15, Mm).Column(currency =>
                    {
                        SwissHeading(currency, labels[7], 8);
                        currency.Item().PaddingTop(1).Text(slip.Currency).FontSize(10);
                    });
                    amountRow.RelativeItem().Column(amount =>
                    {
                        SwissHeading(amount, labels[8], 8);
                        if (slip.Amount.Length != 0)
                            amount.Item().PaddingTop(1).Text(slip.Amount).FontSize(10);
                        else
                            amount.Item().PaddingTop(0.5f, Mm).Element(c => CornerMarks(c, 40, 15));
                    });
                });
            });
            row.RelativeItem().Column(info =>
            {
                SwissSection(info, labels[2], slip.Creditor, 8, 10, last: false);
                if (slip.Reference.Length != 0)
                    SwissSection(info, labels[3], [slip.Reference], 8, 10, last: false);
                if (slip.Information.Count != 0)
                    SwissSection(info, labels[4], slip.Information, 8, 10, last: false);
                if (slip.Debtor is { } debtor)
                {
                    SwissSection(info, labels[5], debtor, 8, 10, last: true);
                }
                else
                {
                    SwissHeading(info, labels[6], 8);
                    info.Item().PaddingTop(1, Mm).Element(c => CornerMarks(c, 65, 25));
                }
            });
        });
    }

    private static void SwissHeading(ColumnDescriptor column, string text, float size) =>
        column.Item().Text(text).FontSize(size).Bold();

    private static void SwissSection(ColumnDescriptor column, string heading, IReadOnlyList<string> values, float headingSize, float valueSize, bool last)
    {
        SwissHeading(column, heading, headingSize);
        foreach (var value in values)
            column.Item().Text(value).FontSize(valueSize).LineHeight((valueSize + 1) / valueSize);
        // One empty line of the value size between sections.
        if (!last)
            column.Item().Height(valueSize + 1);
    }

    /// <summary>The corner marks of a blank field, 3 mm long and 0.75 pt thick, as an SVG of the field's size.</summary>
    private static void CornerMarks(IContainer container, float width, float height)
    {
        // The stroke is 0.265 mm (0.75 pt); its centre line sits half a stroke inside the field so none is cut off.
        const float half = 0.1325f;
        var w = width - half;
        var h = height - half;
        var svg = string.Create(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}mm\" height=\"{height}mm\" viewBox=\"0 0 {width} {height}\"><path d=\"M{half} 3V{half}H3M{width - 3} {half}H{w}V3M{w} {height - 3}V{h}H{width - 3}M3 {h}H{half}V{height - 3}\" fill=\"none\" stroke=\"#000\" stroke-width=\"0.265\"/></svg>");
        container.Width(width, Mm).Height(height, Mm).Svg(svg).FitArea();
    }

    // Receipt, payment part, account / payable to, reference, additional information, payable by, payable by
    // (name/address), currency, amount and acceptance point, in the four languages of the style guide.
    private static readonly string[][] SwissLabels =
    [
        ["Receipt", "Payment part", "Account / Payable to", "Reference", "Additional information", "Payable by",
            "Payable by (name/address)", "Currency", "Amount", "Acceptance point"],
        ["Empfangsschein", "Zahlteil", "Konto / Zahlbar an", "Referenz", "Zusätzliche Informationen", "Zahlbar durch",
            "Zahlbar durch (Name/Adresse)", "Währung", "Betrag", "Annahmestelle"],
        ["Récépissé", "Section paiement", "Compte / Payable à", "Référence", "Informations supplémentaires",
            "Payable par", "Payable par (nom/adresse)", "Monnaie", "Montant", "Point de dépôt"],
        ["Ricevuta", "Sezione pagamento", "Conto / Pagabile a", "Riferimento", "Informazioni supplementari",
            "Pagabile da", "Pagabile da (nome/indirizzo)", "Valuta", "Importo", "Punto di accettazione"],
    ];

    /// <summary>The bill's fields as the style guide prints them: grouped account and reference, address lines.</summary>
    private sealed class SwissSlip
    {
        public readonly List<string> Creditor;
        public readonly List<string>? Debtor;
        public readonly string Reference;
        public readonly List<string> Information = [];
        public readonly string Currency;
        public readonly string Amount;

        public SwissSlip(SwissQrBill bill)
        {
            var account = bill.Account.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            Creditor = [Group(account, 4, fromRight: false), .. Address(bill.Creditor!)];
            Debtor = bill.Debtor is null ? null : Address(bill.Debtor);
            var reference = (bill.Reference ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            // A QR reference is grouped in fives from the right, a creditor reference in fours from the left.
            Reference = reference.Length == 0 ? ""
                : reference.StartsWith("RF", StringComparison.Ordinal) ? Group(reference, 4, fromRight: false)
                : Group(reference, 5, fromRight: true);
            if (!string.IsNullOrWhiteSpace(bill.Message))
                Information.Add(bill.Message.Trim());
            if (!string.IsNullOrWhiteSpace(bill.BillInformation))
                Information.Add(bill.BillInformation.Trim());
            Currency = bill.Currency;
            // A space between thousands and a point before the decimals.
            Amount = bill.Amount is { } amount
                ? amount.ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(',', ' ')
                : "";
        }

        private static List<string> Address(SwissQrAddress address)
        {
            var lines = new List<string>(3) { address.Name.Trim() };
            var street = $"{address.Street?.Trim()} {address.HouseNumber?.Trim()}".Trim();
            if (street.Length != 0)
                lines.Add(street);
            var country = address.Country.Trim().ToUpperInvariant();
            var prefix = country is "CH" or "LI" ? "" : country + "-";
            lines.Add($"{prefix}{address.PostalCode.Trim()} {address.Town.Trim()}");
            return lines;
        }

        private static string Group(string value, int size, bool fromRight)
        {
            var sb = new StringBuilder(value.Length + value.Length / size);
            var offset = fromRight ? value.Length % size : 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (i != 0 && (i - offset) % size == 0)
                    sb.Append(' ');
                sb.Append(value[i]);
            }
            return sb.ToString();
        }
    }
}

/// <summary>The language of the headings of a Swiss QR-bill payment part.</summary>
public enum SwissQrBillLanguage
{
    /// <summary>English.</summary>
    English,
    /// <summary>German.</summary>
    German,
    /// <summary>French.</summary>
    French,
    /// <summary>Italian.</summary>
    Italian,
}
