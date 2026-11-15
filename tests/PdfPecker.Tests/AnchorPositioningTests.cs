using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// CSS anchor positioning: anchor-name, position-anchor, anchor(), anchor-size(), position-area, anchor-center and
/// position-try-fallbacks, checked by where the backgrounds of the anchored boxes land.
/// </summary>
public class AnchorPositioningTests
{
    private const float Width = 600, PageHeight = 400;
    private const float Px = 0.75f;

    private static List<RectOp> Layout(string css, string body)
    {
        var doc = HtmlParser.Parse("<!doctype html><style>body{margin:0;font:16px sans-serif}" + css + "</style><body>" + body);
        Resolve(doc);
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return Flatten(engine.Ops).OfType<RectOp>().ToList();
    }

    private static void Resolve(Element doc)
    {
        var sheet = new Stylesheet();
        foreach (var style in All(doc).Where(e => e.Name == "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
    }

    private static IEnumerable<Element> All(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
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

    /// <summary>The box painted in a colour, in CSS pixels.</summary>
    private static (float X, float Y, float W, float H) Box(List<RectOp> rects, uint rgb)
    {
        var r = rects.First(r => (r.Color & 0xFFFFFF) == rgb);
        return (r.X / Px, r.Y / Px, r.W / Px, r.H / Px);
    }

    private static void Near(float expected, float actual) => Assert.InRange(actual, expected - 0.6f, expected + 0.6f);

    private const string Stage = ".s{position:relative;width:500px;height:300px}"
        + ".a{anchor-name:--a;position:absolute;left:200px;top:100px;width:100px;height:50px;background:#000011}"
        + ".t{position:absolute;position-anchor:--a;width:40px;height:20px;background:#ff0000}";

    [Theory]
    [InlineData("top left", 160, 80)]
    [InlineData("top", 230, 80)]
    [InlineData("top right", 300, 80)]
    [InlineData("left", 160, 115)]
    [InlineData("center", 230, 115)]
    [InlineData("right", 300, 115)]
    [InlineData("bottom span-left", 260, 150)]
    [InlineData("bottom span-right", 200, 150)]
    [InlineData("block-end inline-start", 160, 150)]
    [InlineData("start end", 300, 80)]
    public void Position_area_places_the_box_next_to_its_anchor(string area, float x, float y)
    {
        var rects = Layout(Stage, $"<div class=s><div class=a></div><div class=t style='position-area:{area}'></div></div>");
        var t = Box(rects, 0xFF0000);
        Near(x, t.X);
        Near(y, t.Y);
    }

    [Fact]
    public void Position_area_shrinks_a_box_to_its_content_and_keeps_explicit_alignment()
    {
        var rects = Layout(Stage + ".u{position:absolute;position-anchor:--a;background:#00ff00;font-size:10px}",
            "<div class=s><div class=a></div><div class=u style='position-area:bottom center;justify-self:start'>tip</div></div>");
        var u = Box(rects, 0x00FF00);
        Near(200, u.X);
        Near(150, u.Y);
        Assert.InRange(u.W, 5, 40);
    }

    [Fact]
    public void Mixing_physical_and_logical_keywords_is_invalid()
    {
        Assert.True(PositionArea.TryParse("top left", false, out var ok) && ok != null);
        Assert.False(PositionArea.TryParse("top inline-start", false, out _));
        Assert.False(PositionArea.TryParse("start left", false, out _));
        Assert.False(PositionArea.TryParse("left right", false, out _));
        Assert.True(PositionArea.TryParse("block-start center", false, out _));
    }

    [Fact]
    public void Anchor_functions_resolve_the_insets_against_the_anchor_sides()
    {
        var rects = Layout(Stage + ".u{position:absolute;width:10px;height:10px;background:#00ff00}"
            + ".v{position:absolute;width:10px;height:10px;background:#0000ff}",
            "<div class=s><div class=a></div>"
            + "<div class=u style='top:calc(anchor(--a bottom) + 5px);left:anchor(--a right)'></div>"
            + "<div class=v style='right:anchor(--a left);bottom:anchor(--a center)'></div></div>");
        var u = Box(rects, 0x00FF00);
        Near(300, u.X);
        Near(155, u.Y);
        var v = Box(rects, 0x0000FF);
        Near(190, v.X);
        Near(115, v.Y);
    }

    [Fact]
    public void Percentages_inside_and_outside_name_points_between_the_sides()
    {
        var rects = Layout(Stage + ".u{position:absolute;width:10px;height:10px;background:#00ff00;position-anchor:--a}",
            "<div class=s><div class=a></div><div class=u style='left:anchor(25%);top:anchor(outside)'></div></div>");
        var u = Box(rects, 0x00FF00);
        Near(225, u.X);
        Near(150, u.Y);
    }

    [Fact]
    public void Anchor_size_gives_the_anchor_width_and_height()
    {
        var rects = Layout(Stage + ".u{position:absolute;position-anchor:--a;left:0;top:0;background:#00ff00;width:anchor-size(width);height:calc(anchor-size(height) / 2)}",
            "<div class=s><div class=a></div><div class=u></div></div>");
        var u = Box(rects, 0x00FF00);
        Near(100, u.W);
        Near(25, u.H);
    }

    [Fact]
    public void A_missing_anchor_takes_the_fallback_or_makes_the_value_initial()
    {
        var rects = Layout(".u{position:absolute;width:10px;height:10px;background:#00ff00;top:anchor(--none bottom, 40px);left:20px}"
            + ".v{position:absolute;width:10px;height:10px;background:#0000ff;left:anchor(--none right);top:anchor(--none top)}",
            "<p style='margin:0;height:30px'>x</p><div class=u></div><div class=v></div>");
        var u = Box(rects, 0x00FF00);
        Near(40, u.Y);
        // Without a fallback the insets are auto: the box stays where it would have been in the flow.
        var v = Box(rects, 0x0000FF);
        Near(0, v.X);
        Near(30, v.Y);
    }

    [Fact]
    public void An_inline_anchor_is_the_box_around_all_its_fragments()
    {
        var rects = Layout("p{margin:0;width:200px;line-height:20px}.n{anchor-name:--n}"
            + ".u{position:absolute;position-anchor:--n;top:anchor(bottom);left:anchor(left);width:anchor-size(width);height:4px;background:#00ff00}",
            "<p>aaaa aaaa aaaa <span class=n>bbbb bbbb bbbb bbbb bbbb</span> cc</p><div class=u></div>");
        var u = Box(rects, 0x00FF00);
        // The span wraps onto the second line, so its box starts at the left edge and ends below that line.
        Near(0, u.X);
        Assert.True(u.Y >= 38, $"top {u.Y}");
        Assert.True(u.W > 150, $"width {u.W}");
    }

    [Fact]
    public void The_last_anchor_in_tree_order_with_a_name_wins()
    {
        var rects = Layout(".x{anchor-name:--x;height:20px}.u{position:absolute;position-anchor:--x;top:anchor(top);left:0;width:10px;height:10px;background:#00ff00}",
            "<div class=x></div><div class=x></div><div class=x></div><div class=u></div>");
        Near(40, Box(rects, 0x00FF00).Y);
    }

    [Fact]
    public void An_anchor_inside_another_containing_block_is_out_of_reach()
    {
        // The anchor's containing block is an absolutely positioned box in the query's own containing block.
        var rects = Layout(".r{position:absolute;top:0;width:100px;height:50px}.i{anchor-name:--i;position:absolute;top:20px;left:20px;width:10px;height:10px}"
            + ".u{position:absolute;position-anchor:--i;top:anchor(bottom, 200px);left:0;width:10px;height:10px;background:#00ff00}",
            "<div class=r><div class=i></div></div><div class=u></div>");
        Near(200, Box(rects, 0x00FF00).Y);
    }

    [Fact]
    public void A_box_is_not_its_own_anchor()
    {
        var rects = Layout(".u{anchor-name:--u;position:absolute;position-anchor:--u;top:anchor(bottom, 70px);left:0;width:10px;height:10px;background:#00ff00}",
            "<div class=u></div>");
        Near(70, Box(rects, 0x00FF00).Y);
    }

    [Fact]
    public void Fallbacks_flip_a_box_that_overflows_its_containing_block()
    {
        var rects = Layout(".s{position:relative;width:500px;height:300px}"
            + ".a{anchor-name:--a;position:absolute;left:20px;top:260px;width:80px;height:30px}"
            + ".b{anchor-name:--b;position:absolute;left:440px;top:20px;width:40px;height:30px}"
            + ".u{position:absolute;position-anchor:--a;top:anchor(bottom);left:anchor(left);width:50px;height:20px;background:#00ff00;position-try-fallbacks:flip-block}"
            + ".v{position:absolute;position-anchor:--b;position-area:right;width:50px;height:20px;background:#0000ff;position-try:flip-inline}"
            + ".w{position:absolute;position-anchor:--a;position-area:bottom;width:50px;height:20px;background:#ff0000;position-try-fallbacks:right}",
            "<div class=s><div class=a></div><div class=b></div><div class=u></div><div class=v></div><div class=w></div></div>");
        var u = Box(rects, 0x00FF00);
        Near(20, u.X);
        Near(240, u.Y);
        var v = Box(rects, 0x0000FF);
        Near(390, v.X);
        var w = Box(rects, 0xFF0000);
        Near(100, w.X);
        Near(265, w.Y);
    }

    [Fact]
    public void A_box_that_fits_keeps_its_own_placement()
    {
        var rects = Layout(Stage.Replace(".t{", ".t{position-try-fallbacks:flip-block,flip-inline;", StringComparison.Ordinal),
            "<div class=s><div class=a></div><div class=t style='position-area:bottom'></div></div>");
        Near(150, Box(rects, 0xFF0000).Y);
    }

    [Fact]
    public void Anchor_center_centres_the_box_on_its_anchor()
    {
        var rects = Layout(Stage + ".u{position:absolute;position-anchor:--a;top:anchor(bottom);justify-self:anchor-center;width:30px;height:10px;background:#00ff00}",
            "<div class=s><div class=a></div><div class=u></div></div>");
        Near(235, Box(rects, 0x00FF00).X);
    }

    [Fact]
    public void Right_to_left_reverses_the_inline_keywords()
    {
        var rects = Layout(Stage, "<div class=s dir=rtl><div class=a></div>"
            + "<div class=t style='position-area:block-start inline-start'></div></div>");
        Near(300, Box(rects, 0xFF0000).X);
    }

    [Fact]
    public void Tables_and_flex_items_are_anchors_too()
    {
        var rects = Layout("table{anchor-name:--t;border-collapse:collapse}td{padding:0;width:80px;height:20px}"
            + ".f{display:flex}.f div{width:50px;height:30px}.fi{anchor-name:--f}"
            + ".u{position:absolute;position-anchor:--t;position-area:right;width:10px;height:10px;background:#00ff00}"
            + ".v{position:absolute;position-anchor:--f;position-area:bottom;width:10px;height:10px;background:#0000ff}",
            "<table><tr><td></td></tr></table><div class=f><div></div><div class=fi></div></div><div class=u></div><div class=v></div>");
        Near(80, Box(rects, 0x00FF00).X);
        var v = Box(rects, 0x0000FF);
        Near(70, v.X);
        Near(50, v.Y);
    }

    [Fact]
    public void Supports_knows_the_anchor_properties_and_functions()
    {
        var pdf = HtmlToPdf.Convert("<style>p{color:red}@supports (top: anchor(--a bottom)) and (position-area: top) and (anchor-name: --a){p{color:green}}"
            + "@supports (color: anchor(--a top)){p{font-size:40px}}</style><p>x</p>");
        Assert.NotEmpty(pdf);
        var doc = HtmlParser.Parse("<style>@supports (top: anchor(--a bottom)) and (position-area: top){p{color:#00ff00}}@supports (color: anchor(--a top)){p{width:7px}}</style><p>x</p>");
        Resolve(doc);
        var p = All(doc).First(e => e.Name == "p");
        Assert.Equal(0xFF00FF00u, p.Style.Color);
        Assert.True(p.Style.Width.IsAuto);
    }

    [Fact]
    public void A_later_declaration_without_anchor_functions_replaces_one_with_them()
    {
        var rects = Layout(Stage + ".u{position:absolute;position-anchor:--a;top:anchor(bottom);left:0;width:10px;height:10px;background:#00ff00}.u.v{top:7px}",
            "<div class=s><div class=a></div><div class='u v'></div></div>");
        Near(7, Box(rects, 0x00FF00).Y);
    }

    [Fact]
    public void Anchors_convert_to_pdf_with_the_text_in_place()
    {
        var html = new StringBuilder("<style>.a{anchor-name:--a;margin:40px 100px;width:80px}.t{position:absolute;position-anchor:--a;position-area:bottom}</style>");
        html.Append("<div class=a>Anchor</div><div class=t>Tip</div>");
        var text = PdfText.Extract(HtmlToPdf.Convert(html.ToString()));
        Assert.Contains("Anchor", text, StringComparison.Ordinal);
        Assert.Contains("Tip", text, StringComparison.Ordinal);
    }
}
