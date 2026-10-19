using PdfPecker.Invoices;

namespace PdfPecker;

/// <summary>The kind of invoice (BT-3, UNTDID 1001 code).</summary>
public enum InvoiceType
{
    /// <summary>380: a commercial invoice. The usual choice.</summary>
    Commercial = 380,
    /// <summary>381: a credit note. Its amounts are positive; the type says they are credited.</summary>
    CreditNote = 381,
    /// <summary>384: a corrected invoice that replaces an earlier one (name it in <see cref="InvoiceData.PrecedingInvoiceNumber"/>).</summary>
    Corrected = 384,
    /// <summary>386: a prepayment invoice.</summary>
    Prepayment = 386,
    /// <summary>389: a self-billed invoice, issued by the buyer.</summary>
    SelfBilled = 389,
}

/// <summary>The VAT category of a line, allowance or charge (BT-151, UNTDID 5305 code).</summary>
public enum VatCategory
{
    /// <summary>S: standard rated. Needs a rate above zero.</summary>
    Standard,
    /// <summary>Z: zero rated goods.</summary>
    ZeroRated,
    /// <summary>E: exempt from VAT. Needs an exemption reason in <see cref="InvoiceData.VatExemptionReasons"/>.</summary>
    Exempt,
    /// <summary>AE: VAT reverse charge. Needs the seller's and the buyer's VAT IDs.</summary>
    ReverseCharge,
    /// <summary>K: intra-community supply within the EEA. Needs both VAT IDs, a delivery date or period, and the delivery country.</summary>
    IntraCommunitySupply,
    /// <summary>G: export outside the EU. Needs the seller's VAT ID.</summary>
    Export,
    /// <summary>O: not subject to VAT. The invoice then has no other category and no VAT IDs.</summary>
    OutsideScope,
    /// <summary>L: Canary Islands general indirect tax (IGIC).</summary>
    CanaryIslands,
    /// <summary>M: tax for production, services and importation in Ceuta and Melilla (IPSI).</summary>
    CeutaMelilla,
}

/// <summary>The UBL e-invoice formats <see cref="InvoiceData.ToUbl"/> writes.</summary>
public enum UblFormat
{
    /// <summary>Peppol BIS Billing 3.0, used across the Peppol network (Denmark's NemHandel, Norway, Belgium, the Netherlands and others).</summary>
    PeppolBis3,
    /// <summary>XRechnung 3.0 in its UBL syntax, for German public buyers.</summary>
    XRechnung,
}

/// <summary>
/// An invoice as data, following the European e-invoicing standard EN 16931. PdfPecker calculates the totals and the
/// VAT breakdown from the lines, and writes the invoice as Factur-X / ZUGFeRD or XRechnung CII with <see cref="ToCii"/>,
/// as Peppol BIS 3.0 or XRechnung UBL with <see cref="ToUbl"/>, or as an <see cref="PdfEInvoice"/> to embed in the PDF with
/// <see cref="ToEInvoice"/>.
/// </summary>
/// <remarks>
/// The model covers the fields most invoices use: parties with addresses, contacts, VAT IDs and electronic addresses,
/// lines with quantities, prices, line discounts and VAT, document allowances and charges, payment by credit transfer,
/// payment terms, delivery, prepayment and references. Amounts are in <see cref="Currency"/>; money is rounded to two
/// decimals, half away from zero, as EN 16931 expects.
/// </remarks>
public sealed class InvoiceData
{
    /// <summary>The invoice number (BT-1). Required.</summary>
    public string Number { get; set; } = "";

    /// <summary>The issue date (BT-2). Required.</summary>
    public DateOnly IssueDate { get; set; }

    /// <summary>The kind of invoice (BT-3). Default: <see cref="InvoiceType.Commercial"/>.</summary>
    public InvoiceType Type { get; set; } = InvoiceType.Commercial;

    /// <summary>The ISO 4217 currency code of every amount (BT-5), such as EUR or DKK. Default: EUR.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>The payment due date (BT-9), or null. An invoice with an amount due needs this or <see cref="PaymentTerms"/>.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>The payment terms as text (BT-20), such as "Net 30 days", or null.</summary>
    public string? PaymentTerms { get; set; }

    /// <summary>
    /// The buyer's reference (BT-10), or null. XRechnung requires it: for German public buyers it is the Leitweg-ID.
    /// </summary>
    public string? BuyerReference { get; set; }

    /// <summary>The buyer's purchase order number (BT-13), or null.</summary>
    public string? PurchaseOrderReference { get; set; }

