using System.Globalization;
using System.Text;
using System.Xml;

namespace PdfPecker.Invoices;

/// <summary>
/// Writes an <see cref="InvoiceData"/> as UN/CEFACT Cross Industry Invoice D16B XML, in the element order the CII schema
/// requires, for the Factur-X / ZUGFeRD EN 16931 profile and for XRechnung 3.0.
/// </summary>
internal static class CiiWriter
{
    private const string Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private const string Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private const string Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    private const string Qdt = "urn:un:unece:uncefact:data:standard:QualifiedDataType:100";

    internal const string En16931Guideline = "urn:cen.eu:en16931:2017";
    internal const string XRechnungGuideline = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    internal const string PeppolBillingProcess = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";

    /// <summary>The profile, if PdfPecker writes it.</summary>
    /// <exception cref="ArgumentException">The profile is not EN 16931 or XRechnung.</exception>
    internal static EInvoiceProfile CheckProfile(EInvoiceProfile profile) => profile is EInvoiceProfile.En16931 or EInvoiceProfile.XRechnung
        ? profile
        : throw new ArgumentException($"PdfPecker writes the EN 16931 and XRechnung profiles, not {profile}.", nameof(profile));

    internal static byte[] Write(InvoiceData invoice, InvoiceTotals totals, EInvoiceProfile profile)
    {
        var output = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  ", NewLineChars = "\n" };
        using (var xml = XmlWriter.Create(output, settings))
        {
            var w = new Writer(xml);
            xml.WriteStartDocument();
            xml.WriteStartElement("rsm", "CrossIndustryInvoice", Rsm);
            xml.WriteAttributeString("xmlns", "qdt", null, Qdt);
            xml.WriteAttributeString("xmlns", "ram", null, Ram);
            xml.WriteAttributeString("xmlns", "udt", null, Udt);

            xml.WriteStartElement("rsm", "ExchangedDocumentContext", Rsm);
            var process = invoice.BusinessProcess ?? (profile == EInvoiceProfile.XRechnung ? PeppolBillingProcess : null);
            if (!string.IsNullOrWhiteSpace(process))
            {
                w.Start("BusinessProcessSpecifiedDocumentContextParameter");
                w.Text("ID", process);
                w.End();
            }
            w.Start("GuidelineSpecifiedDocumentContextParameter");
            w.Text("ID", profile == EInvoiceProfile.XRechnung ? XRechnungGuideline : En16931Guideline);
            w.End();
            xml.WriteEndElement();

            xml.WriteStartElement("rsm", "ExchangedDocument", Rsm);
            w.Text("ID", invoice.Number);
            w.Text("TypeCode", ((int)invoice.Type).ToString(CultureInfo.InvariantCulture));
            w.Date("IssueDateTime", invoice.IssueDate);
            foreach (var note in invoice.Notes)
            {
                if (string.IsNullOrWhiteSpace(note)) continue;
                w.Start("IncludedNote");
                w.Text("Content", note);
                w.End();
            }
            xml.WriteEndElement();

            xml.WriteStartElement("rsm", "SupplyChainTradeTransaction", Rsm);
            for (var i = 0; i < invoice.Lines.Count; i++)
                WriteLine(w, invoice.Lines[i], i);
            WriteAgreement(w, invoice);
            WriteDelivery(w, invoice);
            WriteSettlement(w, invoice, totals);
            xml.WriteEndElement();

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return output.ToArray();
    }

    private static void WriteLine(Writer w, InvoiceLine line, int index)
    {
        w.Start("IncludedSupplyChainTradeLineItem");
        w.Start("AssociatedDocumentLineDocument");
        w.Text("LineID", string.IsNullOrWhiteSpace(line.Id) ? (index + 1).ToString(CultureInfo.InvariantCulture) : line.Id);
        if (!string.IsNullOrWhiteSpace(line.Note))
        {
            w.Start("IncludedNote");
            w.Text("Content", line.Note);
            w.End();
        }
        w.End();

        w.Start("SpecifiedTradeProduct");
        w.Optional("SellerAssignedID", line.SellerItemId);
        w.Text("Name", line.Name);
        w.Optional("Description", line.Description);
        w.End();

        w.Start("SpecifiedLineTradeAgreement");
        w.Start("NetPriceProductTradePrice");
        w.Text("ChargeAmount", Number(line.NetPrice));
        if (line.PriceBaseQuantity is { } baseQuantity)
            w.Text("BasisQuantity", Number(baseQuantity), "unitCode", line.UnitCode);
        w.End();
        w.End();

        w.Start("SpecifiedLineTradeDelivery");
        w.Text("BilledQuantity", Number(line.Quantity), "unitCode", line.UnitCode);
        w.End();

        w.Start("SpecifiedLineTradeSettlement");
        WriteTax(w, line.VatCategory, line.VatRate);
        if (line.Discount != 0)
            WriteAllowanceCharge(w, false, line.Discount, line.DiscountReason, null, null);
        w.Start("SpecifiedTradeSettlementLineMonetarySummation");
        w.Text("LineTotalAmount", Money(line.NetAmount));
        w.End();
        w.End();
        w.End();
    }

    private static void WriteAgreement(Writer w, InvoiceData invoice)
    {
        w.Start("ApplicableHeaderTradeAgreement");
        w.Optional("BuyerReference", invoice.BuyerReference);
        WriteParty(w, "SellerTradeParty", invoice.Seller, seller: true);
        WriteParty(w, "BuyerTradeParty", invoice.Buyer, seller: false);
        WriteReference(w, "BuyerOrderReferencedDocument", invoice.PurchaseOrderReference);
        WriteReference(w, "ContractReferencedDocument", invoice.ContractReference);
        w.End();
    }

    private static void WriteReference(Writer w, string element, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        w.Start(element);
        w.Text("IssuerAssignedID", id);
        w.End();
    }

    private static void WriteParty(Writer w, string element, InvoiceParty party, bool seller)
    {
        w.Start(element);
        w.Optional("ID", party.Id);
        w.Text("Name", party.Name);
        if (!string.IsNullOrWhiteSpace(party.LegalRegistrationId) || !string.IsNullOrWhiteSpace(party.TradingName))
        {
            w.Start("SpecifiedLegalOrganization");
            if (!string.IsNullOrWhiteSpace(party.LegalRegistrationId))
            {
                if (string.IsNullOrWhiteSpace(party.LegalRegistrationScheme)) w.Text("ID", party.LegalRegistrationId);
                else w.Text("ID", party.LegalRegistrationId, "schemeID", party.LegalRegistrationScheme.Trim());
            }
            w.Optional("TradingBusinessName", party.TradingName);
            w.End();
        }
        if (party.Contact is { } contact && (Has(contact.Name) || Has(contact.Phone) || Has(contact.Email)))
        {
            w.Start("DefinedTradeContact");
            w.Optional("PersonName", contact.Name);
            if (Has(contact.Phone))
            {
                w.Start("TelephoneUniversalCommunication");
                w.Text("CompleteNumber", contact.Phone!);
                w.End();
            }
            if (Has(contact.Email))
            {
                w.Start("EmailURIUniversalCommunication");
                w.Text("URIID", contact.Email!);
                w.End();
            }
            w.End();
        }
        WriteAddress(w, party.Address);
        if (Has(party.ElectronicAddress))
        {
            w.Start("URIUniversalCommunication");
            w.Text("URIID", party.ElectronicAddress!, "schemeID", InvoiceRules.SchemeOf(party)!);
            w.End();
        }
        if (Has(party.VatId))
        {
            w.Start("SpecifiedTaxRegistration");
            w.Text("ID", party.VatId!, "schemeID", "VA");
            w.End();
        }
        if (seller && Has(party.TaxNumber))
        {
            w.Start("SpecifiedTaxRegistration");
            w.Text("ID", party.TaxNumber!, "schemeID", "FC");
            w.End();
        }
        w.End();
    }

    private static void WriteAddress(Writer w, InvoiceAddress address)
    {
        w.Start("PostalTradeAddress");
        w.Optional("PostcodeCode", address.PostalCode);
        w.Optional("LineOne", address.Line1);
        w.Optional("LineTwo", address.Line2);
        w.Optional("LineThree", address.Line3);
        w.Optional("CityName", address.City);
        w.Text("CountryID", address.CountryCode);
        w.Optional("CountrySubDivisionName", address.Region);
        w.End();
    }

    private static void WriteDelivery(Writer w, InvoiceData invoice)
    {
        // The header delivery is required in CII even when it is empty.
        w.Start("ApplicableHeaderTradeDelivery");
        if (Has(invoice.DeliverToName) || invoice.DeliverToAddress is not null)
        {
            w.Start("ShipToTradeParty");
            w.Optional("Name", invoice.DeliverToName);
            if (invoice.DeliverToAddress is { } address) WriteAddress(w, address);
            w.End();
        }
        if (invoice.DeliveryDate is { } date)
        {
            w.Start("ActualDeliverySupplyChainEvent");
            w.Date("OccurrenceDateTime", date);
            w.End();
        }
        w.End();
    }

    private static void WriteSettlement(Writer w, InvoiceData invoice, InvoiceTotals totals)
    {
        w.Start("ApplicableHeaderTradeSettlement");
        var payment = invoice.Payment;
        w.Optional("PaymentReference", payment?.Reference);
        w.Text("InvoiceCurrencyCode", invoice.Currency);
        if (payment is not null)
        {
            w.Start("SpecifiedTradeSettlementPaymentMeans");
            w.Text("TypeCode", payment.MeansCode.ToString(CultureInfo.InvariantCulture));
            if (Has(payment.Iban) || Has(payment.AccountNumber))
            {
                w.Start("PayeePartyCreditorFinancialAccount");
                if (Has(payment.Iban)) w.Text("IBANID", InvoiceRules.NormalizeIban(payment.Iban!));
                w.Optional("AccountName", payment.AccountName);
                if (Has(payment.AccountNumber)) w.Text("ProprietaryID", payment.AccountNumber!);
                w.End();
            }
            if (Has(payment.Bic))
            {
                w.Start("PayeeSpecifiedCreditorFinancialInstitution");
                w.Text("BICID", payment.Bic!.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant());
                w.End();
            }
            w.End();
        }

        foreach (var vat in totals.Vat)
        {
            w.Start("ApplicableTradeTax");
            w.Text("CalculatedAmount", Money(vat.VatAmount));
            w.Text("TypeCode", "VAT");
            w.Optional("ExemptionReason", vat.ExemptionReason);
            w.Text("BasisAmount", Money(vat.TaxableAmount));
            w.Text("CategoryCode", InvoiceCalculator.Code(vat.Category));
            w.Optional("ExemptionReasonCode", vat.ExemptionReasonCode);
            w.Text("RateApplicablePercent", Rate(vat.Rate));
            w.End();
        }

        if (invoice.PeriodStart is not null || invoice.PeriodEnd is not null)
        {
            w.Start("BillingSpecifiedPeriod");
            if (invoice.PeriodStart is { } start) w.Date("StartDateTime", start);
            if (invoice.PeriodEnd is { } end) w.Date("EndDateTime", end);
            w.End();
        }

        foreach (var item in invoice.AllowancesAndCharges)
            WriteAllowanceCharge(w, item.IsCharge, item.Amount, item.Reason, item.ReasonCode, item);

        if (Has(invoice.PaymentTerms) || invoice.DueDate is not null)
        {
            w.Start("SpecifiedTradePaymentTerms");
            w.Optional("Description", invoice.PaymentTerms);
            if (invoice.DueDate is { } due) w.Date("DueDateDateTime", due);
            w.End();
        }

        w.Start("SpecifiedTradeSettlementHeaderMonetarySummation");
        w.Text("LineTotalAmount", Money(totals.LineTotal));
        if (invoice.AllowancesAndCharges.Exists(a => a.IsCharge)) w.Text("ChargeTotalAmount", Money(totals.ChargeTotal));
        if (invoice.AllowancesAndCharges.Exists(a => !a.IsCharge)) w.Text("AllowanceTotalAmount", Money(totals.AllowanceTotal));
        w.Text("TaxBasisTotalAmount", Money(totals.NetTotal));
        w.Text("TaxTotalAmount", Money(totals.VatTotal), "currencyID", invoice.Currency);
        w.Text("GrandTotalAmount", Money(totals.GrandTotal));
        if (totals.PrepaidAmount != 0) w.Text("TotalPrepaidAmount", Money(totals.PrepaidAmount));
        w.Text("DuePayableAmount", Money(totals.AmountDue));
        w.End();

        if (Has(invoice.PrecedingInvoiceNumber))
        {
            w.Start("InvoiceReferencedDocument");
            w.Text("IssuerAssignedID", invoice.PrecedingInvoiceNumber!);
            if (invoice.PrecedingInvoiceDate is { } date)
            {
                w.Start("FormattedIssueDateTime");
                w.DateString(Qdt, date);
                w.End();
            }
            w.End();
        }
        w.End();
    }

    private static void WriteAllowanceCharge(Writer w, bool charge, decimal amount, string? reason, string? reasonCode, InvoiceAllowanceCharge? header)
    {
        w.Start("SpecifiedTradeAllowanceCharge");
        w.Start("ChargeIndicator");
        w.Udt("Indicator", charge ? "true" : "false");
        w.End();
        w.Text("ActualAmount", Money(amount));
        w.Optional("ReasonCode", reasonCode);
        w.Optional("Reason", Has(reason) ? reason : charge ? null : "Discount");
        if (header is not null)
        {
            w.Start("CategoryTradeTax");
            w.Text("TypeCode", "VAT");
            w.Text("CategoryCode", InvoiceCalculator.Code(header.VatCategory));
            if (header.VatCategory != VatCategory.OutsideScope)
                w.Text("RateApplicablePercent", Rate(InvoiceCalculator.RateOf(header.VatCategory, header.VatRate)));
            w.End();
        }
        w.End();
    }

    private static void WriteTax(Writer w, VatCategory category, decimal rate)
    {
        w.Start("ApplicableTradeTax");
        w.Text("TypeCode", "VAT");
        w.Text("CategoryCode", InvoiceCalculator.Code(category));
        // A line in VAT category O has no rate (BR-O-05).
        if (category != VatCategory.OutsideScope) w.Text("RateApplicablePercent", Rate(InvoiceCalculator.RateOf(category, rate)));
        w.End();
    }

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Rate(decimal rate) => rate.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Writes ram: elements, trimming the text of each.</summary>
    private readonly struct Writer(XmlWriter xml)
    {
        internal void Start(string name) => xml.WriteStartElement("ram", name, Ram);

        internal void End() => xml.WriteEndElement();

        internal void Text(string name, string value)
        {
            xml.WriteStartElement("ram", name, Ram);
            xml.WriteString(value.Trim());
            xml.WriteEndElement();
        }

        internal void Text(string name, string value, string attribute, string attributeValue)
        {
            xml.WriteStartElement("ram", name, Ram);
            xml.WriteAttributeString(attribute, attributeValue.Trim());
            xml.WriteString(value.Trim());
            xml.WriteEndElement();
        }

        internal void Optional(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Text(name, value);
        }

        internal void Udt(string name, string value) => xml.WriteElementString("udt", name, CiiWriter.Udt, value);

        /// <summary>A date as a DateTimeString in format 102 (yyyyMMdd).</summary>
        internal void Date(string name, DateOnly date)
        {
            Start(name);
            DateString(CiiWriter.Udt, date);
            End();
        }

        internal void DateString(string ns, DateOnly date)
        {
            xml.WriteStartElement(ns == CiiWriter.Udt ? "udt" : "qdt", "DateTimeString", ns);
            xml.WriteAttributeString("format", "102");
            xml.WriteString(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }
    }
}
