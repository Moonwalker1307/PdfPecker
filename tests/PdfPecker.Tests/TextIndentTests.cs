using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-indent with hanging, each-line and percentages. The expected lines are Chrome's: hanging moves every line but
/// the first in, each-line moves the lines after forced breaks in as well, and a percentage is of the block's width.
/// </summary>
public class TextIndentTests
{
    private const float Width = 300, PageHeight = 800;

    private const string Text = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero, sed cursus ante "
        + "dapibus diam.";

    private static string Page(string indent, string body = "")
        => $"<style>body{{margin:0;font:12pt/18pt serif}} p{{margin:0;text-indent:{indent}}}</style>"
           + (body.Length > 0 ? body : $"<p>{Text}<br>After a forced break.</p>");

    private static List<DrawOp> Layout(string html)
    {
        // The parser makes no body element of its own, which the body rules need.
        var doc = HtmlParser.Parse("<body>" + html + "</body>");
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return engine.Ops;
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<TextOp> Texts(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            if (op is TextOp t) yield return t;
            if (op is GroupOp g) foreach (var o in Texts(g.Ops)) yield return o;
        }
    }

    /// <summary>Where each line starts, top to bottom.</summary>
    private static float[] LineStarts(string html)
        => Texts(Layout(html)).GroupBy(t => t.Baseline).OrderBy(g => g.Key).Select(g => g.Min(t => t.X)).ToArray();

    [Fact]
    public void Plain_indent_moves_only_the_first_line()
    {
        var starts = LineStarts(Page("24pt"));
        Assert.Equal(4, starts.Length);
        Assert.Equal(24, starts[0], 2);
        Assert.All(starts.Skip(1), x => Assert.Equal(0, x, 2));
    }

    [Fact]
    public void Hanging_moves_every_line_but_the_first()
    {
        var starts = LineStarts(Page("24pt hanging"));
        Assert.Equal(0, starts[0], 2);
        Assert.All(starts.Skip(1), x => Assert.Equal(24, x, 2));
    }

    [Fact]
    public void Each_line_moves_the_lines_after_forced_breaks_too()
    {
        var starts = LineStarts(Page("each-line 24pt"));
        Assert.Equal(new[] { 24f, 0, 0, 24 }, starts);
    }

    [Fact]
    public void Hanging_each_line_moves_only_wrapped_lines()
    {
        var starts = LineStarts(Page("hanging 24pt each-line"));
        Assert.Equal(new[] { 0f, 24, 24, 0 }, starts);
    }

    [Fact]
    public void Wrapped_lines_have_less_room_when_they_hang()
    {
        // The indented lines hold fewer words, so the paragraph takes more lines than without the indent.
        Assert.True(LineStarts(Page("150pt hanging")).Length > LineStarts(Page("0")).Length);
    }

    [Fact]
    public void Percentage_is_of_the_block_width()
    {
        Assert.Equal(30, LineStarts(Page("10%"))[0], 2);
        // An inherited percentage stays one, and resolves against the width of the block it indents.
        var starts = LineStarts(Page("10%", "<div style=\"text-indent:10%\"><p style=\"text-indent:inherit;width:200pt\">Short.</p></div>"));
        Assert.Equal(20, starts[0], 2);
    }

    [Fact]
    public void Text_after_a_block_starts_no_first_line()
    {
        // As in Chrome, the lines after a block inside the paragraph are neither its first line nor after a forced break;
        // the block's own first line takes the indent it inherits.
        var html = "<p>One<span style=\"display:block\">Two</span>Three<br>Four</p>";
        Assert.Equal(new[] { 24f, 24, 0, 24 }, LineStarts(Page("24pt each-line", html)));
        Assert.Equal(new[] { 0f, 0, 24, 24 }, LineStarts(Page("24pt hanging", html)));
    }

    [Theory]
    [InlineData("hanging")]
    [InlineData("24pt 12pt")]
    [InlineData("24pt hanging hanging")]
    [InlineData("auto")]
    [InlineData("24pt auto")]
    [InlineData("24pt each-line each-line")]
    public void Invalid_values_are_dropped(string value)
    {
        var html = $"<style>p{{text-indent:6pt}} p{{text-indent:{value}}}</style><p>Short.</p>";
        Assert.Equal(6, LineStarts(Page("6pt", html))[0], 2);
    }
}