    /// <summary>The contract number (BT-12), or null.</summary>
    public string? ContractReference { get; set; }

    /// <summary>The number of the invoice this one credits or corrects (BT-25), or null.</summary>
    public string? PrecedingInvoiceNumber { get; set; }

    /// <summary>The issue date of the preceding invoice (BT-26), or null.</summary>
    public DateOnly? PrecedingInvoiceDate { get; set; }

    /// <summary>
    /// The business process (BT-23), or null. XRechnung writes the Peppol billing process
    /// (urn:fdc:peppol.eu:2017:poacc:billing:01:1.0) when none is given.
    /// </summary>
    public string? BusinessProcess { get; set; }

    /// <summary>Free-text notes on the whole invoice (BT-22).</summary>
    public List<string> Notes { get; } = [];

    /// <summary>The seller (BG-4). Required.</summary>
    public InvoiceParty Seller { get; set; } = new();

    /// <summary>The buyer (BG-7). Required.</summary>
    public InvoiceParty Buyer { get; set; } = new();

    /// <summary>The actual delivery date (BT-72), or null.</summary>
    public DateOnly? DeliveryDate { get; set; }

    /// <summary>The first day of the invoicing period (BT-73), or null.</summary>
    public DateOnly? PeriodStart { get; set; }

    /// <summary>The last day of the invoicing period (BT-74), or null.</summary>
    public DateOnly? PeriodEnd { get; set; }

    /// <summary>The name of the party delivered to (BT-70), or null.</summary>
    public string? DeliverToName { get; set; }

    /// <summary>The address delivered to (BG-15), or null. Intra-community supplies need at least its country.</summary>
    public InvoiceAddress? DeliverToAddress { get; set; }

    /// <summary>How the invoice is paid (BG-16), or null. XRechnung requires it.</summary>
    public InvoicePayment? Payment { get; set; }

    /// <summary>The invoice lines (BG-25). At least one is required.</summary>
    public List<InvoiceLine> Lines { get; } = [];

    /// <summary>Allowances and charges on the whole invoice (BG-20 and BG-21), such as a discount or a freight charge.</summary>
    public List<InvoiceAllowanceCharge> AllowancesAndCharges { get; } = [];

    /// <summary>
    /// The reason each VAT category other than standard and zero rated is used (BT-120), such as the law that exempts
    /// the supply. Exempt (E) needs one; reverse charge, intra-community supply, export and outside scope get the
    /// standard EU wording and VATEX code when none is given.
    /// </summary>
    public Dictionary<VatCategory, string> VatExemptionReasons { get; } = [];

    /// <summary>An amount already paid (BT-113), deducted from the amount due. Default: 0.</summary>
    public decimal PrepaidAmount { get; set; }

    /// <summary>The totals and VAT breakdown, calculated from the lines, allowances and charges.</summary>
    /// <exception cref="ArgumentException">The invoice is not valid; the message lists every problem.</exception>
    public InvoiceTotals CalculateTotals()
    {
        ThrowIfInvalid(InvoiceRules.Check(this, null));
        return InvoiceCalculator.Calculate(this);
    }

    /// <summary>
    /// The problems that keep the invoice from being a valid e-invoice in <paramref name="profile"/>, or an empty list.
    /// The checks are EN 16931's business rules for the fields this model has, plus XRechnung's (BR-DE) for that profile;
    /// they do not replace a validator for the receiver's own rules.
    /// </summary>
    /// <param name="profile"><see cref="EInvoiceProfile.En16931"/> or <see cref="EInvoiceProfile.XRechnung"/>.</param>
    /// <exception cref="ArgumentException">The profile is not one PdfPecker writes.</exception>
    public IReadOnlyList<string> Validate(EInvoiceProfile profile = EInvoiceProfile.En16931)
        => InvoiceRules.Check(this, InvoiceRules.TargetOf(profile));

    /// <summary>
    /// The problems that keep the invoice from being a valid UBL e-invoice in <paramref name="format"/>, or an empty list:
    /// EN 16931's business rules, plus the Peppol or XRechnung rules this model's fields can break.
    /// </summary>
    /// <param name="format">The UBL format.</param>
    /// <exception cref="ArgumentException">The format is unknown.</exception>
    public IReadOnlyList<string> Validate(UblFormat format) => InvoiceRules.Check(this, InvoiceRules.TargetOf(format));

