using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-emphasis: its values, the marks drawn by each character and the room they take. The expected line positions
/// were measured in Chrome 153 with the same fonts, in CSS pixels; one CSS pixel is 0.75 pt.
/// </summary>
public class TextEmphasisTests
{
    private const float Width = 450, PageHeight = 800, Px = 0.75f;

    private static List<DrawOp> Layout(string html, out Element doc)
    {
        // The parser makes no body element of its own, which the body rules need.
        doc = HtmlParser.Parse("<body>" + html + "</body>");
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return engine.Ops;
    }

    private static List<DrawOp> Layout(string html) => Layout(html, out _);

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<DrawOp> All(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            if (op is GroupOp g) foreach (var o in All(g.Ops)) yield return o;
        }
    }

    private const string Body = "<style>body{margin:0;font:16px sans-serif} p{margin:0}</style>";

    private static ComputedStyle StyleOf(string css)
    {
        Layout($"<style>p{{{css}}}</style><p>x</p>", out var doc);
        return Find(doc, "p").Single().Style;
    }

    [Theory]
    [InlineData("text-emphasis: dot", "Dot", false, null)]
    [InlineData("text-emphasis: open", "Auto", true, null)]
    [InlineData("text-emphasis: filled", "Auto", false, null)]
    [InlineData("text-emphasis: sesame open", "Sesame", true, null)]
    [InlineData("text-emphasis: red circle", "Circle", false, 0xFFFF0000u)]
    [InlineData("text-emphasis: double-circle #00f", "DoubleCircle", false, 0xFF0000FFu)]
    [InlineData("text-emphasis: red", "None", false, 0xFFFF0000u)]
    [InlineData("-webkit-text-emphasis: triangle", "Triangle", false, null)]
    [InlineData("text-emphasis-style: circle; text-emphasis-color: green", "Circle", false, 0xFF008000u)]
    // Chrome drops these, so the earlier declaration stays.
    [InlineData("text-emphasis: dot; text-emphasis: open red sesame", "Dot", false, null)]
    [InlineData("text-emphasis: dot; text-emphasis: dot dot", "Dot", false, null)]
    [InlineData("text-emphasis: dot; text-emphasis: open filled", "Dot", false, null)]
    [InlineData("text-emphasis: dot; text-emphasis-style: square", "Dot", false, null)]
    public void Values_compute_as_in_Chrome(string css, string shape, bool open, uint? color)
    {
        var s = StyleOf(css);
        Assert.Equal(shape, s.EmphasisShape.ToString());
        Assert.Equal(open, s.EmphasisOpen);
        Assert.Equal(color, s.EmphasisColor);
    }

    [Fact]
    public void Marks_are_the_characters_Chrome_draws()
    {
        Assert.Equal("•", StyleOf("text-emphasis: filled").EmphasisMark);
        Assert.Equal("◦", StyleOf("text-emphasis: open").EmphasisMark);
        Assert.Equal("○", StyleOf("text-emphasis: open circle").EmphasisMark);
        Assert.Equal("▲", StyleOf("text-emphasis: triangle").EmphasisMark);
        Assert.Equal("﹅", StyleOf("text-emphasis: filled; writing-mode: vertical-rl").EmphasisMark);
        // A string's first character, with what combines with it.
        Assert.Equal("é", StyleOf("text-emphasis: 'éx'").EmphasisMark);
        Assert.Null(StyleOf("text-emphasis: ''").EmphasisMark);
    }

    [Theory]
    [InlineData("over", false)]
    [InlineData("under", true)]
    [InlineData("under left", true)]
    [InlineData("right over", false)]
    [InlineData("right", false)]
    public void Position_is_over_or_under(string value, bool under)
        => Assert.Equal(under, StyleOf("text-emphasis-position: " + (under ? "over" : "under") + "; text-emphasis-position: " + value).EmphasisUnder);

    [Fact]
    public void Invalid_position_is_dropped()
        => Assert.True(StyleOf("text-emphasis-position: under; text-emphasis-position: under left right").EmphasisUnder);

    [Fact]
    public void Each_letter_gets_a_mark_but_spaces_and_punctuation_do_not()
    {
        var ops = All(Layout(Body + "<p><span style=\"text-emphasis: circle\">a, b. c! #1</span></p>")).OfType<TextOp>().ToList();
        // a, b, c, # and 1: the number sign is one of the symbols that take a mark.
        Assert.Equal(5, ops.Count(t => t.Text == "●"));
        var marks = ops.Where(t => t.Text == "●").ToList();
        Assert.All(marks, m => Assert.Equal(8 * Px, m.Size, 3));
        // Each mark is centred over its letter and sits above the text.
        var text = ops.First(t => t.Text.StartsWith('a'));
        Assert.All(marks, m => Assert.True(m.Baseline < text.Baseline - 12 * Px));
        Assert.True(marks[0].X < marks[1].X);
    }

    [Fact]
    public void Marks_take_the_emphasis_colour()
    {
        var mark = All(Layout(Body + "<p style=\"color:blue\"><span style=\"text-emphasis: dot red\">x</span> <span style=\"text-emphasis: dot\">y</span></p>"))
            .OfType<TextOp>().Where(t => t.Text == "•").ToList();
        Assert.Equal(0xFFFF0000u, mark[0].Color);
        Assert.Equal(0xFF0000FFu, mark[1].Color);
    }

    private static float[] Baselines(string html)
        => All(Layout(html)).OfType<TextOp>().Where(t => t.Size > 10).Select(t => t.Baseline).Distinct().OrderBy(b => b).ToArray();

    [Fact]
    public void A_first_line_with_marks_moves_down_by_what_they_reach_past_it()
    {
        // Chrome: the marks of a 16px font reach 7px above a line of line-height: normal.
        float plain = Baselines(Body + "<div style=\"height:50px\"></div><p>One</p>")[0];
        float marked = Baselines(Body + "<div style=\"height:50px\"></div><p><span style=\"text-emphasis: circle\">One</span></p>")[0];
        Assert.Equal(7 * Px, marked - plain, 2);
        // A top margin holds them: the line stays where it was.
        float withMargin = Baselines(Body + "<div style=\"height:50px\"></div><p style=\"margin-top:10px\"><span style=\"text-emphasis: circle\">One</span></p>")[0];
        Assert.Equal(plain + 10 * Px, withMargin, 2);
        // A margin of 3px holds part of them.
        float smallMargin = Baselines(Body + "<div style=\"height:50px\"></div><p style=\"margin-top:3px\"><span style=\"text-emphasis: circle\">One</span></p>")[0];
        Assert.Equal(plain + 7 * Px, smallMargin, 2);
    }

    [Fact]
    public void Lines_make_room_as_in_Chrome()
    {
        // Chrome 153: marks over the second line move it down by what they reach above it less the space under the
        // first line; marks under a line move the next line down, less the space above that line's text.
        var over = Baselines(Body + "<p style=\"border-top:1px solid\">x<br><span style=\"text-emphasis: circle\">One</span><br>y</p>");
        Assert.Equal(new[] { 24, 18 }, Gaps(over));
        var under = Baselines(Body + "<p style=\"border-top:1px solid\"><span style=\"text-emphasis: circle;text-emphasis-position:under\">One</span><br>y<br><span style=\"text-emphasis: circle;text-emphasis-position:under\">One</span></p>");
        Assert.Equal(new[] { 24, 18 }, Gaps(under));

        static int[] Gaps(float[] b) => b.Zip(b.Skip(1), (p, q) => (int)MathF.Round((q - p) / Px)).ToArray();
    }

    [Fact]
    public void Leading_holds_the_marks()
    {
        var plain = Baselines(Body + "<p style=\"line-height:2\">One<br>Two</p>");
        var marked = Baselines(Body + "<p style=\"line-height:2\"><span style=\"text-emphasis: circle\">One<br>Two</span></p>");
        Assert.Equal(plain, marked);
    }

    [Fact]
    public void Sesame_marks_are_shapes_when_no_font_has_them()
    {
        var ops = All(Layout(Body + "<p><span style=\"text-emphasis: sesame\">ab</span></p>")).ToList();
        Assert.Equal(2, ops.OfType<PathOp>().Count());
        Assert.DoesNotContain(ops.OfType<TextOp>(), t => t.Text == "﹅");
    }

    [Fact]
    public void Marks_print_with_the_bundled_symbols()
    {
        // The document's text is ASCII, but the circles are beyond the standard fonts.
        var pdf = HtmlToPdf.Convert("<p style=\"text-emphasis: circle\">ab</p>", new PdfOptions { CreationDate = DateTimeOffset.UnixEpoch });
        Assert.Equal(2, PdfText.Extract(pdf).Count(c => c == '●'));
    }
}
