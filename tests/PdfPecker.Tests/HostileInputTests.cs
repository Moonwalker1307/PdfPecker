using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;
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

    // Lengths near the float range overflowed to infinity once added or scaled in layout, and the box geometry built from
    // them reached the painters as NaN, where rounding it threw ArithmeticException.
    [Theory]
    [InlineData("h2{margin:x -1e38;border-bottom-style:dotted;border-radius:4px}")]
    [InlineData("h2{margin:0 -1e38px;border:3px solid;border-bottom:5px double red;border-radius:4px}")]
    [InlineData("h2{margin:0 -3e38px;border:9px groove;border-left-style:dashed;border-radius:1e38px}")]
    [InlineData("h2{margin:0 -1e38em;border:2px solid;border-top-color:red;border-radius:4px}")]
    [InlineData("h2{width:1e38em;padding:1e38px;outline:3px dotted;outline-offset:1e38px;border-radius:4px}")]
    [InlineData("h2{margin-left:-1e38px;box-shadow:1e38px 1e38px 1e38px 1e38px red, inset 0 0 4px 1e38px blue;border-radius:4px}")]
    [InlineData("h2{font-size:1e38px;border:1em dotted;border-radius:1em;text-indent:-1e38em}")]
    [InlineData("h2{margin:0 calc(-1e38px * 4);border:1px solid;border-bottom-style:dotted;border-radius:4px}")]
    [InlineData("div{width:1e30%;margin-left:-1e30%;border:1px solid;border-left-style:dotted;border-radius:4px}",
        "<div><div><div><div><div><div><div><div><div><div>x</div></div></div></div></div></div></div></div></div></div>")]
    public void Huge_lengths_give_finite_box_geometry(string css, string body = "<h2>title</h2><p>text</p>")
    {
        // A font millions of points tall can make the document longer than the page limit, which is a documented outcome.
        byte[]? pdf = null;
        var ex = Record.Exception(() => pdf = Bounded($"<style>{css}</style>{body}"));
        if (ex != null) Assert.IsType<PdfLimitException>(ex);
        else AssertStructurallyValid(pdf!);
    }

    // Lower-case words, which small capitals keep whole; the test texts add words that bidi and small capitals each cut
    // in two, in different places (aB|א and A|Bא).
    private const string Lower = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor";

    // Small capitals and bidi levels cut a ::first-line's words into other pieces than the usual lines' words. The
    // first-line pieces were resolved for bidi only after their count had been compared with the usual pieces', so
    // when the counts happened to match before that, the line breaker read past the end of the usual list once the
    // first line was full.
    [Theory]
    [InlineData("font-variant: small-caps", Lower + " aBא")]
    [InlineData("font-variant: small-caps", "aBא " + Lower + " aBא aBא")]
    [InlineData("font-variant: small-caps; letter-spacing: 3px", Lower + " aBא")]
    [InlineData("font-variant: small-caps; word-spacing: 9px", Lower + " aBא")]
    [InlineData("font-variant: small-caps; font-size: 30px", Lower + " aBא")]
    [InlineData("font-variant: small-caps; font-weight: bold", "123年4月5日 اللَّهِ תל־אביב elit " + Lower + " aBא")]
    [InlineData("font-variant: small-caps; text-transform: uppercase", Lower + " aBא")]
    [InlineData("font-variant: all-small-caps", "lorem <bdi>ipsum aBא</bdi> <span dir=rtl>dolor aBא</span> " + Lower + " aBא")]
    [InlineData("font-variant: small-caps", "lorem aBא<br>" + Lower + " aBא")]
    [InlineData("font-variant: small-caps", "loremipsumdolorsitametconsecteturadipiscingelit aBא " + Lower + " aBא")]
    [InlineData("text-transform: lowercase", "Hyphenation Extraordinarily Complicated Hyphenation Extraordinarily Complicated aBא")]
    [InlineData("text-transform: uppercase; font-size: 20px", "hyphenation extraordinarily complicated hyphenation extraordinarily complicated aBא")]
    public void First_line_cut_into_other_pieces_than_the_usual_lines_renders(string firstLine, string text)
    {
        foreach (string block in new[] { "", "direction: rtl;", "overflow-wrap: anywhere;", "unicode-bidi: plaintext;", "hyphens: auto;" })
            foreach (string firstLetter in new[] { "", "p::first-letter { color: red }" })
            {
                var html = $"<style>p::first-line {{ {firstLine} }} {firstLetter}</style>"
                    + $"<p lang=en style='width: 120px; {block}'>{text}</p>";
                AssertStructurallyValid(Bounded(html));
            }
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
    // A later fuzz finding (html, timeout-d2edd0b0): the box after each heading starts with another box, here the
    // anonymous block of a div inside a span, so the heading's box only contains the next heading one level down.
    [InlineData("<h1>Heading</h1><span><div>")]
    [InlineData("<h1>Heading</h1><div><div>")]
    public void Headings_kept_with_nested_boxes_do_not_lay_out_each_level_twice(string level)
    {
        // Each heading must stay with the box after it, whose first content is the next heading. Where a heading moves
        // to the next page, every heading above it has to move too, and each level used to lay out everything below it
        // once more for that: 30 levels took seconds, and 40 would take hours.
        AssertStructurallyValid(Bounded(Repeat(level, 40)));
    }

    [Theory]
    // The fuzz finding (html, timeout-b4187992): each unterminated href swallows the next line, so two lines make one
    // level of nav, heading, list and item. Sixty lines took 13 s.
    [InlineData("<nav><h3>Contents</h3><ul>\n<li><a href=\"</div>\n", 80)]
    [InlineData("<nav><h3>Contents</h3><ul><li>", 40)]
    [InlineData("<h3>Contents</h3><ul><li>", 40)]
    [InlineData("<h3>Contents</h3><ol><li style=\"list-style-position: inside\">", 40)]
    [InlineData("<h3>Contents</h3><ul style=\"list-style-image: url('data:image/svg+xml;utf8,<svg xmlns=%22http://www.w3.org/2000/svg%22 width=%226%22 height=%226%22/>')\"><li>", 40)]
    [InlineData("<h3>Contents</h3><ul><li>", 200)]
    public void Headings_kept_with_nested_list_items_do_not_lay_out_each_level_twice(string level, int count)
    {
        // As above, but the box after each heading is a list whose first item holds the next heading. The item's layout
        // could not stop early once its first content was placed, so each level laid out the levels below it twice:
        // 28 levels took two seconds, and each further level doubled that.
        AssertStructurallyValid(Bounded(Repeat(level, count) + "<p>end</p>"));
    }

    [Fact]
    public void Deeply_nested_subgrids_with_huge_spans_stay_bounded()
    {
        // Two hundred subgrids inside each other, each spanning a thousand of its parent's columns on both axes, with
        // items spanning them all and name lists repeated to the limit. A row subgrid is laid out twice (measured,
        // then placed at the parent's rows); it must reuse the measured boxes or each level would double the work.
        var sb = new StringBuilder("<div style='display:grid; grid-template-columns: repeat(1000, 1pt); grid-auto-rows: 1pt'>");
        for (int i = 0; i < 200; i++)
        {
            sb.Append("<div style='display:grid; grid-column: span 1000; grid-row: span 20; gap: 1pt; padding: 1pt;")
              .Append(" grid-template-columns: subgrid repeat(1000, [a] [b]) repeat(auto-fill, [c]); grid-template-rows: subgrid repeat(auto-fill, [r] [s])'>")
              .Append("<div style='grid-column: a / span 1000 b; grid-row: 1 / 1000'>x</div>");
        }
        sb.Append(Repeat("</div>", 201));
        AssertStructurallyValid(Bounded(sb.ToString()));
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

    [Theory]
    // The boxes fit in the measuring width, so every level measures the ones inside it at the same width.
    [InlineData("", 50000)]
    // The innermost boxes together are wider than the measuring width, so each level offers them a narrower width.
    [InlineData("margin: 0 3pt; padding: 0 3pt", 20000)]
    public void Nested_inline_blocks_are_measured_once_per_level(string box, int siblings)
    {
        // Found by the fuzzer: target html, timeout-b4187992. Measuring an inline-block's intrinsic widths lays out the
        // inline-blocks in it, and laying out each of those measures the ones inside it again, so the boxes of the
        // innermost level were measured once for every level above them and their drawing was copied up through every
        // level each time. The parser stops nesting at 256 levels, so the rest of the boxes are siblings in the innermost.
        var html = $"<style>span {{ display: inline-block; {box} }}</style>" + Repeat("<span>", 300) + Repeat("<span></span>", siblings);
        AssertStructurallyValid(Bounded(html));
    }

    [Theory]
    // A table with a height measures its rows before sharing that height out over them, and a row whose cells have
    // percentage heights inside is laid out once to find its height and again with the percentages resolved: with
    // tables nested in each other, each level doubled the work of the levels inside it (25 levels took minutes).
    [InlineData("<table style='height:400pt'><tr><td style='height:100pt'><div>", "</div></td></tr></table>", 50)]
    [InlineData("<table style='height:400pt'><tr><td style='height:100pt'><div style='height:100%'>", "</div></td></tr></table>", 50)]
    [InlineData("<table style='height:90%'><tr><td><div style='height:100%'>", "</div></td></tr></table>", 50)]
    // Stretched flex and grid items with percentage heights inside are laid out again at their stretched size.
    [InlineData("<div style='display:flex;height:90%'><div>", "</div></div>", 120)]
    [InlineData("<div style='display:grid;height:90%'><div>", "</div></div>", 120)]
    [InlineData("<div style='height:100%;aspect-ratio:1;max-height:90%'>", "</div>", 250)]
    public void Nested_percentage_heights_lay_out_each_level_a_bounded_number_of_times(string open, string close, int levels)
    {
        var html = "<style>html, body { height: 100% }</style><div style='height:500pt'>" + Repeat(open, levels) + "x" + Repeat(close, levels) + "</div>";
        AssertStructurallyValid(Bounded(html));
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
    public void Svg_path_numbers_after_a_closepath_end_the_path()
    {
        // A closepath reads no numbers, so numbers after it were taken as one more closepath that never moved past
        // them, adding close verbs until memory ran out. Like any other error in path data, they now end the path.
        var path = Bounded(() => Svg.PathParser.Parse("M0 0 L10 0 L10 10 z 5 5 L0 10"));
        Assert.NotNull(path);
        Assert.Equal(new[] { Svg.SvgShape.MoveTo, Svg.SvgShape.LineTo, Svg.SvgShape.LineTo, Svg.SvgShape.Close }, path.Verbs);
        AssertStructurallyValid(Bounded("<svg width=20 height=20><path d='M0 0 L10 0 L10 10 Z 5'/></svg>"
            + "<div style=\"width:20px;height:20px;background:red;clip-path:path('M0 0 L10 0 L10 10 z,1')\"></div>"));
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

    [Theory]
    // html, timeout-1f7d8566: a thousand blurred circles, each filter well within its own budget.
    [InlineData("<svg width='760' height='760'><filter id='blur'><feGaussianBlur stdDeviation='4'/></filter>",
        "<circle cx='160' cy='160' r='160' fill='#cc3333' filter='url(#blur)'/>", "</svg>")]
    [InlineData("<body style='margin:0'>", "<div style='width:400px;height:300px;background:#cc3333;filter:blur(4px)'></div>", "</body>")]
    public void Many_small_filters_share_one_work_budget(string before, string element, string after)
    {
        // Every filter renders its content through a page of its own and compresses its result, and each one here is
        // well within its own budget, so the filters of the page ran for minutes before they shared one budget.
        var pdf = Bounded(() => Bounded(before + Repeat(element, 2000) + after, new PdfOptions { Compress = false }));
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        // The first filters run, each an image with an alpha mask; the elements after the document's budget is spent
        // are drawn without them.
        Assert.InRange(CountOf(text, "/Subtype /Image") / 2, 1, 1999);
        Assert.Contains("0.8 0.2 0.2 rg", text);
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

    [Fact]
    public void Markdown_autolinks_after_many_email_addresses_are_found_in_linear_time()
    {
        // The search for the next URL used to run to the end of the text again after every e-mail address.
        const int n = 60000;
        var html = Bounded(() => MarkdownToHtml.Convert(Repeat("a.b-c_d+e@f.gh ", n) + "www.example.com and x@y.z"));
        Assert.Equal(n + 1, CountOf(html, "href=\"mailto:"));
        Assert.Contains("<a href=\"http://www.example.com\">www.example.com</a> and <a href=\"mailto:x@y.z\">x@y.z</a>", html);
    }

    [Fact]
    public void Markdown_links_after_many_unclosed_brackets_close_in_linear_time()
    {
        // Every link used to deactivate all the brackets before it again, though a link before it had done so already.
        const int n = 100000;
        var html = Bounded(() => MarkdownToHtml.Convert(Repeat("![ [ ", n) + Repeat("[a](/u) ", n) + "[b]](/v) ![c](/i)"));
        Assert.Equal(n, CountOf(html, "<a href=\"/u\">a</a>"));
        // The last "]" closes an inactive bracket, which stays literal, and the image after it is still an image.
        Assert.Contains("<a href=\"/u\">a</a> [b]](/v) <img src=\"/i\" alt=\"c\" />", html);
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

    [Theory]
    [InlineData("2147483647")]
    [InlineData("2147483500")]
    [InlineData("99999999999")]
    public void Stream_whose_length_overflows_the_file_offset_is_still_read(string length)
    {
        // The /Length added to the stream's offset no longer fits in an int; the stream must be found by its
        // "endstream" like any other wrong /Length instead of being dropped.
        const string content = "BT /F1 24 Tf 20 20 Td (Hello) Tj ET 0 0 1 rg 50 50 100 100 re f";
        var pdf = SimplePdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        });

        var image = Bounded(() => PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 }));
        int center = (100 * image.Width + 100) * 4;
        Assert.True(image.Rgba[center] < 64 && image.Rgba[center + 2] > 192, "the blue square is not painted");
        Assert.Contains("Hello", Bounded(() => PdfText.Extract(pdf)));
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
    public void Soft_mask_whose_group_sets_the_same_mask_cannot_double_the_work_per_level()
    {
        // The mask's group sets that same mask twice, so each level of nesting builds two full-page masks: millions of
        // them before the content depth limit stops it.
        var objects = SoftMaskPage();
        objects.Add("<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>");
        objects.Add(SoftMaskGroup("<< /ExtGState << /GS0 5 0 R >> >>", "/GS0 gs /GS0 gs"));
        AssertSoftMaskedPageIsRed(objects);
    }

    [Fact]
    public void Chain_of_distinct_nested_soft_masks_cannot_double_the_work_per_level()
    {
        // Twenty-two different masks without a cycle, each one's group setting the next one twice: 2^22 masks.
        var objects = SoftMaskPage();
        for (int k = 0; k < 22; k++)
        {
            int group = objects.Count + 2;
            objects.Add($"<< /Type /ExtGState /SMask << /S /Luminosity /G {group} 0 R >> >>");
            objects.Add(k < 21
                ? SoftMaskGroup($"<< /ExtGState << /GS0 {group + 1} 0 R >> >>", "/GS0 gs /GS0 gs")
                : SoftMaskGroup("<< >>", ""));
        }
        AssertSoftMaskedPageIsRed(objects);
    }

    [Fact]
    public void Many_soft_masks_on_one_page_are_charged_by_their_area()
    {
        // Each operator sets a soft mask, and each mask is a full-page canvas: a few hundred thousand of them are well
        // within the operator budget but would fill and read billions of pixels.
        var objects = SoftMaskPage(Repeat("/GS0 gs ", 300_000));
        objects.Add("<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>");
        objects.Add(SoftMaskGroup("<< >>", ""));
        var renderer = PdfRenderer.Open(SimplePdf(objects));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ex = Bounded(() => Record.Exception(() => renderer.RenderPage(0, new ImageOptions { Dpi = 36 }, cts.Token)));
        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("too complex", ex.Message);
    }

    [Fact]
    public void Cancellation_stops_a_page_of_soft_masks()
    {
        // Two hundred masks, with their groups, run fewer operators than the interval at which the content loop looks at
        // the token, so only a check where each mask is built stops them. At print resolution that interval is a few
        // hundred full-page masks, seconds of work.
        var objects = SoftMaskPage(Repeat("/GS0 gs ", 200));
        objects.Add("<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>");
        objects.Add(SoftMaskGroup("<< >>", ""));
        var renderer = PdfRenderer.Open(SimplePdf(objects));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = Bounded(() => Record.Exception(() => renderer.RenderPage(0, new ImageOptions { Dpi = 72 }, cts.Token)));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public void Dashes_finer_than_a_pixel_are_stroked_solid()
    {
        // A dash period of a few millionths of a point used to be cut into every one of its dashes: tens of millions
        // per line, a second and close to a gigabyte each, so a hundred lines ran for minutes.
        var renderer = PdfRenderer.Open(StrokePage("0.001 0 0 0.001 0 0 cm 2000 w [0.011] 0 d " + Repeat("0 100000 m 100000 100000 l S ", 100)));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var image = Bounded(() => renderer.RenderPage(0, new ImageOptions { Dpi = 72 }, cts.Token));
        AssertStrokedSolid(image);
    }

    [Theory]
    // Visible dashes, but along lines that run far off the page: millions of dashes each, nearly all of them outside
    // the page, and a second of work and close to a gigabyte for each line.
    [InlineData("2 w [4] 0 d ", "0 100 m 30000000 100 l S ", 30)]
    // Fewer dashes, but each with two round caps of about a hundred points: half a second and half a gigabyte a line.
    [InlineData("1 J 3000 w [0.5] 0 d ", "0 100 m 99000 100 l S ", 100)]
    public void Dashes_along_a_very_long_line_are_stroked_solid(string style, string line, int lines)
    {
        var renderer = PdfRenderer.Open(StrokePage(style + Repeat(line, lines)));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var image = Bounded(() => renderer.RenderPage(0, new ImageOptions { Dpi = 72 }, cts.Token));
        AssertStrokedSolid(image);
    }

    [Fact]
    public void Many_dashed_lines_on_one_page_are_charged_by_their_dashes()
    {
        // Each line has fewer dashes than one stroke may draw, but each is one operator: a thousand of them are well
        // within the operator budget and draw ninety million dashes.
        var renderer = PdfRenderer.Open(StrokePage("1 w [1] 0 d " + Repeat("0 100 m 180000 100 l S ", 1000)));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ex = Bounded(() => Record.Exception(() => renderer.RenderPage(0, new ImageOptions { Dpi = 72 }, cts.Token)));
        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("too complex", ex.Message);

        // A few such lines render, dashed.
        var image = PdfRenderer.Open(StrokePage("2 w [4] 0 d 0 100 m 180000 100 l S")).RenderPage(0, new ImageOptions { Dpi = 72 });
        Assert.True(image.Rgba[(99 * image.Width + 2) * 4] < 64, "the first dash is not painted");
        Assert.True(image.Rgba[(99 * image.Width + 6) * 4] > 192, "the first gap is painted");
    }

    // Balancing lays a multi-column box's content out again for each height it tries, and a multi-column box inside
    // it each time again: nested deeply, that would multiply. Below two levels the columns keep their boxes whole.
    [Fact]
    public void Deeply_nested_multicolumn_boxes_are_bounded()
    {
        string text = Repeat("lorem ipsum dolor sit amet ", 40);
        string html = Repeat("<div style='columns: 3; column-gap: 2pt'><p>" + text + "</p>", 60) + Repeat("</div>", 60);
        AssertStructurallyValid(Bounded(html));
    }

    // Columns one pixel wide in a wide page make hundreds of columns, and a box taller than many pages in them would
    // be split between millions of columns; the split stops at a limit.
    [Theory]
    [InlineData("<div style='column-width: 1px; column-gap: 0'>{0}</div>")]
    [InlineData("<div style='columns: 100; column-gap: 0'><div style='height: 10000000px; background: red'></div>{0}</div>")]
    [InlineData("<div style='columns: 100; column-gap: 0; height: 100000px'>{0}</div>")]
    [InlineData("<div style='columns: 2'>" + "<div style='columns: 2; column-span: all'><p style='column-span: all; break-before: column'>{0}</p></div>" + "</div>")]
    public void Many_or_huge_columns_are_bounded(string template)
    {
        string text = Repeat("<p>lorem ipsum dolor sit amet consectetur</p>", 300);
        AssertStructurallyValid(Bounded("<style>@page { size: 2000pt 800pt }</style>" + string.Format(template, text)));
    }

    // Each row of columns but the last is laid out once; only the last row is balanced.
    [Fact]
    public void Long_multicolumn_content_balances_only_its_last_row()
    {
        string html = "<div style='columns: 3'>" + Repeat("<h2>Section</h2><p>" + Repeat("lorem ipsum dolor sit amet ", 60) + "</p>", 400) + "</div>";
        AssertStructurallyValid(Bounded(html));
    }

    /// <summary>A 200 by 200 point page that runs <paramref name="content"/>.</summary>
    private static byte[] StrokePage(string content) => SimplePdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
    });

    /// <summary>
    /// Asserts that a black line two points wide along y = 100 of a <see cref="StrokePage"/> at 72 dpi, from the left
    /// edge to at least x = 100, is painted without gaps: the gaps of its dash pattern are too fine, or too many, to draw.
    /// </summary>
    private static void AssertStrokedSolid(RenderedImage image)
    {
        for (int x = 0; x < 100; x += 3)
            Assert.True(image.Rgba[(99 * image.Width + x) * 4] < 64, $"pixel {x} of the line is {image.Rgba[(99 * image.Width + x) * 4]}");
    }

    /// <summary>
    /// A page whose content sets the soft mask of object 5 (the first object added after these) and fills the page red,
    /// after <paramref name="prefix"/>, if given, instead of a single <c>/GS0 gs</c>.
    /// </summary>
    private static List<string> SoftMaskPage(string prefix = "/GS0 gs ")
    {
        var content = prefix + "1 0 0 rg 0 0 612 792 re f";
        return new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /ExtGState << /GS0 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
    }

    /// <summary>A soft mask group that runs <paramref name="content"/> and then fills the page white.</summary>
    private static string SoftMaskGroup(string resources, string content)
    {
        var data = content + " 1 g 0 0 612 792 re f";
        return $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /CS /DeviceRGB >> /Resources {resources} /Length {data.Length} >>\nstream\n{data}\nendstream";
    }

    /// <summary>
    /// Renders the page within the time bound. Every mask paints white, the innermost one whose nesting is cut off
    /// included, so the red fill shows through them all; each level of masking rounds its coverage down a little, so
    /// the red is not quite pure.
    /// </summary>
    private static void AssertSoftMaskedPageIsRed(List<string> objects)
    {
        var renderer = PdfRenderer.Open(SimplePdf(objects));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var image = Bounded(() => renderer.RenderPage(0, new ImageOptions { Dpi = 36 }, cts.Token));
        var rgba = image.Rgba;
        Assert.True(rgba[0] == 255 && rgba[1] < 16 && rgba[2] < 16 && rgba[3] == 255, $"pixel is {rgba[0]},{rgba[1]},{rgba[2]},{rgba[3]}");
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

    [Fact]
    public void Object_number_too_large_for_an_int_in_a_damaged_pdf_is_invalid_data()
    {
        // Found by the fuzzer: targets pdf-text, pdf-verify and pdf-merge, exception-0d3075ba. Rebuilding the object
        // table read a ten-digit object number with int.Parse.
        var pdf = Encoding.ASCII.GetBytes("9999999999 0obj ");
        Assert.Throws<InvalidDataException>(() => PdfText.Extract(pdf));
        Assert.Throws<InvalidDataException>(() => PdfMerger.Merge(new[] { pdf }));
        Assert.Throws<InvalidDataException>(() => PdfForm.GetFields(pdf));
        Assert.Throws<InvalidDataException>(() => PdfSignatureVerifier.Verify(pdf));
    }

    [Fact]
    public void Nested_svg_filters_do_not_double_their_resolution_at_each_level()
    {
        // Found by the fuzzer: targets svg and html, timeout-1ab0d4a5, four filtered groups inside each other. Each
        // level rendered its content at twice the pixels per point of the level around it, so the innermost of four
        // ran at 64 times the pixels of the outermost, up to the four-million-pixel cap.
        var chain = Repeat("<g filter='url(#f)'>", 4) + "<rect x='10' y='10' width='200' height='150' fill='red'/>" + Repeat("</g>", 4);
        var svg = "<svg width='800' height='600' viewBox='0 0 800 600'><defs><filter id='f'><feGaussianBlur stdDeviation='2'/></filter></defs>"
            + Repeat(chain, 16) + "</svg>";
        AssertStructurallyValid(Bounded("<p>before</p>" + svg + "<p>after</p>"));
    }

    [Fact]
    public void Rebuilding_the_object_table_late_without_finding_a_catalog_keeps_the_table_that_worked()
    {
        // Found by the fuzzer: targets pdf-merge and pdf-form, exception-d2faeb46. The catalog opens through the
        // cross-reference stream, but the page tree entry is wrong, so the table is rebuilt by scanning for object
        // headers. That scan finds none and used to throw the reader's internal exception from deep inside the call.
        var pdf = HiddenHeaderPdf("");
        Assert.Empty(PdfForm.GetFields(pdf));
        Assert.Throws<InvalidDataException>(() => PdfMerger.Merge(new[] { pdf }));
        Assert.Equal("", PdfText.Extract(pdf));
    }

    [Fact]
    public void A_registered_font_is_not_kept_alive_after_its_conversion()
    {
        // The fuzzer's font targets ran out of heap after a few thousand inputs, at whatever font method happened to
        // be running (memory-2506a272, memory-ff834f82, memory-de8e202b), and no single input reproduced it: every
        // font that shaped Arabic or Indic text stayed reachable from a static cache of shaping plans.
        var weak = ConvertWithOwnFont(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansArabic-Subset.ttf")));
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "The font program is still reachable after the conversion and its font collection are gone.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ConvertWithOwnFont(byte[] font)
    {
        var fonts = new FontCollection();
        fonts.Add(font, "Ar");
        AssertStructurallyValid(Bounded("<p style='font-family:Ar'>العربية</p>", new PdfOptions { Fonts = fonts }));
        var program = fonts.GetSnapshot().All[0].Program;
        Assert.NotNull(program);
        return new WeakReference(program);
    }

    [Fact]
    public void Form_whose_catalog_disappears_when_the_object_table_is_rebuilt_is_invalid_data()
    {
        // Found by the fuzzer: target pdf-form, exception-1abf4c6e. Here the scan finds a trailer, in a comment, that
        // names the catalog by a number the scan has no object for, so the catalog the form editor checked when it
        // opened the file now reads as null, and casting it to a dictionary threw InvalidCastException.
        var pdf = HiddenHeaderPdf("%trailer<</Root 1 0 R>>\n");
        Assert.Throws<InvalidDataException>(() => PdfForm.GetFields(pdf));
        Assert.Throws<InvalidDataException>(() => PdfForm.Flatten(pdf));
    }

    /// <summary>
    /// A PDF whose catalog and cross-reference stream follow a regular character, so that a scan for "n g obj" headers
    /// misses them while the cross-reference stream still finds them, and whose page tree entry points nowhere.
    /// <paramref name="extra"/> is inserted after the catalog.
    /// </summary>
    private static byte[] HiddenHeaderPdf(string extra)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes("%PDF-1.5\nx"));
        int catalog = b.Count;
        b.AddRange(Encoding.ASCII.GetBytes("1 0 obj<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[]>>>>endobj\n" + extra + "x"));
        int xref = b.Count;
        byte[] rows = [0, 0, 0, 255, 1, (byte)(catalog >> 8), (byte)catalog, 0, 1, 0, 5, 0, 1, (byte)(xref >> 8), (byte)xref, 0];
        b.AddRange(Encoding.ASCII.GetBytes($"3 0 obj<</Type/XRef/Size 4/W[1 2 1]/Root 1 0 R/Length {rows.Length}>>stream\n"));
        b.AddRange(rows);
        b.AddRange(Encoding.ASCII.GetBytes($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n"));
        return b.ToArray();
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

/// <summary>
/// Accented glyphs (seac) whose base or accent is the glyph itself, or a glyph whose seac points back: building one
/// used to recurse until the stack overflowed, which no handler can catch and which ends the whole process.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class SeacRecursionTests
{
    // Standard encoding codes, and the glyph ids the ISOAdobe charset gives those names (SID = gid).
    private const int CodeA = 65, CodeC = 67, CodeD = 68, CodeE = 69;
    private const int GidA = 34, GidC = 36, GidD = 37, GidE = 38, GidF = 39;

    // A small triangle: one move, two lines and the close that endchar adds.
    private const string Triangle = "10 10 rmoveto 50 0 rlineto 0 50 rlineto";

    [Fact]
    public void Cff_seac_that_refers_to_itself_or_a_cycle_does_not_overflow_the_stack()
    {
        var charStrings = new byte[GidF + 1][];
        for (int gid = 0; gid < charStrings.Length; gid++) charStrings[gid] = Type2("endchar");
        charStrings[GidA] = Type2($"{Triangle} 0 0 {CodeA} {CodeE} endchar"); // A = A + E
        charStrings[GidC] = Type2($"{Triangle} 0 0 {CodeD} {CodeE} endchar"); // C = D + E, and D = C + E
        charStrings[GidD] = Type2($"{Triangle} 0 0 {CodeC} {CodeE} endchar");
        charStrings[GidE] = Type2(Triangle + " endchar");
        charStrings[GidF] = Type2($"{Triangle} 0 0 {CodeE} {CodeE} endchar"); // a valid seac: F = E + E

        var font = CffFont.Parse(Cff(charStrings));
        Assert.NotNull(font);
        AssertBounded(() =>
        {
            Assert.Equal(2, Contours(font!.GetGlyph(GidA))); // its own outline and the accent; the self-reference is dropped
            Assert.Equal(3, Contours(font.GetGlyph(GidC))); // its own outline, D's own outline and the accent
            Assert.Equal(3, Contours(font.GetGlyph(GidD)));
            Assert.Equal(3, Contours(font.GetGlyph(GidF))); // a valid seac keeps both components
        });
    }

    [Fact]
    public void Type1_seac_that_refers_to_itself_or_a_cycle_does_not_overflow_the_stack()
    {
        const string triangle = "0 600 hsbw " + Triangle;
        var program = RendererFontTests.Type1Program("/Encoding StandardEncoding def", new[]
        {
            ("A", $"{triangle} 0 0 0 {CodeA} {CodeE} seac"),
            ("C", $"{triangle} 0 0 0 {CodeD} {CodeE} seac"),
            ("D", $"{triangle} 0 0 0 {CodeC} {CodeE} seac"),
            ("E", triangle + " endchar"),
            ("F", $"{triangle} 0 0 0 {CodeE} {CodeE} seac"),
        });
        var font = Type1Font.Parse(program, Encoding.Latin1.GetString(program).IndexOf("eexec\n", StringComparison.Ordinal) + 6);
        Assert.NotNull(font);
        AssertBounded(() =>
        {
            Assert.Equal(2, Contours(font!.GetGlyph(font.GlyphByName("A"))));
            Assert.Equal(3, Contours(font.GetGlyph(font.GlyphByName("C"))));
            Assert.Equal(3, Contours(font.GetGlyph(font.GlyphByName("D"))));
            Assert.Equal(3, Contours(font.GetGlyph(font.GlyphByName("F"))));
        });
    }

    private static void AssertBounded(Action action)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "building the glyphs did not finish in time");
    }

    private static int Contours(PathData? path)
    {
        Assert.NotNull(path);
        return path!.Verbs.Count(v => v == PathData.MoveTo);
    }

    /// <summary>Assembles a Type 2 charstring from small numbers and the operators these tests use.</summary>
    private static byte[] Type2(string program)
    {
        var o = new List<byte>();
        foreach (var token in program.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out int n)) { o.Add((byte)(n + 139)); continue; }
            o.Add(token switch { "rlineto" => 5, "endchar" => 14, "rmoveto" => 21, _ => throw new ArgumentException(token) });
        }
        return o.ToArray();
    }

    /// <summary>
    /// A bare CFF font: no strings, no subroutines, no Private DICT, the ISOAdobe charset and the standard encoding,
    /// so that glyph id i is named by standard string i.
    /// </summary>
    private static byte[] Cff(byte[][] charStrings)
    {
        var o = new List<byte> { 1, 0, 4, 4 };
        o.AddRange(Index(new[] { "F"u8.ToArray() }));
        int topSize = Index(new[] { TopDict(0) }).Length;
        o.AddRange(Index(new[] { TopDict(o.Count + topSize + 2 + 2) }));
        o.AddRange(new byte[] { 0, 0 }); // empty String INDEX
        o.AddRange(new byte[] { 0, 0 }); // empty Global Subr INDEX
        o.AddRange(Index(charStrings));
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

        static byte[] Index(byte[][] items)
        {
            var o = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 4 };
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

/// <summary>
/// Image XObjects whose /Mask or /SMask leads back to an image already being decoded, directly or through another
/// image: decoding an explicit mask used to apply that mask's own masks too, which recursed until the stack
/// overflowed, which no handler can catch and which ends the whole process.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class ImageMaskRecursionTests
{
    private const string Rgb = "/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB";
    private const string Gray = "/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray";
    private static readonly byte[] Red = { 255, 0, 0, 255, 0, 0 };
    private static readonly byte[] HalfGray = { 128, 128 };

    [Fact]
    public void Explicit_mask_that_is_the_image_itself_is_ignored()
    {
        // An explicit mask must be an image mask; one that is not is ignored, so the image stays opaque.
        var pdf = new FixturePdf();
        int a = pdf.NextNumber;
        pdf.AddStream($"{Rgb} /Mask {a} 0 R", Red);
        AssertImageColor(pdf, a, 255, 0, 0);
    }

    [Fact]
    public void Explicit_masks_that_form_a_cycle_are_ignored()
    {
        var pdf = new FixturePdf();
        int a = pdf.NextNumber, b = a + 1;
        pdf.AddStream($"{Rgb} /Mask {b} 0 R", Red);
        pdf.AddStream($"{Rgb} /Mask {a} 0 R", Red);
        AssertImageColor(pdf, a, 255, 0, 0);
    }

    [Fact]
    public void Explicit_mask_whose_soft_mask_is_the_image_is_ignored()
    {
        var pdf = new FixturePdf();
        int a = pdf.NextNumber, b = a + 1;
        pdf.AddStream($"{Rgb} /Mask {b} 0 R", Red);
        pdf.AddStream($"{Gray} /SMask {a} 0 R", HalfGray);
        AssertImageColor(pdf, a, 255, 0, 0);
    }

    [Fact]
    public void Soft_mask_that_leads_back_to_the_image_still_applies()
    {
        // A soft mask's own masks are not followed, so the half-grey soft mask applies and its /Mask is not looked at.
        var pdf = new FixturePdf();
        int a = pdf.NextNumber, b = a + 1, c = a + 2;
        pdf.AddStream($"{Rgb} /SMask {b} 0 R", Red);
        pdf.AddStream($"{Gray} /Mask {a} 0 R", HalfGray);
        AssertImageColor(pdf, a, 255, 127, 127);

        // An image that is its own soft mask takes its red channel, which is full, as its alpha.
        pdf.AddStream($"{Rgb} /SMask {c} 0 R", Red);
        AssertImageColor(pdf, c, 255, 0, 0);
    }

    private static void AssertImageColor(FixturePdf pdf, int image, int r, int g, int b)
    {
        var bytes = pdf.Build(8, 4, "q 8 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {image} 0 R >>");
        var task = Task.Run(() => FixturePdf.Render(bytes));
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "rendering the image did not finish in time");
        FixturePdf.AssertAt(task.Result, 2, 2, r, g, b);
        FixturePdf.AssertAt(task.Result, 6, 2, r, g, b);
    }
}

/// <summary>
/// Hostile GSUB and GPOS tables whose contextual lookups fan out within the nesting limit: a rule that applies its own
/// lookup again from each of several records, or a lookup whose many subtables all share one set of many rules.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class LayoutLookupFanOutTests
{
    // Arabic words, which the complex shaper shapes with the font's GSUB and GPOS.
    private static readonly string Text = string.Concat(Enumerable.Repeat("كمثرى بطيخ تفاح موز برتقال عنب ", 20));

    // A rule applies its lookup again from this many records, nested eight deep: 8^8 applications per glyph.
    private const int SelfApplications = 8;

    // A lookup has this many subtables, all pointing to one subtable whose rule set has this many rules: as many
    // squared rules tried per glyph. Both arrays of 16-bit offsets must stay within 64 KB of what they are offsets from.
    private const int SharedCount = 30000;

    private static T Bounded<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.Result;
    }

    [Theory]
    [InlineData("GSUB", false)]
    [InlineData("GPOS", false)]
    [InlineData("GSUB", true)]
    [InlineData("GPOS", true)]
    public void Contextual_lookups_that_fan_out_do_not_hang_shaping(string tag, bool sharedRules)
    {
        bool gpos = tag == "GPOS";
        var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansArabic-Subset.ttf"));
        var font = WithTable(original, tag, LayoutTable(gpos, sharedRules));
        var program = Assert.Single(FontProgram.Load(font));
        foreach (bool rtl in new[] { true, false })
        {
            var glyphs = Bounded(() => Shape(program, rtl));
            // The lookups substitute nothing: without GSUB every character keeps its glyph, and the font's own GSUB
            // substitutes as it does without the hostile GPOS.
            if (gpos) Assert.Equal(Shape(Assert.Single(FontProgram.Load(original)), rtl), glyphs);
            else Assert.Equal(Text.Length, glyphs.Count);
        }

        var html = $"<style>@font-face{{font-family:f;src:url(data:font/ttf;base64,{Convert.ToBase64String(font)})}}</style>"
            + $"<p style='font-family:f'>{Text}</p>";
        AssertStructurallyValid(Bounded(() => HtmlToPdf.Convert(html)));
    }

    // A language system that names one feature thousands of times, whose lookup indexes are those same thousands of
    // words, or thousands of features whose lookup arrays overlap: either asked for tens of millions of lookup indexes
    // (8,192 squared, half a gigabyte, and at 65,535 more than a list can hold) where the font has one distinct lookup.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Feature_lookups_that_fan_out_stay_bounded(bool overlappingFeatures)
    {
        var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansArabic-Subset.ttf"));
        var (gsub, lookup) = overlappingFeatures ? OverlappingFeatures(4096, 8192) : SharedFeatureIndexes(8192);
        var font = WithTable(original, "GSUB", gsub);
        var program = Assert.Single(FontProgram.Load(font));
        var lookups = Bounded(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = program.Layout!.FeatureLookups(false, FontProgram.Tag("arab"), FontProgram.Tag("ccmp"));
            return (result, Allocated: GC.GetAllocatedBytesForCurrentThread() - before);
        });
        Assert.InRange(lookups.Allocated, 0, 1 << 20);
        Assert.Equal([lookup], lookups.result);

        Bounded(() => Shape(program, true));
        var html = $"<style>@font-face{{font-family:f;src:url(data:font/ttf;base64,{Convert.ToBase64String(font)})}}</style>"
            + $"<p style='font-family:f'>{Text}</p>";
        AssertStructurallyValid(Bounded(() => HtmlToPdf.Convert(html)));
    }

    // A GSUB table whose DFLT language system names feature 0 (ccmp) n times, and whose ccmp names lookup 0 n times, in
    // the same n zero words: the feature table starts at the language system's feature index count.
    private static (byte[] Table, int Lookup) SharedFeatureIndexes(int n)
    {
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)(v >> 8)); t.Add((byte)v); }
        void Tag(string s) => t.AddRange(Encoding.ASCII.GetBytes(s));
        U16(1); U16(0); U16(10); U16(22); U16(30); // script list at 10, feature list at 22, lookup list at 30
        U16(1); Tag("DFLT"); U16(8); // the DFLT script at 18
        U16(22); U16(0); // its default language system at 40
        U16(1); Tag("ccmp"); U16(20); // ccmp at 42, which reads the language system's 0xFFFF and n
        U16(1); U16(4); // one lookup at 34
        U16(1); U16(0); U16(0); // single substitution with no subtables
        U16(0); U16(0xFFFF); U16(n); // the language system: no required feature and n feature indexes
        for (int i = 0; i < n; i++) U16(0);
        return (t.ToArray(), 0);
    }

    // A GSUB table whose DFLT language system names features 0 to n - 1, all ccmp, each starting one word further
    // into one run of words that all hold v: every feature names lookup v, v times.
    private static (byte[] Table, int Lookup) OverlappingFeatures(int n, int v)
    {
        var t = new List<byte>();
        void U16(int x) { t.Add((byte)(x >> 8)); t.Add((byte)x); }
        void Tag(string s) => t.AddRange(Encoding.ASCII.GetBytes(s));
        int features = 28 + n * 2, run = 2 + n * 6;
        U16(1); U16(0); U16(10); U16(features); U16(16); // script list at 10, lookup list at 16
        U16(1); Tag("DFLT"); U16(8); // the DFLT script at 18; the lookup list at 16 reads 8 lookups, none of them v
        U16(4); U16(0); // its default language system at 22
        U16(0); U16(0xFFFF); U16(n); // the language system: no required feature and n feature indexes
        for (int i = 0; i < n; i++) U16(i);
        U16(n);
        for (int i = 0; i < n; i++) { Tag("ccmp"); U16(run + i * 2); }
        for (int i = 0; i < n + v + 1; i++) U16(v);
        return (t.ToArray(), v);
    }

    private static List<int> Shape(FontProgram font, bool rtl)
    {
        var output = new List<ShapedGlyph>();
        ComplexShaper.Shape(font, Text, 0, Text.Length, rtl, output);
        return output.Select(g => g.Gid).ToList();
    }

    private static byte[] WithTable(byte[] font, string tag, byte[] table)
    {
        var p = Assert.Single(FontProgram.Load(font));
        var tables = p.Tables.Select(t => (t.Tag, t.Tag == FontProgram.Tag(tag) ? table : p.Data.AsSpan(t.Offset, t.Length).ToArray())).ToList();
        return SfntBuilder.Build(0x00010000, tables);
    }

    /// <summary>
    /// A GSUB or GPOS table whose DFLT script (which Arabic falls back to) has one feature the shaper applies to Arabic
    /// with one contextual lookup: GSUB lookup type 5 or GPOS lookup type 7.
    /// </summary>
    private static byte[] LayoutTable(bool gpos, bool sharedRules)
    {
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)(v >> 8)); t.Add((byte)v); }
        void Tag(string s) => t.AddRange(Encoding.ASCII.GetBytes(s));

        // Header: version 1.0 and the script, feature and lookup lists at 10, 30 and 44.
        U16(1); U16(0); U16(10); U16(30); U16(44);
        // Script list: DFLT, its script table (a default language system right after it) and that language system,
        // whose only feature is feature 0.
        U16(1); Tag("DFLT"); U16(8);
        U16(4); U16(0);
        U16(0); U16(0xFFFF); U16(1); U16(0);
        // Feature list: feature 0 has lookup 0.
        U16(1); Tag(gpos ? "mark" : "ccmp"); U16(8);
        U16(0); U16(1); U16(0);
        // Lookup list: lookup 0 right after it, with no lookup flags.
        U16(1); U16(4);
        U16(gpos ? 7 : 5); U16(0);
        if (!sharedRules)
        {
            // One subtable of format 3: one input glyph, covered whatever it is, and records that apply lookup 0 at it.
            U16(1); U16(8); // one subtable, right after the lookup
            U16(3); U16(1); U16(SelfApplications); U16(8 + SelfApplications * 4);
            for (int r = 0; r < SelfApplications; r++) { U16(0); U16(0); }
            CoverAll();
        }
        else
        {
            // Many subtables of format 2 that are all the same one: every glyph is of class 0, whose rule set holds
            // many offsets of one rule that wants a glyph of class 1 next, which no glyph is.
            U16(SharedCount);
            int subtable = 6 + SharedCount * 2;
            for (int s = 0; s < SharedCount; s++) U16(subtable);
            int sub = t.Count;
            U16(2); U16(10); U16(20); U16(1); U16(24); // format, coverage, class definitions, one rule set
            CoverAll();
            U16(2); U16(0); // class definitions of format 2 with no ranges
            int set = t.Count - sub;
            U16(SharedCount);
            for (int r = 0; r < SharedCount; r++) U16(2 + SharedCount * 2);
            U16(2); U16(0); U16(1); // two input glyphs, no records, the second of class 1
            if (set != 24) throw new InvalidOperationException("The rule set is not where the subtable says.");
        }
        return t.ToArray();

        // A coverage table of format 2 with one range of every glyph id.
        void CoverAll() { U16(2); U16(1); U16(0); U16(0xFFFF); U16(0); }
    }
}