    /// <summary>
    /// The invoice as UN/CEFACT Cross Industry Invoice XML (UTF-8): Factur-X / ZUGFeRD EN 16931, or XRechnung 3.0 CII.
    /// The same invoice always gives the same bytes.
    /// </summary>
    /// <param name="profile"><see cref="EInvoiceProfile.En16931"/> (default) or <see cref="EInvoiceProfile.XRechnung"/>.</param>
    /// <exception cref="ArgumentException">The profile is not one PdfPecker writes, or the invoice is not valid; the message lists every problem.</exception>
    public byte[] ToCii(EInvoiceProfile profile = EInvoiceProfile.En16931)
    {
        ThrowIfInvalid(InvoiceRules.Check(this, InvoiceRules.TargetOf(profile)));
        return CiiWriter.Write(this, InvoiceCalculator.Calculate(this), profile);
    }

    /// <summary>
    /// The invoice as UBL 2.1 XML (UTF-8): an Invoice, or a CreditNote for <see cref="InvoiceType.CreditNote"/>. Send it
    /// over Peppol as it is, or attach the PDF you rendered with <see cref="UblInvoice.AttachPdf"/>. The same invoice
    /// always gives the same bytes.
    /// </summary>
    /// <param name="format">Peppol BIS Billing 3.0 (default) or XRechnung 3.0 UBL.</param>
    /// <exception cref="ArgumentException">The format is unknown, or the invoice is not valid; the message lists every problem.</exception>
    public byte[] ToUbl(UblFormat format = UblFormat.PeppolBis3)
    {
        ThrowIfInvalid(InvoiceRules.Check(this, InvoiceRules.TargetOf(format)));
        return UblWriter.Write(this, InvoiceCalculator.Calculate(this), format);
    }

    /// <summary>
    /// The invoice as a Factur-X / ZUGFeRD e-invoice for <see cref="PdfOptions.EInvoice"/>, which embeds it in a PDF/A-3
    /// next to the pages. See <see cref="ToCii"/>.
    /// </summary>
    /// <param name="profile"><see cref="EInvoiceProfile.En16931"/> (default) or <see cref="EInvoiceProfile.XRechnung"/>.</param>
    /// <exception cref="ArgumentException">The profile is not one PdfPecker writes, or the invoice is not valid; the message lists every problem.</exception>
    public PdfEInvoice ToEInvoice(EInvoiceProfile profile = EInvoiceProfile.En16931) => new(ToCii(profile), profile);

    private static void ThrowIfInvalid(List<string> problems)
    {
        if (problems.Count != 0)
            throw new ArgumentException("The invoice is not valid: " + string.Join(" ", problems));
    }
}

/// <summary>The seller or the buyer of an invoice.</summary>
public sealed class InvoiceParty
{
    /// <summary>The registered name (BT-27 or BT-44). Required.</summary>
    public string Name { get; set; } = "";

    /// <summary>A trading name, if different from the registered name (BT-28 or BT-45), or null.</summary>
    public string? TradingName { get; set; }

    /// <summary>An identifier the other party knows this party by (BT-29 or BT-46), such as a customer number, or null.</summary>
    public string? Id { get; set; }

    /// <summary>The legal registration number (BT-30 or BT-47), such as a company register number or Danish CVR number, or null.</summary>
    public string? LegalRegistrationId { get; set; }

    /// <summary>The ISO 6523 scheme of <see cref="LegalRegistrationId"/>, such as 0184 for a Danish CVR number, or null.</summary>
    public string? LegalRegistrationScheme { get; set; }

    /// <summary>The VAT ID with its country prefix (BT-31 or BT-48), such as DE123456789, or null.</summary>
    public string? VatId { get; set; }

    /// <summary>The seller's local tax number (BT-32), such as a German Steuernummer, or null. Not used for the buyer.</summary>
    public string? TaxNumber { get; set; }

    /// <summary>The postal address (BG-5 or BG-8). Its country is required.</summary>
    public InvoiceAddress Address { get; set; } = new();

    /// <summary>A contact person, phone and email (BG-6 or BG-9), or null. XRechnung requires the seller's.</summary>
    public InvoiceContact? Contact { get; set; }

    /// <summary>
    /// The electronic address invoices are delivered to (BT-34 or BT-49), such as a Peppol ID or an email address, or
    /// null. XRechnung requires both parties' addresses.
    /// </summary>
    public string? ElectronicAddress { get; set; }

    /// <summary>
    /// The scheme of <see cref="ElectronicAddress"/> (a Peppol EAS code such as 0088 for a GLN, 0184 for a Danish CVR
    /// number or 9930 for a German VAT ID). Default: null, which means EM (email) for an address with an @ in it.
    /// </summary>
    public string? ElectronicAddressScheme { get; set; }
}

