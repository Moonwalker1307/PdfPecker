using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// shape-outside, shape-margin and shape-image-threshold: lines beside a float wrap around its shape. The expected
/// positions are Chrome's (CSS Shapes 1 as Chrome computes it), in points: one CSS pixel is 0.75 pt.
/// </summary>
public class FloatShapeTests
{
    private const float Width = 500, PageHeight = 800;

    // A 100px float beside lines 20px high, each of one short word.
    private const string Style = "<style>body{margin:0;font:16px/20px sans-serif} p{margin:0} .f{float:left;width:100px;height:100px}</style>";
    private const string Words = "<p>a<br>b<br>c<br>d<br>e<br>f<br>g<br>h</p>";

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

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            if (op is GroupOp g) foreach (var o in Flatten(g.Ops)) yield return o;
        }
    }

    /// <summary>Where each line starts, top to bottom.</summary>
    private static float[] LineStarts(List<DrawOp> ops)
        => Flatten(ops).OfType<TextOp>().GroupBy(t => t.LineTop).OrderBy(g => g.Key).Select(g => g.Min(t => t.X)).ToArray();

    private static float[] LineStarts(string floatStyle, string? text = null)
        => LineStarts(Layout(Style + $"<div class=f style='{floatStyle}'></div>" + (text ?? Words)));

    [Fact]
    public void Lines_without_a_shape_start_after_the_margin_box()
    {
        var starts = LineStarts("");
        Assert.All(starts.Take(5), x => Assert.Equal(75, x, 2));
    }

    [Fact]
    public void Circle_lets_lines_into_its_corners()
    {
        // circle(50%) of 50px: the first line (0 to 20px) meets it at 20px, 40px from the left edge's middle.
        var starts = LineStarts("shape-outside: circle(50%)");
        Assert.Equal(90 * 0.75f, starts[0], 2);
        Assert.Equal(75, starts[2], 2); // the line across the middle
        Assert.Equal(90 * 0.75f, starts[4], 2);
        Assert.Equal(0, starts[5], 2); // below the float
    }

    [Fact]
    public void Shape_margin_grows_the_shape_within_the_margin_box()
    {
        // The circle grows to 60px; at the first line it would reach 102px, but the float area stays in the margin box.
        var starts = LineStarts("shape-outside: circle(50%); shape-margin: 10px");
        Assert.Equal(75, starts[0], 2);
        var narrow = LineStarts("shape-outside: circle(30%); shape-margin: 5px");
        // circle(30%) has a radius of 30px, 35px with the margin: the first line meets it at dy = 30px.
        Assert.Equal((50 + 35 * MathF.Sqrt(1 - 30f * 30 / (35 * 35))) * 0.75f, narrow[0], 2);
    }

    [Fact]
    public void Ellipse_and_inset_follow_their_outlines()
    {
        var ellipse = LineStarts("shape-outside: ellipse(25% 50% at 0 50%)");
        Assert.Equal(25 * MathF.Sqrt(1 - 30f * 30 / (50 * 50)) * 0.75f, ellipse[0], 2);
        // inset(): the lines above and below it pass, and the rounded corner lets the first line in.
        var inset = LineStarts("shape-outside: inset(20px 40px 20px 0 round 0 20px 0 0)", Words);
        Assert.Equal(0, inset[0], 2);
        Assert.Equal(60 * 0.75f, inset[1], 2);
        Assert.Equal(0, inset[4], 2);
    }

    [Fact]
    public void Polygon_lines_take_its_widest_point_in_their_band()
    {
        // A triangle with its right angle top left: the band from 40 to 60px is widest at its top, 60px.
        var starts = LineStarts("shape-outside: polygon(0 0, 100% 0, 0 100%)");
        Assert.Equal(75, starts[0], 2);
        Assert.Equal(60 * 0.75f, starts[2], 2);
        Assert.Equal(20 * 0.75f, starts[4], 2);
    }

    [Fact]
    public void Polygon_margin_adds_a_circle_round_each_corner()
    {
        var starts = LineStarts("shape-outside: polygon(0 0, 50% 0, 0 100%); shape-margin: 10px");
        // The edge from (50, 0) to (0, 100) moved out by 10px, at the band's top of 40px.
        float nx = 100 / MathF.Sqrt(100 * 100 + 50 * 50), ny = 50 / MathF.Sqrt(100 * 100 + 50 * 50);
        float x = 50 + 10 * nx - (40 - 10 * ny) * 50 / 100;
        Assert.Equal(x * 0.75f, starts[2], 1);
    }

    [Fact]
    public void Right_float_shape_moves_the_line_ends()
    {
        var ops = Layout(Style + "<div class=f style='float:right;shape-outside:circle(50%)'></div><p style='text-align:right'>a<br>b<br>c<br>d<br>e<br>f<br>g<br>h</p>");
        var ends = Flatten(ops).OfType<TextOp>().GroupBy(t => t.LineTop).OrderBy(g => g.Key)
            .Select(g => g.Max(t => t.X + t.Font.Measure(t.Text, t.Size))).ToArray();
        Assert.Equal((500 / 0.75f - 90) * 0.75f, ends[0], 1);
        Assert.Equal(Width - 75, ends[2], 1);
    }

    [Fact]
    public void Reference_boxes_and_rounded_corners()
    {
        // margin-box with border-radius follows the corners; content-box shrinks the box by the border and padding.
        var rounded = LineStarts("border-radius: 50px; shape-outside: margin-box");
        Assert.Equal(90 * 0.75f, rounded[0], 2);
        var content = LineStarts("width:60px;height:60px;padding:10px;border:10px solid;shape-outside: content-box");
        Assert.Equal(0, content[0], 2);
        Assert.Equal(80 * 0.75f, content[1], 2);
        Assert.Equal(0, content[4], 2);
    }

    [Fact]
    public void Image_alpha_gives_the_shape_and_the_threshold_cuts_it()
    {
        // The image fades from opaque on the left to clear on the right.
        string src = DataUri(AlphaPng(100, 100, (x, _) => (byte)(255 - x * 255 / 99)));
        var any = LineStarts(Layout(Style + $"<img class=f src='{src}' style='shape-outside:url({src})'>" + Words));
        Assert.Equal(99 * 0.75f, any[0], 2);
        var half = LineStarts(Layout(Style + $"<img class=f src='{src}' style='shape-outside:url({src});shape-image-threshold:0.5'>" + Words));
        Assert.Equal(50 * 0.75f, half[0], 2);
    }

    [Fact]
    public void Image_rows_are_whole_pixels_and_follow_the_alpha_shape()
    {
        // A right triangle of opaque pixels: row y is opaque up to 100 - y.
        string src = DataUri(AlphaPng(100, 100, (x, y) => (byte)(x < 100 - y ? 255 : 0)));
        var starts = LineStarts(Layout(Style + $"<img class=f src='{src}' style='shape-outside:url({src})'>" + Words));
        Assert.Equal(75, starts[0], 2);
        Assert.Equal(60 * 0.75f, starts[2], 2);
        Assert.Equal(20 * 0.75f, starts[4], 2);
    }

    [Fact]
    public void Gradient_alpha_gives_the_shape()
    {
        var starts = LineStarts("shape-outside: linear-gradient(to right, black 50%, transparent 50%)");
        Assert.Equal(50 * 0.75f, starts[0], 2);
        var radial = LineStarts("shape-outside: radial-gradient(closest-side, black 99%, transparent 100%)");
        Assert.True(radial[0] < 75 && radial[2] > radial[0], string.Join(", ", radial));
    }

    [Fact]
    public void Path_shape_is_scanned_by_rows()
    {
        var starts = LineStarts("shape-outside: path(\"M0 0 L100 0 L0 100 Z\")");
        Assert.Equal(75, starts[0], 2);
        Assert.Equal(60 * 0.75f, starts[2], 2);
    }

    [Fact]
    public void Lines_wrap_around_an_unusable_image_as_a_box()
    {
        var starts = LineStarts("shape-outside: url(missing.png)");
        Assert.Equal(75, starts[0], 2);
    }

    [Fact]
    public void Words_that_do_not_fit_beside_a_shape_move_down_a_pixel_at_a_time()
    {
        // A word too wide for the room beside the top half of the circle fits beside its lower half, before the float
        // ends, as in Chrome, which tries every pixel down the shape.
        var ops = Layout("<style>body{margin:0;font:16px/20px sans-serif} p{margin:0;width:160px}</style>" +
                         "<p><span style='float:left;width:100px;height:100px;shape-outside:circle(50%)'></span>Wwwwww</p>");
        var word = Flatten(ops).OfType<TextOp>().Single();
        Assert.InRange(word.LineTop, 50 * 0.75f, 99 * 0.75f);
        Assert.InRange(word.X, 50 * 0.75f, 75);
    }

    [Fact]
    public void Lines_beside_a_shape_paint_above_the_float()
    {
        var ops = Layout(Style + "<div class=f style='background:red;shape-outside:circle(50%)'></div>" + Words);
        var lifted = Flatten(ops).OfType<LayerOp>().Single(l => l.Order == LayerOp.InlineOverFloatsOrder);
        Assert.NotEmpty(Flatten(lifted.Ops).OfType<TextOp>());
        // Without a shape, nothing moves.
        var plain = Layout(Style + "<div class=f style='background:red'></div>" + Words);
        Assert.DoesNotContain(Flatten(plain).OfType<LayerOp>(), l => l.Order == LayerOp.InlineOverFloatsOrder);
    }

    [Theory]
    [InlineData("shape-outside: circle(50%) fill-box")]
    [InlineData("shape-outside: circle(50%) circle(10%)")]
    [InlineData("shape-outside: bogus(1px)")]
    [InlineData("shape-outside: url(a.png) url(b.png)")]
    [InlineData("shape-outside: url(a.png), url(b.png)")]
    [InlineData("shape-margin: -5px")]
    [InlineData("shape-image-threshold: half")]
    public void Invalid_values_are_ignored(string declaration)
    {
        var doc = HtmlParser.Parse($"<div style='float:left;shape-outside:circle(10%);shape-margin:2px;shape-image-threshold:0.3;{declaration}'></div>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var s = Find(doc, "div").Single().Style;
        Assert.Equal(ClipShape.ShapeKind.Circle, s.ShapeOutside!.Shape!.Kind);
        Assert.Equal(1.5f, s.ShapeMargin.Value, 3);
        Assert.Equal(0.3f, s.ShapeImageThreshold, 3);
    }

    [Fact]
    public void Values_parse_and_apply()
    {
        var doc = HtmlParser.Parse("<div id=a style='shape-outside:inset(10%) border-box;shape-margin:5%;shape-image-threshold:150%'></div>" +
                                   "<div id=b style='shape-outside:padding-box'></div><div id=c style='shape-outside:linear-gradient(red, blue)'></div>" +
                                   "<div id=d style='shape-outside:circle(50%);shape-outside:none'></div>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var divs = Find(doc, "div").ToDictionary(d => d.GetAttribute("id")!, d => d.Style);
        Assert.Equal(ShapeBox.BorderBox, divs["a"].ShapeOutside!.Shape!.Box);
        Assert.Equal(LengthUnit.Percent, divs["a"].ShapeMargin.Unit);
        Assert.Equal(1, divs["a"].ShapeImageThreshold);
        Assert.Equal(ShapeBox.PaddingBox, divs["b"].ShapeOutside!.Shape!.Box);
        Assert.NotNull(divs["c"].ShapeOutside!.Image!.Gradient);
        Assert.Null(divs["d"].ShapeOutside);
        Assert.True(CssSupports.Evaluate("(shape-outside: circle(50%))"));
    }

    [Fact]
    public void Output_is_a_valid_pdf()
    {
        AssertStructurallyValid(ConvertUncompressed(Style + "<body><div class=f style='shape-outside:circle(50%);shape-margin:1em;background:#ccc;border-radius:50%'></div>" + Words));
    }
}

/// <summary>Shapes that are huge, many-sided or scanned from images larger than the page.</summary>
[Collection(HostileInputCollection.Name)]
public class FloatShapeHostileInputTests
{
    private static byte[] Bounded(string html)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return HtmlToPdf.Convert(html, null, cts.Token);
    }

    [Fact]
    public void Huge_shapes_and_margins_stay_bounded()
    {
        string many = string.Join(",", Enumerable.Range(0, 20_000).Select(i => $"{i % 97}% {i % 89}%"));
        string big = DataUri(AlphaPng(2000, 2000, (x, y) => (byte)((x ^ y) & 1) == 0 ? (byte)255 : (byte)0));
        var html = "<style>.f{float:left;width:300px;height:300px}</style>"
            + $"<div class=f style='shape-outside:polygon({many});shape-margin:7px'></div>" + string.Concat(Enumerable.Repeat("word ", 3000))
            + "<div class=f style='height:100000px;shape-outside:linear-gradient(red,transparent);shape-margin:1e9px'></div>word word"
            + "<div class=f style='width:1e7px;shape-outside:radial-gradient(red,transparent)'></div>word"
            + $"<img class=f src='{big}' style='width:2000px;height:2000px;shape-outside:url({big});shape-margin:300px'>" + string.Concat(Enumerable.Repeat("word ", 2000))
            + "<div class=f style='shape-outside:path(\"M0 0 L1e9 1e9 L0 1e9 Z\")'></div>word"
            + "<div class=f style='height:50000px;shape-outside:circle(1px);shape-margin:0'></div><p style='width:100px'>Wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww</p>";
        PdfTestHelpers.AssertStructurallyValid(Bounded(html));
    }
}
