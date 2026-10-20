using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>mask-image and its family, the mask shorthand, their -webkit- spellings, and backdrop-filter.</summary>
public class CssMaskTests
{
    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    private static byte[] Pdf(string body, PdfOptions? options = null)
        => HtmlToPdf.Convert($"<body style='margin:0'>{body}</body>", options ?? NoMargins);

    private static RenderedImage Render(byte[] pdf, int page = 0) => PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });

    /// <summary>The RGB of a pixel, at a position given in CSS pixels (the page is rendered at 72 dpi, 1 pt per pixel).</summary>
    private static (int R, int G, int B) At(RenderedImage img, float cssX, float cssY)
    {
        int x = (int)(cssX * 0.75f), y = (int)(cssY * 0.75f);
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Near(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 10)
        => Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    private static void White((int R, int G, int B) p) => Near(255, 255, 255, p, 8);

    // An SVG image whose left half is opaque and right half empty, as mask icons usually are.
    private const string LeftHalf = "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'%3E%3Crect width='5' height='10'/%3E%3C/svg%3E\")";

    private const string Box = "width:200px;height:100px;background:red";

    // ---- parsing ----

    [Fact]
    public void Mask_and_backdrop_properties_are_known_to_supports()
    {
        Assert.True(CssSupports.Evaluate("(mask-image: linear-gradient(red, blue))"));
        Assert.True(CssSupports.Evaluate("(-webkit-mask-image: none)"));
        Assert.True(CssSupports.Evaluate("(mask-composite: exclude)"));
        Assert.True(CssSupports.Evaluate("(-webkit-mask: none)"));
        Assert.True(CssSupports.Evaluate("(backdrop-filter: blur(2px))"));
        Assert.True(CssSupports.Evaluate("(-webkit-backdrop-filter: blur(2px))"));
    }

    [Fact]
    public void Shorthand_splits_into_the_longhands()
    {
        var longhands = new Dictionary<string, string>();
        CssMask.ExpandShorthand($"{LeftHalf} center / contain no-repeat content-box luminance, linear-gradient(red, blue) subtract",
            (name, value) => longhands[name] = value);
        Assert.StartsWith("url(", longhands["mask-image"], StringComparison.Ordinal);
        Assert.EndsWith(", linear-gradient(red, blue)", longhands["mask-image"], StringComparison.Ordinal);
        Assert.Equal("center, 0% 0%", longhands["mask-position"]);
        Assert.Equal("contain, auto", longhands["mask-size"]);
        Assert.Equal("no-repeat, repeat", longhands["mask-repeat"]);
        Assert.Equal("content-box, border-box", longhands["mask-origin"]);
        Assert.Equal("content-box, border-box", longhands["mask-clip"]);
        Assert.Equal("luminance, match-source", longhands["mask-mode"]);
        Assert.Equal("add, subtract", longhands["mask-composite"]);
    }

    [Theory]
    [InlineData("source-over", "Add")]
    [InlineData("source-out", "Subtract")]
    [InlineData("source-in", "Intersect")]
    [InlineData("xor", "Exclude")]
    [InlineData("exclude", "Exclude")]
    public void Webkit_composite_names_map_to_mask_composite(string value, string expected)
        => Assert.Equal(expected, CssMask.Composite(value)?.ToString());

    // ---- mask-image ----

    [Theory]
    [InlineData("mask-image")]
    [InlineData("-webkit-mask-image")]
    [InlineData("mask")]
    [InlineData("-webkit-mask")]
    public void A_gradient_mask_fades_the_box(string property)
    {
        var img = Render(Pdf($"<div style='{Box};{property}:linear-gradient(to right, #000, transparent)'></div>"));
        Near(255, 0, 0, At(img, 4, 50), 16);
        var middle = At(img, 100, 50);
        Assert.InRange(middle.R, 250, 255);
        Assert.InRange(middle.G, 100, 160);
        White(At(img, 197, 50));
    }

    [Fact]
    public void An_svg_image_masks_like_an_icon()
    {
        var img = Render(Pdf($"<style>i {{ display:block;width:100px;height:100px;background:blue;mask:{LeftHalf} center / contain no-repeat }}</style><i></i>"));
        Near(0, 0, 255, At(img, 20, 50));
        White(At(img, 80, 50));
    }

    [Fact]
    public void Mask_size_and_repeat_tile_the_mask_image()
    {
        // Four 50px tiles across: each has its left 25px opaque.
        var img = Render(Pdf($"<style>i {{ display:block;width:200px;height:50px;background:blue;mask-image:{LeftHalf};mask-size:50px 50px;mask-repeat:repeat-x }}</style><i></i>"));
        Near(0, 0, 255, At(img, 10, 25));
        White(At(img, 40, 25));
        Near(0, 0, 255, At(img, 160, 25));
        White(At(img, 190, 25));
    }

    [Fact]
    public void Luminance_mode_uses_the_brightness_of_the_mask()
    {
        var mask = "linear-gradient(to right, white 50%, black 50%)";
        var lum = Render(Pdf($"<div style='{Box};mask:{mask} luminance'></div>"));
        Near(255, 0, 0, At(lum, 50, 50));
        White(At(lum, 150, 50));
        // In the alpha mode both halves are opaque, so the whole box shows.
        var alpha = Render(Pdf($"<div style='{Box};mask-image:{mask}'></div>"));
        Near(255, 0, 0, At(alpha, 150, 50));
    }

    [Theory]
    // The top layer is the left half, the layer below it the top half.
    [InlineData("add", true, true, true, false)]
    [InlineData("intersect", true, false, false, false)]
    [InlineData("subtract", false, false, true, false)]
    [InlineData("exclude", false, true, true, false)]
    public void Mask_composite_combines_the_layers(string composite, bool topLeft, bool topRight, bool bottomLeft, bool bottomRight)
    {
        var img = Render(Pdf($"<div style='{Box};mask-image:linear-gradient(to right, #000 50%, transparent 50%), linear-gradient(#000 50%, transparent 50%);mask-composite:{composite}, add'></div>"));
        foreach (var (x, y, shown) in new[] { (50, 25, topLeft), (150, 25, topRight), (50, 75, bottomLeft), (150, 75, bottomRight) })
            if (shown) Near(255, 0, 0, At(img, x, y));
            else White(At(img, x, y));
    }

    [Fact]
    public void Webkit_mask_composite_takes_the_porter_duff_names()
    {
        var img = Render(Pdf($"<div style='{Box};-webkit-mask-image:linear-gradient(to right, #000 50%, transparent 50%), linear-gradient(#000 50%, transparent 50%);-webkit-mask-composite:source-in'></div>"));
        Near(255, 0, 0, At(img, 50, 25));
        White(At(img, 150, 25));
        White(At(img, 50, 75));
    }

    [Fact]
    public void Url_references_an_svg_mask_element()
    {
        var pdf = Pdf("<svg width=0 height=0 style='position:absolute'><mask id=m><rect width=100 height=100 fill='white'/></mask></svg>"
                      + $"<div style='{Box};mask:url(#m)'></div>");
        var img = Render(pdf);
        Near(255, 0, 0, At(img, 50, 50));
        White(At(img, 150, 50));
    }

    [Fact]
    public void A_mask_type_alpha_svg_mask_uses_alpha()
    {
        // Black is fully opaque, so in the alpha mode it shows the box where luminance would hide it.
        var pdf = Pdf("<svg width=0 height=0 style='position:absolute'><mask id=m style='mask-type:alpha'><rect width=100 height=100 fill='black'/></mask></svg>"
                      + $"<div style='{Box};mask-image:url(#m)'></div>");
        var img = Render(pdf);
        Near(255, 0, 0, At(img, 50, 50));
        White(At(img, 150, 50));
    }

    [Fact]
    public void A_mask_that_cannot_load_leaves_the_box_unmasked()
    {
        var img = Render(Pdf($"<div style='{Box};mask-image:url(missing.png)'></div><div style='{Box};mask:url(#nothing)'></div>"));
        Near(255, 0, 0, At(img, 150, 50));
        Near(255, 0, 0, At(img, 150, 150));
    }

    [Fact]
    public void Mask_none_adds_no_soft_mask()
    {
        var text = Latin1(Pdf($"<div style='{Box};mask-image:none'></div><div style='{Box};-webkit-mask:none'></div>"));
        Assert.DoesNotContain("/SMask <<", text);
    }

    [Fact]
    public void Masked_text_stays_text()
    {
        var pdf = Pdf("<p style='mask-image:linear-gradient(#000, transparent)'>Masked words</p>");
        Assert.Contains("(Masked words)", Latin1(pdf));
        Assert.Contains("/SMask <<", Latin1(pdf));
    }

    [Fact]
    public void Masks_work_in_pdf_a_and_tagged_files()
    {
        var html = "<title>Masks</title><p style='mask-image:linear-gradient(#000, transparent)'>Masked</p><p style='backdrop-filter:invert(1)'>Backdrop</p>";
        AssertStructurallyValid(HtmlToPdf.Convert(html, new PdfOptions { Conformance = PdfConformance.PdfA2b, Compress = false }));
        AssertStructurallyValid(HtmlToPdf.Convert(html, new PdfOptions { Accessibility = PdfAccessibility.PdfUA1, Compress = false }));
    }

    [Fact]
    public void Current_color_backgrounds_use_the_colour_declared_after_them()
    {
        // Icons are often drawn as background-color: currentColor under a mask, with the colour set by a later rule.
        var img = Render(Pdf("<style>i { display:block;width:50px;height:50px;background-color:currentColor } .blue { color:blue }</style><i class=blue></i>"
                             + "<b style='display:block;width:50px;height:50px;background:currentColor;color:green'></b>"));
        Near(0, 0, 255, At(img, 25, 25));
        Near(0, 128, 0, At(img, 25, 75));
    }

    // ---- backdrop-filter ----

    private const string Behind = "<div style='width:200px;height:100px;background:red'></div>";

    private static string Over(string style) => $"<div style='position:absolute;left:100px;top:0;width:100px;height:100px;{style}'></div>";

    [Theory]
    [InlineData("backdrop-filter")]
    [InlineData("-webkit-backdrop-filter")]
    public void Backdrop_filter_changes_what_is_behind_the_box(string property)
    {
        var img = Render(Pdf(Behind + Over($"{property}:grayscale(1)")));
        Near(255, 0, 0, At(img, 50, 50));
        var gray = At(img, 150, 50);
        Assert.InRange(gray.G - gray.R, -3, 3);
        Assert.InRange(gray.R, 40, 80);
    }

    [Fact]
    public void Backdrop_filter_includes_the_white_page()
    {
        var img = Render(Pdf("<div style='width:300px;height:100px'></div>" + Over("backdrop-filter:invert(1)")));
        Near(0, 0, 0, At(img, 150, 50));
        White(At(img, 250, 50));
    }

    [Fact]
    public void Backdrop_filter_follows_the_rounded_corners()
    {
        var img = Render(Pdf(Behind + Over("backdrop-filter:invert(1);border-radius:30px")));
        Near(0, 255, 255, At(img, 150, 50));
        Near(255, 0, 0, At(img, 102, 2));
    }

    [Fact]
    public void Backdrop_blur_softens_edges_behind_the_box()
    {
        var img = Render(Pdf("<div style='width:300px;height:100px;background:linear-gradient(to right, black 150px, white 150px)'></div>"
                             + "<div style='position:absolute;left:100px;top:0;width:100px;height:100px;backdrop-filter:blur(8px)'></div>"));
        var edge = At(img, 148, 50);
        Assert.InRange(edge.R, 40, 200);
        Near(0, 0, 0, At(img, 50, 50), 4);
    }

    [Fact]
    public void The_box_paints_over_its_backdrop()
    {
        var img = Render(Pdf(Behind + Over("backdrop-filter:grayscale(1);background:rgba(0,0,255,0.5)")));
        var p = At(img, 150, 50);
        Assert.InRange(p.B, 140, 170);
        Assert.InRange(p.R, 15, 45);
    }

    [Fact]
    public void Backdrop_filter_stops_at_an_opacity_backdrop_root()
    {
        // The backdrop of the inner box is only what its translucent parent painted before it, nothing here, so the red
        // under the parent is not inverted.
        var img = Render(Pdf("<div style='width:200px;height:100px;background:red'></div>"
                             + "<div style='position:absolute;left:0;top:0;width:200px;height:100px;opacity:0.99'>"
                             + "<div style='position:absolute;left:100px;top:0;width:100px;height:100px;backdrop-filter:invert(1)'></div></div>"));
        Near(255, 0, 0, At(img, 150, 50), 16);
    }
}
