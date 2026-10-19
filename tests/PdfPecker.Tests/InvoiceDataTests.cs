using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PdfPecker.Tests;

/// <summary>
/// Invoice XML from <see cref="InvoiceData"/>. Each sample's CII is compared with a baseline under
/// Fixtures/einvoice/generated/, which was checked with the Mustang validator (EN 16931 and XRechnung schematron and the
/// CII schema). Regenerate the baselines with <c>PDFPECKER_UPDATE_GOLDEN=1</c>, then validate them again.
/// </summary>
public class InvoiceDataTests
{
    private static string BaselineDirectory([CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "einvoice", "generated");

    internal static InvoiceData Sample()
    {
        var invoice = new InvoiceData
        {
            Number = "INV-2026-0042",
            IssueDate = new DateOnly(2026, 10, 13),
            DueDate = new DateOnly(2026, 11, 12),
            PaymentTerms = "Net 30 days",
            BuyerReference = "04011000-12345-34",
            PurchaseOrderReference = "PO-7781",
            DeliveryDate = new DateOnly(2026, 10, 10),
            Seller = new InvoiceParty
            {
                Name = "Example Seller GmbH",
                LegalRegistrationId = "HRB 12345",
                VatId = "DE123456789",
                TaxNumber = "201/113/40209",
                Address = new InvoiceAddress { Line1 = "Musterstraße 1", City = "Berlin", PostalCode = "10115", CountryCode = "DE" },
                Contact = new InvoiceContact { Name = "Anna Schmidt", Phone = "+49 30 1234567", Email = "billing@seller.example" },
                ElectronicAddress = "DE123456789",
                ElectronicAddressScheme = "9930",
            },
            Buyer = new InvoiceParty
            {
                Name = "Example Buyer AG",
                Id = "CUST-0815",
                VatId = "DE987654321",
                Address = new InvoiceAddress { Line1 = "Beispielweg 2", City = "München", PostalCode = "80331", CountryCode = "DE" },
                ElectronicAddress = "04011000-12345-34",
                ElectronicAddressScheme = "0204",
            },
            Payment = new InvoicePayment
            {
                Iban = "DE02 1203 0000 0000 2020 51",
                Bic = "BYLADEM1001",
                AccountName = "Example Seller GmbH",
                Reference = "INV-2026-0042",
            },
        };
        invoice.Notes.Add("Thank you for your order.");
        invoice.Lines.Add(new InvoiceLine { Name = "Consulting", Description = "On-site workshop", Quantity = 10, UnitCode = "HUR", NetPrice = 100, VatRate = 19 });
        invoice.Lines.Add(new InvoiceLine { Name = "Printed manual", SellerItemId = "MAN-01", Quantity = 3, NetPrice = 12.495m, VatRate = 7, Discount = 2.50m, DiscountReason = "Loyalty" });
        invoice.Lines.Add(new InvoiceLine { Name = "Paper", Quantity = 2500, NetPrice = 4.20m, PriceBaseQuantity = 1000, VatRate = 19 });
        invoice.AllowancesAndCharges.Add(new InvoiceAllowanceCharge { Amount = 50, Reason = "Early customer", VatRate = 19 });
        invoice.AllowancesAndCharges.Add(new InvoiceAllowanceCharge { IsCharge = true, Amount = 9.90m, Reason = "Shipping", VatRate = 19 });
        return invoice;
    }

    private static InvoiceData ReverseCharge()
    {
        var invoice = Sample();
        invoice.Number = "INV-2026-0043";
        invoice.Buyer.VatId = "DK12345678";
        invoice.Buyer.ElectronicAddress = "12345678";
        invoice.Buyer.ElectronicAddressScheme = "0184";
        invoice.Buyer.Address = new InvoiceAddress { Line1 = "Eksempelvej 3", City = "København", PostalCode = "1050", CountryCode = "DK" };
        invoice.AllowancesAndCharges.Clear();
        invoice.Lines.Clear();
        invoice.Lines.Add(new InvoiceLine { Name = "Software development", Quantity = 40, UnitCode = "HUR", NetPrice = 95, VatCategory = VatCategory.ReverseCharge });
        return invoice;
    }

    private static InvoiceData CreditNote()
    {
        var invoice = Sample();
        invoice.Number = "CN-2026-0007";
        invoice.Type = InvoiceType.CreditNote;
        invoice.PrecedingInvoiceNumber = "INV-2026-0042";
        invoice.PrecedingInvoiceDate = new DateOnly(2026, 10, 13);
        invoice.DueDate = null;
        invoice.PaymentTerms = "Credited to your account";
        invoice.AllowancesAndCharges.Clear();
        invoice.Lines.Clear();
        invoice.Lines.Add(new InvoiceLine { Name = "Consulting (returned hours)", Quantity = 2, UnitCode = "HUR", NetPrice = 100, VatRate = 19 });
        return invoice;
    }

    private static InvoiceData Mixed()
    {
        var invoice = Sample();
        invoice.Number = "INV-2026-0044";
        invoice.Currency = "DKK";
        invoice.PrepaidAmount = 100;
        invoice.PeriodStart = new DateOnly(2026, 9, 1);
        invoice.PeriodEnd = new DateOnly(2026, 9, 30);
        invoice.Lines.Add(new InvoiceLine { Name = "Medical consultation", Quantity = 1, NetPrice = 80, VatCategory = VatCategory.Exempt });
        invoice.Lines.Add(new InvoiceLine { Name = "Newspaper", Quantity = 1, NetPrice = 5, VatCategory = VatCategory.ZeroRated });
        invoice.VatExemptionReasons[VatCategory.Exempt] = "Exempt under § 4 Nr. 14 UStG";
        invoice.Payment = new InvoicePayment { MeansCode = 30, AccountNumber = "1234 5678901234", Reference = "INV-2026-0044" };
        return invoice;
    }

    private static InvoiceData Intracommunity()
    {
        var invoice = ReverseCharge();
        invoice.Number = "INV-2026-0045";
        invoice.Lines.Clear();
        invoice.Lines.Add(new InvoiceLine { Name = "Pallet of widgets", Quantity = 4, NetPrice = 250, VatCategory = VatCategory.IntraCommunitySupply });
        invoice.DeliverToName = "Example Buyer Warehouse";
        invoice.DeliverToAddress = new InvoiceAddress { City = "Aarhus", PostalCode = "8000", CountryCode = "DK" };
        return invoice;
    }

    private static InvoiceData OutsideScope()
    {
        var invoice = Sample();
        invoice.Number = "INV-2026-0046";
        invoice.Seller.VatId = null;
        invoice.Seller.TaxNumber = null;
        invoice.Buyer.VatId = null;
        invoice.AllowancesAndCharges.Clear();
        invoice.Lines.Clear();
        invoice.Lines.Add(new InvoiceLine { Name = "Membership fee", Quantity = 1, NetPrice = 120, VatCategory = VatCategory.OutsideScope });
        return invoice;
    }

    public static TheoryData<string> Samples() =>
    [
        "en16931", "xrechnung", "reverse-charge", "credit-note", "mixed", "intracommunity", "outside-scope",
        "peppol", "xrechnung-ubl", "peppol-reverse-charge", "peppol-credit-note", "peppol-mixed", "peppol-intracommunity", "peppol-outside-scope",
    ];

    private static byte[] Generate(string sample) => sample switch
    {
        "en16931" => Sample().ToCii(),
        "xrechnung" => Sample().ToCii(EInvoiceProfile.XRechnung),
        "reverse-charge" => ReverseCharge().ToCii(),
        "credit-note" => CreditNote().ToCii(),
        "mixed" => Mixed().ToCii(),
        "intracommunity" => Intracommunity().ToCii(),
        "outside-scope" => OutsideScope().ToCii(),
        "peppol" => Sample().ToUbl(),
        "xrechnung-ubl" => Sample().ToUbl(UblFormat.XRechnung),
        "peppol-reverse-charge" => ReverseCharge().ToUbl(),
        "peppol-credit-note" => CreditNote().ToUbl(),
        "peppol-mixed" => Mixed().ToUbl(),
        "peppol-intracommunity" => Intracommunity().ToUbl(),
        "peppol-outside-scope" => OutsideScope().ToUbl(),
        _ => throw new ArgumentOutOfRangeException(nameof(sample)),
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Cii_matches_the_validated_baseline(string sample)
    {
        var actual = Generate(sample);
        var path = Path.Combine(BaselineDirectory(), sample + ".xml");
        if (Environment.GetEnvironmentVariable("PDFPECKER_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(BaselineDirectory());
            File.WriteAllBytes(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"No baseline for {sample}. Create it with PDFPECKER_UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path), Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void Totals_follow_the_en16931_rules()
    {
        var totals = Sample().CalculateTotals();
        // 10 × 100 = 1000; 3 × 12.495 = 37.485 → 37.49, minus 2.50 = 34.99; 2500 × 4.20 / 1000 = 10.50.
        Assert.Equal(1045.49m, totals.LineTotal);
        Assert.Equal(50m, totals.AllowanceTotal);
        Assert.Equal(9.90m, totals.ChargeTotal);
        Assert.Equal(1005.39m, totals.NetTotal);
        Assert.Collection(totals.Vat,
            v => { Assert.Equal(19m, v.Rate); Assert.Equal(970.40m, v.TaxableAmount); Assert.Equal(184.38m, v.VatAmount); },
            v => { Assert.Equal(7m, v.Rate); Assert.Equal(34.99m, v.TaxableAmount); Assert.Equal(2.45m, v.VatAmount); });
        Assert.Equal(186.83m, totals.VatTotal);
        Assert.Equal(1192.22m, totals.GrandTotal);
        Assert.Equal(1192.22m, totals.AmountDue);
    }

    [Fact]
    public void Prepaid_amount_reduces_the_amount_due()
    {
        var totals = Mixed().CalculateTotals();
        Assert.Equal(totals.GrandTotal - 100, totals.AmountDue);
        Assert.Equal(["S", "S", "E", "Z"], totals.Vat.Select(v => v.Category switch
        {
            VatCategory.Standard => "S",
            VatCategory.Exempt => "E",
            VatCategory.ZeroRated => "Z",
            _ => "?",
        }));
    }

    [Fact]
    public void Output_does_not_depend_on_the_culture()
    {
        var expected = Sample().ToCii();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "ar-SA", "fa-IR", "de-DE", "da-DK" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(expected, Sample().ToCii());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Embeds_as_a_factur_x_pdf_and_reads_back()
    {
        var invoice = Sample();
        var pdf = HtmlToPdf.Convert("<h1>Invoice INV-2026-0042</h1>", new PdfOptions { EInvoice = invoice.ToEInvoice() });
        var read = PdfEInvoice.Read(pdf);
        Assert.NotNull(read);
        Assert.Equal(EInvoiceProfile.En16931, read.Profile);
        Assert.Equal(invoice.ToCii(), read.Xml);

        var xrechnung = PdfEInvoice.Read(HtmlToPdf.Convert("<p>x</p>", new PdfOptions { EInvoice = invoice.ToEInvoice(EInvoiceProfile.XRechnung) }));
        Assert.Equal(EInvoiceProfile.XRechnung, xrechnung!.Profile);
        Assert.Equal("xrechnung.xml", xrechnung.FileName);
    }

    [Fact]
    public void Ubl_takes_the_rendered_pdf_as_an_attachment()
    {
        var invoice = Sample();
        var pdf = HtmlToPdf.Convert("<h1>Invoice INV-2026-0042</h1>");
        var xml = Encoding.UTF8.GetString(UblInvoice.AttachPdf(invoice.ToUbl(), pdf, "INV-2026-0042.pdf"));
        Assert.Contains("<cbc:EmbeddedDocumentBinaryObject mimeCode=\"application/pdf\" filename=\"INV-2026-0042.pdf\">", xml);
        // The attachment goes after the contract and order references and before the parties, where UBL expects it.
        Assert.True(xml.IndexOf("<cac:OrderReference>", StringComparison.Ordinal) < xml.IndexOf("<cac:AdditionalDocumentReference>", StringComparison.Ordinal));
        Assert.True(xml.IndexOf("<cac:AdditionalDocumentReference>", StringComparison.Ordinal) < xml.IndexOf("<cac:AccountingSupplierParty>", StringComparison.Ordinal));
    }

    [Fact]
    public void Peppol_needs_electronic_addresses_and_a_reference()
    {
        var invoice = Sample();
        invoice.Buyer.ElectronicAddress = null;
        invoice.BuyerReference = null;
        invoice.PurchaseOrderReference = null;
        Assert.Empty(invoice.Validate());
        var problems = invoice.Validate(UblFormat.PeppolBis3);
        Assert.Contains(problems, p => p.Contains("PEPPOL-EN16931-R010", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("PEPPOL-EN16931-R003", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => invoice.ToUbl());
    }

    [Fact]
    public void An_empty_invoice_lists_every_missing_field()
    {
        var problems = new InvoiceData().Validate();
        Assert.Contains(problems, p => p.Contains("BR-02", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("BR-03", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("BR-16", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("BR-06", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("BR-09", StringComparison.Ordinal));
        var ex = Assert.Throws<ArgumentException>(() => new InvoiceData().ToCii());
        Assert.Contains("BR-02", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XRechnung_needs_more_than_en16931()
    {
        var invoice = Sample();
        invoice.BuyerReference = null;
        invoice.Seller.Contact = null;
        invoice.Buyer.ElectronicAddress = null;
        Assert.Empty(invoice.Validate());
        var problems = invoice.Validate(EInvoiceProfile.XRechnung);
        Assert.Contains(problems, p => p.Contains("BR-DE-15", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("BR-DE-2", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("PEPPOL-EN16931-R010", StringComparison.Ordinal));
    }

    [Fact]
    public void Vat_category_rules_are_checked()
    {
        var invoice = ReverseCharge();
        invoice.Buyer.VatId = null;
        Assert.Contains(invoice.Validate(), p => p.Contains("BR-AE-02", StringComparison.Ordinal));

        invoice = Sample();
        invoice.Lines[0].VatRate = 0;
        Assert.Contains(invoice.Validate(), p => p.Contains("BR-S-05", StringComparison.Ordinal));

        invoice = Mixed();
        invoice.VatExemptionReasons.Clear();
        Assert.Contains(invoice.Validate(), p => p.Contains("BR-E-10", StringComparison.Ordinal));

        invoice = OutsideScope();
        invoice.Lines.Add(new InvoiceLine { Name = "Taxed", NetPrice = 1, VatRate = 25 });
        Assert.Contains(invoice.Validate(), p => p.Contains("BR-O-11", StringComparison.Ordinal));

        invoice = Intracommunity();
        invoice.DeliverToAddress = null;
        Assert.Contains(invoice.Validate(), p => p.Contains("BR-IC-12", StringComparison.Ordinal));
    }

    [Fact]
    public void Amount_and_payment_mistakes_are_reported()
    {
        var invoice = Sample();
        invoice.Lines[0].NetPrice = -1;
        invoice.Lines[1].Discount = 0.005m;
        invoice.Payment!.Iban = "DE02 1203 0000 0000 2020 52";
        invoice.DueDate = null;
        invoice.PaymentTerms = null;
        var problems = invoice.Validate();
        Assert.Contains(problems, p => p.Contains("BR-27", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("two decimals", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("not a valid IBAN", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => invoice.CalculateTotals());

        invoice = Sample();
        invoice.DueDate = null;
        invoice.PaymentTerms = null;
        Assert.Contains(invoice.Validate(), p => p.Contains("BR-CO-25", StringComparison.Ordinal));
    }

    [Fact]
    public void Huge_and_null_values_are_reported_not_thrown()
    {
        var invoice = Sample();
        invoice.Lines.Add(null!);
        invoice.Lines.Add(new InvoiceLine { Name = "Huge", Quantity = decimal.MaxValue, NetPrice = decimal.MaxValue, VatRate = 19 });
        invoice.Seller = null!;
        var problems = invoice.Validate();
        Assert.Contains(problems, p => p.Contains("is null", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("too large", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("The seller is required", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => invoice.ToCii());
    }

    [Theory]
    [InlineData(EInvoiceProfile.Minimum)]
    [InlineData(EInvoiceProfile.Basic)]
    [InlineData(EInvoiceProfile.Extended)]
    public void Other_profiles_are_refused(EInvoiceProfile profile)
        => Assert.Throws<ArgumentException>(() => Sample().ToCii(profile));
}
