namespace PdfPecker.Invoices;

/// <summary>
/// Checks an invoice against the EN 16931 business rules that apply to the fields <see cref="InvoiceData"/> has, and
/// against XRechnung's BR-DE rules for that profile. Each problem names the rule, so it can be looked up.
/// </summary>
internal static class InvoiceRules
{
    /// <summary>The rule set an invoice is checked against.</summary>
    internal enum Target
    {
        En16931,
        XRechnung,
        Peppol,
    }

    internal static Target TargetOf(EInvoiceProfile profile) => CiiWriter.CheckProfile(profile) == EInvoiceProfile.XRechnung ? Target.XRechnung : Target.En16931;

    internal static Target TargetOf(UblFormat format) => format switch
    {
        UblFormat.PeppolBis3 => Target.Peppol,
        UblFormat.XRechnung => Target.XRechnung,
        _ => throw new ArgumentException($"Unknown UBL format {(int)format}.", nameof(format)),
    };

    // Large enough for any real invoice and small enough that quantity times price stays far inside decimal's range.
    private const decimal MaxValue = 1_000_000_000_000m;

    /// <summary>
    /// The problems with the invoice. With no target only the amounts are checked, which is what calculating the
    /// totals needs; with one, every rule for that target.
    /// </summary>
    internal static List<string> Check(InvoiceData invoice, Target? target)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var problems = new List<string>();
        CheckAmounts(invoice, problems);
        if (target is { } t) CheckFields(invoice, t, amountsValid: problems.Count == 0, problems);
        return problems;
    }

    private static void CheckAmounts(InvoiceData invoice, List<string> problems)
    {
        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            var line = invoice.Lines[i];
            if (line is null) { problems.Add($"Line {i + 1} is null."); continue; }
            var name = $"Line {i + 1}";
            if (Math.Abs(line.Quantity) > MaxValue) problems.Add($"{name}: the quantity is too large.");
            if (line.NetPrice < 0) problems.Add($"{name}: the net price must not be negative (BR-27).");
            if (line.NetPrice > MaxValue) problems.Add($"{name}: the net price is too large.");
            if (line.PriceBaseQuantity is { } baseQuantity && (baseQuantity <= 0 || baseQuantity > MaxValue))
                problems.Add($"{name}: the price base quantity must be above zero.");
            CheckMoney(line.Discount, $"{name}: the discount", problems);
            CheckRate(line.VatCategory, line.VatRate, name, problems);
        }
        for (var i = 0; i < invoice.AllowancesAndCharges.Count; i++)
        {
            var item = invoice.AllowancesAndCharges[i];
            var name = $"Allowance or charge {i + 1}";
            if (item is null) { problems.Add($"{name} is null."); continue; }
            CheckMoney(item.Amount, $"{name}: the amount", problems);
            CheckRate(item.VatCategory, item.VatRate, name, problems);
        }
        CheckMoney(invoice.PrepaidAmount, "The prepaid amount", problems);
    }

    private static void CheckMoney(decimal amount, string what, List<string> problems)
    {
        if (amount < 0) problems.Add($"{what} must not be negative.");
        else if (amount > MaxValue) problems.Add($"{what} is too large.");
        else if (decimal.Round(amount, 2) != amount) problems.Add($"{what} has more than two decimals (BR-DEC).");
    }

    private static void CheckRate(VatCategory category, decimal rate, string name, List<string> problems)
    {
        if (!Enum.IsDefined(category)) { problems.Add($"{name}: unknown VAT category {(int)category}."); return; }
        if (category == VatCategory.Standard && rate <= 0)
            problems.Add($"{name}: standard rated VAT needs a rate above zero (BR-S-05).");
        else if (InvoiceCalculator.HasRate(category) && (rate < 0 || rate >= 100))
            problems.Add($"{name}: the VAT rate must be from 0 to 100 percent.");
        else if (!InvoiceCalculator.HasRate(category) && rate != 0)
            problems.Add($"{name}: VAT category {InvoiceCalculator.Code(category)} has a rate of 0.");
    }

    private static void CheckFields(InvoiceData invoice, Target target, bool amountsValid, List<string> problems)
    {
        bool xrechnung = target == Target.XRechnung;
        Required(invoice.Number, "The invoice number (BT-1)", "BR-02", problems);
        if (invoice.IssueDate == default) problems.Add("The issue date (BT-2) is required (BR-03).");
        if (!Enum.IsDefined(invoice.Type)) problems.Add($"Unknown invoice type {(int)invoice.Type}.");
        if (invoice.Currency is not { Length: 3 } currency || !currency.All(char.IsAsciiLetterUpper))
            problems.Add($"The currency \"{invoice.Currency}\" is not an ISO 4217 code such as EUR (BR-05).");
        if (invoice.Lines.Count == 0) problems.Add("An invoice needs at least one line (BR-16).");

        CheckParty(invoice.Seller, "seller", target, problems);
        CheckParty(invoice.Buyer, "buyer", target, problems);
        if (invoice.Seller is null || invoice.Buyer is null) return;

        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            if (invoice.Lines[i] is not { } line) continue;
            Required(line.Name, $"Line {i + 1}: the item name (BT-153)", "BR-25", problems);
            Required(line.UnitCode, $"Line {i + 1}: the unit code (BT-130)", "BR-23", problems);
        }
        for (var i = 0; i < invoice.AllowancesAndCharges.Count; i++)
            if (invoice.AllowancesAndCharges[i] is { IsCharge: true } charge && string.IsNullOrWhiteSpace(charge.Reason) && string.IsNullOrWhiteSpace(charge.ReasonCode))
                problems.Add($"Allowance or charge {i + 1}: a charge needs a reason or a reason code (BR-42).");

        if (invoice.PeriodStart is { } start && invoice.PeriodEnd is { } end && end < start)
            problems.Add("The invoicing period ends before it starts (BR-29).");
        if (invoice.PrecedingInvoiceDate is not null && string.IsNullOrWhiteSpace(invoice.PrecedingInvoiceNumber))
            problems.Add("A preceding invoice date needs the preceding invoice's number (BT-25).");
        if (invoice.DeliverToAddress is { } deliverTo && !IsCountry(deliverTo.CountryCode))
            problems.Add($"The delivery country \"{deliverTo.CountryCode}\" is not an ISO 3166-1 code such as DE (BR-57).");

        CheckVat(invoice, problems);
        CheckPayment(invoice, amountsValid, problems);

        if (xrechnung)
        {
            Required(invoice.BuyerReference, "XRechnung: the buyer reference (BT-10), for public buyers the Leitweg-ID,", "BR-DE-15", problems);
            if (invoice.Payment is null) problems.Add("XRechnung: the payment instructions (BG-16) are required (BR-DE-1).");
        }
        if (target == Target.Peppol && string.IsNullOrWhiteSpace(invoice.BuyerReference) && string.IsNullOrWhiteSpace(invoice.PurchaseOrderReference))
            problems.Add("Peppol: the buyer reference (BT-10) or the purchase order reference (BT-13) is required (PEPPOL-EN16931-R003).");
    }

    private static void CheckParty(InvoiceParty? party, string role, Target target, List<string> problems)
    {
        var who = role == "seller" ? "The seller's" : "The buyer's";
        if (party is null) { problems.Add($"The {role} is required."); return; }
        Required(party.Name, $"{who} name ({(role == "seller" ? "BT-27" : "BT-44")})", role == "seller" ? "BR-06" : "BR-07", problems);
        var address = party.Address;
        if (address is null || !IsCountry(address.CountryCode))
            problems.Add($"{who} country \"{address?.CountryCode}\" is not an ISO 3166-1 code such as DE ({(role == "seller" ? "BR-09" : "BR-11")}).");
        if (party.ElectronicAddress is { } electronic && !string.IsNullOrWhiteSpace(electronic) && SchemeOf(party) is null)
            problems.Add($"{who} electronic address needs a scheme, such as 0088 for a GLN ({(role == "seller" ? "BR-62" : "BR-63")}).");
        if (role == "buyer" && !string.IsNullOrWhiteSpace(party.TaxNumber))
            problems.Add("The buyer has no tax number field in EN 16931; use its VAT ID or legal registration ID.");
        if (party.Contact is { Email: { } email } && !string.IsNullOrWhiteSpace(email) && !email.Contains('@', StringComparison.Ordinal))
            problems.Add($"{who} contact email \"{email}\" is not an email address.");

        if (target == Target.Peppol && SchemeOf(party) == "EM")
            problems.Add($"Peppol: {who.ToLowerInvariant()} electronic address must be a Peppol participant ID, not an email address (PEPPOL-EN16931-CL008).");
        if (target == Target.Peppol)
            Required(party.ElectronicAddress, $"Peppol: {who.ToLowerInvariant()} electronic address ({(role == "seller" ? "BT-34" : "BT-49")})",
                role == "seller" ? "PEPPOL-EN16931-R020" : "PEPPOL-EN16931-R010", problems);
        if (target != Target.XRechnung) return;
        // XRechnung 3.0 requires both parties' addresses and electronic addresses, and the seller's full contact.
        var de = role == "seller" ? ("BR-DE-3", "BR-DE-4") : ("BR-DE-8", "BR-DE-9");
        Required(address?.City, $"XRechnung: {who.ToLowerInvariant()} city", de.Item1, problems);
        Required(address?.PostalCode, $"XRechnung: {who.ToLowerInvariant()} postal code", de.Item2, problems);
        Required(party.ElectronicAddress, $"XRechnung: {who.ToLowerInvariant()} electronic address ({(role == "seller" ? "BT-34" : "BT-49")})",
            role == "seller" ? "PEPPOL-EN16931-R020" : "PEPPOL-EN16931-R010", problems);
        if (role != "seller") return;
        if (party.Contact is not { } contact) { problems.Add("XRechnung: the seller's contact (BG-6) is required (BR-DE-2)."); return; }
        Required(contact.Name, "XRechnung: the seller's contact name (BT-41)", "BR-DE-5", problems);
        Required(contact.Phone, "XRechnung: the seller's contact phone (BT-42)", "BR-DE-6", problems);
        Required(contact.Email, "XRechnung: the seller's contact email (BT-43)", "BR-DE-7", problems);
    }

    private static void CheckVat(InvoiceData invoice, List<string> problems)
    {
        var categories = new HashSet<VatCategory>();
        foreach (var line in invoice.Lines) if (line is not null) categories.Add(line.VatCategory);
        foreach (var item in invoice.AllowancesAndCharges) if (item is not null) categories.Add(item.VatCategory);

        var seller = invoice.Seller;
        var buyer = invoice.Buyer;
        bool sellerVat = !string.IsNullOrWhiteSpace(seller.VatId), sellerTax = !string.IsNullOrWhiteSpace(seller.TaxNumber);
        bool buyerVat = !string.IsNullOrWhiteSpace(buyer.VatId);
        foreach (var category in categories)
        {
            var code = InvoiceCalculator.Code(category);
            switch (category)
            {
                case VatCategory.Standard or VatCategory.ZeroRated or VatCategory.Exempt or VatCategory.CanaryIslands or VatCategory.CeutaMelilla:
                    if (!sellerVat && !sellerTax)
                        problems.Add($"VAT category {code} needs the seller's VAT ID or tax number (BR-{RuleLetter(category)}-02).");
                    break;
                case VatCategory.ReverseCharge:
                    if (!sellerVat && !sellerTax) problems.Add("Reverse charge needs the seller's VAT ID (BR-AE-02).");
                    if (!buyerVat && string.IsNullOrWhiteSpace(buyer.LegalRegistrationId)) problems.Add("Reverse charge needs the buyer's VAT ID (BR-AE-02).");
                    break;
                case VatCategory.IntraCommunitySupply:
                    if (!sellerVat) problems.Add("An intra-community supply needs the seller's VAT ID (BR-IC-02).");
                    if (!buyerVat) problems.Add("An intra-community supply needs the buyer's VAT ID (BR-IC-02).");
                    if (invoice.DeliveryDate is null && invoice.PeriodStart is null && invoice.PeriodEnd is null)
                        problems.Add("An intra-community supply needs the delivery date or the invoicing period (BR-IC-11).");
                    if (invoice.DeliverToAddress is null) problems.Add("An intra-community supply needs the delivery address's country (BR-IC-12).");
                    break;
                case VatCategory.Export:
                    if (!sellerVat) problems.Add("An export needs the seller's VAT ID (BR-G-02).");
                    break;
                case VatCategory.OutsideScope:
                    if (categories.Count > 1) problems.Add("An invoice with VAT category O (not subject to VAT) has no other category (BR-O-11).");
                    if (sellerVat || buyerVat) problems.Add("An invoice not subject to VAT has no VAT IDs (BR-O-02).");
                    break;
            }
        }
        if (categories.Contains(VatCategory.Exempt) && InvoiceCalculator.Exemption(invoice, VatCategory.Exempt).Reason is null)
            problems.Add("Exempt VAT needs an exemption reason in VatExemptionReasons, such as the law that exempts it (BR-E-10).");
        foreach (var category in invoice.VatExemptionReasons.Keys)
            if (category is VatCategory.Standard or VatCategory.ZeroRated or VatCategory.CanaryIslands or VatCategory.CeutaMelilla)
                problems.Add($"VAT category {InvoiceCalculator.Code(category)} has no exemption reason (BR-{RuleLetter(category)}-10).");
    }

    private static void CheckPayment(InvoiceData invoice, bool amountsValid, List<string> problems)
    {
        if (invoice.Payment is { } payment)
        {
            if (payment.MeansCode is < 1 or > 999) problems.Add($"The payment means code {payment.MeansCode} is not a UNTDID 4461 code (BR-49).");
            bool iban = !string.IsNullOrWhiteSpace(payment.Iban), account = !string.IsNullOrWhiteSpace(payment.AccountNumber);
            if (iban && !EpcPayment.IsValidIban(NormalizeIban(payment.Iban!)))
                problems.Add($"\"{payment.Iban}\" is not a valid IBAN.");
            if (iban && account) problems.Add("Give the IBAN or the account number, not both.");
            if (payment.MeansCode is 30 or 58 && !iban && !account)
                problems.Add("A credit transfer needs the IBAN or account number to pay to (BR-61).");
            if (payment.MeansCode == 58 && !iban && account)
                problems.Add("A SEPA credit transfer (58) needs an IBAN; use means code 30 for another account number.");
        }
        // Only an invoice with something to pay needs to say when or on what terms (BR-CO-25). Amounts the amount checks
        // rejected cannot be totalled, so the check waits until they are fixed.
        if (invoice.DueDate is null && string.IsNullOrWhiteSpace(invoice.PaymentTerms) && amountsValid
            && InvoiceCalculator.Calculate(invoice).AmountDue > 0)
            problems.Add("An invoice with an amount due needs a due date or payment terms (BR-CO-25).");
    }

    internal static string NormalizeIban(string iban) => iban.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>The electronic address scheme, EM for an email address when none is given, or null.</summary>
    internal static string? SchemeOf(InvoiceParty party)
        => !string.IsNullOrWhiteSpace(party.ElectronicAddressScheme) ? party.ElectronicAddressScheme.Trim()
            : party.ElectronicAddress?.Contains('@', StringComparison.Ordinal) == true ? "EM" : null;

    private static bool IsCountry(string? code) => code is { Length: 2 } && char.IsAsciiLetterUpper(code[0]) && char.IsAsciiLetterUpper(code[1]);

    private static string RuleLetter(VatCategory category) => category switch
    {
        VatCategory.CanaryIslands => "AF",
        VatCategory.CeutaMelilla => "AG",
        _ => InvoiceCalculator.Code(category),
    };

    private static void Required(string? value, string what, string rule, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value)) problems.Add($"{what} is required ({rule}).");
    }
}
