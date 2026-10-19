namespace PdfPecker.Invoices;

/// <summary>Calculates an invoice's totals and VAT breakdown the way EN 16931's business rules (BR-CO) define them.</summary>
internal static class InvoiceCalculator
{
    /// <summary>Rounds money to two decimals, half away from zero, which is the commercial rounding EN 16931 expects.</summary>
    internal static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Whether a category carries a VAT rate. The others are written with a rate of 0, or none for O.</summary>
    internal static bool HasRate(VatCategory category) => category is VatCategory.Standard or VatCategory.CanaryIslands or VatCategory.CeutaMelilla;

    internal static decimal RateOf(VatCategory category, decimal rate) => HasRate(category) ? rate : 0;

    internal static InvoiceTotals Calculate(InvoiceData invoice)
    {
        // The breakdown keeps the order in which each category and rate first appears, so the XML is the same every time.
        var groups = new List<(VatCategory Category, decimal Rate, decimal Taxable)>();
        void Add(VatCategory category, decimal rate, decimal amount)
        {
            rate = RateOf(category, rate);
            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i].Category != category || groups[i].Rate != rate) continue;
                groups[i] = (category, rate, groups[i].Taxable + amount);
                return;
            }
            groups.Add((category, rate, amount));
        }

        decimal lines = 0, allowances = 0, charges = 0;
        foreach (var line in invoice.Lines)
        {
            var net = line.NetAmount;
            lines += net;
            Add(line.VatCategory, line.VatRate, net);
        }
        foreach (var item in invoice.AllowancesAndCharges)
        {
            if (item.IsCharge) charges += item.Amount;
            else allowances += item.Amount;
            Add(item.VatCategory, item.VatRate, item.IsCharge ? item.Amount : -item.Amount);
        }

        var vat = new List<InvoiceVatTotal>(groups.Count);
        decimal vatTotal = 0;
        foreach (var (category, rate, taxable) in groups)
        {
            var amount = Round(taxable * rate / 100);
            vatTotal += amount;
            var (reason, code) = Exemption(invoice, category);
            vat.Add(new InvoiceVatTotal(category, rate, taxable, amount, reason, code));
        }
        return new InvoiceTotals(lines, allowances, charges, vatTotal, invoice.PrepaidAmount, vat);
    }

    /// <summary>The exemption reason and VATEX code for a category: the caller's text, else the standard EU wording.</summary>
    internal static (string? Reason, string? Code) Exemption(InvoiceData invoice, VatCategory category)
    {
        invoice.VatExemptionReasons.TryGetValue(category, out var given);
        given = string.IsNullOrWhiteSpace(given) ? null : given.Trim();
        return category switch
        {
            VatCategory.Exempt => (given, null),
            VatCategory.ReverseCharge => (given ?? "Reverse charge", "VATEX-EU-AE"),
            VatCategory.IntraCommunitySupply => (given ?? "Intra-Community supply", "VATEX-EU-IC"),
            VatCategory.Export => (given ?? "Export outside the EU", "VATEX-EU-G"),
            VatCategory.OutsideScope => (given ?? "Not subject to VAT", "VATEX-EU-O"),
            _ => (null, null),
        };
    }

    /// <summary>The UNTDID 5305 code of a category.</summary>
    internal static string Code(VatCategory category) => category switch
    {
        VatCategory.Standard => "S",
        VatCategory.ZeroRated => "Z",
        VatCategory.Exempt => "E",
        VatCategory.ReverseCharge => "AE",
        VatCategory.IntraCommunitySupply => "K",
        VatCategory.Export => "G",
        VatCategory.OutsideScope => "O",
        VatCategory.CanaryIslands => "L",
        VatCategory.CeutaMelilla => "M",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown VAT category."),
    };
}
