using System.Text;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>PDF/X-4 output (ISO 15930-7): the output intent, page boxes, colour spaces, metadata and the options it refuses.</summary>
public class PdfXTests
{
    /// <summary>An ICC profile header of the given device class and colour space, with no tags: enough for the checks.</summary>
    internal static byte[] Profile(string deviceClass = "prtr", string space = "CMYK")
    {
        var p = new byte[132];
        p[3] = 132;
        Encoding.ASCII.GetBytes(deviceClass).CopyTo(p, 12);
        Encoding.ASCII.GetBytes(space).CopyTo(p, 16);
        Encoding.ASCII.GetBytes("Lab ").CopyTo(p, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(p, 36);
        return p;
    }

    private static PdfOptions Options() => new()
    {
        PdfX = new PdfXOptions(Profile(), "FOGRA39") { OutputConditionInfo = "Coated FOGRA39 (ISO 12647-2:2004)" },
        CreationDate = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
        Compress = false,
    };

    [Fact]
    public void The_file_is_pdf_1_6_with_a_pdfx_output_intent()
    {
        var text = Latin1(HtmlToPdf.Convert("<title>Flyer</title><p>Print me</p>", Options()));
        Assert.StartsWith("%PDF-1.6", text);
        Assert.Contains("/OutputIntents [", text);
        Assert.Contains("/S /GTS_PDFX /OutputConditionIdentifier (FOGRA39) /Info (Coated FOGRA39 \\(ISO 12647-2:2004\\))", text);
        Assert.Contains("/RegistryName (http://www.color.org) /DestOutputProfile", text);
        Assert.Contains("/N 4", text);
        Assert.Contains("/GTS_PDFXVersion (PDF/X-4) /Trapped /False", text);
        Assert.Contains("<pdfxid:GTS_PDFXVersion>PDF/X-4</pdfxid:GTS_PDFXVersion>", text);
        Assert.Contains("<pdf:Trapped>False</pdf:Trapped>", text);
        Assert.Contains("<xmpMM:VersionID>1</xmpMM:VersionID>", text);
        Assert.Contains("<rdf:li xml:lang=\"x-default\">Flyer</rdf:li>", text);
        Assert.DoesNotContain("pdfaid:part", text);
        AssertStructurallyValid(HtmlToPdf.Convert("<p>x</p>", Options()));
    }

    [Fact]
    public void Every_page_has_a_trim_box()
    {
        var text = Latin1(HtmlToPdf.Convert("<style>@page { size: 100pt 200pt; margin: 0 }</style><p>A</p>", Options()));
        Assert.Contains("/MediaBox [0 0 100 200] /TrimBox [0 0 100 200]", text);

        var bled = Latin1(HtmlToPdf.Convert("<style>@page { size: 100pt 200pt; margin: 0; bleed: 3pt }</style><p>A</p>", Options()));
        Assert.Contains("/MediaBox [-3 -3 103 203] /BleedBox [-3 -3 103 203] /TrimBox [0 0 100 200]", bled);
    }

    [Fact]
    public void Srgb_is_tagged_with_the_srgb_profile()
    {
        var text = Latin1(HtmlToPdf.Convert($"""
            <title>Colours</title>
            <p style="color: #c00; opacity: 0.5">Red</p>
            <div style="height: 20px; background: linear-gradient(red, blue)"></div>
            <img src="{DataUri(RgbPng())}">
            """, Options()));
        Assert.Contains("/ColorSpace << /DefaultRGB [/ICCBased ", text);
        Assert.DoesNotContain("/DeviceRGB", text);
        Assert.Contains("/ColorSpace [/ICCBased ", text);
        Assert.Contains("/N 3", text);
    }

    [Fact]
    public void Links_and_fillable_fields_are_left_out()
    {
        var text = Latin1(HtmlToPdf.Convert("<a href='https://example.com'>Site</a><input name='a' value='b'><div data-pdf-signature='Signer' style='height:40px'></div>", Options()));
        Assert.DoesNotContain("/Annot", text);
        Assert.DoesNotContain("/AcroForm", text);
        Assert.Contains("Site", PdfText.Extract(HtmlToPdf.Convert("<a href='https://example.com'>Site</a>", Options())));
    }

    [Fact]
    public void Standard_fonts_are_embedded()
    {
        var text = Latin1(HtmlToPdf.Convert("<p style='font-family: Helvetica'>Embedded</p>", Options()));
        Assert.Contains("/FontFile2", text);
    }

    [Fact]
    public void Untitled_documents_get_a_title()
        => Assert.Contains("/Title (Untitled)", Latin1(HtmlToPdf.Convert("<p>x</p>", Options())));

    [Fact]
    public void Reproducible_files_are_byte_identical()
        => Assert.Equal(HtmlToPdf.Convert("<p>x</p>", Options()), HtmlToPdf.Convert("<p>x</p>", Options()));

    [Fact]
    public void Trapped_and_registry_can_be_set()
    {
        var options = Options();
        options.PdfX = new PdfXOptions(Profile(), "Press 7 coated") { RegistryName = null, Trapped = true };
        var text = Latin1(HtmlToPdf.Convert("<p>x</p>", options));
        Assert.DoesNotContain("/RegistryName", text);
        Assert.Contains("/Trapped /True", text);
        Assert.Contains("/Info (Press 7 coated)", text);
    }

    [Theory]
    [InlineData("mntr", "RGB ")]
    [InlineData("prtr", "RGB ")]
    [InlineData("scnr", "CMYK")]
    public void Profiles_that_are_not_cmyk_press_profiles_are_refused(string deviceClass, string space)
        => Assert.Throws<ArgumentException>(() => new PdfXOptions(Profile(deviceClass, space), "X"));

    [Fact]
    public void Broken_profiles_and_names_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new PdfXOptions(new byte[64], "FOGRA39"));
        var cut = Profile();
        cut[2] = 1; // declares 388 bytes
        Assert.Throws<ArgumentException>(() => new PdfXOptions(cut, "FOGRA39"));
        Assert.Throws<ArgumentException>(() => new PdfXOptions(Profile(), " "));
        Assert.Throws<ArgumentNullException>(() => new PdfXOptions(null!, "FOGRA39"));
    }

    [Fact]
    public void Options_pdfx_does_not_allow_are_refused()
    {
        void Refused(Action<PdfOptions> set)
        {
            var options = Options();
            set(options);
            Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", options));
        }
        Refused(o => o.Conformance = PdfConformance.PdfA2b);
        Refused(o => o.Accessibility = PdfAccessibility.PdfUA1);
        Refused(o => o.Security = new PdfSecurity { OwnerPassword = "owner-password" });
        Refused(o => o.Attachments.Add(new PdfAttachment("a.txt", new byte[] { 1 })));
    }

    [Fact]
    public void The_renderer_draws_tagged_srgb_as_plain_srgb()
    {
        const string html = "<style>@page{size:20px 20px;margin:0}</style><div style='height:10px;background:#c03'></div>"
            + "<div style='height:10px;background:linear-gradient(#0c3,#0c3)'></div>";
        var plain = PdfRenderer.Open(HtmlToPdf.Convert(html)).RenderPageExact(0, 15, 15, new ImageOptions(), CancellationToken.None).Premultiplied;
        var pdfx = PdfRenderer.Open(HtmlToPdf.Convert(html, Options())).RenderPageExact(0, 15, 15, new ImageOptions(), CancellationToken.None).Premultiplied;
        Assert.Equal(plain[3 * 15 + 7], pdfx[3 * 15 + 7]);
        Assert.Equal(plain[11 * 15 + 7], pdfx[11 * 15 + 7]);
    }
}
