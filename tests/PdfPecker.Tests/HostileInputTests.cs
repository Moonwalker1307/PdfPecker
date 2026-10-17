using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
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

    [Theory]
    [InlineData("<h2>text-shadow</h2>\n<di</span>\n")] // the fuzz finding: each malformed tag opens an inline element
    [InlineData("<h2>Heading</h2><div>")]
    public void Headings_kept_with_nested_boxes_do_not_lay_out_each_level_twice(string level)
    {
        // Each heading must stay with the box after it, whose first content is the next heading. Where a heading moves
        // to the next page, every heading above it has to move too, and each level used to lay out everything below it
        // once more for that: 30 levels took seconds, and 40 would take hours.
        AssertStructurallyValid(Bounded(Repeat(level, 40)));
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
    public void Nested_floats_that_paginate_are_laid_out_once_per_level()
    {
        // Found by the fuzzer: target html, timeout-1abcdf31. A float that holds a forced break cannot be moved into
        // place as measured, so it is laid out again on the page, and so was every float nested in it, once per level
        // above it. The parser stops nesting at 256 levels, so the rest of the floats are siblings in the innermost one.
        var html = Repeat("<div style='float:left'>", 250) + Repeat("<div style='float:left'></div>", 8000)
                   + "<p>before the break</p><h2 style='page-break-before:always'>after the break</h2>";
        var pdf = Bounded(html);
        AssertStructurallyValid(pdf);
        Assert.Equal(2, PageCount(pdf));
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

    [Fact]
    public void PostScript_function_with_many_rolls_does_not_overflow_the_stack()
    {
        // A tint transform that rolls 99 entries 33,000 times, within the calculator's step limit. The calculator used
        // to take a fresh stack buffer for every roll, so this crashed the process with a stack overflow.
        var program = "{ " + Repeat("0.5 ", 99) + Repeat("99 1 roll ", 33_000) + "}";
        const string content = "/CS0 cs 0.5 scn 0 0 612 792 re f";
        var renderer = PdfRenderer.Open(SimplePdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /ColorSpace << /CS0 [/Separation /Spot /DeviceGray 5 0 R] >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /FunctionType 4 /Domain [0 1] /Range [0 1] /Length {program.Length} >>\nstream\n{program}\nendstream",
        }));
        var image = Bounded(() => renderer.RenderPage(0, new ImageOptions { Dpi = 36 }));
        // Every entry is 0.5, so the page is filled with mid grey.
        Assert.Equal(128, image.Rgba[0]);
    }

    [Fact]
    public void Text_extraction_of_a_letter_with_a_million_combining_marks_stays_linear()
    {
        // Code 1 is a combining acute accent drawn with no width, so every mark sits on the letter before it. The
        // trailing letters make the marks a minority, so they are joined to that letter rather than kept on their own.
        var pdf = TextPdf("BT /F1 12 Tf 72 700 Td (C" + new string('\x01', 999_999) + ") Tj 0 -20 Td (" + new string('B', 1_000_000) + ") Tj ET", 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var page = Bounded(() => PdfText.ExtractPage(pdf, 1, null, cts.Token));
        Assert.Equal(2, page.Lines.Count);
        Assert.Equal("C" + new string('\u0301', 999_999), page.Lines[0].Text);
        Assert.Equal(new string('B', 1_000_000), page.Lines[1].Text);
    }

    [Fact]
    public void Text_extraction_of_a_million_combining_marks_away_from_any_letter_stays_linear()
    {
        // Here the marks have a width, so they run on past their letter and each is kept as a character of its own.
        var pdf = TextPdf("BT /F1 12 Tf 72 700 Td (C" + new string('\x01', 999_999) + ") Tj 0 -20 Td (" + new string('B', 1_000_000) + ") Tj ET", 600);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var page = Bounded(() => PdfText.ExtractPage(pdf, 1, null, cts.Token));
        Assert.Equal(2, page.Lines.Count);
        Assert.Equal(999_999, page.Lines[0].Text.Count(c => c == '\u0301'));
    }

    [Fact]
    public void Text_extraction_of_a_right_to_left_letter_and_a_million_spaces_stays_linear()
    {
        // Code A is a Hebrew alef: one right-to-left letter makes the line go through bidi ordering, where every space
        // is a neutral that looks for its nearest strong neighbours.
        var pdf = TextPdf("BT /F1 12 Tf 72 700 Td (A" + new string(' ', 1_000_000) + "A) Tj ET", 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var page = Bounded(() => PdfText.ExtractPage(pdf, 1, null, cts.Token));
        Assert.Equal("\u05D0 \u05D0", page.Text);
    }

    /// <summary>
    /// A one-page PDF showing <paramref name="content"/>, compressed, in a font mapping code 1 to U+0301, drawn
    /// <paramref name="markWidth"/> wide, and A to U+05D0.
    /// </summary>
    private static byte[] TextPdf(string content, int markWidth)
    {
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Test def\n"
            + "1 begincodespacerange <00> <FF> endcodespacerange\n"
            + "5 beginbfchar <01> <0301> <20> <0020> <41> <05D0> <42> <0042> <43> <0043> endbfchar\n"
            + "endcmap CMapName currentdict /CMap defineresource pop end end";
        var widths = markWidth + " " + string.Join(" ", Enumerable.Repeat("600", 66));
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(Encoding.Latin1.GetBytes(content));
        var data = Encoding.Latin1.GetString(ms.ToArray());
        return SimplePdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 1 /LastChar 67 /Widths [{widths}] /ToUnicode 5 0 R >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
            $"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n{data}\nendstream",
        });
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

    /// <summary>Runs <paramref name="work"/> on one thread and returns its result with the bytes that thread allocated.</summary>
    private static (T Result, long Allocated) Allocating<T>(Func<T> work) => Bounded(() =>
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = work();
        return (result, GC.GetAllocatedBytesForCurrentThread() - before);
    });

    /// <summary>A little-endian TIFF of one image directory whose tags all hold one LONG, followed by <paramref name="data"/>.</summary>
    private static byte[] Tiff((int Tag, uint Value)[] tags, byte[] data, int dataOffsetTag)
    {
        int dataAt = 8 + 2 + tags.Length * 12 + 4;
        var d = new byte[dataAt + data.Length];
        "II*\0"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), (ushort)tags.Length);
        int at = 10;
        foreach (var (tag, value) in tags.OrderBy(t => t.Tag))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), (ushort)tag);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at + 2), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at + 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at + 8), tag == dataOffsetTag ? (uint)dataAt : value);
            at += 12;
        }
        data.CopyTo(d, dataAt);
        return d;
    }

    [Theory]
    [InlineData(32773)] // PackBits
    [InlineData(8)] // Deflate
    [InlineData(1)] // uncompressed
    public void Tiff_tile_far_larger_than_the_image_is_not_allocated(int compression)
    {
        // A 1 x 1 image of eight 16-bit samples in one 10000 x 10000 tile: 1.6 GB for the tile from a few bytes.
        var tiff = Tiff(new[]
        {
            (256, 1u), (257, 1u), (258, 16u), (259, (uint)compression), (262, 1u), (277, 8u),
            (322, 10000u), (323, 10000u), (324, 0u), (325, 16u),
        }, new byte[16], 324);
        var (_, allocated) = Allocating(() => TiffDecoder.Decode(tiff, 100_000_000));
        Assert.True(allocated < 64 << 20, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Tiff_strips_sharing_one_lzw_blob_decode_only_their_own_size()
    {
        // 1000 strips of one row each all point at the same LZW data, which decodes to about 256 KB of zeros: each strip
        // needs 16 bytes, so decoding the whole blob for each would be over ten thousand times the image.
        var lzw = new List<byte>();
        int bits = 0, count = 0;
        void Code(int c) { bits = bits << 9 | c; count += 9; while (count >= 8) { lzw.Add((byte)(bits >> (count - 8))); count -= 8; } }
        for (int cycle = 0; cycle < 8; cycle++)
        {
            Code(256);
            Code(0);
            for (int c = 258; c < 510; c++) Code(c); // each code is the previous one plus another zero, still 9 bits wide
        }
        Code(257);
        if (count > 0) lzw.Add((byte)(bits << (8 - count)));
        var data = lzw.ToArray();

        const int rows = 1000;
        var tiff = Tiff(new[]
        {
            (256, 16u), (257, (uint)rows), (258, 8u), (259, 5u), (262, 1u), (277, 1u), (278, 1u), (273, 0u), (279, 0u),
        }, data, 273);
        // Point every strip at the one blob through offset and byte-count arrays appended after it.
        int blobAt = tiff.Length - data.Length, offsetsAt = tiff.Length, countsAt = offsetsAt + rows * 4;
        Array.Resize(ref tiff, tiff.Length + rows * 8);
        for (int i = 0; i < rows; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offsetsAt + i * 4), (uint)blobAt);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(countsAt + i * 4), (uint)data.Length);
        }
        // The directory holds the tags in order: 273 is the sixth entry and 279 the ninth.
        foreach (var (index, at) in new[] { (5, offsetsAt), (8, countsAt) })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(10 + index * 12 + 4), rows);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(10 + index * 12 + 8), (uint)at);
        }

        // Refusing the file and decoding each strip only as far as its own 16 bytes are both fine.
        var (_, allocated) = Allocating(() => TiffDecoder.Decode(tiff, 100_000_000));
        Assert.True(allocated < 200 << 20, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Animated_webp_with_a_huge_canvas_and_a_tiny_frame_is_not_allocated()
    {
        // The lossless frame of a small fixture as the first frame of an animation on a 10000 x 10000 canvas.
        var source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", "lossless-palette.webp"));
        int vp8l = Encoding.ASCII.GetString(source).IndexOf("VP8L", StringComparison.Ordinal);
        var frame = source.AsSpan(vp8l, 8 + BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(vp8l + 4))).ToArray();
        if (frame.Length % 2 == 1) Array.Resize(ref frame, frame.Length + 1);

        var webp = new MemoryStream();
        void Chunk(string fourCc, byte[] body)
        {
            webp.Write(Encoding.ASCII.GetBytes(fourCc));
            webp.Write(BitConverter.GetBytes(body.Length));
            webp.Write(body);
        }
        webp.Write("RIFF\0\0\0\0WEBP"u8);
        var vp8x = new byte[10];
        vp8x[0] = 0x02; // animation
        vp8x[4] = (9999 & 0xFF); vp8x[5] = 9999 >> 8; vp8x[7] = (9999 & 0xFF); vp8x[8] = 9999 >> 8;
        Chunk("VP8X", vp8x);
        Chunk("ANIM", new byte[6]);
        Chunk("ANMF", new byte[16].Concat(frame).ToArray()); // offset 0, frame size and timing left at zero
        var bytes = webp.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);

        var (_, allocated) = Allocating(() => WebPDecoder.Decode(bytes, 100_000_000));
        Assert.True(allocated < 64 << 20, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Device_files_and_pipes_are_not_read_as_images_or_stylesheets()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Directory.CreateTempSubdirectory("pdfpecker-fifo");
        try
        {
            // A pipe nobody writes to blocks whoever opens it; /dev/zero never ends.
            var fifo = Path.Combine(dir.FullName, "pipe.css");
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", fifo)) mkfifo.WaitForExit();
            Assert.True(File.Exists(fifo));
            var html = $"<link rel=stylesheet href='{fifo}'><link rel=stylesheet href='/dev/zero'>"
                + $"<img src='/dev/zero'><img src='file:///dev/zero'><img src='pipe.css'><p>text</p>";
            var options = new PdfOptions { BaseDirectory = dir.FullName, AllowFileAccess = true };
            var (pdf, allocated) = Allocating(() => HtmlToPdf.Convert(html, options));
            AssertStructurallyValid(pdf);
            Assert.True(allocated < 256 << 20, $"Allocated {allocated:N0} bytes");
            // Without AllowFileAccess the pipe inside the base directory is still in reach.
            AssertStructurallyValid(Bounded(() => HtmlToPdf.Convert("<img src='pipe.css'><link rel=stylesheet href='pipe.css'>",
                new PdfOptions { BaseDirectory = dir.FullName })));
        }
        finally
        {
            dir.Delete(true);
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

/// <summary>Hostile CFF and CFF2 fonts whose subroutines fan out exponentially.</summary>
[Collection(HostileInputCollection.Name)]
public class CffSubroutineTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));

    private static byte[] Bounded(string html)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return HtmlToPdf.Convert(html, null, cts.Token);
    }

    private static string FontFace(byte[] font)
        => $"<style>@font-face{{font-family:f;src:url(data:font/otf;base64,{Convert.ToBase64String(font)})}}</style>";

    [Fact]
    public void Cff_subroutines_that_fan_out_do_not_hang_subsetting()
    {
        // Every glyph of Inter becomes a call into global subroutines that each call the next a thousand times.
        var font = ReplaceTable(Fixture("Inter-Subr.otf"), "CFF ", n => FanOutCff(cff2: false, n));
        AssertStructurallyValid(Bounded(FontFace(font) + "<p style='font-family:f'>xyz</p>"));
    }

    [Fact]
    public void Cff2_subroutines_that_fan_out_do_not_hang_instancing()
    {
        // A bold run makes the converter instance the CFF2 variable font, which runs every glyph's charstring.
        var font = ReplaceTable(Fixture("VarTestCFF-Subset.otf"), "CFF2", n => FanOutCff(cff2: true, n));
        AssertStructurallyValid(Bounded(FontFace(font) + "<p style='font-family:f'>Hen <b>Hen</b></p>"));
    }

    private static byte[] ReplaceTable(byte[] font, string tag, Func<int, byte[]> table)
    {
        var p = Assert.Single(FontProgram.Load(font));
        var replacement = table(p.NumGlyphs);
        var tables = p.Tables.Select(t => (t.Tag, t.Tag == FontProgram.Tag(tag) ? replacement : p.Data.AsSpan(t.Offset, t.Length).ToArray())).ToList();
        return SfntBuilder.Build(0x4F54544F, tables); // 'OTTO'
    }

    /// <summary>
    /// A CFF (or CFF2) table whose glyphs all call global subroutine 0, and whose subroutine i calls subroutine i + 1
    /// a thousand times: 1000^8 calls if walked naively. With fewer than 1240 subroutines the bias is 107, so
    /// subroutine i is the one-byte number i - 107.
    /// </summary>
    private static byte[] FanOutCff(bool cff2, int glyphs)
    {
        const int levels = 9, fanOut = 1000; // nine levels stay within the nesting limit of ten, with the glyph itself
        var subrs = new byte[levels][];
        for (int i = 0; i < levels; i++)
        {
            var s = new List<byte>();
            if (i + 1 < levels)
                for (int k = 0; k < fanOut; k++) { s.Add((byte)(i + 1 - 107 + 139)); s.Add(29); } // n callgsubr
            if (!cff2) s.Add(11); // return
            subrs[i] = s.ToArray();
        }
        var glyph = cff2 ? new byte[] { 32, 29 } : new byte[] { 32, 29, 14 }; // -107 callgsubr (endchar)
        var charStrings = Index(cff2, Enumerable.Repeat(glyph, glyphs).ToArray());
        var gsubrs = Index(cff2, subrs);

        var o = new List<byte>();
        if (cff2)
        {
            o.AddRange(new byte[] { 2, 0, 5, 0, 6 }); // version 2, header size 5, Top DICT length 6
            o.AddRange(TopDict(5 + 6 + gsubrs.Length));
        }
        else
        {
            o.AddRange(new byte[] { 1, 0, 4, 4 });
            o.AddRange(Index(false, new[] { "F"u8.ToArray() }));
            int topSize = Index(false, new[] { TopDict(0) }).Length;
            o.AddRange(Index(false, new[] { TopDict(o.Count + topSize + 2 + gsubrs.Length) }));
            o.AddRange(new byte[] { 0, 0 }); // empty String INDEX
        }
        o.AddRange(gsubrs);
        o.AddRange(charStrings);
        return o.ToArray();

        // The Top DICT holds only the CharStrings offset, as a five-byte integer so that its size is known up front.
        static byte[] TopDict(int charStrings)
        {
            var b = new byte[6];
            b[0] = 29;
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), charStrings);
            b[5] = 17;
            return b;
        }

        static byte[] Index(bool cff2, byte[][] items)
        {
            var o = new List<byte>();
            var count = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(count, items.Length);
            o.AddRange(cff2 ? count : count[2..]);
            o.Add(4);
            var off = new byte[4];
            int at = 1;
            BinaryPrimitives.WriteInt32BigEndian(off, at);
            o.AddRange(off);
            foreach (var item in items)
            {
                at += item.Length;
                BinaryPrimitives.WriteInt32BigEndian(off, at);
                o.AddRange(off);
            }
            foreach (var item in items) o.AddRange(item);
            return o.ToArray();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostileInputCollection
{
    public const string Name = "Hostile input";
}
