using System.Globalization;
using System.Text;
using System.Xml;

namespace PdfPecker.Invoices;

/// <summary>
/// Writes an <see cref="InvoiceData"/> as a UBL 2.1 Invoice or CreditNote, in the element order the UBL schema requires,
/// for Peppol BIS Billing 3.0 and for XRechnung 3.0 UBL.
/// </summary>
internal static class UblWriter
{
    private const string Cac = UblInvoice.CacNamespace;
    private const string Cbc = UblInvoice.CbcNamespace;

    internal const string PeppolCustomization = "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";

    internal static byte[] Write(InvoiceData invoice, InvoiceTotals totals, UblFormat format)
    {
        bool credit = invoice.Type == InvoiceType.CreditNote;
        var root = credit ? "CreditNote" : "Invoice";
        var output = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  ", NewLineChars = "\n" };
        using (var xml = XmlWriter.Create(output, settings))
        {
            var w = new Writer(xml, invoice.Currency);
            xml.WriteStartDocument();
            xml.WriteStartElement(root, credit ? UblInvoice.CreditNoteNamespace : UblInvoice.InvoiceNamespace);
            xml.WriteAttributeString("xmlns", "cac", null, Cac);
            xml.WriteAttributeString("xmlns", "cbc", null, Cbc);

            w.Text("CustomizationID", format == UblFormat.XRechnung ? CiiWriter.XRechnungGuideline : PeppolCustomization);
            w.Text("ProfileID", string.IsNullOrWhiteSpace(invoice.BusinessProcess) ? CiiWriter.PeppolBillingProcess : invoice.BusinessProcess);
            w.Text("ID", invoice.Number);
            w.Date("IssueDate", invoice.IssueDate);
            // A UBL credit note has no due date of its own; it goes in the payment means instead (BT-9).
            if (!credit && invoice.DueDate is { } due) w.Date("DueDate", due);
            w.Text(credit ? "CreditNoteTypeCode" : "InvoiceTypeCode", ((int)invoice.Type).ToString(CultureInfo.InvariantCulture));
            foreach (var note in invoice.Notes)
                w.Optional("Note", note);
            w.Text("DocumentCurrencyCode", invoice.Currency);
            w.Optional("BuyerReference", invoice.BuyerReference);
            if (invoice.PeriodStart is not null || invoice.PeriodEnd is not null)
            {
                w.Start("InvoicePeriod");
                if (invoice.PeriodStart is { } start) w.Date("StartDate", start);
                if (invoice.PeriodEnd is { } end) w.Date("EndDate", end);
                w.End();
            }
            WriteReference(w, "OrderReference", invoice.PurchaseOrderReference);
            if (Has(invoice.PrecedingInvoiceNumber))
            {
                w.Start("BillingReference");
                w.Start("InvoiceDocumentReference");
                w.Text("ID", invoice.PrecedingInvoiceNumber!);
                if (invoice.PrecedingInvoiceDate is { } date) w.Date("IssueDate", date);
                w.End();
                w.End();
            }
            WriteReference(w, "ContractDocumentReference", invoice.ContractReference);

            w.Start("AccountingSupplierParty");
            WriteParty(w, invoice.Seller, seller: true);
            w.End();
            w.Start("AccountingCustomerParty");
            WriteParty(w, invoice.Buyer, seller: false);
            w.End();

            WriteDelivery(w, invoice);
            WritePayment(w, invoice, credit);

            foreach (var item in invoice.AllowancesAndCharges)
                WriteAllowanceCharge(w, item.IsCharge, item.Amount, item.Reason, item.ReasonCode, item);

            w.Start("TaxTotal");
            w.Amount("TaxAmount", totals.VatTotal);
            foreach (var vat in totals.Vat)
            {
                w.Start("TaxSubtotal");
                w.Amount("TaxableAmount", vat.TaxableAmount);
                w.Amount("TaxAmount", vat.VatAmount);
                w.Start("TaxCategory");
                w.Text("ID", InvoiceCalculator.Code(vat.Category));
                w.Text("Percent", Rate(vat.Rate));
                w.Optional("TaxExemptionReasonCode", vat.ExemptionReasonCode);
                w.Optional("TaxExemptionReason", vat.ExemptionReason);
                WriteTaxScheme(w);
                w.End();
                w.End();
            }
            w.End();

            w.Start("LegalMonetaryTotal");
            w.Amount("LineExtensionAmount", totals.LineTotal);
            w.Amount("TaxExclusiveAmount", totals.NetTotal);
            w.Amount("TaxInclusiveAmount", totals.GrandTotal);
            if (invoice.AllowancesAndCharges.Exists(a => !a.IsCharge)) w.Amount("AllowanceTotalAmount", totals.AllowanceTotal);
            if (invoice.AllowancesAndCharges.Exists(a => a.IsCharge)) w.Amount("ChargeTotalAmount", totals.ChargeTotal);
            if (totals.PrepaidAmount != 0) w.Amount("PrepaidAmount", totals.PrepaidAmount);
            w.Amount("PayableAmount", totals.AmountDue);
            w.End();

            for (var i = 0; i < invoice.Lines.Count; i++)
                WriteLine(w, invoice.Lines[i], i, credit);

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return output.ToArray();
    }

    private static void WriteReference(Writer w, string element, string? id)
    {
        if (!Has(id)) return;
        w.Start(element);
        w.Text("ID", id!);
        w.End();
    }

    private static void WriteParty(Writer w, InvoiceParty party, bool seller)
    {
        w.Start("Party");
        if (Has(party.ElectronicAddress))
            w.Text("EndpointID", party.ElectronicAddress!, "schemeID", InvoiceRules.SchemeOf(party)!);
        if (Has(party.Id))
        {
            w.Start("PartyIdentification");
            w.Text("ID", party.Id!);
            w.End();
        }
        if (Has(party.TradingName))
        {
            w.Start("PartyName");
            w.Text("Name", party.TradingName!);
            w.End();
        }
        WriteAddress(w, "PostalAddress", party.Address);
        if (Has(party.VatId)) WritePartyTax(w, party.VatId!, "VAT");
        if (seller && Has(party.TaxNumber)) WritePartyTax(w, party.TaxNumber!, "TAX");
        w.Start("PartyLegalEntity");
        w.Text("RegistrationName", party.Name);
        if (Has(party.LegalRegistrationId))
        {
            if (Has(party.LegalRegistrationScheme)) w.Text("CompanyID", party.LegalRegistrationId!, "schemeID", party.LegalRegistrationScheme!);
            else w.Text("CompanyID", party.LegalRegistrationId!);
        }
        w.End();
        if (party.Contact is { } contact && (Has(contact.Name) || Has(contact.Phone) || Has(contact.Email)))
        {
            w.Start("Contact");
            w.Optional("Name", contact.Name);
            w.Optional("Telephone", contact.Phone);
            w.Optional("ElectronicMail", contact.Email);
            w.End();
        }
        w.End();
    }

    private static void WritePartyTax(Writer w, string id, string scheme)
    {
        w.Start("PartyTaxScheme");
        w.Text("CompanyID", id);
        w.Start("TaxScheme");
        w.Text("ID", scheme);
        w.End();
        w.End();
    }

    private static void WriteAddress(Writer w, string element, InvoiceAddress address)
    {
        w.Start(element);
        w.Optional("StreetName", address.Line1);
        w.Optional("AdditionalStreetName", address.Line2);
        w.Optional("CityName", address.City);
        w.Optional("PostalZone", address.PostalCode);
        w.Optional("CountrySubentity", address.Region);
        if (Has(address.Line3))
        {
            w.Start("AddressLine");
            w.Text("Line", address.Line3!);
            w.End();
        }
        w.Start("Country");
        w.Text("IdentificationCode", address.CountryCode);
        w.End();
        w.End();
    }

    private static void WriteDelivery(Writer w, InvoiceData invoice)
    {
        if (invoice.DeliveryDate is null && invoice.DeliverToAddress is null && !Has(invoice.DeliverToName)) return;
        w.Start("Delivery");
        if (invoice.DeliveryDate is { } date) w.Date("ActualDeliveryDate", date);
        if (invoice.DeliverToAddress is { } address)
        {
            w.Start("DeliveryLocation");
            WriteAddress(w, "Address", address);
            w.End();
        }
        if (Has(invoice.DeliverToName))
        {
            w.Start("DeliveryParty");
            w.Start("PartyName");
            w.Text("Name", invoice.DeliverToName!);
            w.End();
            w.End();
        }
        w.End();
    }

    private static void WritePayment(Writer w, InvoiceData invoice, bool credit)
    {
        if (invoice.Payment is { } payment)
        {
            w.Start("PaymentMeans");
            w.Text("PaymentMeansCode", payment.MeansCode.ToString(CultureInfo.InvariantCulture));
            if (credit && invoice.DueDate is { } due) w.Date("PaymentDueDate", due);
            w.Optional("PaymentID", payment.Reference);
            if (Has(payment.Iban) || Has(payment.AccountNumber))
            {
                w.Start("PayeeFinancialAccount");
                w.Text("ID", Has(payment.Iban) ? InvoiceRules.NormalizeIban(payment.Iban!) : payment.AccountNumber!);
                w.Optional("Name", payment.AccountName);
                if (Has(payment.Bic))
                {
                    w.Start("FinancialInstitutionBranch");
                    w.Text("ID", payment.Bic!.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant());
                    w.End();
                }
                w.End();
            }
            w.End();
        }
        if (Has(invoice.PaymentTerms))
        {
            w.Start("PaymentTerms");
            w.Text("Note", invoice.PaymentTerms!);
            w.End();
        }
    }

    private static void WriteAllowanceCharge(Writer w, bool charge, decimal amount, string? reason, string? reasonCode, InvoiceAllowanceCharge? header)
    {
        w.Start("AllowanceCharge");
        w.Text("ChargeIndicator", charge ? "true" : "false");
        w.Optional("AllowanceChargeReasonCode", reasonCode);
        w.Optional("AllowanceChargeReason", Has(reason) ? reason : charge ? null : "Discount");
        w.Amount("Amount", amount);
        if (header is not null)
        {
            w.Start("TaxCategory");
            w.Text("ID", InvoiceCalculator.Code(header.VatCategory));
            if (header.VatCategory != VatCategory.OutsideScope)
                w.Text("Percent", Rate(InvoiceCalculator.RateOf(header.VatCategory, header.VatRate)));
            WriteTaxScheme(w);
            w.End();
        }
        w.End();
    }

    private static void WriteLine(Writer w, InvoiceLine line, int index, bool credit)
    {
        w.Start(credit ? "CreditNoteLine" : "InvoiceLine");
        w.Text("ID", Has(line.Id) ? line.Id! : (index + 1).ToString(CultureInfo.InvariantCulture));
        w.Optional("Note", line.Note);
        w.Text(credit ? "CreditedQuantity" : "InvoicedQuantity", Number(line.Quantity), "unitCode", line.UnitCode);
        w.Amount("LineExtensionAmount", line.NetAmount);
        if (line.Discount != 0)
            WriteAllowanceCharge(w, false, line.Discount, line.DiscountReason, null, null);
        w.Start("Item");
        w.Optional("Description", line.Description);
        w.Text("Name", line.Name);
        if (Has(line.SellerItemId))
        {
            w.Start("SellersItemIdentification");
            w.Text("ID", line.SellerItemId!);
            w.End();
        }
        w.Start("ClassifiedTaxCategory");
        w.Text("ID", InvoiceCalculator.Code(line.VatCategory));
        if (line.VatCategory != VatCategory.OutsideScope)
            w.Text("Percent", Rate(InvoiceCalculator.RateOf(line.VatCategory, line.VatRate)));
        WriteTaxScheme(w);
        w.End();
        w.End();
        w.Start("Price");
        w.Price("PriceAmount", Number(line.NetPrice));
        if (line.PriceBaseQuantity is { } baseQuantity)
            w.Text("BaseQuantity", Number(baseQuantity), "unitCode", line.UnitCode);
        w.End();
        w.End();
    }

    private static void WriteTaxScheme(Writer w)
    {
        w.Start("TaxScheme");
        w.Text("ID", "VAT");
        w.End();
    }

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Rate(decimal rate) => rate.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Writes cac: groups and cbc: values, trimming the text of each.</summary>
    private readonly struct Writer(XmlWriter xml, string currency)
    {
        internal void Start(string name) => xml.WriteStartElement("cac", name, Cac);

        internal void End() => xml.WriteEndElement();

        internal void Text(string name, string value)
        {
            xml.WriteStartElement("cbc", name, Cbc);
            xml.WriteString(value.Trim());
            xml.WriteEndElement();
        }

        internal void Text(string name, string value, string attribute, string attributeValue)
        {
            xml.WriteStartElement("cbc", name, Cbc);
            xml.WriteAttributeString(attribute, attributeValue.Trim());
            xml.WriteString(value.Trim());
            xml.WriteEndElement();
        }

        internal void Optional(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Text(name, value);
        }

        /// <summary>Money with two decimals and the invoice's currency.</summary>
        internal void Amount(string name, decimal amount)
            => Text(name, amount.ToString("0.00", CultureInfo.InvariantCulture), "currencyID", currency);

        /// <summary>An amount written as given, for unit prices, which may have more than two decimals.</summary>
        internal void Price(string name, string text) => Text(name, text, "currencyID", currency);

        internal void Date(string name, DateOnly date) => Text(name, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
