using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;
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
    public void Content_moved_out_of_tables_stays_linear()
    {
        // Each stray node between rows goes in front of its table; inserting at the table's place must not search or
        // shift its parent's children for every node. Tables nested in the moved content repeat it at every level.
        var parse = Bounded(() => Html.HtmlParser.Parse("<table>" + Repeat("<b>x</b>y<input>", 100_000) + "<tr><td>1</table>"));
        Assert.Equal("table", ((Html.Element)parse.Children[^1]).Name);
        AssertStructurallyValid(Bounded("<table>" + Repeat("<div>a<table>b<tr>", 200) + "<td>c</table>"));
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

    [Theory]
    [InlineData("\u05A7\uFFFD")]
    [InlineData("\u0301hyphenation")]
    [InlineData("\u0301\u0302\u0303")]
    public void Hyphenating_a_word_that_starts_with_a_combining_mark_does_not_recurse(string word)
    {
        // Found by the fuzzer: target html, crash-fc14afb8. A word with combining marks is composed letter by letter
        // for the patterns, but a mark with no letter before it stays a mark, so the composed word was composed again
        // until the stack overflowed and the process died.
        AssertStructurallyValid(Bounded($"<p lang=en style='hyphens: auto'>{word}</p>"));
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

    // Fuzz finding (css, timeout-d0d017cf): a variable that names itself in its reference and again in each nested
    // fallback was resolved twice per level down to the depth limit, about 2^32 substitutions.
    [Fact]
    public void Self_referencing_custom_property_with_nested_fallbacks_is_linear()
    {
        var value = new StringBuilder();
        for (int i = 0; i < 40; i++) value.Append("var(--a, ");
        value.Append("10px").Append(')', 40);
        var doc = Bounded(() => Styled($"<style>:root{{--a:{value}}} p{{color:var(--a, lime)}}</style><p id=a>a</p>"));
        Assert.Equal(0xFF00FF00u, ById(doc, "a").Style.Color);
    }

    // The same blow-up without a cycle: each variable names the next one twice, and every one ends up empty.
    [Fact]
    public void Custom_properties_that_resolve_to_nothing_are_resolved_once()
    {
        var sb = new StringBuilder("<style>:root{");
        for (int i = 0; i < 40; i++) sb.Append($"--a{i}:var(--a{i + 1},)var(--a{i + 1},);");
        sb.Append("--a40:var(--none,)} p{margin-left:var(--a0) 1pt}</style><p>text</p>");
        AssertStructurallyValid(Bounded(sb.ToString()));
    }

    [Fact]
    public void Custom_properties_on_a_reference_cycle_take_the_fallback()
    {
        var doc = Styled("<style>:root{--a:var(--b, red); --b:var(--a, red); --c:var(--a, blue)} #a{color:var(--a, lime)}"
            + " #b{color:var(--b, lime)} #c{color:var(--c)}</style><p id=a>a</p><p id=b>b</p><p id=c>c</p>");
        Assert.Equal(0xFF00FF00u, ById(doc, "a").Style.Color);
        Assert.Equal(0xFF00FF00u, ById(doc, "b").Style.Color);
        Assert.Equal(0xFF0000FFu, ById(doc, "c").Style.Color);
    }

    // Fuzz finding (html, memory-0aab5f6e): a huge negative padding made the line billions of points wide, and its
    // leader built a string of a billion dots.
    [Fact]
    public void Leader_on_a_huge_line_is_capped()
    {
        AssertStructurallyValid(Bounded("<style>\"\"{}ul{padding:-1e38}:after{content:leader(dotted)target-counter(,)</style><ul"));
    }

    [Fact]
    public void Nested_rules_that_repeat_the_parent_selector_stay_small()
    {
        // Four "&" per level would make selectors 4^16 characters long at the deepest nesting level.
        var css = ".a { " + Repeat("& & & & { color: red; ", 15) + Repeat("} ", 16);
        AssertStructurallyValid(Bounded($"<style>{css} p {{ color: blue }}</style><p class=a>x</p>"));
        var doc = Styled("<style>.a { & .b { color: lime } & + & { color: blue } }</style>"
            + "<div class=a><p id=x class=b>x</p></div><p id=y class=a>y</p><p id=z class=a>z</p>");
        Assert.Equal(0xFF00FF00u, ById(doc, "x").Style.Color);
        Assert.Equal(0xFF0000FFu, ById(doc, "z").Style.Color);
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

    [Theory]
    [InlineData("<h3>Contents</h3><table><tr><td>", "<p>end</p>")]
    [InlineData("<h3>Contents</h3><table><tr><td>", "<p style='margin-top:900pt'>end</p>")]
    // Vertical alignment, borders and backgrounds are applied to a cell after its content, and can only move the first
    // content down.
    [InlineData("<h3>Contents</h3><table style='border-collapse:collapse;background:#eee'><tr><td style='vertical-align:middle;border:1pt solid'>", "<p>end</p>")]
    [InlineData("<h3>Contents</h3><table><tr><td style='overflow:hidden'></td><td>", "<p style='margin-top:900pt'>end</p>")]
    public void Headings_kept_with_nested_tables_do_not_lay_out_each_level_twice(string level, string end)
    {
        // As above, but the box after each heading is a table whose first cell holds the next heading. A table was laid
        // out in full before it was clear that its heading has to move to the next page, and then again after the move,
        // so each level laid out the levels below it twice: 16 levels took 70 ms, and each further level nearly doubled it.
        AssertStructurallyValid(Bounded(Repeat(level, 50) + end));
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

    [Theory]
    // The fuzz finding (html, timeout-1abcdf31 of a later run): unclosed cards nest flex rows of 50% and pages of 100%
    // with padding, on a first page whose margins leave no room, so every percentage height comes to zero.
    [InlineData("<div style='display:flex;height:50%'><div style='box-sizing:border-box;height:100%;padding-top:4mm'>", 100)]
    [InlineData("<div style='display:flex;height:100%'><div style='box-sizing:border-box;height:100%;border-top:1pt solid'>", 100)]
    [InlineData("<div style='display:flex;height:100%'><div style='box-sizing:border-box;height:100%;padding-top:5%'>", 100)]
    public void Nested_flex_items_whose_percentage_height_is_below_their_frame_are_laid_out_once(string open, int levels)
    {
        // A flex item with a percentage height is laid out again at that height when it came out a different height,
        // but a box cannot be shorter than its padding and borders, so one whose percentage comes to less than those
        // came out the same and was laid out again for nothing, and so was every level of flex rows inside it, once
        // per level above it: 25 levels took 38 s.
        var html = "<style>html, body { height: 100%; margin: 0 }</style><div style='height:0'>" + Repeat(open, levels) + "x"
                   + Repeat("</div></div>", levels) + "</div>";
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
    public void Huge_svg_dash_arrays_are_ignored()
    {
        // A million dash lengths on each of many strokes would be written out again for every one of them.
        var dashes = Repeat("1 ", 1_000_000);
        var pdf = Bounded($"<svg width='400' height='400'><g stroke-dasharray='{dashes}'>{Repeat("<line x2='400' y2='400' stroke='red'/>", 200)}</g></svg>",
            new PdfOptions { Compress = false });
        AssertStructurallyValid(pdf);
        Assert.DoesNotContain("[1 1 ", Latin1(pdf));
    }

    [Fact]
    public void Svg_text_paths_are_bounded()
    {
        // A path of more commands than text is laid along is not measured, and long text along a long curved path is
        // placed glyph by glyph within the drawing's budget.
        var huge = "<path id='h' d='M0 0 " + Repeat("L400 400 L0 0 ", 15_000) + "'/>";
        var curvy = "<path id='c' d='M0 200 " + Repeat("q 2 -50 4 0 t 4 0 ", 2_000) + "'/>";
        var text = Repeat("text on a path ", 20_000);
        var pdf = Bounded($"<svg width='600' height='400'>{huge}{curvy}{Repeat("<text><textPath href='#h'>x</textPath></text>", 1_000)}"
            + Repeat($"<text font-size='4'><textPath href='#c'>{text}</textPath></text>", 20) + "</svg>", new PdfOptions { Compress = false });
        AssertStructurallyValid(pdf);
        Bounded(() => PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 }));
    }

    [Theory]
    [InlineData("<text id='t'><textPath id='tp' href='#tp'>self</textPath></text>")]
    [InlineData("<text id='t'><textPath href='#t'>its own text</textPath></text>")]
    [InlineData("<text><textPath href='#nothing'>missing</textPath></text>")]
    [InlineData("<path id='p' d='M0 0'/><text><textPath href='#p'>no length</textPath></text>")]
    [InlineData("<path id='p' d='M0 0 L1e38 1e38 L-1e38 -1e38'/><text><textPath href='#p' startOffset='1e38'>far</textPath></text>")]
    [InlineData("<path id='p' d='M0 0 L100 100'/><text><textPath href='#p'><textPath href='#p'><textPath href='#p'>nested</textPath></textPath></textPath></text>")]
    public void Svg_text_paths_with_broken_references_draw_nothing_or_little(string content)
        => AssertStructurallyValid(Bounded($"<svg width='200' height='200'>{content}</svg>"));

    [Fact]
    public void Tiny_svg_dashes_on_a_long_stroke_render_within_bounds()
    {
        // Dashes far below a pixel would make the renderer stroke hundreds of millions of segments.
        var pdf = Bounded("<svg width='600' height='600'><path d='M0 0 " + Repeat("L600 600 L0 0 ", 500) + "' stroke='red' stroke-width='2' stroke-dasharray='0.001 0.001'/></svg>");
        AssertStructurallyValid(pdf);
        Bounded(() => PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 300 }));
    }

    [Fact]
    public void Page_count_is_limited()
    {
        Assert.Throws<PdfLimitException>(() => Bounded("<div style='height:100000000px'>x</div>", new PdfOptions { MaxPages = 1000 }));
        // A top margin that reaches past the end of a page is cut off at the break, as in Chrome: the paragraph starts
        // the second page instead of one far down the document.
        Assert.Equal(2, PageCount(Bounded("<p style='margin-top:1e30px'>x</p>")));
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

    [Theory]
    // Every line paints a fragment for each decorated inline box open on it, so deep nesting multiplied the work by
    // depth times lines: the first of these ran out of memory, and background-clip: text copied the line's text into
    // every fragment.
    [InlineData("background:linear-gradient(red,blue);border-radius:3px;box-shadow:0 0 2px #000;outline:1px solid red", 50_000)]
    [InlineData("background:linear-gradient(red,blue);background-clip:text;color:transparent", 20_000)]
    [InlineData("box-shadow:0 0 2px #000", 20_000)]
    [InlineData("background:linear-gradient(red,blue)", 20_000)]
    public void Deeply_nested_decorated_inline_boxes_stay_bounded(string style, int words)
    {
        string html = "<p>" + Repeat($"<span style=\"{style}\">", 250) + Repeat("word ", words) + Repeat("</span>", 250) + "</p>";
        var pdf = Bounded(() => Bounded(html));
        AssertStructurallyValid(pdf);
        Assert.True(pdf.Length < 16 << 20, $"{pdf.Length} bytes");
    }

    [Fact]
    public void Inline_boxes_inside_the_depth_limit_still_paint_their_backgrounds()
    {
        string html = "<p>" + Repeat("<span style=\"background:#00ff00\">", 15) + "word</span></p>";
        var text = Latin1(Bounded(html, new PdfOptions { Compress = false }));
        Assert.Contains("0 1 0 rg", text);
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
    public void Repeating_gradients_share_one_tiling_budget()
    {
        // Each box tiles twenty repeating gradients over 250 periods, about 33 KB of PDF each: four kilobytes of HTML
        // wrote 440 MB and took 2.3 GB of memory before the document's gradients shared one budget.
        var gradient = "repeating-linear-gradient(red 0 0.06px,blue 0.06px 0.12px)";
        var html = "<style>i{display:block;height:30px;background:" + string.Join(",", Enumerable.Repeat(gradient, 20)) + "}</style>"
                   + Repeat("<i></i>", 400);
        var (pdf, allocated) = Allocating(() => Bounded(html, new PdfOptions { Compress = false }));
        AssertStructurallyValid(pdf);
        Assert.InRange(pdf.Length, 1, 64 << 20);
        Assert.InRange(allocated, 0, 512L << 20);
        // The first boxes are tiled; once the budget is spent, the rest are painted once, padded.
        var text = Latin1(pdf);
        Assert.Contains("/FunctionType 3 /Domain [0 250]", text);
        Assert.Contains("/Domain [0 1] /Extend [true true]", text);

        // A gradient with thousands of stops is not tiled at all: each period would copy all of them.
        var stops = string.Join(",", Enumerable.Range(0, 2000).Select(i => $"#{i % 256:x2}0000 {i * 0.001:0.###}px"));
        pdf = Bounded($"<div style='height:30px;background:repeating-linear-gradient({stops})'></div>", new PdfOptions { Compress = false });
        Assert.DoesNotContain("/Domain [0 15]", Latin1(pdf));
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

    [Fact]
    public void Markdown_footnotes_by_the_thousand_stay_linear()
    {
        // Each footnote cites the next, around in a circle, and one footnote has thousands of references, each with a
        // link back: every footnote's text is still rendered once.
        const int n = 5000;
        var md = Repeat("a[^1] ", n) + "\n\n" + string.Concat(Enumerable.Range(0, n).Select(i => $"b[^n{i}]\n"))
                 + "\n[^1]: one\n" + string.Concat(Enumerable.Range(0, n).Select(i => $"[^n{i}]: Note {i} [^n{(i + 1) % n}]\n"));
        var html = Bounded(() => MarkdownToHtml.Convert(md));
        Assert.Equal(n + 1, CountOf(html, "<li id=\"fn-"));
        Assert.Equal(n - 1, CountOf(html, "href=\"#fnref-1-"));

        // In a PDF every footnote floats to the bottom of a page.
        const int m = 1000;
        var notes = string.Concat(Enumerable.Range(0, m).Select(i => $"b[^n{i}]\n")) + "\n"
                    + string.Concat(Enumerable.Range(0, m).Select(i => $"[^n{i}]: Note {i} [^n{(i + 1) % m}]\n"));
        AssertStructurallyValid(Bounded(() => MarkdownToPdf.Convert(notes)));
    }

    [Fact]
    public void Markdown_definition_and_footnote_nesting_is_capped()
    {
        var nestedTerms = string.Concat(Enumerable.Range(0, 1000).Select(i => new string(' ', 2 * i) + "t\n" + new string(' ', 2 * i) + ": d\n"));
        var html = Bounded(() => MarkdownToHtml.Convert(nestedTerms));
        // A list and its definition are two levels of nesting.
        Assert.Equal(Markdown.BlockParser.MaxNesting / 2, CountOf(html, "<dl>"));
        html = Bounded(() => MarkdownToHtml.Convert(Repeat("[^a]: ", 200000) + "x [^a]\n\nText [^a]"));
        Assert.Equal(1, CountOf(html, "<li id=\"fn-"));
        AssertStructurallyValid(Bounded(() => MarkdownToPdf.Convert(nestedTerms + Repeat("[^a]: ", 20000) + "x [^a]\n\nText [^a]")));
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

    // A PDF without pages used to reach the TIFF encoder and fail with an ArgumentException naming its internal
    // parameter; it is malformed input, so every TIFF entry point reports it as InvalidDataException.
    [Fact]
    public async Task Pdf_without_pages_cannot_become_a_tiff()
    {
        var pdf = SimplePdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
        });

        Assert.Throws<InvalidDataException>(() => PdfToImage.ConvertToTiff(pdf));
        Assert.Throws<InvalidDataException>(() => PdfToImage.ConvertToTiff(pdf, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => PdfToImage.ConvertToTiffAsync(pdf));
        Assert.Throws<InvalidDataException>(() => PdfRenderer.Open(pdf).RenderAllToTiff());
        var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => PdfToImage.ConvertToTiff(new MemoryStream(pdf), output));
        Assert.Equal(0, output.Length);

        var dir = Directory.CreateTempSubdirectory("pdfpecker-tiff-");
        try
        {
            var pdfPath = Path.Combine(dir.FullName, "empty.pdf");
            var tiffPath = Path.Combine(dir.FullName, "empty.tif");
            File.WriteAllBytes(pdfPath, pdf);
            Assert.Throws<InvalidDataException>(() => PdfToImage.ConvertFileToTiff(pdfPath, tiffPath));
            Assert.False(File.Exists(tiffPath));
        }
        finally { dir.Delete(recursive: true); }
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

    [Theory]
    [InlineData("<div style='display: flex'><aside style='width: 2px'>x</aside><div>", "</div></div>")]
    [InlineData("<div style='display: grid; grid-template-columns: 2px 1fr'><aside>x</aside><div>", "</div></div>")]
    public void Nested_flex_and_grid_items_across_pages_are_laid_out_again_a_bounded_number_of_times(string open, string close)
    {
        // An item that lands across a page boundary is laid out again there, and a flex line that grows lays out its
        // stretched items once more; at every level of nesting that doubled the work below it.
        var table = "<table>" + Repeat("<tr><td style='height: 40pt'>row</td></tr>", 200) + "</table>";
        AssertStructurallyValid(Bounded(Repeat(open, 30) + table + Repeat(close, 30)));
    }

    /// <summary>A one-page form whose AcroForm lists object 4 as its only field and asks for every appearance.</summary>
    private static byte[] FormPdf(string field, IEnumerable<string>? more = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true /DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 14400 14400] >>",
            field,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (more != null) objects.AddRange(more);
        return SimplePdf(objects);
    }

    [Fact]
    public void Long_values_in_a_wide_multiline_field_wrap_in_linear_time()
    {
        // Wrapping measured the whole line again for every word, up to 17 times while finding the automatic size, so a
        // box as wide as a page that holds thousands of words per line took minutes.
        var pdf = FormPdf("<< /FT /Tx /Subtype /Widget /T (t) /Ff 4096 /DA (/Helv 0 Tf 0 g) /Rect [0 0 14400 100] /V (" + Repeat("a ", 400_000) + ") >>");
        Assert.NotEmpty(Bounded(() => PdfForm.Fill(pdf, new Dictionary<string, string>())));
    }

    [Fact]
    public void A_list_box_with_a_tiny_font_draws_a_bounded_number_of_rows()
    {
        // A font size of a hundredth of a point drew every one of the options in every widget, and each option was
        // looked up in the list of selected values one by one.
        var options = string.Concat(Enumerable.Range(0, 20_000).Select(i => "(o" + i + ") "));
        var widgets = Enumerable.Range(0, 50).Select(_ => "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [0 0 100 14000] >>").ToList();
        var kids = string.Concat(Enumerable.Range(6, widgets.Count).Select(i => i + " 0 R "));
        var pdf = FormPdf($"<< /FT /Ch /T (l) /DA (/Helv 0.01 Tf 0 g) /Opt [{options}] /V [{options}] /Kids [{kids}] >>", widgets);

        var filled = Bounded(() => PdfForm.Fill(pdf, new Dictionary<string, string>()));
        var file = PdfFile.Open(filled, null);
        var widget = file.Resolve(file.GetArray(file.Resolve(file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")![0]) as PdfDict, "Kids")![0]) as PdfDict;
        var appearance = Encoding.Latin1.GetString(file.DecodeStream(file.GetStream(file.GetDict(widget, "AP"), "N")!));
        Assert.InRange(System.Text.RegularExpressions.Regex.Count(appearance, " Tj"), 1, 1_000);
    }

    [Fact]
    public void Long_field_names_repeated_over_many_kids_stay_bounded()
    {
        // Each kid's fully qualified name repeats its parent's, so a two-megabyte name over a thousand kids made two
        // gigabytes of names when listing the fields or looking for signatures.
        var kids = Enumerable.Range(0, 1_000).Select(i => "<< /T (k" + i + ") /FT /Tx /Parent 4 0 R >>").ToList();
        var refs = string.Concat(Enumerable.Range(6, kids.Count).Select(i => i + " 0 R "));
        var pdf = FormPdf("<< /T (" + Repeat("n", 2 * 1024 * 1024) + ") /Kids [" + refs + "] >>", kids);

        Assert.InRange(Bounded(() => PdfForm.GetFields(pdf)).Count, 1, 10);
        Assert.Empty(Bounded(() => PdfSignatureVerifier.Verify(pdf)));
    }

    [Fact]
    public void Signature_fields_that_share_one_signature_are_verified_once()
    {
        // A thousand fields pointing at one signature dictionary each hashed the file and built a chain again.
        // Object 6 is the shared signature, and the fields follow it.
        var more = new List<string> { "<< /Type /Sig /ByteRange [0 10 20 5] /Contents <00> >>" };
        more.AddRange(Enumerable.Range(0, 1_000).Select(i => "<< /FT /Sig /T (s" + i + ") /V 6 0 R >>"));
        var refs = string.Concat(Enumerable.Range(7, 1_000).Select(i => i + " 0 R "));
        var pdf = FormPdf("<< /T (p) /Kids [" + refs + "] >>", more);

        Assert.Single(Bounded(() => PdfSignatureVerifier.Verify(pdf)));
    }

    [Fact]
    public void Long_names_in_a_pdf_are_not_kept_for_the_process()
    {
        var name = Repeat("n", 1_000);
        Assert.NotSame(PdfName.Get(name), PdfName.Get(name));
        Assert.Same(PdfName.Get("Type"), PdfName.Get("Type"));
    }

    [Fact]
    public void Compacting_keeps_a_stream_that_decodes_past_the_limit_as_it_is()
    {
        // 300 MB of zeros deflate to a few hundred kilobytes; the decoder stops at its 256 MB limit, and the cut data
        // must not replace the stream.
        var zeros = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(zeros, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var block = new byte[1 << 20];
            for (int i = 0; i < 300; i++) z.Write(block);
        }
        var bomb = zeros.ToArray();
        var head = Encoding.Latin1.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"
            + "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>\nendobj\n4 0 obj\n<< /Length " + bomb.Length
            + " /Filter /FlateDecode >>\nstream\n");
        var pdf = head.Concat(bomb).Concat(Encoding.Latin1.GetBytes("\nendstream\nendobj\ntrailer << /Root 1 0 R >>\n%%EOF")).ToArray();
        var compact = Bounded(() => PdfCompactor.Compact(pdf));
        var file = PdfFile.Open(compact, null);
        var page = (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")![0])!;
        Assert.Equal(bomb, file.GetStream(page, "Contents")!.RawData);
    }

    [Fact]
    public void Compacting_gives_up_finding_image_sizes_in_forms_that_fan_out()
    {
        // Twelve levels of forms, each drawing the next ten times, would be drawn 10^12 times. The walk that finds how
        // large images are drawn stops at its limit, and then no image is downsampled on what it saw so far.
        const int Levels = 12;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /F 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 100 0 0 100 0 0 cm /F Do Q"),
        };
        for (int level = 0; level < Levels; level++)
        {
            int next = objects.Count + 2;
            string draw = level == Levels - 1 ? "/I Do" : Repeat("/F Do ", 10);
            string resources = level == Levels - 1 ? $"/XObject << /I {next} 0 R >>" : $"/XObject << /F {next} 0 R >>";
            objects.Add(Stream(draw, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << {resources} >>"));
        }
        objects.Add(Stream(Repeat("A", 600 * 600), "/Type /XObject /Subtype /Image /Width 600 /Height 600 /ColorSpace /DeviceGray /BitsPerComponent 8"));
        var compact = Bounded(() => PdfCompactor.Compact(SimplePdf(objects), new PdfCompactOptions { MaxImageDpi = 10 }));
        var file = PdfFile.Open(compact, null);
        var image = Enumerable.Range(1, file.MaxObjectNumber).Select(file.GetObject).OfType<PdfStream>().Single(s => file.GetName(s, "Subtype") == "Image");
        Assert.Equal(600, file.GetInt(image, "Width", 0));

        static string Stream(string data, string entries = "") => $"<< {entries} /Length {data.Length} >>\nstream\n{data}\nendstream";
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

    /// <summary>A PDF whose embedded-files list has many entries, all pointing at one stream of 200 MB of zeros.</summary>
    private static byte[] SharedAttachmentBomb(int entries, string extension)
    {
        var bomb = new MemoryStream();
        using (var z = new ZLibStream(bomb, CompressionLevel.SmallestSize, leaveOpen: true))
            for (int i = 0; i < 200; i++) z.Write(new byte[1 << 20]);
        static byte[] L(string t) => Encoding.Latin1.GetBytes(t);
        var names = new StringBuilder();
        for (int i = 0; i < entries; i++) names.Append($"(f{i:D5}{extension}) {6 + i} 0 R ");
        var objects = new List<byte[]>
        {
            L("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 5 0 R >> >>\nendobj\n"),
            L("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"),
            L("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\n"),
            L("4 0 obj\n<< /Type /EmbeddedFile /Length " + bomb.Length + " /Filter /FlateDecode >>\nstream\n").Concat(bomb.ToArray()).Concat(L("\nendstream\nendobj\n")).ToArray(),
            L("5 0 obj\n<< /Names [" + names + "] >>\nendobj\n"),
        };
        // Each entry has a file specification of its own, so de-duplicating specifications does not help.
        for (int i = 0; i < entries; i++)
            objects.Add(L($"{6 + i} 0 obj\n<< /Type /Filespec /F (f{i:D5}{extension}) /EF << /F 4 0 R >> >>\nendobj\n"));
        var ms = new MemoryStream();
        ms.Write(L("%PDF-1.7\n"));
        var offsets = new List<long>();
        foreach (var o in objects) { offsets.Add(ms.Position); ms.Write(o); }
        long xref = ms.Position;
        var sb = new StringBuilder("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        ms.Write(L(sb.ToString()));
        return ms.ToArray();
    }

    [Fact]
    public void Many_attachments_sharing_one_compression_bomb_share_one_decode_budget()
    {
        // Each entry would decode the 200 MB stream again: 80 GB in all.
        var pdf = SharedAttachmentBomb(400, ".bin");
        var task = Task.Run(() => PdfAttachments.List(pdf));
        Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(20)), "Did not finish in time");
        Assert.IsType<InvalidDataException>(task.Exception?.InnerException);
    }

    [Fact]
    public void Many_einvoice_candidates_sharing_one_compression_bomb_are_cheap()
    {
        var pdf = SharedAttachmentBomb(400, ".xml");
        Assert.Null(Bounded(() => PdfEInvoice.Read(pdf)));
    }

    [Fact]
    public void Xmp_properties_that_never_close_are_matched_in_linear_time()
    {
        var xmp = Repeat("<x:DocumentFileName>", 1) + Repeat("y:DocumentFileName>abc ", 100_000);
        Assert.Null(Bounded(() => PdfPecker.Pdf.EmbeddedFiles.XmpProperty(xmp, "DocumentFileName")));
        Assert.Equal("factur-x.xml", PdfPecker.Pdf.EmbeddedFiles.XmpProperty(
            "<rdf:Description fx:DocumentFileName=\"factur-x.xml\"/>", "DocumentFileName"));
        Assert.Equal("EN 16931", PdfPecker.Pdf.EmbeddedFiles.XmpProperty(
            "<fx:ConformanceLevel> EN 16931 </fx:ConformanceLevel>", "ConformanceLevel"));
    }

    [Fact]
    public void Narrow_column_width_on_a_huge_box_is_capped_at_a_hundred_columns()
    {
        var html = "<div style='width:1e9px;column-width:1px'>"
            + Repeat("<p style='break-before:column'>x</p>", 400) + "</div>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Failed_imports_count_towards_the_import_limit()
    {
        int calls = 0;
        var options = new PdfOptions { ResourceResolver = _ => { Interlocked.Increment(ref calls); return null; } };
        var css = string.Concat(Enumerable.Range(0, 1000).Select(i => $"@import url(https://example.invalid/{i}.css);"));
        AssertStructurallyValid(Bounded($"<style>{css}</style><p>x</p>", options));
        Assert.InRange(calls, 1, 128); // at most 64 per stylesheet read; the styles are read again once the page size is known
    }

    [Theory]
    [InlineData("line-height:Infinity")]
    [InlineData("line-height:1e38")]
    [InlineData("font-size:1e38px")]
    public void Infinite_or_huge_line_heights_do_not_make_border_corners_NaN(string css)
    {
        // Found by the fuzzer: target html, exception-b889eeb4. "Infinity" parsed as a line-height factor, and a huge
        // finite one overflowed, so an inline box's height became NaN and splitting its rounded border threw.
        var html = $"<style>p{{{css}}}.ring{{border:solid #c00;border-right-color:2;border-radius:4px}}</style><p><span class=ring>x</span>";
        try
        {
            AssertStructurallyValid(Bounded(html));
        }
        catch (PdfLimitException)
        {
            // A line taller than the page limit is a documented failure.
        }
    }

    [Fact]
    public void Merging_a_tagged_pdf_whose_catalog_changes_when_the_damaged_file_is_rebuilt_works()
    {
        // Found by the fuzzer: target pdf-merge, exception-13f9fba8. A wrong offset found while copying the pages made
        // the file rebuild its object table, which took a later copy of the catalog without a structure tree, and
        // writing the structure looked the catalog up again and got null.
        var text = Encoding.Latin1.GetString(HtmlToPdf.Convert("<h1>Title</h1><p>Text</p>", new PdfOptions { Accessibility = PdfAccessibility.Tagged }));
        int root = int.Parse(Regex.Match(text, @"/Root (\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);
        string pages = Regex.Match(text, @"/Pages (\d+) 0 R").Groups[1].Value;
        int contents = int.Parse(Regex.Match(text, @"/Contents (\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);
        // The content stream's xref entry points at the header, and a second catalog with the same number follows the end.
        int xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 6;
        int entry = text.IndexOf('\n', xref) + 1 + contents * 20;
        text = text[..entry] + "0000000001" + text[(entry + 10)..] + $"\n{root} 0 obj\n<< /Type /Catalog /Pages {pages} 0 R >>\nendobj\n";
        var merged = PdfMerger.Merge(new[] { Encoding.Latin1.GetBytes(text), HtmlToPdf.Convert("<p>Second</p>") });
        Assert.Equal(2, PdfRenderer.Open(merged).PageCount);
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
    public void Nested_boxes_kept_whole_in_columns_are_not_tried_again_at_every_level()
    {
        // Found by the fuzzer: target html, timeout-14fbab57, 1.4 KB. A box with break-inside: avoid that crosses a
        // column is tried at the top of the next one and, if it does not fit there either, laid out again where it
        // was, and so is every such box inside it. Each level of nesting doubled the work: 18 levels took 8 s and 30
        // did not finish.
        var html = "<style>@page{size:148mm 190mm;margin:12mm}.c2{column-count:2}"
            + ".card{break-inside:avoid;border:0.5pt solid #888;padding:4pt;margin-bottom:4pt}</style>"
            + "<h1>Columns</h1><div class=\"c2\"><p>Lorem ipsum dolor sit amet, consectetur adipiscing elit.</p>"
            + Repeat("<div class=\"card\"><b>Card</b>Lorem ipsum", 30);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void A_box_taller_than_the_page_limit_on_pages_of_different_widths_is_rejected()
    {
        // Found by the nightly fuzz run: target css, timeout-d0d017cf. A line 99,999,999,999 times the font size made
        // its paragraph billions of pages tall, and on pages of different widths its background was painted page by
        // page, which ran for minutes without checking for cancellation. Only the line's top was checked against the
        // page limit.
        var html = "<!doctype html><html><head><style>@page :right { margin-left: 14mm } body { font: 10.5pt/99999999999 serif }"
            + "</style></head><body><div><h1>Heading</h1><p>Text <span>span</span> <a href=\"#c\">link</a></p><ul><li>One<li>Two</ul>"
            + "<table><thead><tr><th>A<th>B<tbody><tr><td>cell<td>cell</table><img alt=x><input type=text value=v></div><p>After</p></body></html>";
        Assert.Throws<PdfLimitException>(() => Bounded(html));
    }

    [Fact]
    public void Nested_flex_rows_across_pages_are_not_measured_again_at_every_level()
    {
        // Found by the fuzzer: target html, timeout-1abcdf31 and timeout-79c23971. A flex item that crosses a page is
        // laid out again there, and a stretched one again at its new height; each time, every flex item nested in it
        // was measured once more, so the work grew with the square of the depth: this took 22 s.
        var html = Repeat("<div style=\"display:flex\"><div>side</div><div style=\"display:flex\">", 120)
            + Repeat("<h3>Title</h3><p>" + Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 20) + "</p>", 40);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_grids_across_pages_are_not_measured_again_at_every_level()
    {
        // Found by the fuzzer: target html, timeout-d2edd0b0, 40 KB. The same as for flex rows, with grids nested in
        // a grid area.
        var html = Repeat("<h3>Named grid areas</h3><div style=\"display:grid;grid-template-areas:'head head' 'side main' 'foot foot';"
            + "grid-template-columns:100px 1fr;gap:4px\"><div style=\"grid-area:main\">main</div><div style=\"grid-area:head\">head</div>"
            + "<div style=\"grid-area:foot\">foot", 127);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Deeply_nested_boxes_kept_whole_in_balanced_columns_are_bounded()
    {
        // Found by the nightly fuzz run: target html, timeout-14fbab57, 3.2 KB. With three columns to balance, each
        // nested box kept whole was tried in the next column once for every box around it, at every height balancing
        // tried: 80 levels took 7 s and 130 more than 30 s. Boxes nested in more than eight such boxes now break where
        // they fall.
        var html = "<style>@page{size:148mm 190mm;margin:12mm}.c3{columns:3 30mm;column-gap:4mm}"
            + ".card{break-inside:avoid;border:0.5pt solid #888;border-radius:3pt;padding:4pt;margin-bottom:4pt}</style>"
            + "<h1>Columns</h1><div class=\"c3\"><p>Lorem ipsum dolor sit amet.</p>"
            + Repeat("<div class=\"card\"><b>Card</b>Lorem ipsum dolor sit amet, consectetur ", 130);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Deeply_nested_boxes_in_columns_find_their_first_page_once()
    {
        // Found by the fuzzer: target html, timeout-14fbab57. Every box in a flow looked up the page of its first content
        // among all the ops below it, in case the next box had to stay with it, so 2,800 nested boxes in balanced
        // columns took 4.5 s. The page is now only looked up when a box does start such a chain.
        var html = "<style>@page{size:148mm 190mm;margin:12mm}.c3{columns:3 30mm;column-gap:4mm}"
            + ".card{break-inside:avoid;border:0.5pt solid #888;border-radius:3pt;padding:4pt;margin-bottom:4pt}</style>"
            + "<h1>Columns</h1><div class=\"c3\"><p>Lorem ipsum dolor sit amet.</p>" + Repeat("<div class=\"card\">", 3000);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_grids_on_the_pages_reuse_their_measured_items()
    {
        // Found by the fuzzer: target html, timeout-1b5a0815. A grid area's width is the difference of two track
        // positions, which rounds differently when the grid is laid out again lower on the page, so the items measured
        // before were not found and every nested grid was measured again in full: 1,500 nested grids took 6 s.
        var html = "<style>.kpis{display:grid;grid-template-columns:repeat(4,1fr);gap:4mm;margin:4mm 0}</style>"
            + Repeat("<div class=\"kpis\"><span>Week 42</span><span>Draft</span>", 1500);
        AssertStructurallyValid(Bounded(html, new PdfOptions { MaxPages = 1000 }));
    }

    [Fact]
    public void Nested_flex_boxes_across_columns_are_laid_out_again_to_a_bounded_depth()
    {
        // Found by the nightly fuzz run: target html, timeout-1b5a0815. In columns the boxes measured inside a flex
        // item cannot be reused, so each nested item laid out again where it crossed a column laid out everything below
        // it again, in every balancing pass: 150 levels took 3 s. Items nested deeper than eight keep their measured
        // layout.
        const string lorem = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante. ";
        var html = "<style>@page{size:148mm 190mm;margin:12mm}body{font-size:8pt}.c2{column-count:2;column-gap:6mm}</style>"
            + "<div class=\"c2\"><p>" + Repeat(lorem, 2) + Repeat("<div style=\"display:flex\">", 200) + Repeat(lorem, 4)
            + "</p><p>" + Repeat(lorem, 3);
        AssertStructurallyValid(Bounded(html));
    }

    [Theory]
    [InlineData("<div style='height:690px'></div>", "<div class=\"c\">", "text")]
    [InlineData("<div style='height:300px'></div>", "<div class=\"c\">", "text<br>text<br>text<br>text<br>text")]
    [InlineData("", "<div style='height:37px'></div><div class=\"c\">text<br>text", "")]
    [InlineData("<div style='height:470px'></div>", "<div style='border-bottom:1px solid'>", "text")]
    public void Nested_boxes_at_page_ends_are_moved_without_laying_out_each_level_again(string before, string level, string after)
    {
        // A box whose first content moves to the next page moves with it, and a bottom border that does not fit takes
        // the last lines along. Each move lays the box out again; deciding it only after the boxes inside were laid out
        // in full made every level lay out all the levels inside it again, which took exponential time.
        var html = "<style>@page{size:A5;margin:12mm}.c{border:1px solid #888;padding:0 2px}</style>" + before
            + Repeat(level, 3000) + after;
        AssertStructurallyValid(Bounded(html, new PdfOptions { MaxPages = 1000 }));
    }

    [Theory]
    [InlineData("<h3>Named grid areas</h3><div style=\"display:grid; grid-template-areas:'head head' 'side main'; grid-template-columns:100px 1fr\"><div style=\"grid-area:main\">main</div><div style=\"grid-area:head\">head", 30)]
    [InlineData("<h2>WebP and GIF</h2><div style=\"display:flex; flex-wrap:wrap; gap:10px\"><figurev>", 40)]
    public void Headings_nested_in_boxes_kept_with_headings_move_to_a_bounded_depth(string level, int depth)
    {
        // Found by the nightly fuzz run: target html, timeout-1b5a0815, timeout-14fbab57 and timeout-b4187992. Each
        // level is a heading kept with a box that holds the next level, and where its first text fell on a later page
        // the heading moved to the next page and everything in the box was laid out again, at every level, so the work
        // doubled with each one: 31 levels took 15 s. Headings nested deeper than eight such boxes stay where they are.
        var html = "<p style='height: 600pt'>Before</p>" + Repeat(level, depth) + "end";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Headings_nested_deeper_than_chains_move_are_laid_out_once()
    {
        // Found by the nightly fuzz run: target html, timeout-b4187992. Each level is a box holding an empty bordered box
        // and a heading kept with the next level. Deeper than chains move to the next page, each box was still laid out
        // until its first text was placed, then again in full, so the work doubled with each page: 147 levels took 4 s.
        var html = "<style>h2{margin:14px 0 6px}.b{height:90px;border:1px solid}</style><div style='margin-top:16px'>x"
            + Repeat("<div><div class=b></div><h2>WebP and GIF</h2>", 250);
        AssertStructurallyValid(Bounded(html, new PdfOptions { MaxPages = 1000 }));
    }

    [Fact]
    public void Nested_painted_boxes_across_a_page_keep_their_edges_in_bounded_time()
    {
        // Found by the nightly fuzz run: target html, timeout-d2edd0b0. Nested boxes with a background and padding end
        // their content at two places in turn, so room kept at the page end for their bottom edges, found not to move
        // that content, was tried again at every other level: once they crossed a page, 116 levels did not finish.
        var html = "<style>.op{background:#111;padding:8px;border-radius:6px;width:200px}</style><h2>border-radius</h2>"
            + Repeat("<div class=op style='opacity:1'>", 150);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_boxes_kept_whole_across_pages_move_to_the_next_page_to_a_bounded_depth()
    {
        // Found by the nightly fuzz run: target html, timeout-b4187992. Empty nested boxes kept whole and taller than
        // a page leave only their top edges at each page end; each moved to the next page, the boxes around it followed
        // one by one, and boxes inside moved again from the next page, four levels deep: 53 levels did not finish.
        var html = "<style>@page{size:A5;margin:18mm 14mm}.box{break-inside:avoid;border:0.75pt solid;background:#eef8f6;"
            + "padding:5pt;margin:6pt 0}</style>" + Repeat("<div class=box>", 120);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_figures_kept_whole_across_pages_are_laid_out_again_within_a_budget()
    {
        // Found by the nightly fuzz run: target html, timeout-c201378d. Each figure kept whole that crossed a page and fit
        // on the next moved there, and so did each figure around it, every one laying out all the figures inside again,
        // which top and bottom edges kept with their content repeated: 200 levels took 6 s, three stacks of 300 32 s.
        var html = "<style>@page{size:A6}figure{margin:0;border:0.5pt solid #ccc;padding:4pt;break-inside:avoid}</style>"
            + Repeat("<h1>SVG charts</h1><div>" + Repeat("<figure>", 300) + Repeat("</figure>", 300) + "</div>", 3);
        AssertStructurallyValid(Bounded(html, new PdfOptions { MaxPages = 1000 }));
    }

    [Fact]
    public void Nested_cards_kept_whole_in_a_wrapping_flex_row_are_laid_out_again_within_a_budget()
    {
        // Found by the nightly fuzz run: target html, timeout-d2edd0b0. The same nesting as above, inside a card of a
        // wrapping flex row: 200 levels took 10 s, 800 levels 27 s.
        var html = "<style>@page{size:140mm 180mm;margin:10mm}.wrap{display:flex;flex-wrap:wrap;gap:4pt}.card{flex:1 1 35mm;"
            + "border:0.5pt solid;border-radius:3pt;padding:4pt;background:#f7fbfc;break-inside:avoid}</style>"
            + "<h1>Flex and grid</h1><h2>Wrapping flex cards</h2><div class=wrap><div class=card><h3>Card 1</h3>"
            + "<p>Lorem ipsum dolor sit amet.</p></div>" + Repeat("<div class=card>", 800);
        AssertStructurallyValid(Bounded(html, new PdfOptions { MaxPages = 1000 }));
    }

    [Fact]
    public void Nested_translucent_boxes_before_text_on_a_later_page_are_probed_within_the_budget()
    {
        // Found by the fuzzer: target html, timeout-d2edd0b0. Each painted box whose first text lands on a later page
        // stops its layout there and is laid out again, once the budget for such layouts was spent too: 132 levels took
        // 6 s, 200 levels more than a minute.
        var html = "<style>.op{background:#111;color:white;padding:8px;border-radius:6px;width:200px}</style>"
            + Repeat("<div class=op style='opacity:0.6'>", 200) + "the right edge.";
        AssertStructurallyValid(Bounded(html));
    }

    [Theory]
    [InlineData("grid-column: 99999999999 / -1")]
    [InlineData("grid-column: 5000 / span 3; grid-row: span 2")]
    public void Grid_items_placed_past_the_column_limit_are_placed(string placement)
    {
        // Found by the fuzzer: target html, timeout-d0d017cf. A grid keeps at most 1,000 columns, and an item placed
        // after them never found a free cell, so placement looped for good and cancellation could not stop it.
        var html = "<div style='display:grid; grid-template-columns: 2fr repeat(16777216, 1fr)'><div style='" + placement
            + "'>x</div><div>y</div></div>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Deeply_nested_flex_rows_find_their_measured_items_in_linear_time()
    {
        // Found by the fuzzer: target html, timeout-d2edd0b0. A flex row laid out on the pages looks up the items it
        // measured, and a miss scanned every box measured inside it, so 12,000 nested rows took 26 s.
        var html = "<!doctype html><html><head><style>\nbody { font-family: Helvetica, Arial, sans-serif; margin: 0; padding: 20px; }\n"
            + ".row { display: flex; gap: 12px; margin-bottom: 12px; }\n</style></head><body>\n"
            + Repeat("<div class=\"row\">", 12000) + "<di\"glow\">Soft glow</div>\n<h2>aspect-ratio</h2>\n<div class=\"ratio\">16 / 9</div>\n</body>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_boxes_kept_whole_around_a_long_table_stay_within_the_layout_budget()
    {
        // Found by the fuzzer: target html, timeout-d2edd0b0. Boxes kept whole around a table on a named page are laid
        // out again many times, and the work budget counted only block boxes, not the table's cells or the lines of
        // text, so it was spent long after the time was: 110 boxes around a 720-row table took 26 s.
        var rows = new StringBuilder();
        for (int i = 0; i < 720; i++) rows.Append("<tr><td>Measure ").Append(i).Append("</td><td>1.").Append(i).Append("</td><td>2.").Append(i).Append("</td></tr>");
        var html = "<style>@page { size: A5 } @page landscape { size: A5 landscape } .wide { page: landscape }"
            + " .box { break-inside: avoid; border: 1px solid #2a9d8f; background: #eef8f6; padding: 5pt; margin: 6pt 0 }</style>"
            + Repeat("<div class=box><h4>Kept together</h4><p>Lorem ipsum dolor sit amet</p>", 110)
            + "<div class=wide><h2>Key figures</h2><table>" + rows + "</table></div>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_grids_far_from_the_page_origin_reuse_their_measured_items()
    {
        // Found by the fuzzer: target html, timeout-d2edd0b0. A margin of 2,147,483,647 px puts the grids where a float
        // steps in 128 px, so their column positions, and the area widths taken from them, never matched the widths
        // the items were measured at, and every level measured all of its content again: 14,000 grids took more than 20 s.
        var html = "<style>body { margin: 0 2147483647px } .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 3mm 6mm }</style><body>"
            + Repeat("<div class=grid>", 14000);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Headings_before_tables_nested_in_split_header_cells_are_laid_out_once()
    {
        // Found by the fuzzer: target html, timeout-c201378d. Each level is a heading kept with a table whose header row
        // holds the next level. The header cells were centred in their rows, which span pages, so each table's first
        // text ended on a later page than its heading after the table was laid out in full; the heading then moved to
        // the next page and the table was laid out again, at every level: 17 levels took 7 s.
        var html = Repeat("<h2>A table</h2><table><thead><tr><th>Language</th><th>Sample</th><th>", 22)
            + "<table><tr><td>end</td></tr></table>" + Repeat("<p>Lorem ipsum dolor sit amet.</p>", 80);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Deeply_nested_tables_in_columns_move_to_the_next_column_to_a_bounded_depth()
    {
        // Found by the nightly fuzz run: target html, timeout-14fbab57. In columns each nested table that did not fit
        // moved whole to the next column and laid out every table inside it again, in every balancing pass, so the work
        // grew with the cube of the depth: 60 levels took 10 s. Tables nested deeper than eight break where they fall.
        var nest = Repeat("<table><tr><td>", 60) + "quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo"
            + Repeat("</td></tr></table>", 60);
        var html = "<style>.cols{columns:3;column-gap:14pt;font-size:9pt}</style>"
            + Repeat("<div class=\"cols\"><p>Column rules sit in the gaps between columns.</p>" + nest + "</div>", 6);
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Deeply_nested_flex_boxes_in_columns_are_measured_to_a_bounded_depth()
    {
        // Found by the fuzzer: target html, timeout-1b5a0815. Each flex box in columns measures its content by laying it
        // out, and every level measured the levels inside it afresh, so the work grew with the square of the depth:
        // flex boxes nested to the parser's depth limit, twice over, took 6.5 s.
        const string text = "Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt. ";
        var html = "<style>body{font-size:9pt}h4{break-after:avoid}</style><div style='columns:3'><p>" + Repeat(text, 12)
            + "<h4>Heading</h4><p>" + text + Repeat("<div style='display:flex'>", 500) + Repeat("</div>", 250)
            + Repeat("<div style='display:flex'>", 200) + "end</div>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_boxes_kept_whole_in_columns_are_tried_in_the_next_column_to_a_bounded_depth()
    {
        // Found by the fuzzer: target html, timeout-14fbab57. Each break-inside: avoid box in columns that did not fit
        // was laid out again in the next column, together with every box inside it, so the work doubled with each
        // level: 33 levels of boxes holding empty headings took 6 s.
        const string text = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero. ";
        var html = "<style>@page{size:A5;margin:18mm 14mm 16mm}.cols{column-count:2;column-gap:8mm}"
            + ".box{break-inside:avoid;border:0.75pt solid;padding:5pt;margin:6pt 0}h4{margin:0 0 3pt}td{padding:2pt 3pt}</style>"
            + "<li><a href='#x'>Contents<div class=cols>" + Repeat("<p>" + text + text + "</p>", 5)
            + Repeat("<div class=box><h4></p>", 40) + Repeat("<table><tr><td>", 20) + "end</div>";
        AssertStructurallyValid(Bounded(html));
    }

    [Fact]
    public void Nested_flex_columns_across_pages_are_measured_once()
    {
        // Found by the fuzzer: target html, timeout-d2edd0b0. Each flex column that spread its items measured them and,
        // where they crossed a page, laid them out again with everything inside measured afresh, at every level, so the
        // work grew with the square of the depth: grids of flex columns nested to the parser's depth limit took 6 s.
        var html = "<style>body{font:8pt sans-serif}.nest{display:grid;grid-template-columns:repeat(auto-fill,minmax(28mm,1fr))}"
            + ".nest>div{display:flex;flex-direction:column;justify-content:space-between;min-height:14mm}</style><p>Lorem "
            + Repeat("<div class=nest><div>Cell<b>7</b></div> consectetur adipiscing elit. Integer nec odio praesent libero. ", 300);
        AssertStructurallyValid(Bounded(html, new PdfOptions { MaxPages = 1000 }));
    }

    [Fact]
    public void Anchors_in_nested_flex_boxes_are_noted_once_per_level()
    {
        // Found by the fuzzer: target html, memory-c058a39b. Content placed twice in a capture noted its anchors twice,
        // so a heading's anchor was noted 2^n times n boxes up: 25 levels of positioned flex boxes in a grid ran out of
        // memory.
        var html = "<style>.g{display:grid}.g div{padding:4pt}.l div{position:absolute;width:80pt;height:24pt}</style>"
            + "<div class=g><div class=l>" + Repeat("<div style='display:flex'>", 40) + "<h2>Heading";
        AssertStructurallyValid(Bounded(html));
    }

    [Theory]
    [InlineData("<style>td{border:solid</style><table ><tr><td rowspan=2><tfoot><tr")]
    [InlineData("<style>td{border:1px solid}</style><table><thead><tr><td rowspan=3>h</td></tr></thead><tr><td>a</td></tr><tr><td>b</td></tr></table>")]
    [InlineData("<style>td{border:1px solid}</style><table><tr><td rowspan=0>a</td></tr><tr><td>b</td></tr><tfoot><tr><td>f</td></tr></tfoot></table>")]
    public void Cells_spanning_rows_into_another_table_part_do_not_throw(string html)
    {
        // Found by the fuzzer: target html, exception-0ed0ecfb. A body cell's rowspan reached into the footer, whose
        // rows are laid out on their own to repeat on every page, so the cell was finished there without having been
        // laid out and painting its borders threw NullReferenceException.
        AssertStructurallyValid(Bounded(html));
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

/// <summary>
/// Indic, Thai and Lao text built to be as hard to shape as possible: endless syllables, floods of signs with nothing to
/// sit on (each of which gets a dotted circle), joiners and split vowels. Shaping stays linear in the text.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class ComplexScriptShapingHostileTests
{
    private const int Repeats = 50_000;

    public static TheoryData<string, string> Floods() => new()
    {
        { "NotoSansDevanagari-Subset", "क्" },          // one consonant cluster that never ends
        { "NotoSansDevanagari-Subset", "्" },            // viramas with no consonant: a dotted circle each
        { "NotoSansDevanagari-Subset", "र्\u200Dक्\u200C" }, // reph, joiners and half forms
        { "NotoSansDevanagari-Subset", "ि" },            // pre-base vowel signs, each moved before a dotted circle
        { "NotoSansBengali-Subset", "ক্ষোৗ" },          // split vowels around a conjunct
        { "NotoSansMalayalam-Subset", "ര്" },            // a reph that reorders to the end of each syllable
        { "NotoSansTamil-Subset", "ொ" },
        { "NotoSansThai-Subset", "ำ" },                  // SARA AM decomposed with nothing before it
        { "NotoSansThai-Subset", "ป\u0E34\u0E48\u0E39" }, // marks that move to avoid a tall consonant
        { "NotoSansLao-Subset", "\u0EB3\u200D" },
    };

    [Theory]
    [MemberData(nameof(Floods))]
    public async Task Floods_of_hard_syllables_shape_in_time(string font, string unit)
    {
        var program = Assert.Single(FontProgram.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", font + ".woff2"))));
        string text = string.Concat(Enumerable.Repeat(System.Text.RegularExpressions.Regex.Unescape(unit), Repeats));
        int count = await Task.Run(() =>
        {
            var glyphs = new List<ShapedGlyph>();
            ComplexShaper.Shape(program, text, 0, text.Length, rtl: false, glyphs);
            return glyphs.Count;
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.InRange(count, 1, OpenTypeLayout.MaxRunLength(text.Length));
    }
}

[Collection(HostileInputCollection.Name)]
public class Jbig2SymbolDictionaryTests
{
    // jbig2, found while rewriting the decoder (fuzzer framing: width, height, globals length, then the stream): a
    // Huffman-coded symbol dictionary with refinement and aggregation whose aggregated symbol has a refined instance. The
    // instance's refinement data was looked for without the segment data, which threw NullReferenceException.
    private const string RefinedAggregateInstance =
        "DAAAAEUAAAAAAAAAAAAAADAAAQAAABMAAAAMAAAARQAAAAAAAAAAAAAAAAAAAQAAAQAAACsAAAP//f8C/v7+AAAAAwAAAAM/ufCwlT++P9GDL6HHQuAFta053fvzn/+sAAAAAgAgAQEAAABTDAMA///+AAAAAgAAAAJL3SEep7kOo9ORLSZnpZEG+MJAfXdQ1M9H+txZHUbZ1MvZkE3+jv7/sg+W1gsNkQooo1AhYfO++wlR1qzdtYWMt1hajhQAAAADBCABAQAAACUAAAAVAAAAHf////0AAAAQA0dXe4D/AQABAAAAB7OzAvXkmuZc";

    private static T Bounded<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.Result;
    }

    [Fact]
    public void Refined_instance_of_a_huffman_aggregate_with_damaged_data_fails_cleanly()
    {
        var input = Convert.FromBase64String(RefinedAggregateInstance);
        int width = BitConverter.ToInt32(input, 0), height = BitConverter.ToInt32(input, 4);
        Assert.Equal(0, BitConverter.ToInt32(input, 8));
        var ex = Record.Exception(() => Bounded(() => Jbig2Decoder.Decode(input[12..], null, width, height)));
        Assert.Null(ex);
    }

    [Fact]
    public void Refined_instance_of_a_huffman_aggregate_renders_through_pdf_to_image()
    {
        var input = Convert.FromBase64String(RefinedAggregateInstance);
        int width = BitConverter.ToInt32(input, 0), height = BitConverter.ToInt32(input, 4);
        var damaged = JpxJbig2Tests.BuildPdf(width, height, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
            "/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode", input[12..], null);
        Assert.NotEmpty(Bounded(() => PdfToImage.ConvertPage(damaged, 0, new ImageOptions { Dpi = 72 })));

        // The same kind of dictionary written as the standard says decodes to the symbol it describes.
        var valid = JpxJbig2Tests.BuildPdf(16, 8, "<< /Type /XObject /Subtype /Image /Width 16 /Height 8 " +
            "/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode", JpxJbig2Tests.HuffmanAggregateWithRefinedInstance(), null);
        Assert.NotEmpty(PdfToImage.ConvertPage(valid, 0, new ImageOptions { Dpi = 72 }));
        var img = PdfRenderer.Open(valid).RenderPage(0, new ImageOptions { Dpi = 72 });
        Assert.Equal((16, 8), (img.Width, img.Height));
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 16; x++)
            Assert.True(JpxJbig2Tests.HuffmanAggregatePixel(x, y) == img.Rgba[(y * 16 + x) * 4] < 128, $"Pixel {x},{y}");
    }

    [Fact]
    public void Refined_symbols_do_not_copy_the_symbol_list()
    {
        // A dictionary of 65,536 empty symbols, then one with refinement and aggregation whose 16,384 new symbols each
        // refine one of them. Every new symbol used to copy the list of all symbols before it, 26 GB in all, which the
        // work budget did not count.
        const int inputCount = 1 << 16, newCount = 1 << 14, codeLength = 17;
        var input = new Jbig2TestStreams.Bits().Huffman(Jbig2TestStreams.B4, 1);
        for (int i = 0; i < inputCount; i++) input.Huffman(Jbig2TestStreams.B2, 0);
        input.Oob(Jbig2TestStreams.B2).Huffman(Jbig2TestStreams.B1, 0).Align()
            .Huffman(Jbig2TestStreams.B1, 0).Huffman(Jbig2TestStreams.B1, inputCount);
        var refined = new Jbig2TestStreams.Bits().Huffman(Jbig2TestStreams.B4, 1);
        for (int i = 0; i < newCount; i++)
        {
            refined.Huffman(Jbig2TestStreams.B2, 0).Huffman(Jbig2TestStreams.B1, 1).Write(i, codeLength)
                .Huffman(Jbig2TestStreams.B15, 0).Huffman(Jbig2TestStreams.B15, 0).Huffman(Jbig2TestStreams.B1, 0).Align();
        }
        refined.Oob(Jbig2TestStreams.B2).Huffman(Jbig2TestStreams.B1, inputCount + newCount);
        var stream = Jbig2TestStreams.Segment(1, Jbig2TestStreams.SymbolDictionary, Array.Empty<int>(),
                Jbig2TestStreams.U16(0x0001).Concat(Jbig2TestStreams.U32(inputCount)).Concat(Jbig2TestStreams.U32(inputCount))
                    .Concat(input.ToArray()).ToArray())
            .Concat(Jbig2TestStreams.Segment(2, Jbig2TestStreams.SymbolDictionary, new[] { 1 },
                Jbig2TestStreams.U16(0x1003).Concat(Jbig2TestStreams.U32(0)).Concat(Jbig2TestStreams.U32(newCount))
                    .Concat(refined.ToArray()).ToArray()))
            .ToArray();

        long allocated = Bounded(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Jbig2Decoder.Decode(stream, null, 16, 16);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.True(allocated < 32 << 20, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Repeated_references_to_a_symbol_dictionary_are_bounded()
    {
        // A text region that refers 65,535 times to a dictionary of 16,384 symbols, 76 KB in all. The referred
        // dictionaries are concatenated, so this asks for a billion input symbols: the list of them took 17 GB and let
        // OutOfMemoryException escape after 39 s.
        var stream = RepeatedReferences(1 << 14, ushort.MaxValue, regions: 1);
        Assert.InRange(stream.Length, 70_000, 80_000);
        var (rows, allocated) = Bounded(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var decoded = Jbig2Decoder.Decode(stream, null, 16, 16);
            return (decoded, GC.GetAllocatedBytesForCurrentThread() - before);
        });
        Assert.True(allocated < 32 << 20, $"Allocated {allocated:N0} bytes");
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 2 * 16).ToArray(), rows); // the page as far as it was decoded: white

        var pdf = JpxJbig2Tests.BuildPdf(16, 16, "<< /Type /XObject /Subtype /Image /Width 16 /Height 16 " +
            "/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode", stream, null);
        Assert.NotEmpty(Bounded(() => PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 72 })));
    }

    [Fact]
    public void Referred_symbols_are_charged_to_the_work_budget()
    {
        // Two thousand text regions that each refer 64 times to a dictionary of 65,536 symbols: each one's input symbols,
        // four million, stay within the bound, but gathering them all used to copy 64 GB of references.
        var stream = RepeatedReferences(1 << 16, 64, regions: 2000);
        long allocated = Bounded(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.NotNull(Jbig2Decoder.Decode(stream, null, 16, 16));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.True(allocated < 1L << 30, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Repeated_references_to_a_symbol_dictionary_each_add_its_symbols()
    {
        // The referred dictionaries are concatenated in order (7.4.3.2), so a text region that refers twice to a dictionary
        // of two symbols has four, and symbol ID 3 is the second symbol of the second copy.
        var dict = Jbig2TestStreams.U16(0x0001).Concat(Jbig2TestStreams.U32(2)).Concat(Jbig2TestStreams.U32(2))
            .Concat(new Jbig2TestStreams.Bits()
                .Huffman(Jbig2TestStreams.B4, 1) // height 1
                .Huffman(Jbig2TestStreams.B2, 1).Huffman(Jbig2TestStreams.B2, 1).Oob(Jbig2TestStreams.B2) // widths 1 and 2
                .Huffman(Jbig2TestStreams.B1, 0).Align().Bytes(new byte[] { 0b01100000 }) // the first white, the second black
                .Huffman(Jbig2TestStreams.B1, 0).Huffman(Jbig2TestStreams.B1, 2)
                .ToArray())
            .ToArray();
        // A Huffman-coded text region of one instance of symbol 3, with a code table that gives each of the four symbols
        // a two-bit code.
        var placement = new Jbig2TestStreams.Bits();
        for (int i = 0; i < 35; i++) placement.Write(i == 2 ? 1 : 0, 4);
        placement.Write(0, 1).Write(0, 1).Write(0, 1).Write(0, 1).Align()
            .Huffman(Jbig2TestStreams.B11, 1).Huffman(Jbig2TestStreams.B11, 1).Huffman(Jbig2TestStreams.B6, 0)
            .Write(3, 2).Oob(Jbig2TestStreams.B8);
        var region = Jbig2TestStreams.U32(4).Concat(Jbig2TestStreams.U32(1)).Concat(Jbig2TestStreams.U32(0)).Concat(Jbig2TestStreams.U32(0))
            .Append((byte)0).Concat(Jbig2TestStreams.U16(0x0011)).Concat(Jbig2TestStreams.U16(0)).Concat(Jbig2TestStreams.U32(1))
            .Concat(placement.ToArray()).ToArray();
        var stream = Jbig2TestStreams.Segment(0, Jbig2TestStreams.PageInformation, Array.Empty<int>(), Jbig2TestStreams.PageInfo(8, 1))
            .Concat(Jbig2TestStreams.Segment(1, Jbig2TestStreams.SymbolDictionary, Array.Empty<int>(), dict))
            .Concat(Jbig2TestStreams.Segment(2, Jbig2TestStreams.ImmediateTextRegion, new[] { 1, 1 }, region))
            .ToArray();
        Assert.Equal(new byte[] { 0b00111111 }, Jbig2Decoder.Decode(stream, null, 8, 1));
    }

    /// <summary>
    /// A page, a Huffman-coded dictionary of <paramref name="symbols"/> empty symbols, then <paramref name="regions"/>
    /// text regions of no width that each refer to it <paramref name="references"/> times. A region of no width is skipped
    /// once its input symbols are gathered.
    /// </summary>
    private static byte[] RepeatedReferences(int symbols, int references, int regions)
    {
        var input = new Jbig2TestStreams.Bits().Huffman(Jbig2TestStreams.B4, 1);
        for (int i = 0; i < symbols; i++) input.Huffman(Jbig2TestStreams.B2, 0);
        input.Oob(Jbig2TestStreams.B2).Huffman(Jbig2TestStreams.B1, 0).Align()
            .Huffman(Jbig2TestStreams.B1, 0).Huffman(Jbig2TestStreams.B1, symbols);
        var stream = new List<byte>();
        stream.AddRange(Jbig2TestStreams.Segment(0, Jbig2TestStreams.PageInformation, Array.Empty<int>(), Jbig2TestStreams.PageInfo(16, 16)));
        stream.AddRange(Jbig2TestStreams.Segment(1, Jbig2TestStreams.SymbolDictionary, Array.Empty<int>(),
            Jbig2TestStreams.U16(0x0001).Concat(Jbig2TestStreams.U32(symbols)).Concat(Jbig2TestStreams.U32(symbols))
                .Concat(input.ToArray()).ToArray()));
        var region = new byte[17 + 2 + 4]; // a region of no size, arithmetic-coded, with no instances
        var refs = Enumerable.Repeat(1, references).ToArray();
        for (int i = 0; i < regions; i++) stream.AddRange(Jbig2TestStreams.Segment(2 + i, Jbig2TestStreams.ImmediateTextRegion, refs, region));
        return stream.ToArray();
    }
}

/// <summary>
/// JPEG 2000 files whose box and segment lengths overflow when added to a position, or whose palette depths overflow a
/// shift: a wrapped box length sent the box reader back into the header box, which recursed until the stack overflowed
/// and ended the process, and the others threw exceptions that the decoder's callers do not expect.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class JpxLengthTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codecs", name));

    // The signature box, then a header box running to the end that holds a free box whose 64-bit length is
    // 0x7FFFFFFFFFFFFFF8: added to its position, it wraps around to the header box.
    private const string WrappedHeaderBox = "0000000c6a5020200d0a870a000000006a70326800000001667265657ffffffffffffff800000000000000000000000000000000";

    private static readonly byte[] Signature = Convert.FromHexString("0000000c6a5020200d0a870a");

    [Fact]
    public void Wrapped_box_length_does_not_recurse()
    {
        Assert.Null(Decode(Convert.FromHexString(WrappedHeaderBox)));
    }

    [Fact]
    public void Deeply_nested_header_boxes_do_not_overflow_the_stack()
    {
        // 200,000 header boxes, each running to the end of the one around it.
        var data = Cat(Signature, Repeat(Cat(Be32(0), Ascii("jp2h")), 200_000));
        Assert.Null(Decode(data));
    }

    [Theory]
    [InlineData(WrappedHeaderBox)]
    [InlineData(null)]
    public async Task Pdf_with_a_recursive_jpx_image_renders(string? hex)
    {
        var jpx = hex != null ? Convert.FromHexString(hex) : Cat(Signature, Repeat(Cat(Be32(0), Ascii("jp2h")), 200_000));
        var pdf = new FixturePdf();
        int image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /Filter /JPXDecode", jpx);
        var bytes = pdf.Build(4, 4, "q 4 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {image} 0 R >>");
        var png = await Task.Run(() => PdfToImage.ConvertPage(bytes)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEmpty(png);
    }

    [Fact]
    public void Wrapped_component_mapping_box_length_fails_cleanly()
    {
        // A cmap box at offset 12 whose 64-bit length makes its end, once wrapped, fall before its start.
        const int pos = 12;
        ulong length = 0x8000000000000000UL - pos + 4;
        var data = Cat(Signature, Be32(1), Ascii("cmap"), Be64(length), new byte[8], Box("jp2c", Codestream()));
        Assert.Null(Decode(data));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(128)]
    public void Palette_deeper_than_38_bits_is_ignored(int bits)
    {
        // Such a column used to make the scale (1 << bits) - 1 wrap to 0 and divide by zero. Without the palette the
        // component itself is the image.
        var img = Decode(PalettedJp2(bits, 2, i => i));
        Assert.NotNull(img);
        Assert.Equal(1, img!.Components);
        Assert.Equal((61, 47), (img.Width, img.Height));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(38)]
    public void Deep_palette_columns_scale_to_8_bits(int bits)
    {
        // Entry i holds the smallest value of its depth that scales to i, so the image is the first component, whose
        // samples are the palette indexes. A 32-bit column used to divide by zero, and from 24 bits on the scaling
        // overflowed.
        long max = (1L << bits) - 1;
        var img = Decode(PalettedJp2(bits, 256, i => (i * max + 254) / 255));
        Assert.NotNull(img);
        Assert.Equal((61, 47, 1), (img!.Width, img.Height, img.Components));
        var reference = Decode(Fixture("jpx-lossless.jp2"))!;
        for (int i = 0; i < img.Data.Length; i++) Assert.Equal(reference.Data[i * 3], img.Data[i]);
    }

    [Fact]
    public void Palette_index_of_a_deep_component_does_not_overflow()
    {
        // The palette index of a 28-bit component used to be recovered in int arithmetic, which overflowed to a
        // negative index. Such an index is past the palette's 256 entries, so it maps to 0 like any other.
        var jp2 = PalettedJp2(8, 256, i => i);
        int ssiz = IndexOf(jp2, 0xFF, 0x51) + 40;
        jp2[ssiz] = 27;
        Assert.NotNull(Decode(jp2));
    }

    [Theory]
    [InlineData(0x7FFFFFFCu)]
    [InlineData(0x7FFFFFFFu)]
    public void Packed_header_length_near_the_maximum_fails_cleanly(uint nppm)
    {
        // A PPM segment whose first tile-part length, added to its position, overflows: the part takes what is left.
        var cs = Codestream();
        int sot = IndexOf(cs, 0xFF, 0x90);
        var ppm = Segment(0xFF60, Cat(new byte[] { 0 }, Be32(nppm), new byte[] { 0x80, 0, 0, 0 }));
        Assert.NotNull(Decode(Cat(cs[..sot], ppm, cs[sot..])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Empty_component_quantization_at_the_end_of_the_data_fails_cleanly(int index)
    {
        // An empty QCC segment takes its component index from the byte after it, which here does not exist: after the
        // tile-parts in place of the end marker, or right after the main header.
        var cs = Codestream();
        int end = index == 0 ? cs.Length - 2 : IndexOf(cs, 0xFF, 0x90);
        Assert.NotNull(Decode(Cat(cs[..end], Segment(0xFF5D, Array.Empty<byte>()))));
    }

    private static PdfPecker.Rendering.Images.JpxImage? Decode(byte[] data)
    {
        var task = Task.Run(() => PdfPecker.Rendering.Images.JpxDecoder.Decode(data));
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "decoding did not finish in time");
        return task.Result;
    }

    /// <summary>The codestream of the lossless fixture: a 61x47 RGB image in one tile-part.</summary>
    private static byte[] Codestream()
    {
        var jp2 = Fixture("jpx-lossless.jp2");
        int at = IndexOf(jp2, (byte)'j', (byte)'p', (byte)'2', (byte)'c');
        int length = BinaryPrimitives.ReadInt32BigEndian(jp2.AsSpan(at - 4));
        return length == 0 ? jp2[(at + 4)..] : jp2[(at + 4)..(at - 4 + length)];
    }

    /// <summary>A JP2 file whose grey image is the first component mapped through a one-column palette.</summary>
    private static byte[] PalettedJp2(int bits, int entries, Func<long, long> value)
    {
        int bytes = (bits + 7) / 8;
        var values = new byte[entries * bytes];
        for (int i = 0; i < entries; i++)
        {
            long v = value(i);
            for (int k = 0; k < bytes; k++) values[i * bytes + k] = (byte)(v >> (8 * (bytes - 1 - k)));
        }
        var pclr = Box("pclr", Cat(Be16(entries), new byte[] { 1, (byte)(bits - 1) }, values));
        var cmap = Box("cmap", Cat(Be16(0), new byte[] { 1, 0 }));
        var colr = Box("colr", Cat(new byte[] { 1, 0, 0 }, Be32(17)));
        return Cat(Signature, Box("jp2h", Cat(Box("ihdr", new byte[14]), colr, pclr, cmap)), Box("jp2c", Codestream()));
    }

    private static int IndexOf(byte[] data, params byte[] what)
    {
        int at = data.AsSpan().IndexOf(what);
        Assert.True(at > 0);
        return at;
    }

    private static byte[] Box(string type, byte[] body) => Cat(Be32((uint)body.Length + 8), Ascii(type), body);
    private static byte[] Segment(int marker, byte[] body) => Cat(Be16(marker), Be16(body.Length + 2), body);
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] Repeat(byte[] part, int count) => Enumerable.Repeat(part, count).SelectMany(p => p).ToArray();
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static byte[] Be32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Be64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostileInputCollection
{
    public const string Name = "Hostile input";
}
