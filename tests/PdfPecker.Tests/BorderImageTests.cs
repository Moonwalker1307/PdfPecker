using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>CSS border-image: the longhands, the shorthand, the nine-part layout and how it renders.</summary>
public class BorderImageTests
{
    private const float Width = 500, PageHeight = 800;

    // A 30 x 30 px image of nine 10 px cells, each its own colour, so that every part of a border image can be told apart.
    private static readonly (byte R, byte G, byte B)[,] Cells =
    {
        { (255, 0, 0), (0, 255, 0), (0, 0, 255) },
        { (255, 255, 0), (255, 0, 255), (0, 255, 255) },
        { (0, 0, 0), (128, 128, 128), (255, 128, 0) },
    };

    private static readonly string Grid = DataUri(PixelPng(30, 30, (x, y) => Cells[y / 10, x / 10]));

    private static readonly string SvgGrid = "data:image/svg+xml," + Uri.EscapeDataString(
        "<svg xmlns='http://www.w3.org/2000/svg' width='30' height='30'>"
        + "<rect x='0' y='0' width='10' height='10' fill='#f00'/><rect x='10' y='0' width='10' height='10' fill='#0f0'/>"
        + "<rect x='20' y='0' width='10' height='10' fill='#00f'/><rect x='0' y='10' width='10' height='10' fill='#ff0'/>"
        + "<rect x='10' y='10' width='10' height='10' fill='#f0f'/><rect x='20' y='10' width='10' height='10' fill='#0ff'/>"
        + "<rect x='0' y='20' width='10' height='10' fill='#000'/><rect x='10' y='20' width='10' height='10' fill='#808080'/>"
        + "<rect x='20' y='20' width='10' height='10' fill='#ff8000'/></svg>");

    private static ComputedStyle StyleOf(string style)
    {
        var doc = HtmlParser.Parse($"<div style='{style}'></div>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc.FindFirst("div")!.Style!;
    }

    private static List<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        var list = new List<DrawOp>();
        foreach (var o in ops)
        {
            if (o is GroupOp g) list.AddRange(Flatten(g.Ops));
            else list.Add(o);
        }
        return list;
    }

