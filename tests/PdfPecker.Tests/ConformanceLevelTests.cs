using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>The PDF/A and PDF/UA levels: their header version, XMP identification and the combinations that are refused.</summary>
public class ConformanceLevelTests
{
    private const string Html = "<html lang='en'><head><title>Report</title></head><body><h1>Report</h1><p>Hello <a href='#end'>world</a>.</p>"
                                + "<ul><li>One</li><li>Two</li></ul><p id='end'>End</p></body></html>";

    private static string Convert(PdfConformance conformance, PdfAccessibility accessibility = PdfAccessibility.None, PdfEInvoice? invoice = null)
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(Html, new PdfOptions
        {
            Conformance = conformance, Accessibility = accessibility, EInvoice = invoice, Author = "Ann",
            CustomProperties = new Dictionary<string, string> { ["OrderNo"] = "42" },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        return Encoding.UTF8.GetString(pdf);
    }

    private static PdfEInvoice Invoice()
        => new(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "einvoice", "en16931.xml")));

    [Theory]
    [InlineData(PdfConformance.PdfA2b, 2, "B")]
    [InlineData(PdfConformance.PdfA2u, 2, "U")]
    [InlineData(PdfConformance.PdfA3b, 3, "B")]
    [InlineData(PdfConformance.PdfA3u, 3, "U")]
    public void PdfA2_and_3_are_pdf17_with_part_and_conformance(PdfConformance conformance, int part, string level)
    {
        var text = Convert(conformance);
        Assert.StartsWith("%PDF-1.7\n", text);
        Assert.Contains($"<pdfaid:part>{part}</pdfaid:part>\n<pdfaid:conformance>{level}</pdfaid:conformance>", text);
        Assert.DoesNotContain("pdfaid:rev", text);
        // The information dictionary agrees with the XMP, and the custom property has an extension schema.
        Assert.Contains("/Author (Ann)", text);
        Assert.Contains("<pdfaSchema:prefix>pdfx</pdfaSchema:prefix>", text);
        Assert.Matches(@"trailer\n<< /Size \d+ /Root 1 0 R /Info \d+ 0 R", text);
    }

    [Fact]
    public void PdfA4_is_pdf20_with_a_revision_and_no_conformance_letter_or_info_dictionary()
    {
        var text = Convert(PdfConformance.PdfA4);
        Assert.StartsWith("%PDF-2.0\n", text);
        Assert.Contains("<pdfaid:part>4</pdfaid:part>\n<pdfaid:rev>2020</pdfaid:rev>\n", text);
        Assert.DoesNotContain("pdfaid:conformance", text);
        // PDF 2.0 deprecates the information dictionary: the properties are in the XMP only.
        Assert.Matches(@"trailer\n<< /Size \d+ /Root 1 0 R /ID", text);
        Assert.DoesNotContain("/Author (Ann)", text);
        Assert.Contains("<dc:creator><rdf:Seq><rdf:li>Ann</rdf:li></rdf:Seq></dc:creator>", text);
        Assert.Contains("<pdfx:OrderNo>42</pdfx:OrderNo>", text);
        // PDF/A-4 needs no extension schemas.
        Assert.DoesNotContain("pdfaExtension", text);
        Assert.Contains("/OutputIntents [", text);
        // Untagged: plain destinations.
        Assert.Contains("/Dest [", text);
    }

    [Fact]
    public void PdfA4f_carries_an_einvoice()
    {
        var text = Convert(PdfConformance.PdfA4f, invoice: Invoice());
        Assert.StartsWith("%PDF-2.0\n", text);
        Assert.Contains("<pdfaid:part>4</pdfaid:part>\n<pdfaid:rev>2020</pdfaid:rev>\n<pdfaid:conformance>F</pdfaid:conformance>", text);
        Assert.Contains("/AFRelationship /Alternative", text);
        Assert.Contains("<fx:DocumentType>INVOICE</fx:DocumentType>", text);
    }

    [Fact]
    public void PdfA4f_needs_an_attachment()
    {
        var error = Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", new PdfOptions { Conformance = PdfConformance.PdfA4f }));
        Assert.Contains("attachments", error.Message);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA3u, 3)]
    [InlineData(PdfConformance.PdfA4f, 4)]
    public void An_einvoice_accepts_the_levels_that_allow_attachments(PdfConformance conformance, int part)
    {
        Assert.Contains($"<pdfaid:part>{part}</pdfaid:part>", Convert(conformance, invoice: Invoice()));
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", new PdfOptions { EInvoice = Invoice(), Conformance = PdfConformance.PdfA4 }));
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", new PdfOptions { EInvoice = Invoice(), Conformance = PdfConformance.PdfA2u }));
    }

    [Fact]
    public void PdfUA2_is_pdf20_with_the_pdf2_structure_namespace()
    {
        var text = Convert(PdfConformance.None, PdfAccessibility.PdfUA2);
        Assert.StartsWith("%PDF-2.0\n", text);
        Assert.Contains("<pdfuaid:part>2</pdfuaid:part>\n<pdfuaid:rev>2024</pdfuaid:rev>", text);
        Assert.DoesNotContain("pdfaid:part", text);
        Assert.Contains("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Report</rdf:li>", text);
        Assert.Contains("/Lang (en)", text);
        Assert.Contains("/ViewerPreferences << /DisplayDocTitle true >>", text);
        var ns = Regex.Match(text, @"(\d+) 0 obj\n<< /Type /Namespace /NS \(http://iso.org/pdf2/ssn\) >>");
        Assert.True(ns.Success);
        Assert.Contains("/Namespaces [" + ns.Groups[1].Value + " 0 R]", text);
        Assert.Matches(@"/Type /StructElem /S /Document /P \d+ 0 R /NS " + ns.Groups[1].Value + " 0 R", text);
        // Links and bookmarks go to structure destinations; the list says how its labels number it.
        Assert.Matches(@"/A << /S /GoTo /D \[\d+ 0 R /XYZ [^\]]+\] /SD \[\d+ 0 R /XYZ [^\]]+\] >>", text);
        Assert.DoesNotContain("/Dest [", text);
        Assert.Contains("/A << /O /List /ListNumbering /Disc >>", text);
    }

    [Fact]
    public void PdfUA2_role_maps_the_pdf17_types_that_pdf20_dropped()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<title>Q</title><blockquote><p>Said <code>x</code></p></blockquote>",
            new PdfOptions { Accessibility = PdfAccessibility.PdfUA2 });
        var text = PdfTestHelpers.Latin1(pdf);
        var ns2 = Regex.Match(text, @"(\d+) 0 obj\n<< /Type /Namespace /NS \(http://iso.org/pdf2/ssn\) >>").Groups[1].Value;
        var ns17 = Regex.Match(text, @"(\d+) 0 obj\n<< /Type /Namespace /NS \(http://iso.org/pdf/ssn\) /RoleMapNS <<([^>]*)>> >>");
        Assert.True(ns17.Success);
        Assert.Contains($"/BlockQuote [/Div {ns2} 0 R]", ns17.Groups[2].Value);
        Assert.Contains($"/Code [/Span {ns2} 0 R]", ns17.Groups[2].Value);
        Assert.Matches(@"/S /BlockQuote /P \d+ 0 R /NS " + ns17.Groups[1].Value + " 0 R", text);
    }

    [Fact]
    public void PdfA4_combines_with_PdfUA2()
    {
        var text = Convert(PdfConformance.PdfA4, PdfAccessibility.PdfUA2);
        Assert.StartsWith("%PDF-2.0\n", text);
        Assert.Contains("<pdfaid:part>4</pdfaid:part>", text);
        Assert.Contains("<pdfuaid:part>2</pdfuaid:part>", text);
        Assert.DoesNotContain("pdfaExtension", text);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b, PdfAccessibility.PdfUA2)]
    [InlineData(PdfConformance.PdfA3u, PdfAccessibility.PdfUA2)]
    [InlineData(PdfConformance.PdfA4, PdfAccessibility.PdfUA1)]
    public void PdfUA_and_PdfA_must_share_a_pdf_version(PdfConformance conformance, PdfAccessibility accessibility)
    {
        var error = Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<title>T</title><p>x</p>",
            new PdfOptions { Conformance = conformance, Accessibility = accessibility }));
        Assert.Contains("PDF 2.0", error.Message);
    }

    [Fact]
    public void PdfUA1_and_PdfA2u_stay_pdf17_with_an_info_dictionary()
    {
        var text = Convert(PdfConformance.PdfA2u, PdfAccessibility.PdfUA1);
        Assert.StartsWith("%PDF-1.7\n", text);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>\n</rdf:Description>", text);
        Assert.DoesNotContain("pdfuaid:rev", text);
        Assert.DoesNotContain("/Namespaces", text);
        Assert.Contains("/Dest [", text);
    }

    [Fact]
    public void Pdf20_output_reads_back_and_renders()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions { Conformance = PdfConformance.PdfA4, Accessibility = PdfAccessibility.PdfUA2 });
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
        Assert.NotNull(PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 30 }));
        Assert.NotEmpty(PdfMerger.Merge(pdf, pdf));
    }

    [Fact]
    public void PdfUA2_encrypts_the_namespace_names()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Accessibility = PdfAccessibility.PdfUA2,
            Security = new PdfSecurity { UserPassword = "x", Permissions = PdfPermissions.ExtractForAccessibility },
        });
        Assert.StartsWith("%PDF-2.0\n", PdfTestHelpers.Latin1(pdf));
        Assert.DoesNotContain("/Extensions", PdfTestHelpers.Latin1(pdf));
        var file = PdfFile.Open(pdf, "x");
        var spaces = file.GetArray(file.GetDict(file.Catalog, "StructTreeRoot"), "Namespaces")!;
        Assert.Equal("http://iso.org/pdf2/ssn", ((PdfString)file.Resolve(((PdfDict)file.Resolve(spaces.Items[0])!)["NS"])!).Text);
    }

    [Fact]
    public void Svg_text_in_PdfA_never_shows_notdef()
    {
        // ★ is in no bundled font: the glyph is skipped, not shown as .notdef (CID 0).
        var pdf = PdfTestHelpers.ConvertUncompressed("<svg width='200' height='40'><text x='5' y='20'>A ★ B</text></svg>",
            new PdfOptions { Conformance = PdfConformance.PdfA2u });
        var content = PdfTestHelpers.AllContent(pdf);
        var shown = Regex.Match(content, @"Tm \[(.*)\] TJ ET");
        Assert.True(shown.Success, content);
        // The missing glyph is a move by its width instead.
        Assert.Matches(@"<[0-9A-F]+>-[\d.]+<", shown.Groups[1].Value);
        var cids = Regex.Matches(shown.Groups[1].Value, "<([0-9A-F]+)>").SelectMany(m => m.Groups[1].Value.Chunk(4).Select(c => new string(c)));
        Assert.DoesNotContain("0000", cids);
    }
}
