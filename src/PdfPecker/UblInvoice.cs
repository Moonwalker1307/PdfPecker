using System.Text;
using System.Xml;

namespace PdfPecker;

/// <summary>
/// Helpers for UBL e-invoices (Peppol BIS Billing 3.0, OIOUBL, XRechnung UBL, EHF and other EN 16931 UBL formats).
/// In these formats the XML is the invoice and a PDF can only travel inside it, as an attachment.
/// </summary>
public static class UblInvoice
{
    internal const string InvoiceNamespace = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    internal const string CreditNoteNamespace = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    internal const string CacNamespace = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    internal const string CbcNamespace = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    // The root elements that come after AdditionalDocumentReference in the UBL 2.0 and 2.1 schemas. The one new element
    // goes in front of the first of these when the invoice has no AdditionalDocumentReference yet.
    private static readonly HashSet<string> AfterInInvoice =
    [
        "ProjectReference", "Signature", "AccountingSupplierParty", "AccountingCustomerParty", "PayeeParty",
        "BuyerCustomerParty", "SellerSupplierParty", "TaxRepresentativeParty", "Delivery", "DeliveryTerms",
        "PaymentMeans", "PaymentTerms", "PrepaidPayment", "AllowanceCharge", "TaxExchangeRate", "PricingExchangeRate",
        "PaymentExchangeRate", "PaymentAlternativeExchangeRate", "TaxTotal", "WithholdingTaxTotal", "LegalMonetaryTotal",
        "InvoiceLine",
    ];

    // In a credit note the statement and originator references follow AdditionalDocumentReference; in an invoice they precede it.
    private static readonly HashSet<string> AfterInCreditNote =
    [
        "StatementDocumentReference", "OriginatorDocumentReference", "Signature", "AccountingSupplierParty",
        "AccountingCustomerParty", "PayeeParty", "BuyerCustomerParty", "SellerSupplierParty", "TaxRepresentativeParty",
        "Delivery", "DeliveryTerms", "PaymentMeans", "PaymentTerms", "TaxExchangeRate", "PricingExchangeRate",
        "PaymentExchangeRate", "PaymentAlternativeExchangeRate", "AllowanceCharge", "TaxTotal", "LegalMonetaryTotal",
        "CreditNoteLine",
    ];

