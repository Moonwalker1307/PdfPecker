using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Small inputs that used to make the converter run for hours, exhaust memory or crash: each must now finish quickly
/// (the cancellation token fails the test instead of hanging the run) or stop at a limit.
/// </summary>
/// <remarks>
/// Several of these run up to a work budget, such as twenty million drawing operations, against a wall-clock bound.
/// On a busy CI runner that also tests three target frameworks at once, competing with the rest of the suite made them
/// miss that bound, so they run in their own collection after the parallel tests instead of alongside them.
/// </remarks>
[Collection(HostileInputCollection.Name)]
public class HostileInputTests
{
    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    private static byte[] Bounded(string html, PdfOptions? options = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return HtmlToPdf.Convert(html, options, cts.Token);
    }

    private static T Bounded<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.Result;
    }

    [Fact]
    public void Custom_property_expansion_is_capped()
    {
        // Each variable uses the previous one four times: 4^40 characters if expanded.
        var sb = new StringBuilder("<style>:root{--a0:x;");
        for (int i = 1; i < 40; i++) sb.Append($"--a{i}:var(--a{i - 1}) var(--a{i - 1}) var(--a{i - 1}) var(--a{i - 1});");
        sb.Append("} p{font-family:var(--a39)}</style><p>text</p>");
        AssertStructurallyValid(Bounded(sb.ToString()));
    }

    [Fact]
    public void Many_custom_properties_on_many_elements_stay_linear()
    {
        var sb = new StringBuilder("<style>:root{");
        for (int i = 0; i < 20000; i++) sb.Append($"--v{i}:{i}px;");
        sb.Append("} *{--x:1} p{margin-left:var(--v19999)}</style>").Append(Repeat("<p>x</p>", 20000));
        AssertStructurallyValid(Bounded(sb.ToString()));
    }

    [Fact]
    public void Custom_properties_still_inherit_and_override()
    {
        var doc = Styled("<style>:root{--c:red} .b{--c:blue} p{color:var(--c)} .n{--c:initial}</style>"
            + "<p id=a>a</p><div class=b><p id=b>b</p><div class=n><p id=n style='color:var(--c, lime)'>n</p></div></div>");
        Assert.Equal(0xFFFF0000u, ById(doc, "a").Style.Color);
        Assert.Equal(0xFF0000FFu, ById(doc, "b").Style.Color);
        Assert.Equal(0xFF00FF00u, ById(doc, "n").Style.Color);
    }

    [Fact]
    public void Long_descendant_selectors_do_not_backtrack_exponentially()
    {
        var html = "<style>" + Repeat("div ", 300) + "p{color:red}</style>" + Repeat("<div>", 250) + Repeat("<p>x</p>", 500);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Long_sibling_selectors_do_not_backtrack_exponentially()
    {
        var html = "<style>" + Repeat("b ~ ", 60) + "i{color:red}</style>" + Repeat("<b></b>", 3000) + "<i>x</i>";
        AssertStructurallyValid(Bounded(html));
    }

    [Theory]
    // .a > .b .c: the nearest .b is not a child of .a, the outer one is.
    [InlineData(".a > .b .c", "<div class=a><div class=b><div><div class=b><p id=t class=c>x</p></div></div></div></div>", true)]
    [InlineData(".a > .b .c", "<div class=a><div><div class=b><p id=t class=c>x</p></div></div></div>", false)]
    [InlineData(".a .b > .c", "<div class=a><div class=b><div class=b><p id=t class=c>x</p></div></div></div>", true)]
    [InlineData(".a .b > .c", "<div class=b><div class=a><p id=t class=c>x</p></div></div>", false)]
    [InlineData(".x ~ .y + .z", "<span class=x></span><span class=y></span><span></span><span class=y></span><span id=t class=z></span>", true)]
    [InlineData(".x + .y ~ .z", "<span class=y></span><span class=x></span><span id=t class=z></span>", false)]
    [InlineData(".x + .y ~ .z", "<span class=w></span><span class=x></span><span class=y></span><span></span><span id=t class=z></span>", true)]
    [InlineData(".s ~ .a .c", "<div class=s></div><div class=a><div><p id=t class=c>x</p></div></div>", true)]
    [InlineData(".s ~ .a .c", "<div class=a><div class=s></div><p id=t class=c>x</p></div>", false)]
    public void Combinators_still_match_like_browsers(string selector, string body, bool matches)
    {
        var doc = Styled($"<style>{selector} {{ color: red }}</style>{body}");
        Assert.Equal(matches, ById(doc, "t").Style.Color == 0xFFFF0000u);
    }

    [Fact]
    public void Deeply_nested_selectors_and_media_blocks_are_cheap()
    {
        var html = "<style>" + Repeat(":not(", 50000) + "p" + Repeat(")", 50000) + "{color:red}"
            + Repeat("@media print{", 100000) + "</style><p>x</p>";
        AssertStructurallyValid(Bounded(html));
        AssertStructurallyValid(Bounded("<style>" + Repeat("@font-face{}", 200000) + "</style>x"));
    }

    [Fact]
    public void Extreme_nth_child_arguments_do_not_throw()
    {
        var doc = Styled("<style>p:nth-child(-n+-2147483647){color:red} p:nth-last-child(-1n-2147483648){color:blue}</style><p id=t>x</p>");
        Assert.NotEqual(0u, ById(doc, "t").Style.Color);
    }

    [Fact]
    public void Root_matches_without_scanning_every_top_level_element()
    {
        AssertStructurallyValid(Bounded("<style>:root{color:red}</style>" + Repeat("<b>x</b>", 50000)));
    }

    [Fact]
    public void Long_unbreakable_words_break_in_linear_time()
    {
        var pdf = Bounded("<p style='width:30px; overflow-wrap:anywhere'>" + Repeat("x", 300000) + "</p>");
        AssertStructurallyValid(pdf);
        // The word is cut into lines that each hold a few characters.
        Assert.True(PageCount(pdf) > 10);
    }

    [Fact]
    public void Broken_words_keep_every_character()
    {
        var pdf = ConvertUncompressed("<p style='width:40pt;font-family:Courier;font-size:10pt;overflow-wrap:break-word'>ABCDEFGHIJKLMNOPQRSTUVWXYZ</p>");
        var text = string.Concat(System.Text.RegularExpressions.Regex.Matches(Latin1(pdf), @"\(([A-Z]+)\) ?Tj").Select(m => m.Groups[1].Value));
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZ", text);
    }

    [Fact]
    public void Page_count_is_limited()
    {
        Assert.Throws<PdfLimitException>(() => Bounded("<div style='height:100000000px'>x</div>", new PdfOptions { MaxPages = 1000 }));
        Assert.Throws<PdfLimitException>(() => Bounded("<p style='margin-top:1e30px'>x</p>"));
        Assert.Throws<PdfLimitException>(() => Bounded("<div style='border-radius:1e30px;border:1e30px solid red'>x</div>"));
        Assert.Throws<PdfLimitException>(() => Bounded(Repeat("<div style='page-break-before:always'>x</div>", 50), new PdfOptions { MaxPages = 20 }));
        Assert.Equal(20, PageCount(Bounded(Repeat("<div style='page-break-before:always'>x</div>", 20), new PdfOptions { MaxPages = 20 })));
    }

    [Fact]
    public void Cancellation_stops_a_conversion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => HtmlToPdf.Convert("<p>x</p>", null, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => HtmlToPdf.ConvertAsync("<p>x</p>", null, cts.Token).GetAwaiter().GetResult());
    }

    [Fact]
    public void Svg_use_references_cannot_expand_exponentially()
    {
        var sb = new StringBuilder("<svg width=100 height=100><defs><g id=g0><rect width=1 height=1/></g>");
        for (int i = 1; i < 30; i++)
        {
            sb.Append($"<g id=g{i}>");
            for (int k = 0; k < 10; k++) sb.Append($"<use href='#g{i - 1}'/>");
            sb.Append("</g>");
        }
        sb.Append("</defs><use href='#g29'/></svg>");
        AssertStructurallyValid(Bounded(sb.ToString()));
    }

    [Fact]
    public void Svg_filter_work_is_bounded()
    {
        // Ten-thousand-fold kernels on four million pixels: tens of billions of operations for each primitive.
        string kernel = string.Join(" ", Enumerable.Repeat("1", 64 * 64));
        var sb = new StringBuilder("<body style='margin:0'><svg width=1000 height=1000><filter id=f x=0 y=0 width=1000 height=1000 filterUnits=userSpaceOnUse>");
        for (int i = 0; i < 20; i++) sb.Append($"<feConvolveMatrix order=64 kernelMatrix='{kernel}'/><feMorphology radius=200/>");
        sb.Append("</filter><rect width=1000 height=1000 fill='#ff0000' filter='url(#f)'/></svg></body>");
        var pdf = Bounded(() => Bounded(sb.ToString(), new PdfOptions { Compress = false }));
        AssertStructurallyValid(pdf);
        // A filter beyond the budget is left out, and the element drawn as it would be without it.
        var text = Latin1(pdf);
        Assert.DoesNotContain("/Subtype /Image", text);
        Assert.Contains("1 0 0 rg", text);
    }

    [Fact]
    public void Svg_morphology_with_a_wide_radius_runs_in_linear_time()
    {
        var sb = new StringBuilder("<body style='margin:0'><svg width=1000 height=1000><filter id=f x=0 y=0 width=1000 height=1000 filterUnits=userSpaceOnUse>");
        for (int i = 0; i < 8; i++) sb.Append($"<feMorphology operator='{(i % 2 == 0 ? "dilate" : "erode")}' radius=200/>");
        sb.Append("</filter><rect x=300 y=300 width=400 height=400 fill='#ff0000' filter='url(#f)'/></svg></body>");
        var pdf = Bounded(() => Bounded(sb.ToString(), new PdfOptions { Compress = false }));
        AssertStructurallyValid(pdf);
        Assert.Contains("/Subtype /Image", Latin1(pdf));
    }

    [Fact]
    public void Text_split_by_many_ignored_tags_is_merged_in_linear_time()
    {
        AssertStructurallyValid(Bounded("<p>" + Repeat("a</em>", 200000) + "</p>"));
        var doc = HtmlParser.Parse("a<!--x-->b</i>c");
        Assert.Equal("abc", Assert.IsType<TextNode>(Assert.Single(doc.Children)).Text);
    }

    [Fact]
    public void Tabs_in_preformatted_text_do_not_multiply_memory()
    {
        var pdf = ConvertUncompressed("<pre>a\tb\t\tc</pre>");
        Assert.Contains("(a       b               c) Tj", Latin1(pdf));
        AssertStructurallyValid(Bounded("<pre>" + Repeat("\t", 1000000) + "</pre>"));
    }

    [Fact]
    public void Markdown_nesting_is_capped()
    {
        var html = Bounded(() => MarkdownToHtml.ConvertDocument(Repeat("- ", 200000) + "x"));
        Assert.Equal(Markdown.BlockParser.MaxNesting, CountOf(html, "<ul>"));
        Bounded(() => MarkdownToHtml.ConvertDocument(Repeat(">", 200000) + " x"));
        // Ordinary nesting is untouched.
        Assert.Equal(3, CountOf(MarkdownToHtml.ConvertDocument("- a\n  - b\n    - c\n"), "<ul>"));
    }

    private static int CountOf(string s, string part)
    {
        int n = 0;
        for (int i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Png_decompression_bombs_are_dropped()
    {
        // A 16x16 RGBA image whose pixel data inflates to 64 MB.
        var png = Png(16, 16, colorType: 6, new byte[64 * 1024 * 1024]);
        Assert.Throws<FormatException>(() => Images.PdfImage.FromBytes(png));
        var pdf = Bounded($"<p>before</p><img src='data:image/png;base64,{Convert.ToBase64String(png)}'><p>after</p>");
        AssertStructurallyValid(pdf);
        Assert.DoesNotContain("/Subtype /Image", Latin1(pdf));
    }

    [Fact]
    public void Png_with_huge_dimensions_is_dropped()
    {
        var png = Png(100000, 100000, colorType: 6, new byte[16]);
        Assert.Null(Images.PdfImage.FromBytes(png));
    }

    [Fact]
    public void Woff_tables_cannot_claim_gigabytes()
    {
        var woff = new byte[44 + 20 + 8];
        BinaryPrimitives.WriteUInt32BigEndian(woff, 0x774F4646); // wOFF
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(8), (uint)woff.Length);
        BinaryPrimitives.WriteUInt16BigEndian(woff.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(44), 0x676C7966); // glyf
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(48), 64); // offset
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(52), 8); // compressed length
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(56), 0x7FFFFFFF); // claimed original length
        Assert.Throws<FormatException>(() => FontProgram.Load(woff));
        var html = $"<style>@font-face{{font-family:f;src:url(data:font/woff;base64,{Convert.ToBase64String(woff)})}}</style><p style='font-family:f'>x</p>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Woff2_tables_cannot_claim_gigabytes()
    {
        var woff2 = new byte[48 + 7 + 8];
        "wOF2"u8.CopyTo(woff2);
        BinaryPrimitives.WriteUInt32BigEndian(woff2.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(woff2.AsSpan(8), (uint)woff2.Length);
        BinaryPrimitives.WriteUInt16BigEndian(woff2.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(woff2.AsSpan(20), 8); // compressed size
        woff2[48] = 7; // post
        new byte[] { 0x87, 0xFF, 0xFF, 0xFF, 0x7F }.CopyTo(woff2, 49); // claims 2 GB
        Assert.Throws<FormatException>(() => FontProgram.Load(woff2));
        var html = $"<style>@font-face{{font-family:f;src:url(data:font/woff2;base64,{Convert.ToBase64String(woff2)})}}</style><p style='font-family:f'>x</p>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Damaged_woff2_glyph_data_fails_cleanly()
    {
        // Damage the decompressed table data (where the glyf reconstruction reads its streams), not just the
        // Brotli bytes, which would only fail decompression.
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.woff2"));
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(12));
        int compressedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(20));
        int at = 48;
        for (int i = 0; i < numTables; i++)
        {
            int flags = file[at++];
            if ((flags & 63) == 63) at += 4;
            int numbers = (flags & 63) is 10 or 11 ? (flags >> 6 == 3 ? 1 : 2) : (flags >> 6 == 0 ? 1 : 2);
            for (int k = 0; k < numbers; k++) while ((file[at++] & 0x80) != 0) { }
        }
        var header = file.AsSpan(0, at).ToArray();
        var stream = new MemoryStream();
        using (var b = new BrotliStream(new MemoryStream(file, at, compressedSize), CompressionMode.Decompress)) b.CopyTo(stream);
        var clean = stream.ToArray();

        var random = new Random(1234);
        for (int round = 0; round < 300; round++)
        {
            var data = (byte[])clean.Clone();
            for (int k = 0, n = random.Next(1, 8); k < n; k++) data[random.Next(data.Length)] = (byte)random.Next(256);
            var packed = new MemoryStream();
            using (var b = new BrotliStream(packed, CompressionLevel.Fastest, leaveOpen: true)) b.Write(data);
            var damaged = header.Concat(packed.ToArray()).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(20), (uint)packed.Length);
            BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(8), (uint)damaged.Length);
            try { Bounded(() => FontProgram.Load(damaged)); }
            catch (AggregateException ex) when (ex.InnerException is FormatException) { }
        }
    }

    [Fact]
    public void Pdf_with_unterminated_streams_opens_in_linear_time()
    {
        var sb = new StringBuilder("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj 2 0 obj << /Type /Pages /Kids [] /Count 0 >> endobj\n");
        for (int i = 3; sb.Length < 4 * 1024 * 1024; i++) sb.Append(i).Append(" 0 obj << /Length 999999999 >> stream\nxx\n");
        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        var renderer = Bounded(() => PdfRenderer.Open(bytes));
        Assert.Equal(0, renderer.PageCount);
    }

    [Fact]
    public void Nested_form_xobjects_cannot_multiply_drawing_work()
    {
        // Twelve forms, each drawing the previous one ten times: 10^11 rectangles from under 2 KB.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /F11 16 0 R >> >> >>",
            Stream("<<", "/F11 Do"),
        };
        for (int k = 0; k < 12; k++)
        {
            var body = k == 0 ? "0 0 1 1 re f" : Repeat($"/F{k - 1} Do ", 10);
            var res = k == 0 ? "<< >>" : $"<< /XObject << /F{k - 1} {4 + k} 0 R >> >>";
            objects.Add(Stream($"<< /Type /XObject /Subtype /Form /BBox [0 0 600 800] /Resources {res}", body));
        }
        var renderer = PdfRenderer.Open(SimplePdf(objects));
        var ex = Bounded(() => Record.Exception(() => renderer.RenderPage(0, new ImageOptions { Dpi = 36 })));
        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("too complex", ex.Message);

        // The same nesting with fewer repeats renders normally.
        objects[3] = Stream("<<", "/F3 Do");
        objects[2] = objects[2].Replace("/F11 16", "/F3 8");
        Assert.True(PdfRenderer.Open(SimplePdf(objects)).RenderPage(0, new ImageOptions { Dpi = 36 }).Width > 0);

        static string Stream(string dict, string data) => $"{dict} /Length {data.Length} >>\nstream\n{data}\nendstream";
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] SimplePdf(List<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer << /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Png(int width, int height, byte colorType, byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = colorType;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write(new byte[4]); // CRC: not checked by the decoder
        }
    }

    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Descendants(doc).Where(e => e.Name == "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
    }

    private static Element ById(Element root, string id) => Descendants(root).First(e => e.Id == id);

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    [Fact]
    public void Many_certificate_store_entries_pointing_at_one_compression_bomb_are_decoded_once_within_a_budget()
    {
        // Two hundred megabytes of zeros compress to a few hundred kilobytes; a thousand references to it would decode
        // two hundred gigabytes if every entry were decoded on its own.
        var bomb = new MemoryStream();
        using (var z = new ZLibStream(bomb, CompressionLevel.SmallestSize, leaveOpen: true))
            for (int i = 0; i < 200; i++) z.Write(new byte[1 << 20]);
        static byte[] L(string t) => Encoding.Latin1.GetBytes(t);
        var refs = new StringBuilder();
        for (int i = 0; i < 1000; i++) refs.Append("4 0 R ");
        var objects = new List<byte[]>
        {
            L("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /DSS 5 0 R >>\nendobj\n"),
            L("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"),
            L("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\n"),
            L("4 0 obj\n<< /Length " + bomb.Length + " /Filter /FlateDecode >>\nstream\n").Concat(bomb.ToArray()).Concat(L("\nendstream\nendobj\n")).ToArray(),
            L("5 0 obj\n<< /Certs [" + refs + "] >>\nendobj\n"),
        };
        var ms = new MemoryStream();
        ms.Write(L("%PDF-1.7\n"));
        var offsets = new List<long>();
        foreach (var o in objects) { offsets.Add(ms.Position); ms.Write(o); }
        long xref = ms.Position;
        var sb = new StringBuilder("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        ms.Write(L(sb.ToString()));
        var pdf = ms.ToArray();

        Assert.Empty(Bounded(() => PdfSignatureVerifier.Verify(pdf)));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostileInputCollection
{
    public const string Name = "Hostile input";
}
