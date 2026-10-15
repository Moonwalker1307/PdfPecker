using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;
using PdfPecker.Svg;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>CSS and SVG gradients, SVG text and clip paths, and named grid lines and areas.</summary>
public class GraphicsFeatureTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string html, float width = Width)
    {
        var doc = Styled(html, width);
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return engine.Ops;
    }

    private static Element Styled(string html, float width = Width)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
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

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static RectOp Box(List<DrawOp> ops, string color)
    {
        Assert.True(CssColor.TryParse(color, 0xFF000000, out var c));
        return Flatten(ops).OfType<RectOp>().Single(r => r.Color == c);
    }

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}");

    /// <summary>The RGB of a pixel of the first page rendered at 72 dpi (1 px = 1 pt).</summary>
    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0) };

    // ---- CSS gradients ----

    [Fact]
    public void Linear_gradient_geometry_follows_the_direction()
    {
        var right = CssGradient.Parse("linear-gradient(to right, red, blue)", CssColor.Black, 12, 12, 5, 8)!.Resolve(200, 100, 0xFFFFFFFF);
        Near(0, right.X0); Near(50, right.Y0); Near(200, right.X1); Near(50, right.Y1);
        Assert.Equal(new[] { 0f, 1f }, right.Offsets);
        Assert.Equal(0xFFFF0000u, right.Colors[0]);

        // Default is "to bottom"; 45deg runs to the top right with a line long enough to reach both corners.
        var down = CssGradient.Parse("linear-gradient(red, blue)", CssColor.Black, 12, 12, 5, 8)!.Resolve(200, 100, 0xFFFFFFFF);
        Near(0, down.Y0); Near(100, down.Y1);
        var diag = CssGradient.Parse("linear-gradient(45deg, red, blue)", CssColor.Black, 12, 12, 5, 8)!.Resolve(100, 100, 0xFFFFFFFF);
        Near(100 * MathF.Sqrt(2), MathF.Sqrt((diag.X1 - diag.X0) * (diag.X1 - diag.X0) + (diag.Y1 - diag.Y0) * (diag.Y1 - diag.Y0)), 0.1f);
        Assert.True(diag.X1 > diag.X0 && diag.Y1 < diag.Y0);
    }

    [Fact]
    public void Gradient_stops_get_positions_and_flatten_on_the_backdrop()
    {
        var g = CssGradient.Parse("linear-gradient(90deg, red, rgba(0,0,255,0.5) 25%, lime, 60pt, black 100pt)", CssColor.Black, 12, 12, 5, 8)!
            .Resolve(200, 50, 0xFF000000);
        Assert.Equal(4, g.Offsets.Length); // the 60pt colour hint is ignored
        Near(0.25f, g.Offsets[1]); Near(0.375f, g.Offsets[2]); Near(0.5f, g.Offsets[3]);
        Assert.Equal(0xFF000080u, g.Colors[1]); // half-transparent blue over black
    }

    [Fact]
    public void Repeating_gradients_and_radial_sizes_resolve()
    {
        var stripes = CssGradient.Parse("repeating-linear-gradient(to right, red 10pt, blue 30pt)", CssColor.Black, 12, 12, 5, 8)!.Resolve(100, 10, 0xFFFFFFFF);
        Assert.Equal(GradientSpread.Repeat, stripes.Spread);
        Near(10, stripes.X0); Near(30, stripes.X1);

        var circle = CssGradient.Parse("radial-gradient(circle closest-side at 30pt 20pt, red, blue)", CssColor.Black, 12, 12, 5, 8)!.Resolve(100, 100, 0xFFFFFFFF);
        Assert.True(circle.Radial);
        Near(20, circle.R1);
        float x = circle.X1, y = circle.Y1;
        circle.Transform.Apply(ref x, ref y);
        Near(30, x); Near(20, y);

        var ellipse = CssGradient.Parse("radial-gradient(red, blue)", CssColor.Black, 12, 12, 5, 8)!.Resolve(200, 100, 0xFFFFFFFF);
        Near(100 * MathF.Sqrt(2), ellipse.R1, 0.1f); // farthest-corner keeps the farthest-side aspect ratio
        Near(0.5f, ellipse.Transform.D);
    }

    [Fact]
    public void Background_gradient_is_painted_as_a_shading_pattern()
    {
        var html = "<div style='height:50pt; background: linear-gradient(to right, #ff0000, #0000ff), #00ff00'></div>";
        var ops = Flatten(Layout(html)).ToList();
        Assert.Single(ops.OfType<BackgroundOp>());
        Assert.Single(ops.OfType<RectOp>()); // the colour layer underneath

        var pdf = ConvertUncompressed("<body style='margin:0'>" + html + "</body>", NoMargins);
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("/Pattern << /P1 << /PatternType 2", text);
        Assert.Contains("/ShadingType 2", text);
        Assert.Contains("/Pattern cs /P1 scn", AllContent(pdf));
        var left = Pixel(pdf, 3, 20);
        var right = Pixel(pdf, 590, 20);
        Assert.True(left.R > 230 && left.B < 30, $"left {left}");
        Assert.True(right.B > 230 && right.R < 30, $"right {right}");
    }

    [Fact]
    public void Gradient_background_split_over_pages_continues()
    {
        var pdf = ConvertUncompressed("<div style='height:1500pt; background:linear-gradient(red, blue)'></div>");
        Assert.Equal(2, PageCount(pdf));
        Assert.Contains("/P2 ", Latin1(pdf));
    }

    // ---- SVG gradients, clip paths and text ----

    [Fact]
    public void Svg_gradients_become_shadings()
    {
        var doc = Styled("""
            <svg id="s" width="100" height="100">
              <defs>
                <linearGradient id="a"><stop offset="0" stop-color="red"/><stop offset="100%" stop-color="blue"/></linearGradient>
                <linearGradient id="b" href="#a" x2="0" y2="1" spreadMethod="reflect"/>
                <radialGradient id="c" gradientUnits="userSpaceOnUse" cx="50" cy="50" r="40"><stop offset=".5" stop-color="#fff"/><stop offset="1" stop-color="#000"/></radialGradient>
                <linearGradient id="one"><stop stop-color="#00ff00"/></linearGradient>
              </defs>
              <rect x="10" y="20" width="40" height="10" fill="url(#a)"/>
              <rect width="10" height="10" fill="url(#b)"/>
              <circle cx="50" cy="50" r="40" fill="url(#c)" stroke="url(#a)"/>
              <rect width="10" height="10" fill="url(#one)"/>
            </svg>
            """);
        var shapes = SvgImage.Build(ById(doc, "s")).Shapes;
        var a = shapes[0].FillGradient!;
        float x0 = a.X0, y0 = a.Y0, x1 = a.X1, y1 = a.Y1;
        a.Transform.Apply(ref x0, ref y0);
        a.Transform.Apply(ref x1, ref y1);
        Near(10, x0); Near(50, x1); // objectBoundingBox
        var b = shapes[1].FillGradient!;
        Assert.Equal(GradientSpread.Reflect, b.Spread);
        Near(0, b.X1); Near(1, b.Y1);
        Assert.Equal(2, b.Colors.Length); // stops inherited through href
        var c = shapes[2];
        Assert.True(c.FillGradient!.Radial);
        Near(40, c.FillGradient.R1);
        Assert.NotNull(c.StrokeGradient);
        Assert.Equal(0xFF00FF00u, shapes[3].Fill); // one stop: a plain colour
    }

    [Fact]
    public void Svg_clip_path_clips_drawing()
    {
        var html = """
            <body style="margin:0"><svg width="100" height="100" viewBox="0 0 100 100">
              <clipPath id="c"><circle cx="50" cy="50" r="30"/></clipPath>
              <rect width="100" height="100" fill="#ff0000" clip-path="url(#c)"/>
              <clipPath id="empty"></clipPath>
              <rect width="100" height="100" fill="#0000ff" clip-path="url(#empty)"/>
            </svg></body>
            """;
        var pdf = ConvertUncompressed(html, NoMargins);
        Assert.Contains("W n", AllContent(pdf));
        var center = Pixel(pdf, 37, 37);   // 50 px = 37.5 pt
        var corner = Pixel(pdf, 4, 4);
        Assert.True(center.R > 230 && center.G < 30 && center.B < 30, $"center {center}");
        Assert.True(corner.R > 230 && corner.G > 230 && corner.B > 230, $"corner {corner}");
    }

    [Fact]
    public void Svg_clip_path_in_object_bounding_box_units()
    {
        var doc = Styled("""
            <svg id="s" width="200" height="200">
              <clipPath id="c" clipPathUnits="objectBoundingBox"><rect x="0" y="0" width=".5" height="1"/></clipPath>
              <g clip-path="url(#c)"><rect x="100" y="0" width="100" height="50" fill="red"/></g>
            </svg>
            """);
        var shapes = SvgImage.Build(ById(doc, "s")).Shapes;
        Assert.Equal(SvgShape.KindClip, shapes[0].Kind);
        Assert.Equal(SvgShape.KindEndClip, shapes[^1].Kind);
        Assert.Equal(new[] { 100f, 0f, 150f, 0f, 150f, 50f, 100f, 50f }, shapes[0].Points);
    }

    [Fact]
    public void Svg_text_is_put_in_visual_order_with_its_direction()
    {
        var doc = Styled("""
            <svg id="s" width="400" height="100" font-family="Helvetica">
              <text x="10" y="20" font-size="20">ab <tspan fill="red">אבג</tspan> cd</text>
              <text x="300" y="60" font-size="20" direction="rtl">ab cd</text>
            </svg>
            """);
        var texts = SvgImage.Build(ById(doc, "s")).Shapes.Where(s => s.Kind == SvgShape.KindText).ToList();
        // Left to right, the Hebrew word stays between the Latin ones and is drawn right to left.
        Assert.Equal(new[] { "ab ", "אבג", " cd" }, texts.Take(3).Select(t => t.Text));
        Assert.Equal(new[] { false, true, false }, texts.Take(3).Select(t => t.Rtl));
        Assert.True(texts[0].TextMatrix.E < texts[1].TextMatrix.E && texts[1].TextMatrix.E < texts[2].TextMatrix.E);
        Near(10, texts[0].TextMatrix.E, 0.01f);
        // Right to left, text-anchor: start puts the end of the text (its right end) at x.
        var rtl = texts.Skip(3).ToList();
        var font = (PdfPecker.Fonts.StandardFont)rtl[0].Font!;
        float total = rtl.Sum(t => font.Measure(t.Text!, 20));
        Near(300 - total, rtl.Min(t => t.TextMatrix.E), 0.01f);
    }

    [Fact]
    public void Svg_text_is_laid_out_with_anchors_and_tspans()
    {
        var doc = Styled("""
            <svg id="s" width="200" height="100" font-family="Helvetica">
              <text x="100" y="50" font-size="20" text-anchor="middle">
                Hello   <tspan font-weight="bold" fill="#ff0000">big</tspan> world
              </text>
              <text x="10" y="80" dx="5" font-size="10" dominant-baseline="middle">A</text>
            </svg>
            """);
        var texts = SvgImage.Build(ById(doc, "s")).Shapes.Where(s => s.Kind == SvgShape.KindText).ToList();
        Assert.Equal(new[] { "Hello ", "big", " world", "A" }, texts.Select(t => t.Text));
        Assert.Contains("Bold", ((PdfPecker.Fonts.StandardFont)texts[1].Font!).BaseFont);
        Assert.Equal(0xFFFF0000u, texts[1].Fill);
        var font = texts[0].Font!;
        float total = font.Measure("Hello ", 20) + texts[1].Font!.Measure("big", 20) + font.Measure(" world", 20);
        Near(100 - total / 2, texts[0].TextMatrix.E, 0.01f);
        Near(50, texts[0].TextMatrix.F);
        Near(-1, texts[0].TextMatrix.D); // glyphs are flipped back upright in the y-down user space
        Near(15, texts[3].TextMatrix.E);
        Near(80 + 3.5f, texts[3].TextMatrix.F, 0.01f);
    }

    [Fact]
    public void Svg_text_is_written_into_the_pdf()
    {
        var pdf = ConvertUncompressed("<svg width='200' height='40'><text x='0' y='30' font-family='Courier' fill='blue'>Svg label</text></svg>");
        AssertStructurallyValid(pdf);
        var content = AllContent(pdf);
        Assert.Contains("(Svg label) Tj", content);
        Assert.Contains("/BaseFont /Courier", Latin1(pdf));
    }

    // ---- Named grid lines and areas ----

    [Fact]
    public void Grid_template_areas_place_items_by_name()
    {
        var ops = Layout("""
            <div style="display:grid; grid-template-areas: 'head head' 'side main' 'foot foot'; grid-template-columns: 100pt 1fr; grid-auto-rows: 20pt">
              <div style="grid-area: main; background:#00ff00"></div>
              <div style="grid-area: foot; background:#0000ff"></div>
              <div style="grid-area: head; background:#ff0000"></div>
              <div style="grid-area: side; background:#ffff00"></div>
            </div>
            """);
        var head = Box(ops, "#ff0000"); var side = Box(ops, "#ffff00"); var main = Box(ops, "#00ff00"); var foot = Box(ops, "#0000ff");
        Near(0, head.Y); Near(500, head.W);
        Near(20, side.Y); Near(0, side.X); Near(100, side.W);
        Near(20, main.Y); Near(100, main.X); Near(400, main.W);
        Near(40, foot.Y); Near(500, foot.W);
    }

    [Fact]
    public void Grid_template_shorthand_and_named_lines()
    {
        var ops = Layout("""
            <div style="display:grid; grid-template: 'a b' 30pt 'c c' 10pt / [left] 1fr [mid] 3fr [right]">
              <div style="grid-area: c; background:#ff0000"></div>
              <div style="grid-row: 1; grid-column: mid / right; background:#00ff00"></div>
              <div style="grid-column: left; grid-row-start: a; background:#0000ff"></div>
            </div>
            """);
        var c = Box(ops, "#ff0000"); var mid = Box(ops, "#00ff00"); var left = Box(ops, "#0000ff");
        Near(30, c.Y); Near(10, c.H); Near(500, c.W);
        Near(125, mid.X); Near(375, mid.W); Near(30, mid.H);
        Near(0, left.X); Near(0, left.Y); Near(125, left.W);
    }

    [Fact]
    public void Grid_areas_parse_and_reject_non_rectangles()
    {
        var areas = StyleResolver.ParseGridAreas("\"a a .\" \"a a b\"")!;
        Assert.Equal(2, areas.Rows);
        Assert.Equal(3, areas.Columns);
        Assert.Equal((0, 0, 2, 2), areas.Areas["a"]);
        Assert.Equal((1, 2, 2, 3), areas.Areas["b"]);
        Assert.Null(StyleResolver.ParseGridAreas("'a a' 'a b' 'a a'"));
        Assert.Null(StyleResolver.ParseGridAreas("'a b' 'c'"));
    }
}