    /// <summary>
    /// Adds a PDF to a UBL invoice or credit note as an embedded attachment (an <c>AdditionalDocumentReference</c>,
    /// EN 16931 BG-24), so the receiver can see the invoice as you rendered it. The attachment goes after any existing
    /// attachments, where the UBL schema expects it; everything else in the XML is kept as it was.
    /// </summary>
    /// <param name="ublXml">The invoice or credit note XML from your invoicing system (UBL 2.0 or 2.1).</param>
    /// <param name="pdf">The PDF, for example from <see cref="HtmlToPdf.Convert(string, PdfOptions?)"/>.</param>
    /// <param name="fileName">The attachment's file name (BT-125 filename), such as <c>INV-1001.pdf</c>.</param>
    /// <param name="id">The supporting document reference (BT-122). Default: the file name.</param>
    /// <param name="description">
    /// An optional description (BT-123). UBL 2.0 (OIOUBL 2.1) has no <c>DocumentDescription</c> and OIOUBL requires a
    /// <c>DocumentType</c>, so there it is written as <c>DocumentType</c>, which is <c>PDF</c> when no description is given.
    /// </param>
    /// <returns>The XML with the attachment, in the input's encoding.</returns>
    /// <exception cref="ArgumentException">The XML is not a UBL invoice or credit note, or the PDF or file name is empty or not a PDF.</exception>
    public static byte[] AttachPdf(byte[] ublXml, byte[] pdf, string fileName, string? id = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(ublXml);
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (id is not null && string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("The attachment ID must not be empty.", nameof(id));
        if (pdf.Length < 5 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F' || pdf[4] != '-')
            throw new ArgumentException("The attachment is not a PDF file.", nameof(pdf));

        var doc = Load(ublXml);
        var root = doc.DocumentElement!;
        var after = root.NamespaceURI switch
        {
            InvoiceNamespace when root.LocalName == "Invoice" => AfterInInvoice,
            CreditNoteNamespace when root.LocalName == "CreditNote" => AfterInCreditNote,
            _ => throw new ArgumentException("The XML is not a UBL Invoice or CreditNote.", nameof(ublXml)),
        };

        XmlElement? lastAttachment = null, firstAfter = null;
        bool ubl20 = false;
        foreach (XmlNode node in root.ChildNodes)
        {
            if (node is not XmlElement child) continue;
            // OIOUBL 2.1 is built on UBL 2.0 (its CustomizationID is OIOUBL-2.01 or OIOUBL-2.02).
            if (child.NamespaceURI == CbcNamespace && (child.LocalName == "UBLVersionID" && child.InnerText.Trim() == "2.0"
                || child.LocalName == "CustomizationID" && child.InnerText.Trim().StartsWith("OIOUBL-2.0", StringComparison.Ordinal)))
                ubl20 = true;
            if (child.NamespaceURI != CacNamespace) continue;
            if (child.LocalName == "AdditionalDocumentReference") lastAttachment = child;
            else if (firstAfter is null && after.Contains(child.LocalName)) firstAfter = child;
        }

        // Match the document's indentation: the whitespace in front of the root's first child, if there is any.
        var first = root.ChildNodes.OfType<XmlElement>().FirstOrDefault();
        string indent = first?.PreviousSibling is XmlWhitespace ws ? ws.Value! : "";
        string level = indent[(indent.LastIndexOf('\n') + 1)..];
        string inner = level.Length == 0 ? "" : indent + level;

        string cac = root.GetPrefixOfNamespace(CacNamespace) is { Length: > 0 } p1 ? p1 : "cac";
        string cbc = root.GetPrefixOfNamespace(CbcNamespace) is { Length: > 0 } p2 ? p2 : "cbc";
        XmlElement Cac(string name) => doc.CreateElement(cac, name, CacNamespace);
        XmlElement Cbc(string name, string text)
        {
            var e = doc.CreateElement(cbc, name, CbcNamespace);
            e.InnerText = text;
            return e;
        }
        void Add(XmlElement parent, XmlNode child, string childIndent)
        {
            if (childIndent.Length > 0) parent.AppendChild(doc.CreateWhitespace(childIndent));
            parent.AppendChild(child);
        }

        var reference = Cac("AdditionalDocumentReference");
        Add(reference, Cbc("ID", id ?? fileName), inner);
        if (ubl20)
            Add(reference, Cbc("DocumentType", string.IsNullOrWhiteSpace(description) ? "PDF" : description), inner);
        else if (!string.IsNullOrWhiteSpace(description))
            Add(reference, Cbc("DocumentDescription", description), inner);
        var attachment = Cac("Attachment");
        var binary = Cbc("EmbeddedDocumentBinaryObject", Convert.ToBase64String(pdf));
        binary.SetAttribute("mimeCode", "application/pdf");
        binary.SetAttribute("filename", fileName);
        Add(attachment, binary, inner + level);
        if (inner.Length > 0) attachment.AppendChild(doc.CreateWhitespace(inner));
        Add(reference, attachment, inner);
        if (indent.Length > 0) reference.AppendChild(doc.CreateWhitespace(indent));

        if (lastAttachment is not null)
        {
            root.InsertAfter(reference, lastAttachment);
            if (indent.Length > 0) root.InsertAfter(doc.CreateWhitespace(indent), lastAttachment);
        }
        else if (firstAfter is not null)
        {
            root.InsertBefore(reference, firstAfter);
            if (indent.Length > 0) root.InsertBefore(doc.CreateWhitespace(indent), firstAfter);
        }
        else
        {
            var end = root.LastChild is XmlWhitespace ? root.LastChild : null;
            if (indent.Length > 0) root.InsertBefore(doc.CreateWhitespace(indent), end);
            root.InsertBefore(reference, end);
        }

        return Save(doc, ublXml);
    }

    private static XmlDocument Load(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), settings);
            doc.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ArgumentException("The UBL XML is not well-formed: " + ex.Message, nameof(xml), ex);
        }
        if (doc.DocumentElement is null) throw new ArgumentException("The UBL XML has no root element.", nameof(xml));
        return doc;
    }

    /// <summary>
    /// Writes the document in the encoding it was read in. The byte order mark and XML declaration are copied from the
    /// input as they were, since XmlWriter would rewrite them.
    /// </summary>
    private static byte[] Save(XmlDocument doc, byte[] original)
    {
        var declaration = doc.FirstChild as XmlDeclaration;
        var encoding = string.IsNullOrEmpty(declaration?.Encoding) ? Encoding.UTF8 : Encoding.GetEncoding(declaration.Encoding);
        int head = 0;
        if (declaration is not null)
        {
            var end = encoding.GetBytes("?>");
            head = original.AsSpan().IndexOf(end) + end.Length;
            doc.RemoveChild(declaration);
        }
        else if (original.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            head = Encoding.UTF8.Preamble.Length;

        var output = new MemoryStream(original.Length + 1024);
        output.Write(original, 0, head);
        var settings = new XmlWriterSettings
        {
            Encoding = encoding,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.None,
            ConformanceLevel = ConformanceLevel.Document,
        };
        using (var writer = XmlWriter.Create(output, settings))
            doc.Save(writer);
        return output.ToArray();
    }
}
