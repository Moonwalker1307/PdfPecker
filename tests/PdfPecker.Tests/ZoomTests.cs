using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// The zoom property. The expected sizes are Chrome's: every absolute length on a zoomed element and its descendants is
/// multiplied by the product of the zooms, percentages are not, and em and rem follow the zoomed font sizes.
/// </summary>
public class ZoomTests
{
    private const float Width = 450, PageHeight = 800; // 600 by 1067 CSS pixels
    private const uint Blue = 0xFF0000FF;

    private static (Element Doc, List<DrawOp> Ops) Layout(string body)
    {
        var doc = HtmlParser.Parse("<html><body><style>body{margin:0;font:16px/20px sans-serif} .b{background:#00f}</style>" + body + "</body></html>");
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return (doc, engine.Ops);
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static Element? ById(Element e, string id)
    {
        foreach (var c in e.Children.OfType<Element>())
            if ((c.GetAttribute("id") == id ? c : ById(c, id)) is { } found) return found;
        return null;
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            if (op is GroupOp g) foreach (var o in Flatten(g.Ops)) yield return o;
        }
    }

    /// <summary>The size of the blue background, in CSS pixels.</summary>
    private static (float W, float H) Box(string body)
    {
        var r = Flatten(Layout(body).Ops).OfType<RectOp>().First(o => o.Color == Blue);
        return (r.W / 0.75f, r.H / 0.75f);
    }

    private static ComputedStyle StyleOf(string body, string id) => ById(Layout(body).Doc, id)!.Style;

    [Fact]
    public void Absolute_lengths_are_multiplied_by_the_zoom()
        => Assert.Equal((114f, 34f), Box("<div class=b style='zoom:.5;width:200px;height:40px;border:4px solid;padding:10px'></div>"));

    [Fact]
    public void Percentages_are_not_zoomed_but_em_follows_the_zoomed_font()
        => Assert.Equal((300f, 16f), Box("<div class=b style='zoom:.5;width:50%;height:2em'></div>"));

    [Fact]
    public void Zooms_multiply()
        => Assert.Equal((100f, 10f), Box("<div style='zoom:.5'><div class=b style='zoom:2;width:100px;height:10px'></div></div>"));

    [Theory]
    [InlineData("200%", 200)]
    [InlineData("2", 200)]
    [InlineData("0", 100)]       // 0 is 1
    [InlineData("normal", 100)]
    [InlineData("-1", 100)]      // invalid
    [InlineData("large", 100)]   // invalid
    public void Zoom_values(string zoom, float width)
        => Assert.Equal(width, Box($"<div class=b style='zoom:{zoom};width:100px;height:10px'></div>").W);

    [Fact]
    public void Viewport_units_and_calc_are_zoomed_where_they_are_lengths()
    {
        Assert.Equal((30f, 16f), Box("<div class=b style='zoom:.5;width:10vw;height:2rem'></div>"));
        Assert.Equal(110f, Box("<div class=b style='zoom:.5;width:calc(100px + 10%);height:10px'></div>").W, 2);
    }

    [Fact]
    public void Font_sizes_are_zoomed_once()
    {
        const string body = "<div style='zoom:.5;font-size:20px'><span id=a>x</span><span id=b style='font-size:1.5em'>x</span>"
                            + "<span id=c style='font-size:1rem'>x</span><span id=d style='font-size:medium'>x</span></div>"
                            + "<div style='font-size:30px'><div style='zoom:.5'><span id=e>x</span><span id=f style='font-size:larger'>x</span></div></div>";
        var doc = Layout(body).Doc;
        float Px(string id) => ById(doc, id)!.Style.FontSize / 0.75f;
        Assert.Equal(10, Px("a"), 3);
        Assert.Equal(15, Px("b"), 3);
        Assert.Equal(8, Px("c"), 3);
        Assert.Equal(8, Px("d"), 3);
        Assert.Equal(15, Px("e"), 3);
        Assert.Equal(18, Px("f"), 3);
    }

    [Fact]
    public void Inherited_lengths_take_the_childs_own_zoom()
    {
        var s = StyleOf("<div style='line-height:30px;letter-spacing:2px'><div id=a style='zoom:2'>x</div></div>", "a");
        Assert.Equal(60, s.LineHeight / 0.75f, 2);
        Assert.Equal(4, s.LetterSpacing / 0.75f, 2);
    }

    [Fact]
    public void Border_width_keywords_are_zoomed()
    {
        var plain = StyleOf("<div id=a style='border:solid;outline:thin solid'>x</div>", "a");
        var zoomed = StyleOf("<div id=a style='zoom:2;border:solid;outline:thin solid'>x</div>", "a");
        Assert.Equal(2 * plain.BorderTopWidth, zoomed.BorderTopWidth, 2);
        Assert.Equal(2 * plain.OutlineWidth, zoomed.OutlineWidth, 2);
    }

    [Fact]
    public void An_images_own_size_is_zoomed()
    {
        const string svg = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='100' height='60'/%3E";
        var (_, ops) = Layout($"<img style='zoom:.5' src=\"{svg}\">");
        var image = Flatten(ops).OfType<SvgOp>().Single();
        Assert.Equal((50f, 30f), (image.W / 0.75f, image.H / 0.75f));
    }

    [Fact]
    public void Deeply_nested_zooms_stay_finite()
    {
        string open = string.Concat(Enumerable.Repeat("<div style='zoom:10;width:100px'>", 100));
        var s = StyleOf(open + "<span id=a>x</span>", "a");
        Assert.True(float.IsFinite(s.FontSize) && s.Zoom <= 64);
    }

    [Fact]
    public void Supports_knows_zoom()
    {
        Assert.True(CssSupports.Evaluate("(zoom: 50%)"));
        Assert.False(CssSupports.Evaluate("(zoom: -1)"));
    }
}
