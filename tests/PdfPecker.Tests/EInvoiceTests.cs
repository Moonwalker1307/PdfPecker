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
}