    /// <summary>The ops of a div at the top left of the flow styled with <paramref name="style"/>.</summary>
    private static List<DrawOp> Ops(string style)
    {
        var doc = HtmlParser.Parse($"<div style='margin:0; {style}'></div>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Flatten(engine.Ops);
    }

    private static List<BackgroundOp> Parts(string style) => Ops(style).OfType<BackgroundOp>().ToList();

    /// <summary>The part whose region starts at (x, y), in points.</summary>
    private static BackgroundOp PartAt(List<BackgroundOp> parts, float x, float y)
        => Assert.Single(parts, p => Math.Abs(p.ClipX - x) < 0.01f && Math.Abs(p.ClipY - y) < 0.01f);

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}");

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    /// <summary>Renders a page at 96 dpi, so that one CSS pixel is one image pixel.</summary>
    private static RenderedImage Render(byte[] pdf, int page = 0)
        => PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 96 });

    private static (int R, int G, int B) Pixel(RenderedImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual, string where = "")
        => Assert.True(Math.Abs(actual.R - r) < 40 && Math.Abs(actual.G - g) < 40 && Math.Abs(actual.B - b) < 40,
            $"Expected ({r}, {g}, {b}) {where}, got {actual}");

    private static RenderedImage RenderBox(string style)
        => Render(HtmlToPdf.Convert($"<body style='margin:0'><div style='{style}'></div></body>", NoMargins));

    /// <summary>Checks the nine parts of a 100 x 60 px box with 20 px borders, drawn from the grid image sliced at 10.</summary>
    private static void AssertNineParts(RenderedImage img, bool fill)
    {
        int[] xs = { 10, 70, 130 }, ys = { 10, 50, 90 };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                var (r, g, b) = row == 1 && col == 1 && !fill ? ((byte)255, (byte)255, (byte)255) : Cells[row, col];
                Color(r, g, b, Pixel(img, xs[col], ys[row]), $"at ({xs[col]}, {ys[row]})");
            }
    }

    // ---- parsing ----

    [Fact]
    public void Shorthand_sets_every_longhand()
    {
        var s = StyleOf($"border-image: url({Grid}) 10 20% fill / 2 5pt auto / 1 4pt round space");
        Assert.Equal(Grid, s.BorderImageSource!.Url);
        Assert.True(s.BorderImageFill);
        var slice = s.BorderImageSlice!;
        Assert.True(slice[0].IsNumber);
        Assert.Equal(10, slice[0].Number);
        Assert.Equal(20, slice[1].Length.Value);
        Assert.Equal(LengthUnit.Percent, slice[1].Length.Unit);
        Assert.Equal(10, slice[2].Number);
        Assert.Equal(20, slice[3].Length.Value);
        var widths = s.BorderImageWidths!;
        Assert.Equal(2, widths[0].Number);
        Assert.Equal(5, widths[1].Length.Value);
        Assert.True(widths[2].IsAuto);
        Assert.Equal(5, widths[3].Length.Value);
        var outsets = s.BorderImageOutsets!;
        Assert.Equal(1, outsets[0].Number);
        Assert.Equal(4, outsets[1].Length.Value);
        Assert.Equal(BorderImageRepeat.Round, s.BorderImageRepeatX);
        Assert.Equal(BorderImageRepeat.Space, s.BorderImageRepeatY);
    }

    [Fact]
    public void Shorthand_resets_the_parts_it_leaves_out()
    {
        var s = StyleOf($"border-image-slice: 5 fill; border-image-repeat: round; border-image: linear-gradient(red, blue)");
        Assert.NotNull(s.BorderImageSource!.Gradient);
        Assert.Equal(100, s.BorderImageSlice![0].Length.Value); // the initial 100%
        Assert.False(s.BorderImageFill);
        Assert.Equal(BorderImageRepeat.Stretch, s.BorderImageRepeatX);

        // An outset without a width, and slashes without spaces.
        var t = StyleOf($"border-image: url({Grid}) 30%//2px");
        Assert.Equal(1, t.BorderImageWidths![0].Number);
        Assert.Equal(1.5f, t.BorderImageOutsets![0].Length.Value);
    }

    [Theory]
    [InlineData("url(a.png) 10 / ")]
    [InlineData("url(a.png) url(b.png)")]
    [InlineData("url(a.png) 10 sideways")]
    [InlineData("url(a.png) 10 / 5px / 3px / 1px")]
    public void Invalid_shorthands_are_dropped(string value)
        => Assert.Null(StyleOf($"border-image: {value}").BorderImageSource);

    [Fact]
    public void Invalid_longhands_keep_the_previous_value()
    {
        var s = StyleOf("border-image-slice: 10; border-image-slice: -5; border-image-width: 3; border-image-width: -1px;"
                        + " border-image-outset: 2; border-image-outset: 10%; border-image-repeat: space; border-image-repeat: tile");
        Assert.Equal(10, s.BorderImageSlice![0].Number);
        Assert.Equal(3, s.BorderImageWidths![0].Number);
        Assert.Equal(2, s.BorderImageOutsets![0].Number);
        Assert.Equal(BorderImageRepeat.Space, s.BorderImageRepeatX);
        Assert.Null(CssBorderImage.Slice("10 fill 20"));
        Assert.True(CssBorderImage.Slice("fill 10 20")!.Value.Fill);
    }

    [Fact]
    public void Border_shorthand_resets_the_border_image()
    {
        var s = StyleOf($"border-image: url({Grid}) 10 fill round; border: 5px solid red");
        Assert.Null(s.BorderImageSource);
        Assert.Null(s.BorderImageSlice);
        Assert.Equal(BorderImageRepeat.Stretch, s.BorderImageRepeatX);
        Assert.NotNull(StyleOf($"border: 5px solid red; border-image: url({Grid}) 10").BorderImageSource);
    }

    [Fact]
    public void Supports_knows_the_border_image_properties()
    {
        Assert.True(CssSupports.Evaluate("(border-image: url(a.png) 10 round)"));
        Assert.True(CssSupports.Evaluate("(border-image-slice: 10 fill)"));
        Assert.True(CssSupports.IsKnownProperty("border-image-outset"));
    }

    [Fact]
    public void Border_image_is_not_inherited()
    {
        var doc = HtmlParser.Parse($"<div style='border-image: url({Grid}) 10'><p>x</p></div>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        Assert.NotNull(doc.FindFirst("div")!.Style!.BorderImageSource);
        Assert.Null(doc.FindFirst("p")!.Style!.BorderImageSource);
    }

    // ---- layout ----

    [Fact]
    public void Nine_parts_map_slices_into_their_regions()
    {
        // 100 x 60 px content and 20 px borders: a 105 x 75 pt border box with 15 pt borders.
        var parts = Parts($"width:100px; height:60px; border:20px solid black; border-image: url({Grid}) 10 fill");
        Assert.Equal(9, parts.Count);
        Assert.DoesNotContain(Ops($"width:100px; height:60px; border:20px solid black; border-image: url({Grid}) 10"), o => o is RectOp or BorderRectOp);

        // The top-left corner is the image's top-left 10 px, cut out as an image of its own and stretched to 15 pt.
        var tl = PartAt(parts, 0, 0);
        Near(15, tl.ClipW);
        Near(15, tl.ClipH);
        Assert.Equal((10, 10), (tl.Image!.Width, tl.Image.Height));
        Assert.Null(tl.Inner);

        // The top edge: 75 pt wide, stretched from the 10 px middle slice, and drawn 15 pt tall.
        var top = PartAt(parts, 15, 0);
        Near(75, top.ClipW);
        Near(15, top.ClipH);
        Near(75, top.TileW);
        Assert.False(top.RepeatX);
        Assert.Equal((10, 10), (top.Image!.Width, top.Image.Height));

        // An image that is not cut (here an SVG file) is drawn whole, scaled so that the slice fills the tile: three
        // times the 7.5 pt slice across, twice down.
        var svgTop = PartAt(Parts($"width:100px; height:60px; border:20px solid black; border-image: url(\"{SvgGrid}\") 10"), 15, 0);
        var (ix, iy, iw, ih) = svgTop.Inner!.Value;
        Near(-75, ix);
        Near(0, iy);
        Near(225, iw);
        Near(45, ih);

        // The right edge and the middle.
        var right = PartAt(parts, 90, 15);
        Near(15, right.ClipW);
        Near(45, right.ClipH);
        var middle = PartAt(parts, 15, 15);
        Near(75, middle.ClipW);
        Near(45, middle.ClipH);

        // Without fill the middle is left out.
        Assert.Equal(8, Parts($"width:100px; height:60px; border:20px solid black; border-image: url({Grid}) 10").Count);
    }

    [Fact]
    public void Repeat_round_and_space_lay_out_tiles_along_the_edges()
    {
        // The top edge is 105 px (78.75 pt) long; its tile is the 10 px slice scaled to the 20 px border: 15 pt.
        const string box = "width:105px; height:25px; border:20px solid; border-image-slice:10; ";

        var repeat = PartAt(Parts(box + $"border-image-source:url({Grid}); border-image-repeat: repeat"), 15, 0);
        Assert.True(repeat.RepeatX);
        Assert.False(repeat.RepeatY);
        Near(15, repeat.TileW);
        Near(15 + (78.75f - 15) / 2, repeat.TileX); // one tile centred, the others around it
        Near(15, repeat.StepX);

        var round = PartAt(Parts(box + $"border-image-source:url({Grid}); border-image-repeat: round"), 15, 0);
        Near(78.75f / 5, round.TileW); // 5.25 tiles round to 5
        Near(15, round.TileX);
        Near(15, round.TileH);

        var space = PartAt(Parts(box + $"border-image-source:url({Grid}); border-image-repeat: space"), 15, 0);
        Near(15, space.TileW);
        Near(15 + 0.625f, space.TileX); // 5 tiles and 6 gaps of 0.625 pt
        Near(15.625f, space.StepX);

        // The second keyword is for the left and right edges.
        var left = PartAt(Parts(box + $"border-image-source:url({Grid}); border-image-repeat: stretch round"), 0, 15);
        Assert.False(left.RepeatX);
        Assert.False(left.RepeatY); // 18.75 pt of edge holds 1.25 tiles: rounded to one, stretched
        Near(18.75f, left.TileH);
    }

    [Fact]
    public void Widths_outsets_and_auto()
    {
        // A width of 20 px on 10 px borders, and the area grown by 5 px.
        var parts = Parts($"width:100px; height:60px; border:10px solid; border-image: url({Grid}) 10 / 20px / 5px");
        var tl = PartAt(parts, -3.75f, -3.75f);
        Near(15, tl.ClipW);
        Near(15, tl.ClipH);
        Assert.Contains(parts, p => Math.Abs(p.ClipX + p.ClipW - (90 + 3.75f)) < 0.01f);

        // A number multiplies the border width; auto takes the slice's own size (10 px).
        Near(15, PartAt(Parts($"width:100px; height:60px; border:5px solid; border-image: url({Grid}) 10 / 4"), 0, 0).ClipW);
        Near(7.5f, PartAt(Parts($"width:100px; height:60px; border:5px solid; border-image: url({Grid}) 10 / auto"), 0, 0).ClipW);

        // Widths that would overlap are scaled down together: 60 px + 60 px on an 80 px tall box.
        var big = Parts($"width:100px; height:40px; border:20px solid; border-image: url({Grid}) 10 / 60px");
        Near(30, PartAt(big, 0, 0).ClipH);
    }

    [Fact]
    public void Without_border_widths_nothing_is_drawn()
    {
        // border-image-width multiplies the border width, which is zero when the border style is none.
        var ops = Ops($"width:100px; height:60px; border-width:20px; border-image: url({Grid}) 10");
        Assert.Empty(ops.OfType<BackgroundOp>());

        // An explicit length still gives the border image room to draw in.
        Assert.Equal(8, Parts($"width:100px; height:60px; border-image: url({Grid}) 10 / 10px").Count);
    }

    [Fact]
    public void An_image_that_does_not_load_draws_the_border_styles()
    {
        var ops = Ops("width:100px; height:60px; border:4px solid red; border-image: url(missing.png) 10");
        Assert.Empty(ops.OfType<BackgroundOp>());
        Assert.Single(ops.OfType<BorderRectOp>());
    }

    // ---- rendering ----

    [Fact]
    public void Raster_border_image_renders_its_nine_parts()
    {
        AssertNineParts(RenderBox($"width:100px; height:60px; border:20px solid #ccc; border-image: url({Grid}) 10 fill"), fill: true);
        AssertNineParts(RenderBox($"width:100px; height:60px; border:20px solid #ccc; border-image: url({Grid}) 10"), fill: false);
    }

    [Fact]
    public void Svg_border_image_renders_its_nine_parts()
        => AssertNineParts(RenderBox($"width:100px; height:60px; border:20px solid #ccc; border-image: url(\"{SvgGrid}\") 10 fill"), fill: true);

    [Fact]
    public void Repeated_tiles_show_only_their_slice()
    {
        // Each 20 px tile of the top edge shows the green middle slice only, never the corners around it.
        var img = RenderBox($"width:100px; height:60px; border:20px solid; border-image: url({Grid}) 10 fill round");
        // The slices are cut out of the image, so even at a tile's edges no pixels of the next slice are blended in.
        for (int x = 20; x < 120; x++) Color(0, 255, 0, Pixel(img, x, 10), $"at x {x}");
        for (int y = 20; y < 80; y++) Color(255, 255, 0, Pixel(img, 10, y), $"at y {y}");
        Color(255, 0, 255, Pixel(img, 70, 50));
        AssertNineParts(img, fill: true);
    }

    [Fact]
    public void Space_leaves_gaps_between_tiles()
    {
        // A 50 px edge holds two 20 px tiles with three gaps of 3.3 px.
        var img = RenderBox($"width:50px; height:60px; border:20px solid; border-image: url({Grid}) 10 space");
        Color(255, 255, 255, Pixel(img, 21, 10));
        Color(0, 255, 0, Pixel(img, 33, 10));
        Color(255, 255, 255, Pixel(img, 45, 10));
        Color(0, 255, 0, Pixel(img, 57, 10));
        Color(255, 0, 0, Pixel(img, 10, 10));
    }

    [Fact]
    public void Gradient_border_image_is_sliced_from_one_gradient_over_the_area()
    {
        // The gradient covers the 140 x 140 px border box; the 25% slices are 35 px, scaled into 20 px borders.
        var img = RenderBox("width:100px; height:100px; border:20px solid; border-image: linear-gradient(red, blue) 25%");
        var top = Pixel(img, 70, 10);
        var bottom = Pixel(img, 70, 130);
        var left = Pixel(img, 10, 70);
        Assert.True(top.R > 200 && top.B < 60, $"top {top}");
        Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom {bottom}");
        Color(128, 0, 128, left, "on the left edge's middle");
        Color(255, 255, 255, Pixel(img, 70, 70), "in the middle");

        // Repeated gradient tiles take the tiling pattern path.
        var round = RenderBox("width:100px; height:100px; border:20px solid; border-image: linear-gradient(red, blue) 25% fill round");
        Assert.True(Pixel(round, 70, 10).R > 200);
        Color(128, 0, 128, Pixel(round, 70, 45), "in the filled middle's first tile");
    }

    [Fact]
    public void Conic_and_radial_gradients_are_scaled_with_their_slices()
    {
        // A conic gradient from red to blue around the middle: the top-left corner is near its end, the top-right near its start.
        var conic = RenderBox("width:100px; height:100px; border:20px solid; border-image: conic-gradient(red, blue) 25% fill");
        var tr = Pixel(conic, 130, 10);
        var tl = Pixel(conic, 10, 10);
        Assert.True(tr.R > tr.B, $"top right {tr}");
        Assert.True(tl.B > tl.R, $"top left {tl}");
        var radial = RenderBox("width:100px; height:100px; border:20px solid; border-image: radial-gradient(red, blue) 25% fill");
        var centre = Pixel(radial, 70, 70);
        var corner = Pixel(radial, 10, 10);
        Assert.True(centre.R > 200, $"centre {centre}");
        Assert.True(corner.B > 150 && corner.R < 100, $"corner {corner}"); // three quarters out along the ray
    }

    [Fact]
    public void Huge_values_do_not_break_the_page()
    {
        var pdf = HtmlToPdf.Convert($"<div style='width:100px; height:60px; border:20px solid; border-image: url({Grid}) 1e30 fill / 1e30 / 1e30 round'></div>");
        AssertStructurallyValid(pdf);
        Assert.True(PageCount(pdf) <= 2); // outsets are capped at 1000 pt
    }

    [Fact]
    public void Border_radius_does_not_clip_the_border_image()
    {
        var img = RenderBox($"width:100px; height:60px; border:20px solid; border-radius:30px; border-image: url({Grid}) 10");
        Color(255, 0, 0, Pixel(img, 2, 2));
    }

    [Fact]
    public void A_border_image_split_by_a_page_break_continues_on_the_next_page()
    {
        // A4 at 96 dpi is 1123 px tall; the box runs about 300 px into the second page.
        var pdf = HtmlToPdf.Convert($"<body style='margin:0'><div style='height:1380px; border:20px solid; border-image: url({Grid}) 10 round'></div></body>",
            NoMargins);
        Assert.Equal(2, PageCount(pdf));
        AssertStructurallyValid(pdf);
        var first = Render(pdf, 0);
        Color(255, 0, 0, Pixel(first, 10, 10));
        Color(255, 255, 0, Pixel(first, 10, 1100));
        var second = Render(pdf, 1);
        Color(255, 255, 0, Pixel(second, 10, 1230 - 1123));
        Color(0, 0, 0, Pixel(second, 10, 1410 - 1123));
        Color(128, 128, 128, Pixel(second, 300, 1410 - 1123));
    }
}