/// <summary>A postal address.</summary>
public sealed class InvoiceAddress
{
    /// <summary>The main address line (BT-35), or null.</summary>
    public string? Line1 { get; set; }

    /// <summary>An additional address line (BT-36), or null.</summary>
    public string? Line2 { get; set; }

    /// <summary>A third address line (BT-162), or null.</summary>
    public string? Line3 { get; set; }

    /// <summary>The city (BT-37), or null. XRechnung requires it.</summary>
    public string? City { get; set; }

    /// <summary>The postal code (BT-38), or null. XRechnung requires it.</summary>
    public string? PostalCode { get; set; }

    /// <summary>The region or state (BT-39), or null.</summary>
    public string? Region { get; set; }

    /// <summary>The ISO 3166-1 alpha-2 country code (BT-40), such as DE or DK. Required.</summary>
    public string CountryCode { get; set; } = "";
}

/// <summary>A contact at the seller or the buyer.</summary>
public sealed class InvoiceContact
{
    /// <summary>The contact person or department (BT-41 or BT-56), or null.</summary>
    public string? Name { get; set; }

    /// <summary>The phone number (BT-42 or BT-57), or null.</summary>
    public string? Phone { get; set; }

    /// <summary>The email address (BT-43 or BT-58), or null.</summary>
    public string? Email { get; set; }
}

/// <summary>How an invoice is paid (BG-16).</summary>
public sealed class InvoicePayment
{
    /// <summary>
    /// The UNTDID 4461 payment means code (BT-81). Default: 58, a SEPA credit transfer. Others are 30 (credit transfer
    /// outside SEPA), 10 (cash), 48 (card), 49 (direct debit) and 59 (SEPA direct debit).
    /// </summary>
    public int MeansCode { get; set; } = 58;

    /// <summary>The IBAN to pay to (BT-84), or null. Spaces are allowed; the check digits are verified.</summary>
    public string? Iban { get; set; }

    /// <summary>A national account number to pay to (BT-84) when there is no IBAN, or null.</summary>
    public string? AccountNumber { get; set; }

    /// <summary>The account holder's name (BT-85), or null.</summary>
    public string? AccountName { get; set; }

    /// <summary>The bank's BIC (BT-86), or null.</summary>
    public string? Bic { get; set; }

    /// <summary>The text the payer quotes with the payment (BT-83), such as the invoice number, or null.</summary>
    public string? Reference { get; set; }
}

/// <summary>An invoice line (BG-25).</summary>
public sealed class InvoiceLine
{
    /// <summary>The line's identifier (BT-126), or null to number the lines 1, 2, 3 and so on.</summary>
    public string? Id { get; set; }

    /// <summary>The item's name (BT-153). Required.</summary>
    public string Name { get; set; } = "";

    /// <summary>A longer description of the item (BT-154), or null.</summary>
    public string? Description { get; set; }

    /// <summary>The seller's item number (BT-155), or null.</summary>
    public string? SellerItemId { get; set; }

    /// <summary>A note on the line (BT-127), or null.</summary>
    public string? Note { get; set; }

    /// <summary>The quantity (BT-129). Negative for a returned or credited quantity. Default: 1.</summary>
    public decimal Quantity { get; set; } = 1;

    /// <summary>
    /// The UN/ECE Recommendation 20 unit code (BT-130). Default: C62 (one, a piece). Others are HUR (hour), DAY, KGM
    /// (kilogram), MTR (metre), LTR (litre) and MON (month).
    /// </summary>
    public string UnitCode { get; set; } = "C62";

    /// <summary>The net price per <see cref="PriceBaseQuantity"/> units, after any price discount (BT-146). Not negative.</summary>
    public decimal NetPrice { get; set; }

    /// <summary>The number of units the price is for (BT-149), or null for one unit.</summary>
    public decimal? PriceBaseQuantity { get; set; }

    /// <summary>A discount on the line as an amount (BT-136), or 0. Default: 0.</summary>
    public decimal Discount { get; set; }

    /// <summary>The reason for <see cref="Discount"/> (BT-139). Default: null, written as "Discount".</summary>
    public string? DiscountReason { get; set; }

    /// <summary>The line's VAT category (BT-151). Default: <see cref="VatCategory.Standard"/>.</summary>
    public VatCategory VatCategory { get; set; } = VatCategory.Standard;

    /// <summary>The VAT rate in percent (BT-152), such as 25 or 19. Leave it at 0 for categories without VAT.</summary>
    public decimal VatRate { get; set; }

