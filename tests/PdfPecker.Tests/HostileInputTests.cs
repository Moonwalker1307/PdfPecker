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
        var pdf = Bounded("<p style='width:30px'>" + Repeat("x", 300000) + "</p>");
        AssertStructurallyValid(pdf);
        // The word is cut into lines that each hold a few characters.
        Assert.True(PageCount(pdf) > 10);
    }

    [Fact]
    public void Broken_words_keep_every_character()
    {
        var pdf = ConvertUncompressed("<p style='width:40pt;font-family:Courier;font-size:10pt'>ABCDEFGHIJKLMNOPQRSTUVWXYZ</p>");
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
    public void Pdf_with_unterminated_streams_opens_in_linear_time()
    {
        var sb = new StringBuilder("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj 2 0 obj << /Type /Pages /Kids [] /Count 0 >> endobj\n");
        for (int i = 3; sb.Length < 4 * 1024 * 1024; i++) sb.Append(i).Append(" 0 obj << /Length 999999999 >> stream\nxx\n");
        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        var renderer = Bounded(() => PdfRenderer.Open(bytes));
        Assert.Equal(0, renderer.PageCount);
    }

    // ------------------------------------------------------------------ helpers

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
}
