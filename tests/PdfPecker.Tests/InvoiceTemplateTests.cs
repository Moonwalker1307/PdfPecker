using System.Text;

namespace PdfPecker.Tests;

public class InvoiceTemplateTests
{
    private const string Logo = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"40\"><rect width=\"120\" height=\"40\" fill=\"#1f4e79\"/></svg>";

    [Fact]
    public void Pdf_shows_the_invoice_and_embeds_the_factur_x_xml()
    {
        var invoice = InvoiceDataTests.Sample();
        var pdf = InvoiceTemplate.ToPdf(invoice, new InvoiceTemplateOptions { Logo = Encoding.UTF8.GetBytes(Logo) });
        PdfTestHelpers.AssertStructurallyValid(pdf);

        var text = PdfText.Extract(pdf);
        Assert.Contains("INVOICE", text, StringComparison.Ordinal);
        Assert.Contains("INV-2026-0042", text, StringComparison.Ordinal);
        Assert.Contains("1,192.22", text, StringComparison.Ordinal);
        Assert.Contains("DE02 1203 0000 0000 2020 51", text, StringComparison.Ordinal);

        var embedded = PdfEInvoice.Read(pdf);
        Assert.NotNull(embedded);
        Assert.Equal(invoice.ToCii(), embedded.Xml);
    }

    [Fact]
    public void Html_shows_every_total_from_the_same_calculation_as_the_xml()
    {
        var invoice = InvoiceDataTests.Sample();
        var totals = invoice.CalculateTotals();
        var html = InvoiceTemplate.ToHtml(invoice);
        Assert.Contains("1,045.49", html, StringComparison.Ordinal);
        Assert.Contains("1,005.39", html, StringComparison.Ordinal);
        Assert.Contains("VAT 19 % of 970.40", html, StringComparison.Ordinal);
        Assert.Contains("184.38", html, StringComparison.Ordinal);
        Assert.Contains("Total EUR", html, StringComparison.Ordinal);
        Assert.Equal(1192.22m, totals.GrandTotal);
        // Lines with different rates get a VAT column; the EUR invoice to an IBAN gets a payment QR code.
        Assert.Contains("<th class=\"num\">VAT</th>", html, StringComparison.Ordinal);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_format_numbers_and_dates()
    {
        var html = InvoiceTemplate.ToHtml(InvoiceDataTests.Sample(), new InvoiceTemplateOptions { Labels = InvoiceLabels.German });
        Assert.Contains("<html lang=\"de\">", html, StringComparison.Ordinal);
        Assert.Contains("Rechnung", html, StringComparison.Ordinal);
        Assert.Contains("1.192,22", html, StringComparison.Ordinal);
        Assert.Contains("13.10.2026", html, StringComparison.Ordinal);
        Assert.Contains("10 Std.", html, StringComparison.Ordinal);

        html = InvoiceTemplate.ToHtml(InvoiceDataTests.Sample(), new InvoiceTemplateOptions { Labels = InvoiceLabels.Danish });
        Assert.Contains("Faktura", html, StringComparison.Ordinal);
        Assert.Contains("I alt ekskl. moms", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_and_options_cannot_inject_markup()
    {
        var invoice = InvoiceDataTests.Sample();
        invoice.Lines[0].Name = "<script>alert(1)</script>";
        invoice.Buyer.Name = "A & B \"Ltd\"";
        var html = InvoiceTemplate.ToHtml(invoice, new InvoiceTemplateOptions
        {
            AccentColor = "red; } body { display: none",
            Css = ".x { color: red } </style><script>x</script>",
            LogoUri = "\" onerror=\"x",
        });
        // The extra CSS cannot close the style element, and text is encoded.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "</style>"));
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("A &amp; B &quot;Ltd&quot;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("display: none", html, StringComparison.Ordinal);
        Assert.Contains("src=\"&quot; onerror=&quot;x\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_pdf_without_e_invoice_needs_only_valid_amounts()
    {
        var invoice = new InvoiceData { Number = "1", Seller = { Name = "Me" }, Buyer = { Name = "You" } };
        invoice.Lines.Add(new InvoiceLine { Name = "Thing", NetPrice = 10, VatRate = 25 });
        var pdf = InvoiceTemplate.ToPdf(invoice, new InvoiceTemplateOptions { EInvoice = null });
        Assert.Null(PdfEInvoice.Read(pdf));
        Assert.Throws<ArgumentException>(() => InvoiceTemplate.ToPdf(invoice));
    }

    [Fact]
    public void A_logo_that_is_not_an_image_is_refused()
        => Assert.Throws<ArgumentException>(() => InvoiceTemplate.ToHtml(InvoiceDataTests.Sample(), new InvoiceTemplateOptions { Logo = [1, 2, 3] }));

    [Fact]
    public void Credit_notes_have_their_own_title_and_no_qr_code()
    {
        var invoice = InvoiceDataTests.Sample();
        invoice.Type = InvoiceType.CreditNote;
        var html = InvoiceTemplate.ToHtml(invoice);
        Assert.Contains("<h1>Credit note</h1>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Scan to pay", html, StringComparison.Ordinal);
    }
}
