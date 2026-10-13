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
            Creator = "MyApp",
            Producer = "MyApp PDF",
            CustomProperties = { ["RunId"] = "42", ["CompanyId"] = "Ærø A/S", ["Run Date"] = "2026-10-11" },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.Contains("/Producer (MyApp PDF)", text);
        Assert.Contains("/Keywords (payslip, 2026)", text);
        Assert.Contains("/Creator (MyApp)", text);
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
            Creator = "MyApp",
            CustomProperties = { ["RunId"] = "42", ["1st key"] = "<v>" },
        });
        var text = Encoding.UTF8.GetString(pdf);
        var xmp = text.Substring(text.IndexOf("<x:xmpmeta", StringComparison.Ordinal));
        xmp = xmp.Substring(0, xmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12);
        Assert.Contains("<pdf:Keywords>a &amp; b</pdf:Keywords>", xmp);
        Assert.Contains("<xmp:CreatorTool>MyApp</xmp:CreatorTool>", xmp);
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
        // Not a PDF, or a PDF without pages: refused, never a crash or an empty document.
        Assert.Throws<InvalidDataException>(() => PdfMerger.Merge(Encoding.ASCII.GetBytes("hello, not a pdf")));
        var empty = BuildPdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>");
        Assert.Throws<InvalidDataException>(() => PdfMerger.Merge(empty));
        // An empty input among others is fine.
        Assert.Equal(1, PdfRenderer.Open(PdfMerger.Merge(empty, HtmlToPdf.Convert(Page("#ff0000")))).PageCount);
    }

    // ------------------------------------------------------------------ real-world inputs

    /// <summary>A small third-party-style file: a red square, with the size and resources inherited from the page tree.</summary>
    private static byte[] RedSquare() => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] /Resources << /ExtGState << /G 5 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
        "<< /Length 33 >>\nstream\n/G gs 1 0 0 rg 20 20 160 160 re f\nendstream",
        "<< /Type /ExtGState /CA 1 >>");

    public static IEnumerable<object[]> DamagedFiles() => new[]
    {
        new object[] { "offsets shifted", (Func<byte[], byte[]>)(d => Insert(d, 9, "%" + new string('x', 40) + "\n")) },
        new object[] { "startxref wrong", (Func<byte[], byte[]>)(d => Replace(d, "startxref\n", "startxref\n9") ) },
        new object[] { "xref table missing", (Func<byte[], byte[]>)(d => Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(d)[..Encoding.Latin1.GetString(d).LastIndexOf("xref", StringComparison.Ordinal)] + "%%EOF\n")) },
        new object[] { "xref subsection starting at 1", (Func<byte[], byte[]>)(d => Replace(d, "xref\n0 6\n0000000000 65535 f \n", "xref\n1 5\n")) },
        new object[] { "junk before the header", (Func<byte[], byte[]>)(d => Insert(d, 0, "Received: from mail gateway\r\n\r\n")) },
        new object[] { "CR line ends", (Func<byte[], byte[]>)(d => Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(d).Replace("\n", "\r"))) },
        new object[] { "trailer cut off", (Func<byte[], byte[]>)(d => d[..(Encoding.Latin1.GetString(d).LastIndexOf("trailer", StringComparison.Ordinal))]) },
    };

    private static byte[] Insert(byte[] d, int at, string text) => d[..at].Concat(Encoding.Latin1.GetBytes(text)).Concat(d[at..]).ToArray();

    private static byte[] Replace(byte[] d, string from, string to)
    {
        var s = Encoding.Latin1.GetString(d);
        Assert.Contains(from, s);
        return Encoding.Latin1.GetBytes(s.Replace(from, to));
    }

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public void Merge_repairs_damaged_files(string damage, Func<byte[], byte[]> mangle)
    {
        var merged = PdfMerger.Merge(mangle(RedSquare()), HtmlToPdf.Convert(Page("#0000ff")));
        PdfTestHelpers.AssertStructurallyValid(merged);
        var r = PdfRenderer.Open(merged);
        Assert.Equal(2, r.PageCount);
        Assert.Equal((200d, 200d), r.GetPageSize(0));
        var img = r.RenderPage(0, new ImageOptions { Dpi = 72 });
        int o = (100 * img.Width + 100) * 4;
        Assert.True(img.Rgba[o] > 200 && img.Rgba[o + 2] < 50, $"{damage}: page 1 is not red");
    }

    [Fact]
    public void Merge_uses_the_newest_revision_of_an_incrementally_updated_file()
    {
        // The update replaces the page's content stream (red becomes green) and adds an annotation.
        var original = RedSquare();
        var text = Encoding.Latin1.GetString(original);
        int prevXref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        var update = new StringBuilder();
        int o4 = original.Length + update.Length;
        update.Append("4 0 obj\n<< /Length 25 >>\nstream\n0 1 0 rg 0 0 200 200 re f\nendstream\nendobj\n");
        int o3 = original.Length + update.Length;
        update.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [6 0 R] >>\nendobj\n");
        int o6 = original.Length + update.Length;
        update.Append("6 0 obj\n<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (Approved) >>\nendobj\n");
        int xref = original.Length + update.Length;
        update.Append($"xref\n3 2\n{o3:D10} 00000 n \n{o4:D10} 00000 n \n6 1\n{o6:D10} 00000 n \n");
        update.Append($"trailer\n<< /Size 7 /Root 1 0 R /Prev {prevXref} >>\nstartxref\n{xref}\n%%EOF\n");
        var updated = original.Concat(Encoding.Latin1.GetBytes(update.ToString())).ToArray();

        var merged = PdfMerger.Merge(updated, updated);
        PdfTestHelpers.AssertStructurallyValid(merged);
        var c = RenderCenter(merged, 1);
        Assert.True(c.G > 200 && c.R < 50, $"page: {c}");
        Assert.Equal(2, Regex.Matches(PdfTestHelpers.Latin1(merged), @"/Subtype /Text").Count);
    }

    [Fact]
    public void Merge_keeps_form_fields_and_renames_clashing_ones()
    {
        var form = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] /DR << /Font << /Helv 7 0 R >> >> /DA (/Helv 0 Tf 0 g) /NeedAppearances true /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [5 0 R 6 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Mette) /Rect [10 10 200 30] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (signature) /Rect [10 50 200 70] /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var merged = PdfMerger.Merge(form, form, form);
        PdfTestHelpers.AssertStructurallyValid(merged);
        var text = PdfTestHelpers.Latin1(merged);
        Assert.Matches(@"/AcroForm << /Fields \[\d+ 0 R \d+ 0 R \d+ 0 R \]", text);
        // Same-named fields would share one value, so the later copies are renamed.
        Assert.Contains("/T <6e616d65>", text); // name
        Assert.Contains("/T <6e616d655f32>", text); // name_2
        Assert.Contains("/T <6e616d655f33>", text); // name_3
        Assert.Equal(3, Regex.Matches(text, @"/V <4d65747465>").Count); // Mette
        Assert.Matches(@"/DR << /Font << /Helv \d+ 0 R", text);
        Assert.Contains("/NeedAppearances true", text);
        // Signature fields and flags are not carried over.
        Assert.DoesNotContain("/Sig", text);
    }

    [Fact]
    public void Merge_writes_identical_images_and_fonts_once()
    {
        var html = $"<p style='font-family:\"Liberation Sans\"'>Payslip</p><img src='data:image/jpeg;base64,{Convert.ToBase64String(Fixture("rgb.jpg"))}'>";
        var one = HtmlToPdf.Convert(html, new PdfOptions { EmbedStandardFonts = true });
        var merged = PdfMerger.Merge(Enumerable.Repeat(one, 20), null);
        PdfTestHelpers.AssertStructurallyValid(merged);
        var text = PdfTestHelpers.Latin1(merged);
        Assert.Equal(Regex.Matches(PdfTestHelpers.Latin1(one), "/Subtype /Image").Count, Regex.Matches(text, "/Subtype /Image").Count);
        Assert.Equal(Regex.Matches(PdfTestHelpers.Latin1(one), "/Length1 ").Count, Regex.Matches(text, "/Length1 ").Count);
        Assert.True(merged.Length < one.Length * 5, $"{merged.Length} bytes for 20 copies of {one.Length}");
        Assert.Equal(20, PdfRenderer.Open(merged).PageCount);
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

    // ------------------------------------------------------------------ merging streams

    /// <summary>A stream that can only be read forwards, like a request body.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => base.Position; set => throw new NotSupportedException(); }
    }

    /// <summary>A stream that can only be written forwards, like a response body; it records how much was written when.</summary>
    private sealed class WriteOnlyStream : Stream
    {
        public readonly MemoryStream Data = new();
        public int Writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { Writes++; Data.Write(buffer, offset, count); }
    }

    [Fact]
    public void Merge_reads_streams_and_writes_to_a_stream()
    {
        var red = HtmlToPdf.Convert(Page("#ff0000"));
        var blue = HtmlToPdf.Convert(Page("#0000ff"));
        var output = new WriteOnlyStream();
        PdfMerger.Merge(new Stream[] { new MemoryStream(red), new ForwardOnlyStream(blue) }, output);
        var merged = output.Data.ToArray();
        PdfTestHelpers.AssertStructurallyValid(merged);
        Assert.Equal((255, 0, 0), RenderCenter(merged, 0));
        Assert.Equal((0, 0, 255), RenderCenter(merged, 1));
    }

    [Fact]
    public void Merge_reads_a_stream_from_its_current_position()
    {
        var red = HtmlToPdf.Convert(Page("#ff0000"));
        var input = new MemoryStream();
        input.Write("junk before the PDF"u8);
        long start = input.Position;
        input.Write(red);
        input.Position = start;
        using var output = new MemoryStream();
        PdfMerger.Merge(new[] { input }, output);
        Assert.Equal(1, PdfRenderer.Open(output.ToArray()).PageCount);
    }

    [Fact]
    public async Task MergeAsync_reads_inputs_one_at_a_time_and_writes_as_it_goes()
    {
        var pdfs = new[] { HtmlToPdf.Convert(Page("#ff0000")), HtmlToPdf.Convert(Page("#00ff00")), HtmlToPdf.Convert(Page("#0000ff")) };
        var output = new WriteOnlyStream();
        var writtenWhenOpened = new List<long>();
        IEnumerable<Stream> Inputs()
        {
            foreach (var pdf in pdfs)
            {
                writtenWhenOpened.Add(output.Data.Length);
                yield return new ForwardOnlyStream(pdf);
            }
        }
        await PdfMerger.MergeAsync(Inputs(), output);
        var merged = output.Data.ToArray();
        PdfTestHelpers.AssertStructurallyValid(merged);
        Assert.Equal(new[] { (255, 0, 0), (0, 255, 0), (0, 0, 255) }, Enumerable.Range(0, 3).Select(i => RenderCenter(merged, i)));
        // Each input's pages were already written out when the next input was asked for.
        Assert.Equal(0, writtenWhenOpened[0]);
        Assert.True(writtenWhenOpened[1] > 0 && writtenWhenOpened[2] > writtenWhenOpened[1], string.Join(", ", writtenWhenOpened));
    }

    [Fact]
    public void Merge_to_a_stream_keeps_offsets_right_across_flushes()
    {
        // Pages of random text make an output of over a megabyte, written out in parts.
        var rng = new Random(7);
        var inputs = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var words = Enumerable.Range(0, 30_000).Select(_ => new string(Enumerable.Range(0, 6).Select(_ => (char)('a' + rng.Next(26))).ToArray()));
            inputs.Add(HtmlToPdf.Convert($"<h1>Part {i}</h1><p>{string.Join(' ', words)}</p>"));
        }
        var output = new WriteOnlyStream();
        PdfMerger.Merge(inputs.Select(b => (Stream)new MemoryStream(b)), output);
        var merged = output.Data.ToArray();
        Assert.True(output.Writes >= 2, $"{output.Writes} writes of {merged.Length} bytes");
        PdfTestHelpers.AssertStructurallyValid(merged);
        Assert.Equal(inputs.Sum(b => PdfRenderer.Open(b).PageCount), PdfRenderer.Open(merged).PageCount);
        // The same as merging in memory, apart from the dates and file id.
        static string Stable(byte[] pdf) => Regex.Replace(PdfTestHelpers.Latin1(pdf), @"/(ModDate|CreationDate) \([^)]*\)|/ID \[<[0-9A-Fa-f]+><[0-9A-Fa-f]+>\]", "");
        Assert.Equal(Stable(PdfMerger.Merge(inputs, null)), Stable(merged));
    }

    [Fact]
    public void Merge_chains_bookmarks_across_inputs_without_any()
    {
        string Doc(string name) => $"<html><body><h1>{name}</h1></body></html>";
        var plain = HtmlToPdf.Convert(Page("#ff0000"));
        using var output = new MemoryStream();
        PdfMerger.Merge(new[] { HtmlToPdf.Convert(Doc("A")), plain, plain, HtmlToPdf.Convert(Doc("B")), plain }.Select(b => (Stream)new MemoryStream(b)), output);
        var text = PdfTestHelpers.Latin1(output.ToArray());
        PdfTestHelpers.AssertStructurallyValid(output.ToArray());
        var outlines = Regex.Match(text, @"<< /Type /Outlines /First (\d+) 0 R /Last (\d+) 0 R /Count 2 >>");
        Assert.True(outlines.Success, "merged outline root");
        string Obj(string id) => Regex.Match(text, $@"\n{id} 0 obj\n(.*?)\nendobj", RegexOptions.Singleline).Groups[1].Value;
        var first = Obj(outlines.Groups[1].Value);
        var last = Obj(outlines.Groups[2].Value);
        Assert.Contains($"/Next {outlines.Groups[2].Value} 0 R", first);
        Assert.Contains($"/Prev {outlines.Groups[1].Value} 0 R", last);
        Assert.DoesNotContain("/Next", last);
        Assert.Contains(Hex("B"), last);
    }

    [Fact]
    public async Task Merge_from_streams_checks_its_arguments()
    {
        var a = HtmlToPdf.Convert("<p>x</p>");
        Assert.Throws<ArgumentException>(() => PdfMerger.Merge(Array.Empty<Stream>(), new MemoryStream()));
        Assert.Throws<ArgumentException>(() => PdfMerger.Merge(new Stream[] { new MemoryStream(a), null! }, new MemoryStream()));
        Assert.Throws<ArgumentException>(() => PdfMerger.Merge(new[] { new MemoryStream(a) }, new MemoryStream(a, writable: false)));
        Assert.Throws<ArgumentNullException>(() => PdfMerger.Merge(new[] { new MemoryStream(a) }, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => PdfMerger.MergeAsync(Array.Empty<Stream>(), new MemoryStream()));
        await Assert.ThrowsAsync<InvalidDataException>(() => PdfMerger.MergeAsync(new[] { new MemoryStream("not a pdf"u8.ToArray()) }, new MemoryStream()));
    }

    [Fact]
    public void MergeFiles_can_overwrite_an_input_and_leaves_nothing_behind_on_failure()
    {
        var dir = Directory.CreateTempSubdirectory("pdfpecker-merge");
        try
        {
            var a = Path.Combine(dir.FullName, "a.pdf");
            var bad = Path.Combine(dir.FullName, "bad.pdf");
            File.WriteAllBytes(a, HtmlToPdf.Convert("<p>a</p>"));
            File.WriteAllText(bad, "not a pdf");
            PdfMerger.MergeFiles(a, new[] { a, a });
            Assert.Equal(2, PdfRenderer.OpenFile(a).PageCount);

            var output = Path.Combine(dir.FullName, "out.pdf");
            Assert.Throws<InvalidDataException>(() => PdfMerger.MergeFiles(output, new[] { a, bad }));
            Assert.Equal(new[] { "a.pdf", "bad.pdf" }, dir.GetFiles().Select(f => f.Name).Order());
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
