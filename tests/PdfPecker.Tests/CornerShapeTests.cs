using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>corner-shape: its longhands and shorthands, the corner curves and how boxes with them are drawn.</summary>
public class CornerShapeTests
{
    private static ComputedStyle StyleOf(string style)
    {
        var doc = HtmlParser.Parse($"<div style='{style}'></div>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc.FindFirst("div")!.Style!;
    }

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Is((int R, int G, int B) expected, (int R, int G, int B) actual)
        => Assert.True(Math.Abs(expected.R - actual.R) < 12 && Math.Abs(expected.G - actual.G) < 12 && Math.Abs(expected.B - actual.B) < 12,
            $"Expected {expected}, got {actual}");

    private static byte[] Box(string style)
        => HtmlToPdf.Convert($"<!DOCTYPE html><body style='margin:0'><div style='width:200px; height:100px; background:#00f; {style}'></div></body>",
            new PdfOptions { Margins = new PageMargins(0) });

    [Theory]
    [InlineData("corner-shape: bevel", 0, 0, 0, 0)]
    [InlineData("corner-shape: round squircle", 1, 2, 1, 2)]
    [InlineData("corner-shape: scoop notch square", -1, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity)]
    [InlineData("corner-shape: superellipse(0.5) superellipse(-infinity) superellipse(infinity) superellipse(-2)", 0.5f,
        float.NegativeInfinity, float.PositiveInfinity, -2)]
    [InlineData("corner-top-shape: bevel scoop", 0, -1, 1, 1)]
    [InlineData("corner-right-shape: bevel", 1, 0, 0, 1)]
    [InlineData("corner-bottom-shape: notch scoop", 1, 1, -1, float.NegativeInfinity)]
    [InlineData("corner-left-shape: squircle", 2, 1, 1, 2)]
    [InlineData("corner-bottom-right-shape: scoop", 1, 1, -1, 1)]
    [InlineData("direction: rtl; corner-start-start-shape: bevel", 1, 0, 1, 1)]
    [InlineData("corner-block-end-shape: bevel scoop", 1, 1, -1, 0)]
    [InlineData("corner-inline-end-shape: notch", 1, float.NegativeInfinity, float.NegativeInfinity, 1)]
    [InlineData("corner-shape: bevel; corner-shape: round", 1, 1, 1, 1)]
    public void Corner_shapes_set_each_corner(string style, float tl, float tr, float br, float bl)
    {
        var k = StyleOf(style).CornerShapes ?? [1, 1, 1, 1];
        Assert.Equal([tl, tr, br, bl], k);
    }

    [Theory]
    [InlineData("corner-shape: bevel; corner-shape: rounded")]
    [InlineData("corner-shape: bevel; corner-shape: bevel bevel bevel bevel bevel")]
    [InlineData("corner-shape: bevel; corner-shape: superellipse(nan)")]
    [InlineData("corner-shape: bevel; corner-shape: superellipse(1px)")]
    [InlineData("corner-shape: bevel; corner-top-shape: bevel bevel bevel")]
    public void Invalid_corner_shapes_keep_the_previous_value(string style)
        => Assert.Equal([0f, 0, 0, 0], StyleOf(style).CornerShapes!);

    [Fact]
    public void Corner_shapes_are_known_to_supports_and_not_inherited()
    {
        Assert.True(CssSupports.Evaluate("(corner-shape: squircle)"));
        Assert.True(CssSupports.Evaluate("(corner-top-left-shape: bevel)"));
        var doc = HtmlParser.Parse("<div style='corner-shape: bevel'><p>x</p></div>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        Assert.Null(doc.FindFirst("p")!.Style!.CornerShapes);
        Assert.Null(StyleOf("corner-shape: inherit").CornerShapes);
    }

    [Fact]
    public void A_bevel_is_a_straight_line_and_its_inner_edge_runs_parallel()
    {
        // A 100pt bevel inside 20pt borders: the inner line is 20pt in from the outer one, so it crosses the left inner
        // edge 20pt × (√2 − 1) below where the outer line meets the box edge, as in Chrome 153.
        var outer = CornerShape.CanonicalCorner(100, 100, 0, 0, 0);
        Assert.All(outer, p => Assert.Equal(100, p.X + p.Y, 2));
        var inner = CornerShape.CanonicalCorner(100, 100, 0, 20, 20);
        Assert.Equal((20f, 100 + 20 * (MathF.Sqrt(2) - 1)), (inner[0].X, MathF.Round(inner[0].Y, 3)), new Near());
        Assert.All(inner, p => Assert.Equal(100 + 20 * MathF.Sqrt(2), p.X + p.Y, 1));
    }

    [Fact]
    public void Fuller_corners_keep_their_curve_inside_the_inner_edges()
    {
        // A squircle's inner edge is a squircle with the radii less the widths, as Chrome draws it.
        var inner = CornerShape.CanonicalCorner(100, 100, 2, 40, 10);
        Assert.Equal(40, inner[0].X, 3);
        Assert.Equal(100, inner[0].Y, 3);
        Assert.Equal(100, inner[^1].X, 3);
        Assert.Equal(10, inner[^1].Y, 3);
        // Widths past the radii leave a square inner corner.
        Assert.Equal([(30f, 30f)], CornerShape.CanonicalCorner(20, 20, 2, 30, 30));
        Assert.Equal([(30f, 30f)], CornerShape.CanonicalCorner(10, 10, 0, 30, 30));
    }

    [Theory]
    // At (6, 6) the corner of the box: cut away by a bevel, a scoop and a notch, kept by a square and a round corner's
    // inner part.
    [InlineData("corner-shape: bevel", 6, 6, false)]
    [InlineData("corner-shape: scoop", 6, 6, false)]
    [InlineData("corner-shape: notch", 6, 6, false)]
    [InlineData("corner-shape: square", 2, 2, true)]
    [InlineData("corner-shape: notch", 30, 30, true)]   // the notch's inner corner
    [InlineData("corner-shape: scoop", 30, 30, true)]   // beyond the scoop's quarter circle
    [InlineData("corner-shape: bevel", 18, 18, true)]   // inside the bevel line x + y = 30
    [InlineData("corner-shape: bevel", 12, 12, false)]
    public void Corners_are_cut_by_their_shape(string style, int x, int y, bool painted)
    {
        var (r, g, b) = Pixel(Box("border-radius: 30px; " + style), x, y);
        Assert.Equal(painted, b > 128 && r < 128 && g < 128);
    }

    [Fact]
    public void Clips_borders_and_shadows_follow_the_shape()
    {
        // overflow: hidden clips a child to the bevel too.
        var clip = Box("border-radius: 30px; corner-shape: bevel; overflow: hidden'><div style='height:100px; background:#f00");
        Is((255, 255, 255), Pixel(clip, 6, 6));
        Is((255, 0, 0), Pixel(clip, 100, 50));
        // The border ring is cut by the bevel outside and by the parallel inner line inside.
        var border = Box("border-radius: 30px; corner-shape: bevel; border: 10px solid #f00");
        Is((255, 255, 255), Pixel(border, 6, 6));
        Is((255, 0, 0), Pixel(border, 15, 18));
        Is((0, 0, 255), Pixel(border, 30, 30));
        // A spread shadow grows the bevel outward.
        var shadow = HtmlToPdf.Convert("<!DOCTYPE html><body style='margin:0'><div style='margin:20px; width:200px; height:100px; "
            + "border-radius:30px; corner-shape:bevel; box-shadow:0 0 0 10px #0f0'></div></body>", new PdfOptions { Margins = new PageMargins(0) });
        Is((0, 255, 0), Pixel(shadow, 22, 40));
        Is((255, 255, 255), Pixel(shadow, 12, 12));
    }

    [Fact]
    public void Round_corners_draw_as_before()
    {
        var plain = Box("border-radius: 30px; border: 4px solid #f00");
        var round = Box("border-radius: 30px; border: 4px solid #f00; corner-shape: round");
        Assert.Equal(PdfTestHelpers.AllContent(plain), PdfTestHelpers.AllContent(round));
    }

    private sealed class Near : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => Math.Abs(a.Item1 - b.Item1) < 0.01f && Math.Abs(a.Item2 - b.Item2) < 0.01f;
        public int GetHashCode((float, float) obj) => 0;
    }
}
