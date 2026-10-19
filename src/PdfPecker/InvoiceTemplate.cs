using System.Globalization;
using System.Net;
using System.Text;
using PdfPecker.Invoices;

namespace PdfPecker;

/// <summary>
/// A ready-made, print-ready invoice layout for <see cref="InvoiceData"/>: the seller and buyer, the lines, the totals
/// and VAT breakdown, payment details with an EPC payment QR code for euro invoices, and the references. Use it as it
/// is, with your logo, colour and labels, or as a starting point: <see cref="ToHtml"/> returns the HTML to adapt.
/// </summary>
/// <remarks>
/// The amounts on the page come from the same calculation as the e-invoice XML, so the visible invoice and the embedded
/// Factur-X data always agree. All methods are static and thread-safe.
/// </remarks>
public static class InvoiceTemplate
{
    /// <summary>The invoice as a PDF. By default the Factur-X EN 16931 XML is embedded, which makes the file PDF/A-3b.</summary>
    /// <param name="invoice">The invoice.</param>
    /// <param name="options">The logo, colour, labels and PDF settings; null for the defaults.</param>
    /// <exception cref="ArgumentException">
    /// The invoice is not valid (for the embedded e-invoice, every EN 16931 rule applies; without one, only the amounts
    /// are checked), or the logo is not an image.
    /// </exception>
    public static byte[] ToPdf(InvoiceData invoice, InvoiceTemplateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        options ??= new InvoiceTemplateOptions();
        var pdfOptions = options.PdfOptions?.Clone() ?? new PdfOptions();
        if (options.EInvoice is { } profile) pdfOptions.EInvoice = invoice.ToEInvoice(profile);
        if (string.IsNullOrEmpty(pdfOptions.Title)) pdfOptions.Title = Title(invoice, options.Labels) + " " + invoice.Number;
        return HtmlToPdf.Convert(ToHtml(invoice, options), pdfOptions);
    }

