using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Tests;

/// <summary>transform-box and transform-origin on SVG elements, and transform-box on HTML boxes.</summary>
public class TransformBoxTests
{
    private static Element Resolve(string html, float width = 500, float height = 800)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Descendants(doc).Where(e => e.Name == "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, width, height).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
    }

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>The bounds of the one shape of an svg of 100 x 100 px holding <paramref name="content"/>.</summary>
    private static (float MinX, float MinY, float MaxX, float MaxY) Bounds(string content)
    {
        var doc = Resolve($"<svg id='s' width='100' height='100'>{content}</svg>");
        var shape = SvgImage.Build(Descendants(doc).First(e => e.Id == "s")).Shapes.Single();
        return (shape.MinX, shape.MinY, shape.MaxX, shape.MaxY);
    }

    private static void Near((float MinX, float MinY, float MaxX, float MaxY) actual, float minX, float minY, float maxX, float maxY)
    {
        Assert.Equal(minX, actual.MinX, 2);
        Assert.Equal(minY, actual.MinY, 2);
        Assert.Equal(maxX, actual.MaxX, 2);
        Assert.Equal(maxY, actual.MaxY, 2);
    }

    // A rectangle of 20 x 40 at (40, 10); its centre is (50, 30).
    private const string Needle = "x='40' y='10' width='20' height='40'";

    [Fact]
    public void An_svg_element_turns_about_the_origin_of_the_viewport_by_default()
    {
        // (x, y) goes to (-y, x): x from -50 to -10, y from 40 to 60.
        Near(Bounds($"<rect {Needle} style='transform:rotate(90deg)'/>"), -50, 40, -10, 60);
    }

    [Fact]
    public void Fill_box_with_a_centre_origin_turns_the_element_about_its_own_centre()
    {
        Near(Bounds($"<rect {Needle} style='transform-box:fill-box;transform-origin:center;transform:rotate(90deg)'/>"), 30, 20, 70, 40);
    }

    [Fact]
    public void Fill_box_without_an_origin_turns_about_the_corner_of_the_bounds()
    {
        // About (40, 10): (x, y) goes to (40 - (y - 10), 10 + (x - 40)).
        Near(Bounds($"<rect {Needle} style='transform-box:fill-box;transform:rotate(90deg)'/>"), 0, 10, 40, 30);
    }

    [Fact]
    public void The_bounds_of_a_group_are_those_of_what_it_holds()
    {
        Near(Bounds("<g style='transform-box:fill-box;transform-origin:center;transform:scale(2)'><rect x='0' y='0' width='10' height='10'/></g>"),
            -5, -5, 15, 15);
    }

    [Fact]
    public void Stroke_box_includes_half_the_stroke_width()
    {
        // The stroke box is 30 x 50 at (35, 5); its bottom-right corner (65, 55) stays where it is.
        Near(Bounds($"<rect {Needle} stroke='red' stroke-width='10' style='transform-box:stroke-box;transform-origin:100% 100%;transform:scale(2)'/>"), 15, -35, 55, 45);
    }

    [Fact]
    public void Content_and_border_box_are_the_fill_and_stroke_box_of_an_svg_element()
    {
        var fill = Bounds($"<rect {Needle} style='transform-box:content-box;transform-origin:center;transform:rotate(90deg)'/>");
        Near(fill, 30, 20, 70, 40);
        var stroke = Bounds($"<rect {Needle} stroke='red' stroke-width='10' style='transform-box:border-box;transform-origin:100% 100%;transform:scale(2)'/>");
        var plain = Bounds($"<rect {Needle} stroke='red' stroke-width='10' style='transform-box:stroke-box;transform-origin:100% 100%;transform:scale(2)'/>");
        Assert.Equal(plain, stroke);
    }

    [Fact]
    public void View_box_with_an_origin_refers_to_the_viewport()
    {
        // 50% of the 100 x 100 viewport is (50, 50); (x, y) goes to (100 - y, x).
        Near(Bounds($"<rect {Needle} style='transform-box:view-box;transform-origin:50% 50%;transform:rotate(90deg)'/>"), 50, 40, 90, 60);
        Near(Bounds($"<rect {Needle} style='transform-origin:50px 50px;transform:rotate(90deg)'/>"), 50, 40, 90, 60);
    }

    [Fact]
    public void Percentages_in_a_transform_refer_to_the_reference_box()
    {
        // 50% of the 20 px wide bounds is 10 px.
        Near(Bounds($"<rect {Needle} style='transform-box:fill-box;transform:translate(50%,0)'/>"), 50, 10, 70, 50);
    }