    /// <summary>
    /// The line's net amount (BT-131): quantity times price per unit, rounded to two decimals, minus the discount.
    /// </summary>
    public decimal NetAmount
        => InvoiceCalculator.Round(Quantity * NetPrice / (PriceBaseQuantity is > 0 ? PriceBaseQuantity.Value : 1)) - Discount;
}

/// <summary>An allowance (such as a discount) or a charge (such as freight) on the whole invoice (BG-20 or BG-21).</summary>
public sealed class InvoiceAllowanceCharge
{
    /// <summary>True for a charge that adds to the invoice, false for an allowance that is deducted.</summary>
    public bool IsCharge { get; set; }

    /// <summary>The amount, not negative, with at most two decimals (BT-92 or BT-99).</summary>
    public decimal Amount { get; set; }

    /// <summary>The reason as text (BT-97 or BT-104). An allowance without one is written as "Discount"; a charge needs one.</summary>
    public string? Reason { get; set; }

    /// <summary>The reason code (BT-98 or BT-105): UNTDID 5189 for allowances, UNTDID 7161 for charges, or null.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>The VAT category the amount falls in (BT-95 or BT-102). Default: <see cref="VatCategory.Standard"/>.</summary>
    public VatCategory VatCategory { get; set; } = VatCategory.Standard;

    /// <summary>The VAT rate in percent (BT-96 or BT-103).</summary>
    public decimal VatRate { get; set; }
}

/// <summary>An invoice's totals (BG-22) and VAT breakdown (BG-23), from <see cref="InvoiceData.CalculateTotals"/>.</summary>
public sealed class InvoiceTotals
{
    internal InvoiceTotals(decimal lineTotal, decimal allowanceTotal, decimal chargeTotal, decimal vatTotal, decimal prepaid,
        IReadOnlyList<InvoiceVatTotal> vat)
    {
        LineTotal = lineTotal;
        AllowanceTotal = allowanceTotal;
        ChargeTotal = chargeTotal;
        VatTotal = vatTotal;
        PrepaidAmount = prepaid;
        Vat = vat;
    }

    /// <summary>The sum of the lines' net amounts (BT-106).</summary>
    public decimal LineTotal { get; }

    /// <summary>The sum of the document allowances (BT-107).</summary>
    public decimal AllowanceTotal { get; }

    /// <summary>The sum of the document charges (BT-108).</summary>
    public decimal ChargeTotal { get; }

    /// <summary>The total without VAT (BT-109): lines minus allowances plus charges.</summary>
    public decimal NetTotal => LineTotal - AllowanceTotal + ChargeTotal;

    /// <summary>The total VAT (BT-110), the sum of the breakdown's VAT amounts.</summary>
    public decimal VatTotal { get; }

    /// <summary>The total with VAT (BT-112).</summary>
    public decimal GrandTotal => NetTotal + VatTotal;

    /// <summary>The amount already paid (BT-113).</summary>
    public decimal PrepaidAmount { get; }

    /// <summary>The amount due for payment (BT-115).</summary>
    public decimal AmountDue => GrandTotal - PrepaidAmount;

    /// <summary>One entry per VAT category and rate, in the order they first appear on the invoice.</summary>
    public IReadOnlyList<InvoiceVatTotal> Vat { get; }
}

/// <summary>One VAT category and rate of an invoice's VAT breakdown (BG-23).</summary>
public sealed class InvoiceVatTotal
{
    internal InvoiceVatTotal(VatCategory category, decimal rate, decimal taxableAmount, decimal vatAmount, string? exemptionReason, string? exemptionReasonCode)
    {
        Category = category;
        Rate = rate;
        TaxableAmount = taxableAmount;
        VatAmount = vatAmount;
        ExemptionReason = exemptionReason;
        ExemptionReasonCode = exemptionReasonCode;
    }

    /// <summary>The VAT category (BT-118).</summary>
    public VatCategory Category { get; }

    /// <summary>The rate in percent (BT-119); 0 for categories without VAT.</summary>
    public decimal Rate { get; }

    /// <summary>The amount the VAT is calculated on (BT-116).</summary>
    public decimal TaxableAmount { get; }

    /// <summary>The VAT (BT-117): the taxable amount times the rate, rounded to two decimals.</summary>
    public decimal VatAmount { get; }

    /// <summary>The exemption reason (BT-120), or null for standard and zero rated VAT.</summary>
    public string? ExemptionReason { get; }

    /// <summary>The VATEX exemption reason code (BT-121), or null.</summary>
    public string? ExemptionReasonCode { get; }
}