    /// <summary>The invoice as an HTML document, to convert with <see cref="HtmlToPdf"/> or to adapt.</summary>
    /// <param name="invoice">The invoice.</param>
    /// <param name="options">The logo, colour and labels; null for the defaults.</param>
    /// <exception cref="ArgumentException">An amount in the invoice is not valid, or the logo is not an image.</exception>
    public static string ToHtml(InvoiceData invoice, InvoiceTemplateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        options ??= new InvoiceTemplateOptions();
        var labels = options.Labels ?? InvoiceLabels.English;
        var totals = invoice.CalculateTotals();
        var f = new Formatter(labels);
        var html = new StringBuilder(8192);

        html.Append("<!DOCTYPE html><html lang=\"").Append(Encode(labels.Language)).Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(Encode(Title(invoice, labels) + " " + invoice.Number)).Append("</title><style>")
            .Append(Css.Replace("{accent}", SafeColor(options.AccentColor), StringComparison.Ordinal)
                .Replace("{page}", CssString(labels.Page), StringComparison.Ordinal)
                .Replace("{of}", CssString(labels.Of), StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(options.Css)) html.Append(options.Css.Replace("</", "<\\/", StringComparison.Ordinal));
        html.Append("</style></head><body>");

        // Header: logo and seller on the left, the document title and its key facts on the right.
        html.Append("<header class=\"top\"><div class=\"seller\">");
        if (LogoSource(options) is { } logo)
            html.Append("<img class=\"logo\" src=\"").Append(Encode(logo)).Append("\" alt=\"").Append(Encode(invoice.Seller.Name)).Append("\">");
        html.Append("<div class=\"seller-name\">").Append(Encode(invoice.Seller.TradingName ?? invoice.Seller.Name)).Append("</div>");
        AppendAddress(html, invoice.Seller.Address);
        html.Append("</div><div class=\"title\"><h1>").Append(Encode(Title(invoice, labels))).Append("</h1><table class=\"facts\">");
        Fact(html, labels.Number, invoice.Number);
        Fact(html, labels.IssueDate, f.Date(invoice.IssueDate));
        if (invoice.DueDate is { } due) Fact(html, labels.DueDate, f.Date(due));
        if (invoice.DeliveryDate is { } delivered) Fact(html, labels.DeliveryDate, f.Date(delivered));
        if (invoice.PeriodStart is not null || invoice.PeriodEnd is not null)
            Fact(html, labels.Period, (invoice.PeriodStart is { } s ? f.Date(s) : "") + " – " + (invoice.PeriodEnd is { } e ? f.Date(e) : ""));
        html.Append("</table></div></header>");

        // The buyer, where the goods went, and the references the buyer needs to match the invoice.
        html.Append("<section class=\"parties\"><div class=\"party\"><div class=\"label\">").Append(Encode(labels.BillTo)).Append("</div><div class=\"party-name\">")
            .Append(Encode(invoice.Buyer.Name)).Append("</div>");
        AppendAddress(html, invoice.Buyer.Address);
        if (Has(invoice.Buyer.VatId)) html.Append("<div>").Append(Encode(labels.VatId)).Append(' ').Append(Encode(invoice.Buyer.VatId!)).Append("</div>");
        if (Has(invoice.Buyer.Contact?.Name)) html.Append("<div>").Append(Encode(invoice.Buyer.Contact!.Name!)).Append("</div>");
        html.Append("</div>");
        if (Has(invoice.DeliverToName) || invoice.DeliverToAddress is not null)
        {
            html.Append("<div class=\"party\"><div class=\"label\">").Append(Encode(labels.DeliverTo)).Append("</div>");
            if (Has(invoice.DeliverToName)) html.Append("<div class=\"party-name\">").Append(Encode(invoice.DeliverToName!)).Append("</div>");
            if (invoice.DeliverToAddress is { } address) AppendAddress(html, address);
            html.Append("</div>");
        }
        var references = new List<(string, string)>();
        if (Has(invoice.Buyer.Id)) references.Add((labels.CustomerNumber, invoice.Buyer.Id!));
        if (Has(invoice.BuyerReference)) references.Add((labels.BuyerReference, invoice.BuyerReference!));
        if (Has(invoice.PurchaseOrderReference)) references.Add((labels.OrderReference, invoice.PurchaseOrderReference!));
        if (Has(invoice.ContractReference)) references.Add((labels.ContractReference, invoice.ContractReference!));
        if (Has(invoice.PrecedingInvoiceNumber))
            references.Add((labels.PrecedingInvoice, invoice.PrecedingInvoiceNumber! + (invoice.PrecedingInvoiceDate is { } p ? " (" + f.Date(p) + ")" : "")));
        if (references.Count != 0)
        {
            html.Append("<div class=\"party\"><table class=\"facts\">");
            foreach (var (label, value) in references) Fact(html, label, value);
            html.Append("</table></div>");
        }
        html.Append("</section>");

        foreach (var note in invoice.Notes)
            if (Has(note)) html.Append("<p class=\"note\">").Append(Encode(note)).Append("</p>");

        // The lines. The VAT column is left out when every line has the same rate, which the breakdown then shows.
        bool vatColumn = invoice.Lines.Select(l => (l.VatCategory, InvoiceCalculator.RateOf(l.VatCategory, l.VatRate))).Distinct().Count() > 1;
        html.Append("<table class=\"lines\"><thead><tr><th class=\"desc\">").Append(Encode(labels.Description))
            .Append("</th><th class=\"num\">").Append(Encode(labels.Quantity))
            .Append("</th><th class=\"num\">").Append(Encode(labels.UnitPrice)).Append("</th>");
        if (vatColumn) html.Append("<th class=\"num\">").Append(Encode(labels.Vat)).Append("</th>");
        html.Append("<th class=\"num\">").Append(Encode(labels.Amount)).Append("</th></tr></thead><tbody>");
        foreach (var line in invoice.Lines)
        {
            html.Append("<tr><td class=\"desc\"><div class=\"item\">").Append(Encode(line.Name)).Append("</div>");
            if (Has(line.Description)) html.Append("<div class=\"sub\">").Append(Encode(line.Description!)).Append("</div>");
            if (Has(line.SellerItemId)) html.Append("<div class=\"sub\">").Append(Encode(labels.ItemNumber)).Append(' ').Append(Encode(line.SellerItemId!)).Append("</div>");
            if (Has(line.Note)) html.Append("<div class=\"sub\">").Append(Encode(line.Note!)).Append("</div>");
            if (line.Discount != 0)
                html.Append("<div class=\"sub\">").Append(Encode(Has(line.DiscountReason) ? line.DiscountReason! : labels.Discount)).Append(" −")
                    .Append(Encode(f.Money(line.Discount))).Append("</div>");
            html.Append("</td><td class=\"num\">").Append(Encode(f.Number(line.Quantity))).Append(' ').Append(Encode(labels.Unit(line.UnitCode)))
                .Append("</td><td class=\"num\">").Append(Encode(f.Price(line.NetPrice)));
            if (line.PriceBaseQuantity is { } baseQuantity && baseQuantity != 1)
                html.Append(" / ").Append(Encode(f.Number(baseQuantity))).Append(' ').Append(Encode(labels.Unit(line.UnitCode)));
            html.Append("</td>");
            if (vatColumn) html.Append("<td class=\"num\">").Append(Encode(f.VatRate(line.VatCategory, line.VatRate))).Append("</td>");
            html.Append("<td class=\"num\">").Append(Encode(f.Money(line.NetAmount))).Append("</td></tr>");
        }
        html.Append("</tbody></table>");

        // Totals, kept together with the payment details so the amount due and how to pay it never part.
        html.Append("<section class=\"end\"><table class=\"totals\">");
        bool adjustments = invoice.AllowancesAndCharges.Count != 0;
        if (adjustments)
        {
            TotalRow(html, labels.Subtotal, f.Money(totals.LineTotal));
            foreach (var item in invoice.AllowancesAndCharges)
            {
                var reason = Has(item.Reason) ? item.Reason! : item.IsCharge ? labels.Charge : labels.Discount;
                TotalRow(html, reason, (item.IsCharge ? "" : "−") + f.Money(item.Amount));
            }
        }
        TotalRow(html, labels.NetTotal, f.Money(totals.NetTotal));
        foreach (var vat in totals.Vat)
        {
            var label = InvoiceCalculator.HasRate(vat.Category) || vat.Category == VatCategory.ZeroRated
                ? labels.Vat + " " + f.Number(vat.Rate) + " % " + labels.Of + " " + f.Money(vat.TaxableAmount)
                : labels.Vat + " " + f.Money(vat.TaxableAmount) + ": " + (vat.ExemptionReason ?? "");
            TotalRow(html, label, f.Money(vat.VatAmount));
        }
        TotalRow(html, labels.Total + " " + invoice.Currency, f.Money(totals.GrandTotal), "grand");
        if (totals.PrepaidAmount != 0)
        {
            TotalRow(html, labels.Prepaid, "−" + f.Money(totals.PrepaidAmount));
            TotalRow(html, labels.AmountDue + " " + invoice.Currency, f.Money(totals.AmountDue), "grand");
        }
        html.Append("</table>");

        AppendPayment(html, invoice, totals, labels, f, options);
        html.Append("</section>");

        AppendSellerFooter(html, invoice.Seller, labels);
        html.Append("</body></html>");
        return html.ToString();
    }

    private static void AppendPayment(StringBuilder html, InvoiceData invoice, InvoiceTotals totals, InvoiceLabels labels, Formatter f, InvoiceTemplateOptions options)
    {
        var payment = invoice.Payment;
        if (payment is null && !Has(invoice.PaymentTerms)) return;
        html.Append("<div class=\"payment\"><div class=\"payment-text\"><div class=\"label\">").Append(Encode(labels.Payment)).Append("</div><table class=\"facts\">");
        if (payment is not null)
        {
            if (Has(payment.Iban)) Fact(html, "IBAN", GroupIban(InvoiceRules.NormalizeIban(payment.Iban!)));
            if (Has(payment.AccountNumber)) Fact(html, labels.AccountNumber, payment.AccountNumber!);
            if (Has(payment.Bic)) Fact(html, "BIC", payment.Bic!);
            if (Has(payment.AccountName)) Fact(html, labels.AccountName, payment.AccountName!);
            Fact(html, labels.PaymentReference, Has(payment.Reference) ? payment.Reference! : invoice.Number);
        }
        if (invoice.DueDate is { } due) Fact(html, labels.DueDate, f.Date(due));
        if (Has(invoice.PaymentTerms)) Fact(html, labels.PaymentTerms, invoice.PaymentTerms!);
        html.Append("</table></div>");

        // An EPC QR code only exists for euro credit transfers to an IBAN, and only makes sense when money is owed.
        if (options.PaymentQrCode && payment is { MeansCode: 58 or 30 } && Has(payment.Iban) && invoice.Currency == "EUR"
            && totals.AmountDue is >= 0.01m and <= 999_999_999.99m && invoice.Type != InvoiceType.CreditNote)
        {
            var epc = new EpcPayment
            {
                Name = Truncate(Has(payment.AccountName) ? payment.AccountName! : invoice.Seller.Name, 70),
                Iban = payment.Iban!,
                Bic = payment.Bic,
                Amount = totals.AmountDue,
                Text = Truncate(Has(payment.Reference) ? payment.Reference! : invoice.Number, 140),
            };
            html.Append("<div class=\"qr\">").Append(Barcode.EpcQrCode(epc, new BarcodeOptions { ModuleSize = 2.2f }))
                .Append("<div class=\"sub\">").Append(Encode(labels.ScanToPay)).Append("</div></div>");
        }
        html.Append("</div>");
    }

    private static void AppendSellerFooter(StringBuilder html, InvoiceParty seller, InvoiceLabels labels)
    {
        html.Append("<footer class=\"legal\"><div><strong>").Append(Encode(seller.Name)).Append("</strong>");
        if (seller.Address is { } a)
        {
            var parts = new[] { a.Line1, a.Line2, a.Line3, Join(" ", a.PostalCode, a.City), a.Region, a.CountryCode }.Where(Has);
            html.Append(" · ").Append(Encode(string.Join(", ", parts)));
        }
        html.Append("</div><div>");
        var items = new List<string>();
        if (Has(seller.VatId)) items.Add(labels.VatId + " " + seller.VatId);
        if (Has(seller.TaxNumber)) items.Add(labels.TaxNumber + " " + seller.TaxNumber);
        if (Has(seller.LegalRegistrationId)) items.Add(labels.RegistrationNumber + " " + seller.LegalRegistrationId);
        if (seller.Contact is { } c)
        {
            if (Has(c.Name)) items.Add(c.Name!);
            if (Has(c.Phone)) items.Add(c.Phone!);
            if (Has(c.Email)) items.Add(c.Email!);
        }
        html.Append(Encode(string.Join(" · ", items))).Append("</div></footer>");
    }

    private static void AppendAddress(StringBuilder html, InvoiceAddress? address)
    {
        if (address is null) return;
        foreach (var line in new[] { address.Line1, address.Line2, address.Line3 })
            if (Has(line)) html.Append("<div>").Append(Encode(line!)).Append("</div>");
        var city = Join(" ", address.PostalCode, address.City);
        if (Has(city)) html.Append("<div>").Append(Encode(city)).Append("</div>");
        if (Has(address.Region)) html.Append("<div>").Append(Encode(address.Region!)).Append("</div>");
        if (Has(address.CountryCode)) html.Append("<div>").Append(Encode(address.CountryCode)).Append("</div>");
    }

    private static void Fact(StringBuilder html, string label, string value)
        => html.Append("<tr><th>").Append(Encode(label)).Append("</th><td>").Append(Encode(value)).Append("</td></tr>");

    private static void TotalRow(StringBuilder html, string label, string value, string? cssClass = null)
    {
        html.Append(cssClass is null ? "<tr>" : "<tr class=\"" + cssClass + "\">").Append("<th>").Append(Encode(label))
            .Append("</th><td>").Append(Encode(value)).Append("</td></tr>");
    }

    internal static string Title(InvoiceData invoice, InvoiceLabels? labels)
    {
        labels ??= InvoiceLabels.English;
        return invoice.Type switch
        {
            InvoiceType.CreditNote => labels.CreditNote,
            InvoiceType.Corrected => labels.CorrectedInvoice,
            InvoiceType.Prepayment => labels.PrepaymentInvoice,
            InvoiceType.SelfBilled => labels.SelfBilledInvoice,
            _ => labels.Invoice,
        };
    }

    /// <summary>The logo's src: a data URI for image bytes, or the caller's URI as given.</summary>
    private static string? LogoSource(InvoiceTemplateOptions options)
    {
        if (options.Logo is { Length: > 0 } bytes)
        {
            var mime = bytes switch
            {
                [0xFF, 0xD8, ..] => "image/jpeg",
                [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
                [0x47, 0x49, 0x46, ..] => "image/gif",
                [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
                [0x42, 0x4D, ..] => "image/bmp",
                _ when LooksLikeSvg(bytes) => "image/svg+xml",
                _ => throw new ArgumentException("The logo is not a PNG, JPEG, GIF, WebP, BMP or SVG image.", nameof(options)),
            };
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        return string.IsNullOrWhiteSpace(options.LogoUri) ? null : options.LogoUri.Trim();
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
        return head.Contains("<svg", StringComparison.Ordinal);
    }

    /// <summary>The accent colour if it is a plain CSS colour (hex, rgb() or a name), else the default, so it cannot break out of the style sheet.</summary>
    private static string SafeColor(string? color)
    {
        const string fallback = "#1f4e79";
        if (string.IsNullOrWhiteSpace(color) || color.Length > 40) return fallback;
        foreach (var c in color)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('#' or '(' or ')' or ',' or '.' or ' ' or '%')) return fallback;
        return color.Trim();
    }

    private static string CssString(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (char.IsControl(c)) sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string GroupIban(string iban)
    {
        var sb = new StringBuilder(iban.Length + iban.Length / 4);
        for (var i = 0; i < iban.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(iban[i]);
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text.Trim() : text[..max].Trim();

    private static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(Has));

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Formats numbers and dates with the labels' separators and date pattern, independently of the machine's culture.</summary>
    private readonly struct Formatter(InvoiceLabels labels)
    {
        internal string Money(decimal amount) => Group(amount.ToString("0.00", CultureInfo.InvariantCulture));

        /// <summary>A unit price, with two decimals or as many more as it has.</summary>
        internal string Price(decimal price) => Group(price.ToString("0.00########", CultureInfo.InvariantCulture));

        internal string Number(decimal value) => Group(value.ToString("0.##########", CultureInfo.InvariantCulture));

        internal string VatRate(VatCategory category, decimal rate)
            => InvoiceCalculator.HasRate(category) || category == VatCategory.ZeroRated ? Number(rate) + " %" : InvoiceCalculator.Code(category);

        internal string Date(DateOnly date) => date.ToString(labels.DateFormat, CultureInfo.InvariantCulture);

        private string Group(string invariant)
        {
            int dot = invariant.IndexOf('.', StringComparison.Ordinal);
            var whole = dot < 0 ? invariant : invariant[..dot];
            var sb = new StringBuilder(invariant.Length + 8);
            int start = whole.StartsWith('-') ? 1 : 0;
            if (start == 1) sb.Append('−');
            for (var i = start; i < whole.Length; i++)
            {
                if (i > start && (whole.Length - i) % 3 == 0) sb.Append(labels.GroupSeparator);
                sb.Append(whole[i]);
            }
            if (dot >= 0) sb.Append(labels.DecimalSeparator).Append(invariant.AsSpan(dot + 1));
            return sb.ToString();
        }
    }

    private const string Css = """
        @page { size: A4; margin: 18mm 16mm 20mm 16mm;
          @bottom-right { content: {page} " " counter(page) " " {of} " " counter(pages); font: 8pt sans-serif; color: #888; } }
        * { box-sizing: border-box; }
        body { font-family: sans-serif; font-size: 9.5pt; color: #222; line-height: 1.35; margin: 0; }
        .top { display: flex; justify-content: space-between; align-items: flex-start; gap: 24px; margin-bottom: 28px; }
        .logo { display: block; max-width: 180px; max-height: 64px; margin-bottom: 10px; }
        .seller-name { font-weight: bold; font-size: 11pt; margin-bottom: 2px; }
        .title { text-align: right; }
        h1 { font-size: 22pt; font-weight: bold; letter-spacing: 1px; color: {accent}; margin: 0 0 8px 0; text-transform: uppercase; }
        table.facts { border-collapse: collapse; margin-left: auto; }
        .title table.facts th { text-align: right; }
        table.facts th { font-weight: normal; color: #666; text-align: left; padding: 1px 10px 1px 0; white-space: nowrap; vertical-align: top; }
        table.facts td { text-align: left; padding: 1px 0; }
        .parties { display: flex; gap: 32px; margin-bottom: 22px; }
        .party { flex: 1; }
        .parties .party:last-child table.facts { margin-left: 0; }
        .label { font-size: 8pt; text-transform: uppercase; letter-spacing: 0.5px; color: {accent}; font-weight: bold; margin-bottom: 4px; }
        .party-name { font-weight: bold; }
        .note { margin: 0 0 12px 0; }
        table.lines { width: 100%; border-collapse: collapse; margin-bottom: 14px; }
        table.lines thead tr { background: {accent}; }
        table.lines th { color: #fff; font-weight: bold; font-size: 8.5pt; padding: 6px 8px; text-align: left; }
        table.lines td { padding: 6px 8px; border-bottom: 0.75pt solid #ddd; vertical-align: top; }
        table.lines tr { break-inside: avoid; }
        table.lines .num { text-align: right; white-space: nowrap; }
        .item { font-weight: bold; }
        .sub { color: #666; font-size: 8.5pt; }
        .end { break-inside: avoid; }
        table.totals { margin-left: auto; border-collapse: collapse; min-width: 50%; }
        table.totals th { font-weight: normal; text-align: left; padding: 3px 16px 3px 0; color: #444; }
        table.totals td { text-align: right; padding: 3px 0; white-space: nowrap; }
        table.totals tr.grand th, table.totals tr.grand td { font-weight: bold; font-size: 11pt; color: #000; border-top: 1.5pt solid {accent}; padding-top: 6px; }
        .payment { display: flex; justify-content: space-between; gap: 24px; margin-top: 22px; padding: 12px 14px; background: #f4f6f8; border-radius: 4px; }
        .payment .facts { margin-left: 0; }
        .qr { text-align: center; }
        .qr svg { display: block; margin: 0 auto 2px auto; }
        .legal { margin-top: 28px; padding-top: 8px; border-top: 0.75pt solid #ddd; font-size: 8pt; color: #666; }
        """;
}

/// <summary>The look and the texts of <see cref="InvoiceTemplate"/>.</summary>
public sealed class InvoiceTemplateOptions
{
    /// <summary>Your logo as PNG, JPEG, GIF, WebP, BMP or SVG bytes, or null. It is drawn at most 180 by 64 pixels.</summary>
    public byte[]? Logo { get; set; }

    /// <summary>
    /// Your logo as a URI (a data URI, or a URL your <see cref="PdfOptions.ResourceResolver"/> loads), or null. Used when
    /// <see cref="Logo"/> is null.
    /// </summary>
    public string? LogoUri { get; set; }

    /// <summary>The colour of the title, headings and table header, as CSS (#1f4e79, rgb(...) or a name). Default: #1f4e79.</summary>
    public string AccentColor { get; set; } = "#1f4e79";

    /// <summary>The texts and number and date formats. Default: <see cref="InvoiceLabels.English"/>.</summary>
    public InvoiceLabels Labels { get; set; } = InvoiceLabels.English;

    /// <summary>Extra CSS added after the template's own, to change fonts, spacing or anything else. Default: null.</summary>
    public string? Css { get; set; }

    /// <summary>
    /// Print an EPC payment QR code (GiroCode) next to the payment details for euro invoices paid by credit transfer to
    /// an IBAN. Default: true.
    /// </summary>
    public bool PaymentQrCode { get; set; } = true;

    /// <summary>
    /// The e-invoice <see cref="InvoiceTemplate.ToPdf"/> embeds: <see cref="EInvoiceProfile.En16931"/> (default) or
    /// <see cref="EInvoiceProfile.XRechnung"/>, or null for a plain PDF.
    /// </summary>
    public EInvoiceProfile? EInvoice { get; set; } = EInvoiceProfile.En16931;

    /// <summary>
    /// The PDF settings <see cref="InvoiceTemplate.ToPdf"/> starts from, such as fonts, metadata or a signature, or
    /// null. They are copied, not changed. Page size and margins come from the template's CSS.
    /// </summary>
    public PdfOptions? PdfOptions { get; set; }
}

/// <summary>
/// The texts of <see cref="InvoiceTemplate"/>, with its number and date formats. Start from <see cref="English"/>,
/// <see cref="German"/> or <see cref="Danish"/> and change what you need; each returns a new instance.
/// </summary>
public sealed class InvoiceLabels
{
    /// <summary>English texts, 1,234.56 and dates as 2026-10-13.</summary>
    public static InvoiceLabels English => new();

    /// <summary>German texts, 1.234,56 and dates as 13.10.2026.</summary>
    public static InvoiceLabels German => new()
    {
        Language = "de", DecimalSeparator = ",", GroupSeparator = ".", DateFormat = "dd.MM.yyyy",
        Invoice = "Rechnung", CreditNote = "Gutschrift", CorrectedInvoice = "Rechnungskorrektur", PrepaymentInvoice = "Vorauszahlungsrechnung",
        SelfBilledInvoice = "Gutschrift (Selbstfakturierung)", Number = "Rechnungsnr.", IssueDate = "Rechnungsdatum", DueDate = "Fällig am",
        DeliveryDate = "Lieferdatum", Period = "Leistungszeitraum", BillTo = "Rechnungsempfänger", DeliverTo = "Lieferanschrift",
        CustomerNumber = "Kundennr.", BuyerReference = "Käuferreferenz", OrderReference = "Bestellnr.", ContractReference = "Vertragsnr.",
        PrecedingInvoice = "Zu Rechnung", Description = "Beschreibung", Quantity = "Menge", UnitPrice = "Einzelpreis", Vat = "USt.",
        Amount = "Betrag", ItemNumber = "Art.-Nr.", Discount = "Rabatt", Charge = "Zuschlag", Subtotal = "Zwischensumme", NetTotal = "Summe netto",
        Of = "von", Total = "Gesamtbetrag", Prepaid = "Bereits gezahlt", AmountDue = "Zu zahlen", Payment = "Zahlung", AccountNumber = "Kontonr.",
        AccountName = "Kontoinhaber", PaymentReference = "Verwendungszweck", PaymentTerms = "Zahlungsbedingungen", ScanToPay = "Scannen und bezahlen",
        VatId = "USt-IdNr.", TaxNumber = "Steuernr.", RegistrationNumber = "Handelsregister", Page = "Seite",
        Units = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C62"] = "Stk.", ["H87"] = "Stk.", ["HUR"] = "Std.", ["DAY"] = "Tage", ["MON"] = "Monate", ["KGM"] = "kg", ["MTR"] = "m", ["LTR"] = "l", ["MIN"] = "Min.",
        },
    };

    /// <summary>Danish texts, 1.234,56 and dates as 13.10.2026.</summary>
    public static InvoiceLabels Danish => new()
    {
        Language = "da", DecimalSeparator = ",", GroupSeparator = ".", DateFormat = "dd.MM.yyyy",
        Invoice = "Faktura", CreditNote = "Kreditnota", CorrectedInvoice = "Korrigeret faktura", PrepaymentInvoice = "Forudbetalingsfaktura",
        SelfBilledInvoice = "Selvfaktura", Number = "Fakturanr.", IssueDate = "Fakturadato", DueDate = "Forfaldsdato",
        DeliveryDate = "Leveringsdato", Period = "Periode", BillTo = "Faktureres til", DeliverTo = "Leveringsadresse",
        CustomerNumber = "Kundenr.", BuyerReference = "Købers reference", OrderReference = "Ordrenr.", ContractReference = "Kontraktnr.",
        PrecedingInvoice = "Vedrørende faktura", Description = "Beskrivelse", Quantity = "Antal", UnitPrice = "Stykpris", Vat = "Moms",
        Amount = "Beløb", ItemNumber = "Varenr.", Discount = "Rabat", Charge = "Gebyr", Subtotal = "Subtotal", NetTotal = "I alt ekskl. moms",
        Of = "af", Total = "I alt", Prepaid = "Allerede betalt", AmountDue = "At betale", Payment = "Betaling", AccountNumber = "Kontonr.",
        AccountName = "Kontohaver", PaymentReference = "Betalingsreference", PaymentTerms = "Betalingsbetingelser", ScanToPay = "Scan og betal",
        VatId = "CVR/moms-nr.", TaxNumber = "Skattenr.", RegistrationNumber = "CVR-nr.", Page = "Side",
        Units = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C62"] = "stk.", ["H87"] = "stk.", ["HUR"] = "timer", ["DAY"] = "dage", ["MON"] = "mdr.", ["KGM"] = "kg", ["MTR"] = "m", ["LTR"] = "l", ["MIN"] = "min.",
        },
    };

    /// <summary>The page's language code (the html lang attribute). Default: en.</summary>
    public string Language { get; set; } = "en";

    /// <summary>The decimal separator. Default: ".".</summary>
    public string DecimalSeparator { get; set; } = ".";

    /// <summary>The thousands separator. Default: ",".</summary>
    public string GroupSeparator { get; set; } = ",";

    /// <summary>The date format, a .NET custom date pattern formatted with the invariant culture. Default: yyyy-MM-dd.</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>The title of a commercial invoice.</summary>
    public string Invoice { get; set; } = "Invoice";

    /// <summary>The title of a credit note.</summary>
    public string CreditNote { get; set; } = "Credit note";

    /// <summary>The title of a corrected invoice.</summary>
    public string CorrectedInvoice { get; set; } = "Corrected invoice";

    /// <summary>The title of a prepayment invoice.</summary>
    public string PrepaymentInvoice { get; set; } = "Prepayment invoice";

    /// <summary>The title of a self-billed invoice.</summary>
    public string SelfBilledInvoice { get; set; } = "Self-billed invoice";

    /// <summary>The label of the invoice number.</summary>
    public string Number { get; set; } = "Invoice no.";

    /// <summary>The label of the issue date.</summary>
    public string IssueDate { get; set; } = "Date";

    /// <summary>The label of the due date.</summary>
    public string DueDate { get; set; } = "Due date";

    /// <summary>The label of the delivery date.</summary>
    public string DeliveryDate { get; set; } = "Delivery date";

    /// <summary>The label of the invoicing period.</summary>
    public string Period { get; set; } = "Period";

    /// <summary>The heading over the buyer.</summary>
    public string BillTo { get; set; } = "Bill to";

    /// <summary>The heading over the delivery address.</summary>
    public string DeliverTo { get; set; } = "Deliver to";

    /// <summary>The label of the buyer's customer number.</summary>
    public string CustomerNumber { get; set; } = "Customer no.";

    /// <summary>The label of the buyer reference.</summary>
    public string BuyerReference { get; set; } = "Your reference";

    /// <summary>The label of the purchase order number.</summary>
    public string OrderReference { get; set; } = "Order no.";

    /// <summary>The label of the contract number.</summary>
    public string ContractReference { get; set; } = "Contract no.";

    /// <summary>The label of the invoice a credit note or correction refers to.</summary>
    public string PrecedingInvoice { get; set; } = "Refers to invoice";

    /// <summary>The heading of the description column.</summary>
    public string Description { get; set; } = "Description";

    /// <summary>The heading of the quantity column.</summary>
    public string Quantity { get; set; } = "Quantity";

    /// <summary>The heading of the unit price column.</summary>
    public string UnitPrice { get; set; } = "Unit price";

    /// <summary>The heading of the VAT column, and the word in the VAT lines of the totals.</summary>
    public string Vat { get; set; } = "VAT";

    /// <summary>The heading of the amount column.</summary>
    public string Amount { get; set; } = "Amount";

    /// <summary>The label before a line's item number.</summary>
    public string ItemNumber { get; set; } = "Item no.";

    /// <summary>The text of a discount without a reason.</summary>
    public string Discount { get; set; } = "Discount";

    /// <summary>The text of a charge without a reason.</summary>
    public string Charge { get; set; } = "Charge";

    /// <summary>The label of the sum of the lines.</summary>
    public string Subtotal { get; set; } = "Subtotal";

    /// <summary>The label of the total without VAT.</summary>
    public string NetTotal { get; set; } = "Total excl. VAT";

    /// <summary>The word between a rate and its base, as in "VAT 19 % of 100.00", and between page numbers.</summary>
    public string Of { get; set; } = "of";

    /// <summary>The label of the total with VAT.</summary>
    public string Total { get; set; } = "Total";

    /// <summary>The label of the prepaid amount.</summary>
    public string Prepaid { get; set; } = "Already paid";

    /// <summary>The label of the amount due.</summary>
    public string AmountDue { get; set; } = "Amount due";

    /// <summary>The heading over the payment details.</summary>
    public string Payment { get; set; } = "Payment";

    /// <summary>The label of a national account number.</summary>
    public string AccountNumber { get; set; } = "Account no.";

    /// <summary>The label of the account holder.</summary>
    public string AccountName { get; set; } = "Account holder";

    /// <summary>The label of the payment reference.</summary>
    public string PaymentReference { get; set; } = "Reference";

    /// <summary>The label of the payment terms.</summary>
    public string PaymentTerms { get; set; } = "Terms";

    /// <summary>The caption under the payment QR code.</summary>
    public string ScanToPay { get; set; } = "Scan to pay";

    /// <summary>The label of a VAT ID.</summary>
    public string VatId { get; set; } = "VAT ID";

    /// <summary>The label of the seller's tax number.</summary>
    public string TaxNumber { get; set; } = "Tax no.";

    /// <summary>The label of the seller's legal registration number.</summary>
    public string RegistrationNumber { get; set; } = "Reg. no.";

    /// <summary>The word before the page number in the footer.</summary>
    public string Page { get; set; } = "Page";

    /// <summary>Unit names by UN/ECE Recommendation 20 code; a code without a name is printed as it is.</summary>
    public Dictionary<string, string> Units { get; set; } = new(StringComparer.Ordinal)
    {
        ["C62"] = "pcs", ["H87"] = "pcs", ["HUR"] = "h", ["DAY"] = "days", ["MON"] = "months", ["KGM"] = "kg", ["MTR"] = "m", ["LTR"] = "l", ["MIN"] = "min",
    };

    internal string Unit(string code) => Units is not null && Units.TryGetValue(code ?? "", out var name) ? name : code ?? "";
}
