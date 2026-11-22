using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>margin-trim, which Chrome applies to the block edges of block containers only.</summary>
public class MarginTrimTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<RectOp> Rects(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Flatten(engine.Ops).OfType<RectOp>().ToList();
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static RectOp Box(List<RectOp> rects, string color)
    {
        Assert.True(CssColor.TryParse(color, 0xFF000000, out var c));
        return rects.Single(r => r.Color == c);
    }

    // The containers' border, whose inside the children's boxes are measured from: 4px, which stays whole.
    private const float B = 3;

    private static void Near(float expected, float actual)
        => Assert.True(Math.Abs(expected - actual) <= 0.05f, $"Expected {expected} but got {actual}");

    private const string Style = "<style>.c { border: 3pt solid black; } p { margin: 12pt 0; height: 10pt; }</style>";

    [Fact]
    public void Block_trims_the_first_and_last_childs_margins_and_those_collapsing_with_them()
    {
        var rects = Rects(Style + "<div class='c' style='margin-trim: block; background: #ffff00'>"
                          + "<div style='margin-top: 8pt'><p style='background: #ff0000'></p></div>"
                          + "<p style='background: #00ff00'></p></div>");
        var box = Box(rects, "#ffff00");
        var first = Box(rects, "#ff0000");
        var last = Box(rects, "#00ff00");
        Near(box.Y + B, first.Y);
        Near(first.Y + 10 + 12, last.Y);
        Near(last.Y + 10 + B, box.Y + box.H);
    }

    [Fact]
    public void Empty_boxes_at_an_edge_lose_their_margins_and_the_trim_goes_on_past_them()
    {
        var rects = Rects(Style + "<div class='c' style='margin-trim: block-start; background: #ffff00'>"
                          + "<div style='margin: 30pt 0'></div><p style='background: #ff0000'></p></div>");
        Near(Box(rects, "#ffff00").Y + B, Box(rects, "#ff0000").Y);
    }

    [Fact]
    public void Block_end_keeps_the_start_margin_and_padding()
    {
        var rects = Rects(Style + "<div class='c' style='margin-trim: block-end; padding: 4pt 0; background: #ffff00'>"
                          + "<p style='background: #ff0000; margin-bottom: 40pt'></p></div>");
        var box = Box(rects, "#ffff00");
        var p = Box(rects, "#ff0000");
        Near(box.Y + B + 4 + 12, p.Y);
        Near(p.Y + 10 + 4 + B, box.Y + box.H);
    }

    [Fact]
    public void Flex_and_grid_containers_and_floats_keep_their_margins_as_in_Chrome()
    {
        var rects = Rects(Style + "<div class='c' style='margin-trim: block; display: flex; flex-direction: column; background: #ffff00'>"
                          + "<p style='background: #ff0000'></p></div>"
                          + "<div class='c' style='margin-trim: block; display: grid; background: #0000ff'><p style='background: #00ff00'></p></div>"
                          + "<div class='c' style='margin-trim: block; background: #00ffff'><p style='float: left; width: 10pt; background: #ff00ff'></p></div>");
        Near(Box(rects, "#ffff00").Y + B + 12, Box(rects, "#ff0000").Y);
        Near(Box(rects, "#0000ff").Y + B + 12, Box(rects, "#00ff00").Y);
        Near(Box(rects, "#00ffff").Y + B + 12, Box(rects, "#ff00ff").Y);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("block", true)]
    [InlineData("inline", true)]
    [InlineData("block-end inline-start", true)]
    [InlineData("block-start block-start", false)]
    [InlineData("inline block", true)]
    [InlineData("block block-end", false)]
    [InlineData("top", false)]
    public void Values_parse_as_in_the_spec(string value, bool valid)
        => Assert.Equal(valid, StyleResolver.ParseMarginTrim(value) != null);
}