    [Fact]
    public void The_transform_attribute_takes_the_origin_too()
    {
        Near(Bounds($"<rect {Needle} transform='rotate(90)' style='transform-origin:50px 30px'/>"), 30, 20, 70, 40);
        Near(Bounds($"<rect {Needle} transform='rotate(90)' style='transform-box:fill-box;transform-origin:center'/>"), 30, 20, 70, 40);
        // Without either, the attribute is unchanged.
        Near(Bounds($"<rect {Needle} transform='rotate(90)'/>"), -50, 40, -10, 60);
    }

    [Fact]
    public void The_css_transform_wins_over_the_attribute()
    {
        Near(Bounds($"<rect {Needle} transform='translate(100 100)' style='transform:translate(5px,5px)'/>"), 45, 15, 65, 55);
    }

    [Fact]
    public void A_stylesheet_rule_applies_the_gauge_needle_pattern()
    {
        var doc = Resolve("<style>.needle{transform-box:fill-box;transform-origin:center;transform:rotate(90deg)}</style>"
            + $"<svg id='s' width='100' height='100'><rect class='needle' {Needle}/></svg>");
        var shape = SvgImage.Build(Descendants(doc).First(e => e.Id == "s")).Shapes.Single();
        Near((shape.MinX, shape.MinY, shape.MaxX, shape.MaxY), 30, 20, 70, 40);
    }

    [Fact]
    public void A_nested_svg_gives_the_percentages_of_its_own_viewport()
    {
        // The inner svg is 40 x 40: 50% is 20, not 50 as in the outer viewport of 100 x 100.
        var doc = Resolve("<svg id='s' width='100' height='100'><svg width='40' height='40'>"
            + "<rect x='0' y='0' width='10' height='10' style='transform-origin:50% 50%;transform:rotate(180deg)'/></svg></svg>");
        var shape = SvgImage.Build(Descendants(doc).First(e => e.Id == "s")).Shapes.Single();
        Near((shape.MinX, shape.MinY, shape.MaxX, shape.MaxY), 30, 30, 40, 40);
    }

    [Theory]
    [InlineData("view-box", "ViewBox")]
    [InlineData("content-box", "ContentBox")]
    [InlineData("border-box", "BorderBox")]
    [InlineData("fill-box", "FillBox")]
    [InlineData("stroke-box", "StrokeBox")]
    [InlineData("nonsense", "ViewBox")]
    public void Transform_box_parses_its_keywords(string value, string expected)
    {
        var doc = Resolve($"<div id='d' style='transform-box:{value}'></div>");
        Assert.Equal(expected, Descendants(doc).First(e => e.Id == "d").Style!.TransformBox.ToString());
    }

    [Fact]
    public void Transform_box_and_vector_effect_are_supported_in_supports()
    {
        Assert.True(CssSupports.Evaluate("(transform-box: fill-box)"));
        Assert.True(CssSupports.Evaluate("(vector-effect: non-scaling-stroke)"));
    }

    // ---- HTML boxes ----

    private static LayerOp BoxLayer(string style)
    {
        var doc = Resolve($"<div style='position:absolute;left:0;top:0;width:100px;height:60px;padding:20px 10px 0 30px;border:6px solid;transform:rotate(10deg);{style}'></div>");
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false)) { FixedAreaHeight = 800 };
        engine.Layout(doc, 500);
        LayerOp? found = null;
        void Walk(IEnumerable<DrawOp> ops)
        {
            foreach (var op in ops)
            {
                if (op is LayerOp l && l.Transform != null) found ??= l;
                if (op is GroupOp g) Walk(g.Ops);
            }
        }
        Walk(engine.Ops);
        return found!;
    }

    [Fact]
    public void An_html_box_turns_about_its_border_box_unless_the_box_is_the_content_box()
    {
        // The border box is 152 x 92 px wide and high; the content box 100 x 60 px, from (36, 26) px.
        var border = BoxLayer("");
        Assert.Equal(76 * 0.75f, border.OriginX, 2);
        Assert.Equal(46 * 0.75f, border.OriginY, 2);
        var content = BoxLayer("transform-box:content-box");
        Assert.Equal((36 + 50) * 0.75f, content.OriginX, 2);
        Assert.Equal((26 + 30) * 0.75f, content.OriginY, 2);
        Assert.Equal(content.OriginX, BoxLayer("transform-box:fill-box").OriginX, 2);
        Assert.Equal(border.OriginX, BoxLayer("transform-box:stroke-box").OriginX, 2);
        Assert.Equal(border.OriginX, BoxLayer("transform-box:view-box").OriginX, 2);
    }

    [Fact]
    public void The_origin_of_a_content_box_takes_its_own_offsets()
    {
        var content = BoxLayer("transform-box:content-box;transform-origin:0 0");
        Assert.Equal(36 * 0.75f, content.OriginX, 2);
        Assert.Equal(26 * 0.75f, content.OriginY, 2);
    }
}