/// <summary>
/// Hostile GSUB tables whose multiple substitutions grow the glyph run: lookups that each expand every glyph into a
/// long sequence, so that the second multiplies what the first made, and a long expanded run that a deletion then
/// shrinks one glyph at a time.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class GlyphRunGrowthTests
{
    private static T Bounded<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.Result;
    }

    [Theory]
    // Two lookups that turn every glyph into 30,000: some billions of glyphs for a few words.
    [InlineData(1, 30000, 30000, -1)]
    // The same with 1,000, which the run has room for a few times.
    [InlineData(1, 1000, 1000, -1)]
    // One lookup that turns every glyph of a long text into 64, and one that deletes every glyph again.
    [InlineData(100, 64, 0, 0)]
    public void Multiple_substitutions_do_not_grow_the_run_without_bound(int repeat, int first, int second, int expected)
    {
        var text = string.Concat(Enumerable.Repeat("كمثرى بطيخ تفاح موز برتقال عنب ", repeat));
        var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansArabic-Subset.ttf"));
        var program = Assert.Single(FontProgram.Load(original));
        int glyph = program.GlyphId('ب');
        var font = WithGsub(original, program.NumGlyphs, Enumerable.Repeat(glyph, first).ToArray(), Enumerable.Repeat(glyph, second).ToArray());
        var hostile = Assert.Single(FontProgram.Load(font));
        foreach (bool rtl in new[] { true, false })
        {
            var glyphs = Bounded(() =>
            {
                var output = new List<ShapedGlyph>();
                ComplexShaper.Shape(hostile, text, 0, text.Length, rtl, output);
                return output;
            });
            if (expected >= 0) Assert.Equal(expected, glyphs.Count);
            else Assert.InRange(glyphs.Count, text.Length, OpenTypeLayout.MaxRunLength(text.Length));
        }

        var html = $"<style>@font-face{{font-family:f;src:url(data:font/ttf;base64,{Convert.ToBase64String(font)})}}</style>"
            + $"<p style='font-family:f'>{text}</p>";
        AssertStructurallyValid(Bounded(() => HtmlToPdf.Convert(html)));
    }

    // The font with a GSUB table whose DFLT script (which Arabic falls back to) has a ccmp feature with one multiple
    // substitution per sequence, each replacing every glyph of the font with that sequence; an empty one deletes it.
    private static byte[] WithGsub(byte[] font, int glyphs, params int[][] sequences)
    {
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)(v >> 8)); t.Add((byte)v); }
        void Tag(string s) => t.AddRange(Encoding.ASCII.GetBytes(s));

        int lookups = sequences.Length, lookupList = 42 + lookups * 2;
        // Header: version 1.0 and the script, feature and lookup lists.
        U16(1); U16(0); U16(10); U16(30); U16(lookupList);
        // Script list: DFLT, its script table (a default language system right after it) and that language system,
        // whose only feature is feature 0.
        U16(1); Tag("DFLT"); U16(8);
        U16(4); U16(0);
        U16(0); U16(0xFFFF); U16(1); U16(0);
        // Feature list: feature 0 has every lookup, in order.
        U16(1); Tag("ccmp"); U16(8);
        U16(0); U16(lookups);
        for (int l = 0; l < lookups; l++) U16(l);
        // Lookup list: the offsets of the lookups, then each lookup with its one subtable right after it.
        U16(lookups);
        int offsets = t.Count;
        for (int l = 0; l < lookups; l++) U16(0);
        foreach (var sequence in sequences)
        {
            int lookup = t.Count - lookupList;
            if (lookup > 0xFFFF) throw new InvalidOperationException("The lookup is past the reach of its 16-bit offset.");
            t[offsets] = (byte)(lookup >> 8);
            t[offsets + 1] = (byte)lookup;
            offsets += 2;
            U16(2); U16(0); U16(1); U16(8);
            // Format 1, the coverage after the sequence offsets, and one offset per glyph, all of the one sequence.
            int coverage = 6 + glyphs * 2;
            U16(1); U16(coverage); U16(glyphs);
            for (int g = 0; g < glyphs; g++) U16(coverage + 10);
            // A coverage table of format 2 with one range of every glyph, so that a glyph's coverage index is its id.
            U16(2); U16(1); U16(0); U16(glyphs - 1); U16(0);
            U16(sequence.Length);
            foreach (int g in sequence) U16(g);
        }

        var p = Assert.Single(FontProgram.Load(font));
        var tables = p.Tables.Select(x => (x.Tag, x.Tag == FontProgram.Tag("GSUB") ? t.ToArray() : p.Data.AsSpan(x.Offset, x.Length).ToArray())).ToList();
        return SfntBuilder.Build(0x00010000, tables);
    }
}

