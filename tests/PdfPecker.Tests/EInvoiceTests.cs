using System.Text;

namespace PdfPecker.Tests;

public class EInvoiceTests
{
    private static byte[] Invoice() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "einvoice", "en16931.xml"));

    private static byte[] WithGuideline(string id)
        => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Invoice()).Replace("urn:cen.eu:en16931:2017</ram:ID>", id + "</ram:ID>"));

    [Fact]
    public void Embeds_the_xml_as_a_pdfa3_associated_file()
    {
        var xml = Invoice();
        var pdf = PdfTestHelpers.ConvertUncompressed("<h1>Invoice INV-2026-0042</h1>", new PdfOptions { EInvoice = new PdfEInvoice(xml) });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = Encoding.UTF8.GetString(pdf);
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", text);
        Assert.Contains("/Names << /EmbeddedFiles << /Names [(factur-x.xml) ", text);
        Assert.Matches(@"/AF \[\d+ 0 R\]", text);
        Assert.Contains("/Type /Filespec /F (factur-x.xml) /UF (factur-x.xml)", text);
        Assert.Contains("/AFRelationship /Alternative", text);
        Assert.Contains("/Type /EmbeddedFile /Subtype /text#2Fxml /Params << /Size " + xml.Length + " /ModDate (D:", text);
        Assert.Contains(Encoding.UTF8.GetString(xml), text);
        // The Factur-X XMP properties and the extension schema that describes them.
        Assert.Contains("<fx:DocumentType>INVOICE</fx:DocumentType>", text);
        Assert.Contains("<fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>", text);
        Assert.Contains("<fx:Version>1.0</fx:Version>", text);
        Assert.Contains("<fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>", text);
        Assert.Contains("<pdfaSchema:namespaceURI>urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#</pdfaSchema:namespaceURI>", text);
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
    }

    [Fact]
    public void Custom_properties_and_the_invoice_share_one_extension_schema_list()
    {
        var options = new PdfOptions { EInvoice = new PdfEInvoice(Invoice()) };
        options.CustomProperties["OrderNo"] = "4711";
        var text = Encoding.UTF8.GetString(PdfTestHelpers.ConvertUncompressed("<p>x</p>", options));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "<pdfaExtension:schemas>"));
        Assert.Contains("<pdfaSchema:prefix>pdfx</pdfaSchema:prefix>", text);
        Assert.Contains("<pdfaSchema:prefix>fx</pdfaSchema:prefix>", text);
    }

    [Theory]
    [InlineData("urn:factur-x.eu:1p0:minimum", EInvoiceProfile.Minimum)]
    [InlineData("urn:factur-x.eu:1p0:basicwl", EInvoiceProfile.BasicWL)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic", EInvoiceProfile.Basic)]
    [InlineData("urn:cen.eu:en16931:2017", EInvoiceProfile.En16931)]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended", EInvoiceProfile.Extended)]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:zugferd.de:2p0:extended", EInvoiceProfile.Extended)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", EInvoiceProfile.XRechnung)]
    public void Reads_the_profile_from_the_guideline(string guideline, EInvoiceProfile expected)
        => Assert.Equal(expected, new PdfEInvoice(WithGuideline(guideline)).Profile);

    [Fact]
    public void Minimum_profile_attaches_the_xml_as_data()
    {
        var text = Encoding.UTF8.GetString(PdfTestHelpers.ConvertUncompressed("<p>x</p>",
            new PdfOptions { EInvoice = new PdfEInvoice(WithGuideline("urn:factur-x.eu:1p0:minimum")) }));
        Assert.Contains("/AFRelationship /Data", text);
        Assert.Contains("<fx:ConformanceLevel>MINIMUM</fx:ConformanceLevel>", text);
    }

    [Fact]
    public void XRechnung_is_embedded_as_xrechnung_xml()
    {
        var text = Encoding.UTF8.GetString(PdfTestHelpers.ConvertUncompressed("<p>x</p>", new PdfOptions
        {
            EInvoice = new PdfEInvoice(WithGuideline("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0")),
        }));
        Assert.Contains("/Type /Filespec /F (xrechnung.xml)", text);
        Assert.Contains("<fx:DocumentFileName>xrechnung.xml</fx:DocumentFileName>", text);
        Assert.Contains("<fx:ConformanceLevel>XRECHNUNG</fx:ConformanceLevel>", text);
    }

    [Fact]
    public void Explicit_profile_and_relationship_win()
    {
        var invoice = new PdfEInvoice(Invoice(), EInvoiceProfile.Extended) { Relationship = EInvoiceRelationship.Source };
        var text = Encoding.UTF8.GetString(PdfTestHelpers.ConvertUncompressed("<p>x</p>", new PdfOptions { EInvoice = invoice }));
        Assert.Contains("/AFRelationship /Source", text);
        Assert.Contains("<fx:ConformanceLevel>EXTENDED</fx:ConformanceLevel>", text);
    }

    [Fact]
    public void Rejects_xml_that_is_not_a_cross_industry_invoice()
    {
        var ubl = "<Invoice xmlns='urn:oasis:names:specification:ubl:schema:xsd:Invoice-2'/>"u8.ToArray();
        Assert.Throws<ArgumentException>(() => new PdfEInvoice(ubl));
        Assert.Throws<ArgumentException>(() => new PdfEInvoice("<not xml"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => new PdfEInvoice(WithGuideline("urn:example:unknown")));
        // A DTD could expand entities without bound: it is refused.
        var dtd = "<!DOCTYPE x [<!ENTITY a 'a'>]><rsm:CrossIndustryInvoice xmlns:rsm='urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100'/>"u8.ToArray();
        Assert.Throws<ArgumentException>(() => new PdfEInvoice(dtd, EInvoiceProfile.En16931));
    }

    [Fact]
    public void Needs_pdfa3_without_encryption()
    {
        var invoice = new PdfEInvoice(Invoice());
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", new PdfOptions { EInvoice = invoice, Conformance = PdfConformance.PdfA2b }));
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", new PdfOptions
        {
            EInvoice = invoice,
            Security = new PdfSecurity { UserPassword = "x" },
        }));
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { EInvoice = invoice, Conformance = PdfConformance.PdfA3b });
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", Encoding.UTF8.GetString(pdf));
    }

    [Fact]
    public void Markdown_can_be_an_e_invoice_too()
    {
        var pdf = MarkdownToPdf.Convert("# Invoice", new PdfOptions { EInvoice = new PdfEInvoice(Invoice()) });
        Assert.Contains("<fx:DocumentType>INVOICE</fx:DocumentType>", Encoding.UTF8.GetString(pdf));
    }

    [Fact]
    public void Reads_the_embedded_invoice_back()
    {
        var xml = Invoice();
        var pdf = HtmlToPdf.Convert("<h1>Invoice</h1>", new PdfOptions { EInvoice = new PdfEInvoice(xml) });
        var invoice = PdfEInvoice.Read(pdf);
        Assert.NotNull(invoice);
        Assert.Equal(xml, invoice.Xml);
        Assert.Equal(EInvoiceProfile.En16931, invoice.Profile);
        Assert.Equal(EInvoiceRelationship.Alternative, invoice.Relationship);
        Assert.Equal("factur-x.xml", invoice.FileName);
    }

    [Fact]
    public void Reads_an_xrechnung_and_its_relationship()
    {
        var xml = WithGuideline("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0");
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { EInvoice = new PdfEInvoice(xml) { Relationship = EInvoiceRelationship.Source } });
        var invoice = PdfEInvoice.Read(pdf)!;
        Assert.Equal(EInvoiceProfile.XRechnung, invoice.Profile);
        Assert.Equal(EInvoiceRelationship.Source, invoice.Relationship);
        Assert.Equal(xml, invoice.Xml);
    }

    [Fact]
    public void Takes_the_profile_from_the_xmp_when_the_guideline_names_none()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { EInvoice = new PdfEInvoice(WithGuideline("urn:example:unknown"), EInvoiceProfile.Extended) });
        Assert.Equal(EInvoiceProfile.Extended, PdfEInvoice.Read(pdf)!.Profile);
    }

    [Fact]
    public void Finds_a_zugferd_2_0_invoice_among_other_attachments()
    {
        var pdf = PdfAttachments.Add(HtmlToPdf.Convert("<p>x</p>"),
            new PdfAttachment("notes.xml", "<notes/>"u8.ToArray()),
            new PdfAttachment("zugferd-invoice.xml", Invoice()) { Relationship = PdfAttachmentRelationship.Data });
        var invoice = PdfEInvoice.Read(pdf)!;
        Assert.Equal(Invoice(), invoice.Xml);
        Assert.Equal(EInvoiceRelationship.Data, invoice.Relationship);
    }

    [Fact]
    public void Reads_an_invoice_attached_to_an_encrypted_pdf()
    {
        var original = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { Security = new PdfSecurity { UserPassword = "pw" } });
        var pdf = PdfAttachments.Add(original, [new PdfAttachment("factur-x.xml", Invoice())], "pw");
        Assert.Throws<PdfPasswordException>(() => PdfEInvoice.Read(pdf));
        Assert.Equal(Invoice(), PdfEInvoice.Read(pdf, "pw")!.Xml);
    }

    [Fact]
    public void Returns_null_without_a_cross_industry_invoice()
    {
        Assert.Null(PdfEInvoice.Read(HtmlToPdf.Convert("<p>x</p>")));
        // A UBL invoice, a ZUGFeRD 1.0-style document and broken XML under the usual names are not Factur-X invoices.
        var pdf = PdfAttachments.Add(HtmlToPdf.Convert("<p>x</p>"),
            new PdfAttachment("factur-x.xml", "<not xml"u8.ToArray()),
            new PdfAttachment("xrechnung.xml", "<Invoice xmlns='urn:oasis:names:specification:ubl:schema:xsd:Invoice-2'/>"u8.ToArray()),
            new PdfAttachment("ZUGFeRD-invoice.xml", "<rsm:CrossIndustryDocument xmlns:rsm='urn:ferd:CrossIndustryDocument:invoice:1p0'/>"u8.ToArray()));
        Assert.Null(PdfEInvoice.Read(pdf));
        Assert.Throws<InvalidDataException>(() => PdfEInvoice.Read("not a pdf"u8.ToArray()));
    }

    [Theory]
    [InlineData("<fx:ConformanceLevel>BASIC WL</fx:ConformanceLevel>", "BASIC WL")]
    [InlineData("<zf:ConformanceLevel> COMFORT </zf:ConformanceLevel>", "COMFORT")]
    [InlineData("<rdf:Description fx:ConformanceLevel=\"EXTENDED\"/>", "EXTENDED")]
    [InlineData("<fx:ConformanceLevel></fx:ConformanceLevel>", null)]
    public void Reads_xmp_properties_in_any_prefix(string xmp, string? expected)
        => Assert.Equal(expected, PdfPecker.Pdf.EmbeddedFiles.XmpProperty(xmp, "ConformanceLevel"));
}
