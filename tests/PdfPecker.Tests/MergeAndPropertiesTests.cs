using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

public class MergeAndPropertiesTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Page(string color, string heading = "") =>
        $"<html><body style='margin:0'>{(heading.Length > 0 ? $"<h1>{heading}</h1>" : "")}<div style='background:{color};width:300pt;height:300pt'></div></body></html>";

    private static (int R, int G, int B) Center(RenderedImage img)
    {
        int o = (img.Width / 10 * img.Width + img.Width / 10) * 4; // 60pt in from the top left, inside the 300pt box
        return (img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2]);
    }

    private static (int R, int G, int B) RenderCenter(byte[] pdf, int page)
        => Center(PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 20 }));

    // ------------------------------------------------------------------ document properties

    [Fact]
    public void Custom_properties_keywords_creator_and_producer_go_into_the_info_dictionary()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<p>x</p>", new PdfOptions
        {
            Title = "Lønseddel",
            Keywords = "payslip, 2026",
            Creator = "DaPrint",
            Producer = "DaPrint PDF",
            CustomProperties = { ["RunId"] = "42", ["CompanyId"] = "Ærø A/S", ["Run Date"] = "2026-10-11" },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.Contains("/Producer (DaPrint PDF)", text);
        Assert.Contains("/Keywords (payslip, 2026)", text);
        Assert.Contains("/Creator (DaPrint)", text);
        Assert.Contains("/RunId (42)", text);
        Assert.Contains("/Run#20Date (2026-10-11)", text);
        Assert.DoesNotContain("PdfPecker", text);
        // Non-ASCII values are UTF-16 text strings.
        var companyHex = "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("Ærø A/S"));
        Assert.Contains("/CompanyId <" + companyHex + ">", text);
    }

    [Fact]
    public void Producer_defaults_to_PdfPecker()
    {
        var text = PdfTestHelpers.Latin1(PdfTestHelpers.ConvertUncompressed("<p>x</p>"));
        Assert.Contains("/Producer (PdfPecker)", text);
        Assert.DoesNotContain("/Keywords", text);
        Assert.DoesNotContain("/Creator", text);
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Producer")]
    [InlineData("CreationDate")]
    [InlineData("")]
    public void Custom_properties_cannot_use_standard_names(string name)
    {
        var options = new PdfOptions { CustomProperties = { [name] = "x" } };
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", options));
    }

    [Fact]
    public void PdfA_xmp_carries_keywords_creator_and_custom_properties_with_an_extension_schema()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<p>x</p>", new PdfOptions
        {
            Conformance = PdfConformance.PdfA2b,
            Keywords = "a & b",
            Creator = "DaPrint",
            CustomProperties = { ["RunId"] = "42", ["1st key"] = "<v>" },
        });
        var text = Encoding.UTF8.GetString(pdf);
        var xmp = text.Substring(text.IndexOf("<x:xmpmeta", StringComparison.Ordinal));
        xmp = xmp.Substring(0, xmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12);
        Assert.Contains("<pdf:Keywords>a &amp; b</pdf:Keywords>", xmp);
        Assert.Contains("<xmp:CreatorTool>DaPrint</xmp:CreatorTool>", xmp);
        Assert.Contains("<pdf:Producer>PdfPecker</pdf:Producer>", xmp);
        Assert.Contains("xmlns:pdfx=\"http://ns.adobe.com/pdfx/1.3/\"", xmp);
        Assert.Contains("<pdfx:RunId>42</pdfx:RunId>", xmp);
        Assert.Contains("<pdfx:_1st_key>&lt;v&gt;</pdfx:_1st_key>", xmp);
        Assert.Contains("<pdfaSchema:namespaceURI>http://ns.adobe.com/pdfx/1.3/</pdfaSchema:namespaceURI>", xmp);
        Assert.Contains("<pdfaProperty:name>RunId</pdfaProperty:name>", xmp);
        Assert.Contains("<pdfaProperty:name>_1st_key</pdfaProperty:name>", xmp);
        // The packet is well-formed XML.
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xmp);
        // The info dictionary keeps the original name.
        Assert.Contains("/1st#20key (<v>)", text);
    }

    [Fact]
    public void PdfA_xmp_has_no_extension_schema_without_custom_properties()
    {
        var text = Encoding.UTF8.GetString(PdfTestHelpers.ConvertUncompressed("<p>x</p>", new PdfOptions { Conformance = PdfConformance.PdfA3b }));
        Assert.DoesNotContain("pdfaExtension", text);
        Assert.DoesNotContain("pdfx", text);
    }

    // ------------------------------------------------------------------ merging

    [Fact]
    public void Merge_puts_the_pages_of_every_input_in_order()
    {
        var twoPages = HtmlToPdf.Convert("<html><body style='margin:0'><div style='background:#00ff00;width:300pt;height:300pt'></div>"
            + "<div style='page-break-before:always;background:#0000ff;width:300pt;height:300pt'></div></body></html>");
        var merged = PdfMerger.Merge(HtmlToPdf.Convert(Page("#ff0000")), twoPages);
        PdfTestHelpers.AssertStructurallyValid(merged);
        Assert.Equal(3, PdfRenderer.Open(merged).PageCount);
        var c0 = RenderCenter(merged, 0);
        var c1 = RenderCenter(merged, 1);
        var c2 = RenderCenter(merged, 2);
        Assert.True(c0.R > 200 && c0.G < 50, $"page 1: {c0}");
        Assert.True(c1.G > 200 && c1.R < 50, $"page 2: {c1}");
        Assert.True(c2.B > 200 && c2.R < 50, $"page 3: {c2}");
        Assert.Contains("/Producer (PdfPecker)", PdfTestHelpers.Latin1(merged));
    }

    [Fact]
    public void Merge_keeps_fonts_and_text()
    {
        var a = HtmlToPdf.Convert("<p>Første dokument</p>", new PdfOptions { EmbedStandardFonts = true });
        var b = HtmlToPdf.Convert("<p>Second document</p>");
        var merged = PdfMerger.Merge(a, b);
        var r = PdfRenderer.Open(merged);
        Assert.Equal(2, r.PageCount);
        // Each page draws ink, i.e. its fonts were copied along.
        for (int i = 0; i < 2; i++)
        {
            var img = r.RenderPage(i, new ImageOptions { Dpi = 72 });
            bool ink = false;
            for (int p = 0; p < img.Rgba.Length && !ink; p += 4) ink = img.Rgba[p] < 128;
            Assert.True(ink, $"page {i + 1} is blank");
        }
    }

    [Fact]
    public void Merge_renders_third_party_pdfs_exactly_as_the_originals()
    {
        foreach (var name in new[] { "features.pdf", "objstm-cairo.pdf" })
        {
            var original = Fixture(name);
            var merged = PdfMerger.Merge(original, original);
            var ro = PdfRenderer.Open(original);
            var rm = PdfRenderer.Open(merged);
            Assert.Equal(ro.PageCount * 2, rm.PageCount);
            for (int i = 0; i < rm.PageCount; i++)
            {
                var a = ro.RenderPage(i % ro.PageCount, new ImageOptions { Dpi = 36 });
                var b = rm.RenderPage(i, new ImageOptions { Dpi = 36 });
                Assert.True(a.Rgba.AsSpan().SequenceEqual(b.Rgba), $"{name} page {i + 1} differs");
            }
        }
    }

    [Fact]
    public void Merge_chains_the_bookmarks_and_keeps_links_working()
    {
        string Doc(string name) => $"<html><body><p><a href='#{name}2'>jump</a></p><h1>{name} 1</h1>"
            + $"<h1 id='{name}2' style='page-break-before:always'>{name} 2</h1></body></html>";
        var merged = PdfMerger.Merge(HtmlToPdf.Convert(Doc("A")), HtmlToPdf.Convert(Doc("B")));
        PdfTestHelpers.AssertStructurallyValid(merged);
        var text = PdfTestHelpers.Latin1(merged);
        var outlines = Regex.Match(text, @"(\d+) 0 obj\n<< /Type /Outlines /First (\d+) 0 R /Last (\d+) 0 R /Count 4 >>");
        Assert.True(outlines.Success, "merged outline root");
        // Walk the top level: A 1, A 2, B 1, B 2.
        var titles = new List<string>();
        int id = int.Parse(outlines.Groups[2].Value);
        for (int guard = 0; id != 0 && guard < 10; guard++)
        {
            var obj = Regex.Match(text, $@"\n{id} 0 obj\n(.*?)\nendobj", RegexOptions.Singleline).Groups[1].Value;
            Assert.Contains($"/Parent {outlines.Groups[1].Value} 0 R", obj);
            titles.Add(Regex.Match(obj, @"/Title (\([^)]*\)|<[0-9a-f]+>)").Groups[1].Value);
            var next = Regex.Match(obj, @"/Next (\d+) 0 R");
            id = next.Success ? int.Parse(next.Groups[1].Value) : 0;
        }
        Assert.Equal(4, titles.Count);
        Assert.Equal(new[] { Hex("A 1"), Hex("A 2"), Hex("B 1"), Hex("B 2") }, titles);

        // Every destination points at a page of the merged tree.
        var pageIds = Regex.Matches(text, @"(\d+) 0 obj\n<< /Type /Page ").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(4, pageIds.Count);
        var dests = Regex.Matches(text, @"/Dest \[(\d+) 0 R").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(6, dests.Count); // 4 bookmarks + 2 links
        Assert.All(dests, d => Assert.Contains(d, pageIds));
        // The second document's link jumps to its own second page (the merged fourth page).
        Assert.Contains(pageIds[3], dests);
    }

    private static string Hex(string s) => "<" + Convert.ToHexString(Encoding.Latin1.GetBytes(s)).ToLowerInvariant() + ">";

    [Fact]
    public void Merge_without_bookmarks_has_no_outline()
    {
        var a = HtmlToPdf.Convert("<h1>A</h1>");
        var merged = PdfMerger.Merge(new[] { a, a }, new PdfMergeOptions { Bookmarks = false });
        Assert.DoesNotContain("/Outlines", PdfTestHelpers.Latin1(merged));
    }

    [Fact]
    public void Merge_takes_the_first_inputs_properties_and_applies_options()
    {
        var a = HtmlToPdf.Convert("<p>a</p>", new PdfOptions { Title = "First", Author = "Ann", CustomProperties = { ["RunId"] = "1", ["Keep"] = "yes" } });
        var b = HtmlToPdf.Convert("<p>b</p>", new PdfOptions { Title = "Second", CustomProperties = { ["Other"] = "no" } });
        var merged = PdfMerger.Merge(new[] { a, b }, new PdfMergeOptions { Author = "Bob", CustomProperties = { ["RunId"] = "2" } });
        var text = PdfTestHelpers.Latin1(merged);
        Assert.Contains("/Title (First)", text);
        Assert.Contains("/Author (Bob)", text);
        Assert.Contains("/Keep (yes)", text);
        Assert.Contains("/RunId (2)", text);
        Assert.DoesNotContain("/RunId (1)", text);
        Assert.DoesNotContain("/Other", text);
        Assert.DoesNotContain("Second", text);
    }

    [Fact]
    public void Merge_opens_encrypted_inputs_with_their_password_and_writes_an_unencrypted_file()
    {
        var secret = HtmlToPdf.Convert(Page("#0000ff"), new PdfOptions { Security = new PdfSecurity { UserPassword = "pw", OwnerPassword = "owner" } });
        var plain = HtmlToPdf.Convert(Page("#ff0000"));
        var ex = Assert.Throws<PdfPasswordException>(() => PdfMerger.Merge(plain, secret));
        Assert.Contains("PDF 2", ex.Message);
        Assert.Throws<PdfPasswordException>(() => PdfMerger.Merge(new[] { plain, secret }, new PdfMergeOptions { Passwords = new string?[] { null, "wrong" } }));

        var merged = PdfMerger.Merge(new[] { plain, secret }, new PdfMergeOptions { Passwords = new string?[] { null, "pw" } });
        Assert.DoesNotContain("/Encrypt", PdfTestHelpers.Latin1(merged));
        var c = RenderCenter(merged, 1);
        Assert.True(c.B > 200 && c.R < 50, $"decrypted page: {c}");
    }

    [Fact]
    public void Merge_opens_a_permissions_only_pdf_without_a_password()
    {
        var restricted = HtmlToPdf.Convert(Page("#00ff00"), new PdfOptions { Security = new PdfSecurity { OwnerPassword = "owner" } });
        var merged = PdfMerger.Merge(restricted);
        var c = RenderCenter(merged, 0);
        Assert.True(c.G > 200 && c.R < 50, $"page: {c}");
    }

    [Fact]
    public void Merge_drops_signatures()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Merge Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var signed = HtmlToPdf.Convert(Page("#ff0000"), new PdfOptions
        {
            Signature = new PdfSignatureOptions(cert) { Appearance = new PdfSignatureAppearance { Text = "Signed" } },
        });
        Assert.Contains("/ByteRange", PdfTestHelpers.Latin1(signed));
        var merged = PdfMerger.Merge(signed, HtmlToPdf.Convert(Page("#0000ff")));
        PdfTestHelpers.AssertStructurallyValid(merged);
        var text = PdfTestHelpers.Latin1(merged);
        Assert.DoesNotContain("/ByteRange", text);
        Assert.DoesNotContain("/Sig", text);
        Assert.DoesNotContain("/AcroForm", text);
        Assert.Equal(2, PdfRenderer.Open(merged).PageCount);
    }

    [Fact]
    public void Merge_resolves_named_destinations()
    {
        // A hand-written file: a link on page 1 to the named destination "two", defined in the catalog's name tree.
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 6 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 100 100] /P 3 0 R /A << /S /GoTo /D (two) >> >>",
            "<< /Names [(two) [4 0 R /Fit]] >>");
        var merged = PdfMerger.Merge(pdf, pdf);
        PdfTestHelpers.AssertStructurallyValid(merged);
        var text = PdfTestHelpers.Latin1(merged);
        var pageIds = Regex.Matches(text, @"(\d+) 0 obj\n<< /Type /Page ").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(4, pageIds.Count);
        // Inherited MediaBox is written on each page.
        Assert.Equal(4, Regex.Matches(text, @"/MediaBox \[0 0 200 200\]").Count);
        var gotos = Regex.Matches(text, @"/S /GoTo /D \[(\d+) 0 R /Fit\]").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { pageIds[1], pageIds[3] }, gotos);
        // The annotation's /P points at its new page.
        Assert.Equal(2, Regex.Matches(text, $@"/P ({pageIds[0]}|{pageIds[2]}) 0 R").Count);
    }

    private static byte[] BuildPdf(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void Merge_enforces_the_page_limit()
    {
        var a = HtmlToPdf.Convert("<div style='height:3000pt'></div>");
        int pages = PdfRenderer.Open(a).PageCount;
        Assert.True(pages > 2);
        Assert.Throws<PdfLimitException>(() => PdfMerger.Merge(new[] { a, a }, new PdfMergeOptions { MaxPages = pages * 2 - 1 }));
        Assert.Equal(pages * 2, PdfRenderer.Open(PdfMerger.Merge(new[] { a, a }, new PdfMergeOptions { MaxPages = pages * 2 })).PageCount);
    }

    [Fact]
    public void Merge_refuses_garbage_and_null_input()
    {
        Assert.Throws<ArgumentException>(() => PdfMerger.Merge());
        Assert.Throws<ArgumentException>(() => PdfMerger.Merge(new byte[][] { null! }));
        // Not a PDF: either refused or merged as an empty document, never a crash.
        try
        {
            var merged = PdfMerger.Merge(Encoding.ASCII.GetBytes("hello, not a pdf"));
            Assert.Equal(0, PdfRenderer.Open(merged).PageCount);
        }
        catch (InvalidDataException) { }
    }

    [Fact]
    public void Merge_handles_reference_cycles_and_self_referencing_pages()
    {
        // A page tree whose kid is its own parent, and a resource dictionary that refers to itself.
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [2 0 R 3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /Resources 4 0 R /MediaBox [0 0 100 100] >>",
            "<< /Self 4 0 R /Loop [4 0 R 3 0 R] >>");
        var merged = PdfMerger.Merge(pdf);
        PdfTestHelpers.AssertStructurallyValid(merged);
        Assert.Equal(1, PdfRenderer.Open(merged).PageCount);
    }

    [Fact]
    public void Merge_is_cancellable()
    {
        var a = HtmlToPdf.Convert("<p>x</p>");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfMerger.Merge(new[] { a }, null, cts.Token));
    }

    [Fact]
    public void MergeFiles_writes_the_merged_file()
    {
        var dir = Directory.CreateTempSubdirectory("pdfpecker-merge");
        try
        {
            var a = Path.Combine(dir.FullName, "a.pdf");
            File.WriteAllBytes(a, HtmlToPdf.Convert("<p>a</p>"));
            var output = Path.Combine(dir.FullName, "out.pdf");
            PdfMerger.MergeFiles(output, new[] { a, a, a });
            Assert.Equal(3, PdfRenderer.OpenFile(output).PageCount);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
