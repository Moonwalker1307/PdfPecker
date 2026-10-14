using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PdfPecker.Tests;

public class UblInvoiceTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ubl", name));

    private static readonly byte[] Pdf = HtmlToPdf.Convert("<h1>Invoice INV-2026-0042</h1>");

    private static XmlElement Root(byte[] xml)
    {
        var doc = new XmlDocument();
        doc.Load(new MemoryStream(xml));
        return doc.DocumentElement!;
    }

    private static string[] ChildNames(XmlElement root)
        => root.ChildNodes.OfType<XmlElement>().Select(e => e.LocalName).ToArray();

    [Fact]
    public void Attaches_the_pdf_before_the_supplier_in_a_peppol_invoice()
    {
        var xml = UblInvoice.AttachPdf(Fixture("peppol-invoice.xml"), Pdf, "INV-2026-0042.pdf");

        var names = ChildNames(Root(xml));
        int at = Array.IndexOf(names, "AdditionalDocumentReference");
        Assert.Equal("BuyerReference", names[at - 1]);
        Assert.Equal("AccountingSupplierParty", names[at + 1]);

        var ns = new XmlNamespaceManager(new NameTable());
        ns.AddNamespace("cac", UblInvoice.CacNamespace);
        ns.AddNamespace("cbc", UblInvoice.CbcNamespace);
        var reference = Root(xml).SelectSingleNode("cac:AdditionalDocumentReference", ns)!;
        Assert.Equal("INV-2026-0042.pdf", reference.SelectSingleNode("cbc:ID", ns)!.InnerText);
        Assert.Null(reference.SelectSingleNode("cbc:DocumentDescription", ns));
        var binary = (XmlElement)reference.SelectSingleNode("cac:Attachment/cbc:EmbeddedDocumentBinaryObject", ns)!;
        Assert.Equal("application/pdf", binary.GetAttribute("mimeCode"));
        Assert.Equal("INV-2026-0042.pdf", binary.GetAttribute("filename"));
        Assert.Equal(Pdf, Convert.FromBase64String(binary.InnerText));
    }

    [Fact]
    public void Keeps_the_rest_of_the_xml_byte_for_byte_and_matches_its_indentation()
    {
        var original = Fixture("peppol-invoice.xml");
        var text = Encoding.UTF8.GetString(UblInvoice.AttachPdf(original, Pdf, "invoice.pdf", "1", "Invoice as PDF"));

        var block = Regex.Match(text, @"  <cac:AdditionalDocumentReference>.*?</cac:AdditionalDocumentReference>\n", RegexOptions.Singleline);
        Assert.True(block.Success);
        Assert.Equal(Encoding.UTF8.GetString(original), text.Remove(block.Index, block.Length));
        Assert.Matches(
            "^  <cac:AdditionalDocumentReference>\n    <cbc:ID>1</cbc:ID>\n    <cbc:DocumentDescription>Invoice as PDF</cbc:DocumentDescription>\n"
            + "    <cac:Attachment>\n      <cbc:EmbeddedDocumentBinaryObject mimeCode=\"application/pdf\" filename=\"invoice.pdf\">[A-Za-z0-9+/=]+</cbc:EmbeddedDocumentBinaryObject>\n"
            + "    </cac:Attachment>\n  </cac:AdditionalDocumentReference>\n$",
            block.Value);
    }

    [Fact]
    public void Goes_after_existing_attachments()
    {
        var once = UblInvoice.AttachPdf(Fixture("peppol-invoice.xml"), Pdf, "first.pdf");
        var twice = UblInvoice.AttachPdf(once, Pdf, "second.pdf");

        var references = Root(twice).ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "AdditionalDocumentReference").ToArray();
        Assert.Equal(["first.pdf", "second.pdf"], references.Select(r => ((XmlElement)r.GetElementsByTagName("EmbeddedDocumentBinaryObject", UblInvoice.CbcNamespace)[0]!).GetAttribute("filename")));
        Assert.Equal("AccountingSupplierParty", references[1].NextSibling!.LocalName);
    }

    [Fact]
    public void Attaches_to_a_credit_note_after_its_billing_reference()
    {
        var names = ChildNames(Root(UblInvoice.AttachPdf(Fixture("peppol-creditnote.xml"), Pdf, "CN-2026-0007.pdf")));
        int at = Array.IndexOf(names, "AdditionalDocumentReference");
        Assert.Equal("BillingReference", names[at - 1]);
        Assert.Equal("AccountingSupplierParty", names[at + 1]);
    }

    [Fact]
    public void Credit_notes_put_statement_references_after_the_attachment()
    {
        var text = Encoding.UTF8.GetString(Fixture("peppol-creditnote.xml")).Replace(
            "  <cac:AccountingSupplierParty>",
            "  <cac:StatementDocumentReference>\n    <cbc:ID>ST-1</cbc:ID>\n  </cac:StatementDocumentReference>\n  <cac:AccountingSupplierParty>");
        var names = ChildNames(Root(UblInvoice.AttachPdf(Encoding.UTF8.GetBytes(text), Pdf, "cn.pdf")));
        int at = Array.IndexOf(names, "AdditionalDocumentReference");
        Assert.Equal("BillingReference", names[at - 1]);
        Assert.Equal("StatementDocumentReference", names[at + 1]);
    }

    [Fact]
    public void Ubl_2_0_gets_the_description_as_document_type()
    {
        // OIOUBL 2.1 is UBL 2.0, which has no DocumentDescription.
        var text = Encoding.UTF8.GetString(Fixture("peppol-invoice.xml")).Replace(
            "  <cbc:CustomizationID>", "  <cbc:UBLVersionID>2.0</cbc:UBLVersionID>\n  <cbc:CustomizationID>");
        var result = Encoding.UTF8.GetString(UblInvoice.AttachPdf(Encoding.UTF8.GetBytes(text), Pdf, "a.pdf", description: "Faktura"));
        Assert.Contains("<cbc:DocumentType>Faktura</cbc:DocumentType>", result);
        Assert.DoesNotContain("DocumentDescription", result);
    }

    [Fact]
    public void Oioubl_2_1_always_gets_a_document_type()
    {
        // OIOUBL rule F-LIB092 needs DocumentType or DocumentTypeCode on every AdditionalDocumentReference.
        var result = Encoding.UTF8.GetString(UblInvoice.AttachPdf(Fixture("oioubl-invoice.xml"), Pdf, "a.pdf"));
        Assert.Contains("\t</cac:OrderReference>\n\t<cac:AdditionalDocumentReference>\n\t\t<cbc:ID>a.pdf</cbc:ID>\n\t\t<cbc:DocumentType>PDF</cbc:DocumentType>\n"
            + "\t\t<cac:Attachment>\n\t\t\t<cbc:EmbeddedDocumentBinaryObject mimeCode=\"application/pdf\" filename=\"a.pdf\">", result);
        Assert.Contains("</cac:Attachment>\n\t</cac:AdditionalDocumentReference>\n\t<cac:AccountingSupplierParty>", result);
    }

    [Fact]
    public void Uses_the_documents_own_prefixes_and_works_without_indentation()
    {
        var xml = "<inv:Invoice xmlns:inv=\"" + UblInvoice.InvoiceNamespace + "\" xmlns:a=\"" + UblInvoice.CacNamespace + "\" xmlns:b=\""
            + UblInvoice.CbcNamespace + "\"><b:ID>1</b:ID><a:AccountingSupplierParty/></inv:Invoice>";
        var result = Encoding.UTF8.GetString(UblInvoice.AttachPdf(Encoding.UTF8.GetBytes(xml), Pdf, "x.pdf"));
        Assert.StartsWith("<inv:Invoice", result);
        Assert.Contains("<b:ID>1</b:ID><a:AdditionalDocumentReference><b:ID>x.pdf</b:ID><a:Attachment><b:EmbeddedDocumentBinaryObject mimeCode=\"application/pdf\" filename=\"x.pdf\">", result);
        Assert.Contains("</a:AdditionalDocumentReference><a:AccountingSupplierParty />", result.Replace("<a:AccountingSupplierParty/>", "<a:AccountingSupplierParty />"));
    }

    [Fact]
    public void Keeps_a_byte_order_mark_only_when_there_was_one()
    {
        var plain = Fixture("peppol-invoice.xml");
        var withBom = Encoding.UTF8.Preamble.ToArray().Concat(plain).ToArray();
        Assert.False(UblInvoice.AttachPdf(plain, Pdf, "a.pdf").AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.True(UblInvoice.AttachPdf(withBom, Pdf, "a.pdf").AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void Rejects_xml_that_is_not_ubl_and_attachments_that_are_not_pdfs()
    {
        var cii = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "einvoice", "en16931.xml"));
        Assert.Throws<ArgumentException>(() => UblInvoice.AttachPdf(cii, Pdf, "a.pdf"));
        Assert.Throws<ArgumentException>(() => UblInvoice.AttachPdf("<Invoice"u8.ToArray(), Pdf, "a.pdf"));
        Assert.Throws<ArgumentException>(() => UblInvoice.AttachPdf(Fixture("peppol-invoice.xml"), "PNG"u8.ToArray(), "a.pdf"));
        Assert.Throws<ArgumentException>(() => UblInvoice.AttachPdf(Fixture("peppol-invoice.xml"), Pdf, " "));
        Assert.Throws<ArgumentException>(() => UblInvoice.AttachPdf(Fixture("peppol-invoice.xml"), Pdf, "a.pdf", id: ""));
    }

    [Fact]
    public void Refuses_a_dtd()
    {
        var xml = "<!DOCTYPE Invoice [<!ENTITY x \"y\">]><Invoice xmlns=\"" + UblInvoice.InvoiceNamespace + "\">&x;</Invoice>";
        Assert.Throws<ArgumentException>(() => UblInvoice.AttachPdf(Encoding.UTF8.GetBytes(xml), Pdf, "a.pdf"));
    }
}
