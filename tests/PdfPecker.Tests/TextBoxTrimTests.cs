using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-box, text-box-trim and text-box-edge. The expected boxes are Chrome's: a block's first line starts at its
/// text-box-edge over the text and its last line ends at the edge under it, through the first and last child blocks
/// that no border or padding separates, in inline-blocks and flex items, but not in a flex container's items or an
/// inline box.
/// </summary>
public class TextBoxTrimTests
{
    private const float Width = 400, PageHeight = 800;
    private const uint Blue = 0xFF0000FF, Red = 0xFFFF0000;

    // Times is the serif standard font; its cap height is the OS/2 one of Liberation Serif.
    private const float Size = 15, Cap = 1341 / 2048f * Size;

    private static List<DrawOp> Layout(string body)
    {
        // The parser makes no body element of its own, which the body rules need.
        var doc = HtmlParser.Parse("<body><style>body{margin:0;font:15pt/30pt serif} .b{background:#00f} .r{background:#f00}</style>" + body + "</body>");
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

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            if (op is GroupOp g) foreach (var o in Flatten(g.Ops)) yield return o;
        }
    }

    /// <summary>The top and height of the background in <paramref name="color"/>, and the baselines of the text, top to bottom.</summary>
    private static (float Y, float H, float[] Baselines) Box(string body, uint color = Blue)
    {
        var ops = Flatten(Layout(body)).ToList();
        var r = ops.OfType<RectOp>().First(o => o.Color == color);
        return (r.Y, r.H, ops.OfType<TextOp>().Select(t => t.Baseline).Distinct().Order().ToArray());
    }

    [Fact]
    public void Trim_both_to_cap_and_alphabetic_leaves_the_cap_height()
    {
        var (y, h, baselines) = Box("<div class=b style='text-box: trim-both cap alphabetic'>Hello</div>");
        Assert.Equal(Cap, h, 2);
        Assert.Equal(y + Cap, baselines[0], 2);
    }

    [Fact]
    public void Shorthand_with_an_edge_alone_trims_both()
        => Assert.Equal(Cap, Box("<div class=b style='text-box: cap alphabetic'>Hello</div>").H, 2);

    [Fact]
    public void Trim_start_alone_removes_the_half_leading_over_the_text()
    {
        var plain = Box("<div class=b>Hello</div>");
        var trimmed = Box("<div class=b style='text-box-trim: trim-start'>Hello</div>");
        float removed = plain.H - trimmed.H;
        Assert.True(removed > 5);
        Assert.Equal(plain.Baselines[0] - plain.Y - removed, trimmed.Baselines[0] - trimmed.Y, 2);
    }

    [Fact]
    public void Trim_end_alone_ends_the_box_at_the_baseline()
    {
        var (y, h, baselines) = Box("<div class=b style='text-box-trim: trim-end; text-box-edge: cap alphabetic'>Hello</div>");
        Assert.Equal(y + h, baselines[0], 2);
    }

    [Fact]
    public void Only_the_first_and_last_lines_are_trimmed()
    {
        var (y, h, baselines) = Box("<div class=b style='width:60pt; text-box: trim-both cap alphabetic'>One two three four five six</div>");
        Assert.True(baselines.Length >= 3);
        for (int i = 1; i < baselines.Length; i++) Assert.Equal(30, baselines[i] - baselines[i - 1], 2);
        Assert.Equal(y + Cap, baselines[0], 2);
        Assert.Equal(y + h, baselines[^1], 2);
    }

    [Fact]
    public void Trimming_reaches_the_first_and_last_child_blocks()
    {
        var (y, h, baselines) = Box("<div class=b style='text-box: trim-both cap alphabetic'><p style='margin:0'>Title</p><p style='margin:0'>Body</p></div>");
        Assert.Equal(y + Cap, baselines[0], 2);
        Assert.Equal(y + h, baselines[^1], 2);
    }

    [Fact]
    public void Padding_of_a_child_block_stops_the_trimming()
    {
        var (y, _, baselines) = Box("<div class=b style='text-box: trim-both cap alphabetic'><p style='margin:0;padding-top:1pt'>Title</p></div>");
        Assert.True(baselines[0] - y > Cap + 5);
    }

    [Fact]
    public void Content_after_a_block_keeps_its_last_line_whole()
    {
        var (y, h, baselines) = Box("<div class=b style='text-box: trim-both cap alphabetic'><p class=r style='margin:0'>Title</p>Tail</div>");
        Assert.Equal(y + h, baselines[^1], 2);
        var title = Box("<div class=b style='text-box: trim-both cap alphabetic'><p class=r style='margin:0'>Title</p>Tail</div>", Red);
        Assert.True(title.Y + title.H > title.Baselines[0] + 5);
    }

    [Fact]
    public void A_float_after_the_last_line_does_not_stop_the_trimming()
    {
        var (y, h, baselines) = Box("<div class=b style='text-box: trim-both cap alphabetic'>Text<div style='float:left;width:5pt;height:1pt'></div> </div>");
        Assert.Equal(y + h, baselines[^1], 2);
    }

    [Fact]
    public void Inline_blocks_and_flex_items_trim_their_own_lines()
    {
        var badge = Box("<div><span class=b style='display:inline-block;padding:4pt;text-box: trim-both cap alphabetic'>New</span></div>");
        Assert.Equal(Cap + 8, badge.H, 1);
        var item = Box("<div style='display:flex'><div class=b style='text-box: trim-both cap alphabetic'>Total</div></div>");
        Assert.Equal(Cap, item.H, 2);
    }

    [Fact]
    public void A_flex_container_does_not_trim_its_items()
    {
        var item = Box("<div style='display:flex;text-box: trim-both cap alphabetic'><div class=b>Total</div></div>");
        Assert.Equal(30, item.H, 2);
    }

    [Fact]
    public void An_inline_box_is_not_trimmed()
    {
        var plain = Box("<div>Text <span class=b>span</span></div>");
        var trimmed = Box("<div>Text <span class=b style='text-box: trim-both cap alphabetic'>span</span></div>");
        Assert.Equal(plain.H, trimmed.H, 2);
    }

    [Fact]
    public void Text_box_edge_is_inherited()
    {
        var (_, h, _) = Box("<div style='text-box-edge: cap alphabetic'><div class=b style='text-box-trim: trim-both'>Hello</div></div>");
        Assert.Equal(Cap, h, 2);
    }

    [Theory]
    [InlineData("text-box: cap")]                  // cap needs an under edge, as in Chrome
    [InlineData("text-box: trim-both junk")]
    [InlineData("text-box-edge: alphabetic")]
    [InlineData("text-box-trim: both")]
    public void Invalid_values_are_dropped(string declaration)
    {
        // The declaration before it stays: trimming both edges to the text.
        var (_, h, _) = Box($"<div class=b style='text-box-trim: trim-both; {declaration}'>Hello</div>");
        Assert.Equal(Box("<div class=b style='text-box-trim: trim-both'>Hello</div>").H, h, 2);
    }

    [Fact]
    public void Normal_turns_trimming_off()
        => Assert.Equal(30, Box("<div class=b style='text-box: trim-both cap alphabetic; text-box: normal'>Hello</div>").H, 2);

    [Fact]
    public void Supports_knows_the_properties()
    {
        Assert.True(CssSupports.Evaluate("(text-box: trim-both cap alphabetic)"));
        Assert.True(CssSupports.Evaluate("(text-box: ex alphabetic trim-start)"));
        Assert.True(CssSupports.Evaluate("(text-box-trim: trim-end)"));
        Assert.True(CssSupports.Evaluate("(text-box-edge: text)"));
        Assert.False(CssSupports.Evaluate("(text-box-edge: cap)"));
        Assert.False(CssSupports.Evaluate("(text-box: trim-both trim-start)"));
    }
}