/// <summary>
/// Mesh shadings (types 4 to 7) with bit widths the PDF specification does not allow, or with far more vertices and
/// patches than a page can show: zero widths read nothing, so the walk never advanced, a width sum that overflowed
/// read billions of bits past the data, and a huge VerticesPerRow allocated rows of that size.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class MeshShadingLimitTests
{
    private const string Rgb = "/ColorSpace /DeviceRGB /Decode [0 40 0 40 0 1 0 1 0 1]";

    // The page paints the shading the given number of times, so that a walk that takes seconds each time adds up to
    // more than the time bound.
    private static RenderedImage RenderShading(string entries, byte[] data, int size = 40, int repeat = 1)
    {
        var pdf = new FixturePdf();
        int sh = pdf.AddStream(entries, data);
        var bytes = pdf.Build(size, size, string.Join(" ", Enumerable.Repeat("/Sh sh", repeat)), $"/Shading << /Sh {sh} 0 R >>");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = Task.Run(() => PdfRenderer.Open(bytes).RenderPage(0, new ImageOptions { Dpi = 72 }, cts.Token));
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "rendering the mesh did not finish in time");
        return task.Result;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Zero_bit_widths_paint_nothing(int type)
    {
        var img = RenderShading($"/ShadingType {type} /VerticesPerRow 2 /BitsPerCoordinate 0 /BitsPerComponent 0 /BitsPerFlag 0 {Rgb}", new byte[] { 1, 2, 3 });
        FixturePdf.AssertAt(img, 20, 20, 255, 255, 255);
    }

    [Theory]
    [InlineData("/BitsPerCoordinate 64 /BitsPerComponent 16 /BitsPerFlag 2147483647")]
    [InlineData("/BitsPerCoordinate 1073741824 /BitsPerComponent 8 /BitsPerFlag 8")]
    [InlineData("/BitsPerCoordinate 8 /BitsPerComponent -8 /BitsPerFlag 8")]
    [InlineData("/BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 3")]
    public void Bit_widths_outside_the_specification_paint_nothing(string widths)
    {
        var data = new byte[164];
        new Random(1158).NextBytes(data);
        foreach (int type in new[] { 4, 6, 7 })
        {
            var img = RenderShading($"/ShadingType {type} {widths} {Rgb}", data, repeat: 20);
            FixturePdf.AssertAt(img, 20, 20, 255, 255, 255);
        }
    }

    [Fact]
    public void Huge_vertices_per_row_paints_nothing()
    {
        byte[] data = { 4, 4, 255, 0, 0, 36, 4, 255, 0, 0, 4, 36, 255, 0, 0, 36, 36, 255, 0, 0 };
        var img = RenderShading($"/ShadingType 5 /VerticesPerRow 2000000000 /BitsPerCoordinate 8 /BitsPerComponent 8 {Rgb}", data);
        FixturePdf.AssertAt(img, 20, 20, 255, 255, 255);
    }

    [Fact]
    public void Many_page_sized_patches_stop_at_the_work_budget()
    {
        // Twenty thousand Coons patches that each cover the whole page are split into thousands of triangles each; the
        // budget stops the walk long before the end, after the first patches have painted the page.
        const int size = 400, patches = 20000;
        int[] boundary = { 0, 0, 0, 85, 0, 170, 0, 255, 85, 255, 170, 255, 255, 255, 255, 170, 255, 85, 255, 0, 170, 0, 85, 0 };
        var patch = new List<byte> { 0 };
        foreach (int v in boundary) patch.Add((byte)v);
        for (int i = 0; i < 4; i++) patch.AddRange(new byte[] { 255, 0, 0 });
        var data = new byte[patch.Count * patches];
        for (int i = 0; i < patches; i++) patch.CopyTo(data, i * patch.Count);
        var img = RenderShading($"/ShadingType 6 /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /ColorSpace /DeviceRGB /Decode [0 {size} 0 {size} 0 1 0 1 0 1]",
            data, size);
        FixturePdf.AssertAt(img, size / 2, size / 2, 255, 0, 0);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostileInputCollection
{
    public const string Name = "Hostile input";
}
