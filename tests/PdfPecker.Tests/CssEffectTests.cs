using System.Text.RegularExpressions;
using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>clip-path, mix-blend-mode, background-blend-mode, isolation and filter on HTML elements.</summary>
public class CssEffectTests
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

    private static (int R, int G, int B) At(byte[] pdf, float cssX, float cssY) => At(Render(pdf), cssX, cssY);

    private static void Near(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 10)
        => Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    private static void White((int R, int G, int B) p) => Near(255, 255, 255, p, 8);

    // ---- parsing ----

    [Theory]
    [InlineData("circle(50%)")]
    [InlineData("circle(closest-side at 20px 30%)")]
    [InlineData("ellipse(40% 20% at left top)")]
    [InlineData("ellipse(farthest-side closest-side at right 10px bottom 20%)")]
    [InlineData("inset(10px)")]
    [InlineData("inset(5% 10px 2em round 8px 4px / 6px)")]
    [InlineData("polygon(evenodd, 0 0, 100% 0, 50% 100%)")]
    [InlineData("path(nonzero, 'M0 0 L10 0 L5 10 Z')")]
    [InlineData("content-box")]
    [InlineData("circle(30px) padding-box")]
    [InlineData("margin-box ellipse()")]
    public void Clip_path_shapes_parse(string value)
        => Assert.NotNull(ClipShape.Parse(value, 12, 12, 6, 8));

    [Theory]
    [InlineData("circle(50%) square(1px)")]
    [InlineData("circle(-5px)")]
    [InlineData("polygon(0 0, 100%)")]
    [InlineData("path('not a path')")]
    [InlineData("inset()")]
    [InlineData("border-box padding-box")]
    [InlineData("star(5)")]
    public void Invalid_clip_path_shapes_are_rejected(string value)
        => Assert.Null(ClipShape.Parse(value, 12, 12, 6, 8));

    [Fact]
    public void Filter_functions_parse_in_order_with_defaults()
    {
        var f = FilterFunction.Parse("grayscale() sepia(50%) hue-rotate(0.5turn) blur(2px) drop-shadow(red 1px 2px 3px) opacity(2)", 12, 12, 6, 8)!;
        Assert.Equal(6, f.Length);
        Assert.Equal(FilterFunction.FilterKind.Grayscale, f[0].Kind);
        Assert.Equal(1, f[0].Amount);
        Assert.Equal(0.5f, f[1].Amount);
        Assert.Equal(180, f[2].Amount, 3);
        Assert.Equal(1.5f, f[3].Amount); // 2px in points
        Assert.Equal(0xFFFF0000u, f[4].Color);
        Assert.Equal(2.25f, f[4].Blur);
        Assert.Equal(1, f[5].Amount);    // opacity cannot go past 1
        Assert.Null(FilterFunction.Parse("grayscale(-1)", 12, 12, 6, 8));
        Assert.Null(FilterFunction.Parse("blur(10%)", 12, 12, 6, 8));
        Assert.Null(FilterFunction.Parse("sparkle(1)", 12, 12, 6, 8));
        Assert.Empty(FilterFunction.Parse("none", 12, 12, 6, 8)!);
    }

    [Fact]
    public void New_properties_are_known_to_supports()
    {
        Assert.True(CssSupports.Evaluate("(mix-blend-mode: multiply)"));
        Assert.True(CssSupports.Evaluate("(background-blend-mode: screen)"));
        Assert.True(CssSupports.Evaluate("(isolation: isolate)"));
        Assert.True(CssSupports.Evaluate("(clip-path: circle(50%))"));
        Assert.True(CssSupports.Evaluate("(filter: grayscale(1))"));
    }

    // ---- clip-path ----

    [Fact]
    public void Circle_clips_the_box_and_its_content()
    {
        var img = Render(Pdf("<div style='width:200px;height:200px;background:red;clip-path:circle(50%)'><div style='height:200px;background:blue;margin-left:150px'></div></div>"));
        Near(255, 0, 0, At(img, 100, 100));
        White(At(img, 5, 5));      // a corner of the box is outside the circle
        White(At(img, 195, 195));
        Near(0, 0, 255, At(img, 170, 100)); // the child is clipped too, but shows inside
        White(At(img, 195, 10));
    }

    [Fact]
    public void Polygon_and_path_clip_to_their_outlines()
    {
        var polygon = Render(Pdf("<div style='width:200px;height:100px;background:blue;clip-path:polygon(50% 0, 100% 100%, 0 100%)'></div>"));
        Near(0, 0, 255, At(polygon, 100, 90));
        White(At(polygon, 10, 10));
        White(At(polygon, 190, 10));
        var path = Render(Pdf("<div style=\"width:200px;height:100px;background:teal;clip-path:path('M0 0 L200 0 L100 100 Z')\"></div>"));
        Near(0, 128, 128, At(path, 100, 10));
        White(At(path, 10, 90));
        Assert.Contains("W n", AllContent(Pdf("<div style=\"width:20px;height:10px;background:teal;clip-path:path(evenodd, 'M0 0 L20 0 L10 10 Z')\"></div>"))
            .Replace("W* n", "W n"));
    }

    [Fact]
    public void Inset_with_round_corners()
    {
        var img = Render(Pdf("<div style='width:200px;height:100px;background:green;clip-path:inset(10px 20px round 30px)'></div>"));
        Near(0, 128, 0, At(img, 100, 50));
        White(At(img, 10, 50));     // left of the inset
        White(At(img, 24, 14));     // the rounded top-left corner
        Near(0, 128, 0, At(img, 60, 14));
    }

    [Fact]
    public void Reference_boxes_move_the_shape()
    {
        // inset(0) of the content box leaves out the padding and the border.
        var img = Render(Pdf("<div style='width:100px;height:100px;padding:20px;border:10px solid red;background:blue;clip-path:inset(0) content-box'></div>"));
        Near(0, 0, 255, At(img, 80, 80));
        White(At(img, 5, 80));   // border
        White(At(img, 20, 80));  // padding
        Near(0, 0, 255, At(img, 35, 80));
        // A reference box alone clips to that box, with its rounded corners.
        var padding = Render(Pdf("<div style='width:100px;height:100px;border:10px solid red;background:blue;clip-path:padding-box'></div>"));
        White(At(padding, 5, 60));
        Near(0, 0, 255, At(padding, 15, 60));
    }

    [Fact]
    public void Url_references_an_inline_svg_clip_path()
    {
        var img = Render(Pdf("""
            <svg width="0" height="0" style="position:absolute"><clipPath id="tri" clipPathUnits="objectBoundingBox">
              <polygon points="0,0 1,0 0,1"/></clipPath></svg>
            <div style="width:200px;height:200px;background:crimson;clip-path:url(#tri)"></div>
            <div style="width:200px;height:200px;background:navy;clip-path:url(#nowhere)"></div>
            """));
        Near(220, 20, 60, At(img, 30, 30));
        White(At(img, 170, 170));
        Near(0, 0, 128, At(img, 170, 370)); // a missing clip path does not clip
    }

    [Fact]
    public void Url_clip_path_in_user_space_uses_css_pixels_from_the_border_box()
    {
        var img = Render(Pdf("""
            <svg width="0" height="0" style="position:absolute"><defs><clipPath id="c"><rect x="50" y="0" width="50" height="100"/></clipPath></defs></svg>
            <div style="margin-left:100px;width:200px;height:100px;background:green;clip-path:url(#c)"></div>
            """));
        White(At(img, 120, 50));
        Near(0, 128, 0, At(img, 170, 50));
        White(At(img, 220, 50));
    }

    [Fact]
    public void Clip_path_turns_with_a_transform()
    {
        // A square clipped to its left half, turned half a turn, shows on the right.
        var img = Render(Pdf("<div style='width:200px;height:100px;background:blue;clip-path:inset(0 50% 0 0);transform:rotate(180deg)'></div>"));
        White(At(img, 50, 50));
        Near(0, 0, 255, At(img, 150, 50));
    }

    [Fact]
    public void Clip_path_on_an_image()
    {
        var src = DataUri(SolidPng(20, 20, 255, 0, 0));
        var img = Render(Pdf($"<img src='{src}' style='display:block;width:200px;height:200px;clip-path:circle(50%)'>"));
        Near(255, 0, 0, At(img, 100, 100));
        White(At(img, 5, 5));
    }

    // ---- blending ----

    [Fact]
    public void Mix_blend_mode_blends_with_what_is_behind()
    {
        var pdf = Pdf("<div style='background:yellow;padding:20px'><div style='width:100px;height:50px;background:blue;mix-blend-mode:multiply'></div></div>");
        var text = Latin1(pdf);
        Assert.Contains("/BM /Multiply", text);
        Assert.Contains("/Group << /S /Transparency /I true >>", text);
        Near(0, 0, 0, At(pdf, 60, 40)); // yellow times blue
        Near(255, 255, 0, At(pdf, 200, 40));
    }

    [Theory]
    [InlineData("screen", 255, 255, 255)]
    [InlineData("difference", 255, 255, 255)]
    [InlineData("darken", 0, 0, 0)]
    [InlineData("lighten", 255, 255, 255)]
    [InlineData("exclusion", 255, 255, 255)]
    public void Blend_modes_are_drawn_by_the_renderer(string mode, int r, int g, int b)
    {
        var pdf = Pdf($"<div style='background:yellow;padding:20px'><div style='width:100px;height:50px;background:blue;mix-blend-mode:{mode}'></div></div>");
        Near(r, g, b, At(pdf, 60, 40));
    }

    [Fact]
    public void Isolation_keeps_blending_inside_the_group()
    {
        var pdf = Pdf("""
            <div style='background:yellow;padding:20px'>
              <div style='isolation:isolate'><div style='width:100px;height:50px;background:blue;mix-blend-mode:multiply'></div></div>
            </div>
            """);
        // Inside an isolated group, the blue box has nothing to blend with: it stays blue.
        Near(0, 0, 255, At(pdf, 60, 40));
    }

    [Fact]
    public void Background_blend_mode_blends_layers_with_the_background_colour_only()
    {
        var pdf = Pdf("""
            <div style='background:red;padding:20px'>
              <div style='width:100px;height:50px;background:linear-gradient(blue, blue), yellow;background-blend-mode:screen'></div>
            </div>
            """);
        Assert.Contains("/BM /Screen", Latin1(pdf));
        // Blue screened on yellow is white; the red behind the box does not take part.
        Near(255, 255, 255, At(pdf, 60, 40));
    }

    // ---- filter ----

    [Theory]
    [InlineData("grayscale(0.7)", 163, 143, 122)]
    [InlineData("sepia(1)", 198, 176, 137)]
    [InlineData("saturate(2.5)", 255, 122, 0)]
    [InlineData("hue-rotate(120deg)", 49, 175, 134)]
    [InlineData("invert(0.8)", 82, 122, 163)]
    [InlineData("brightness(1.4)", 255, 190, 95)]
    [InlineData("contrast(2)", 255, 145, 9)]
    [InlineData("opacity(0.5)", 230, 196, 162)]
    [InlineData("contrast(2) hue-rotate(90deg) brightness(.8)", 7, 170, 49)]
    public void Colour_filters_match_chrome(string filter, int r, int g, int b)
    {
        // The expected colours are Chrome's, for #c84 under each filter.
        var pdf = Pdf($"<div style='width:40px;height:40px;background:#c84;filter:{filter}'></div>");
        Near(r, g, b, At(pdf, 20, 20), 3);
    }

    [Fact]
    public void Colour_filters_keep_text_as_text()
    {
        var pdf = Pdf("<p style='color:red;filter:grayscale(1)'>Grey words</p>");
        var content = AllContent(pdf);
        Assert.Contains("(Grey words) Tj", content);
        Assert.DoesNotContain("1 0 0 rg", content);
        Assert.Contains("0.212 0.212 0.212 rg", content); // red's luminance
    }

    [Fact]
    public void Filters_change_image_pixels()
    {
        var png = DataUri(SolidPng(8, 8, 255, 0, 0));
        var pdf = Pdf($"<img src='{png}' style='display:block;width:80px;height:80px;filter:invert(1)'>");
        Near(0, 255, 255, At(pdf, 40, 40), 3);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var options = NoMargins;
        options.BaseDirectory = fixtures;
        var jpeg = Render(HtmlToPdf.Convert("<body style='margin:0'><img src='rgb.jpg' style='display:block;filter:grayscale(1)'></body>", options));
        for (int x = 10; x < 300; x += 37)
        {
            var p = At(jpeg, x, 80);
            Assert.True(Math.Abs(p.R - p.G) <= 3 && Math.Abs(p.G - p.B) <= 3, $"Expected grey at {x}, got {p}");
        }
    }

    [Fact]
    public void Filters_apply_to_backgrounds_borders_and_gradients()
    {
        var pdf = Pdf("<div style='width:100px;height:60px;border:10px solid red;background:linear-gradient(red, red);filter:invert(1)'></div>");
        Near(0, 255, 255, At(pdf, 5, 40), 3);
        Near(0, 255, 255, At(pdf, 60, 40), 3);
    }

    [Fact]
    public void Drop_shadow_draws_a_shadow_under_the_content()
    {
        var pdf = Pdf("<div style='margin:20px;width:100px;height:50px;background:blue;filter:drop-shadow(20px 20px 0 red)'></div>");
        Assert.Contains("/Xr1 Do", AllContent(pdf));
        var img = Render(pdf);
        Near(0, 0, 255, At(img, 60, 40));
        Near(255, 0, 0, At(img, 130, 80));  // the shadow, past the box
        White(At(img, 30, 80));               // left of the shadow
    }

    [Fact]
    public void Colour_functions_after_drop_shadow_change_the_shadow()
    {
        var pdf = Pdf("<div style='margin:20px;width:100px;height:50px;background:blue;filter:drop-shadow(20px 20px 0 red) invert(1)'></div>");
        var img = Render(pdf);
        Near(255, 255, 0, At(img, 60, 40), 4); // inverted blue
        Near(0, 255, 255, At(img, 130, 80), 4); // inverted red shadow
    }

    [Fact]
    public void Blur_replaces_the_content_with_blurred_pixels()
    {
        var pdf = Pdf("<div style='margin:40px;width:100px;height:100px;background:black;filter:blur(10px)'></div>");
        var img = Render(pdf);
        Near(0, 0, 0, At(img, 90, 90), 4);       // the middle stays dark
        var edge = At(img, 140, 90);             // the edge is half covered
        Assert.InRange(edge.R, 100, 160);
        var outside = At(img, 155, 90);          // and the blur reaches past it
        Assert.InRange(outside.R, 160, 250);
    }

    [Fact]
    public void Drop_shadow_of_a_box_over_two_pages_shows_on_both()
    {
        var options = new PdfOptions { Margins = new PageMargins(20), Compress = false };
        var pdf = HtmlToPdf.Convert("<div style='height:1500px;width:200px;background:white;border:1px solid black;filter:drop-shadow(10px 0 0 red)'></div>", options);
        Assert.Equal(2, PageCount(pdf));
        foreach (int page in new[] { 0, 1 })
        {
            var img = Render(pdf, page);
            Near(255, 0, 0, At(img, 20 / 0.75f + 205, 300), 6);
        }
    }

    [Fact]
    public void Filter_opacity_joins_the_layer_opacity()
    {
        var content = AllContent(Pdf("<div style='width:10px;height:10px;background:red;opacity:0.5;filter:opacity(0.5)'></div>"));
        Assert.Matches(@"/g64 gs /Fx\d+ Do", content);
    }

    [Fact]
    public void Effects_work_in_pdf_a_and_tagged_files()
    {
        var html = "<title>Effects</title><p style='mix-blend-mode:multiply'>Blended</p><p style='filter:blur(1px)'>Blurred</p>"
                   + "<p style='filter:drop-shadow(1px 1px 1px gray) sepia(1)'>Shadowed</p><p style='clip-path:inset(2px)'>Clipped</p>";
        var pdfa = HtmlToPdf.Convert(html, new PdfOptions { Conformance = PdfConformance.PdfA2b, Compress = false });
        AssertStructurallyValid(pdfa);
        Assert.Contains("/BM /Multiply", Latin1(pdfa));
        var tagged = HtmlToPdf.Convert(html, new PdfOptions { Accessibility = PdfAccessibility.PdfUA1, Compress = false });
        AssertStructurallyValid(tagged);
        Assert.Equal(1, PdfRenderer.Open(tagged).PageCount);
    }

    [Fact]
    public void A_filter_makes_the_box_the_containing_block_of_fixed_descendants()
    {
        var pdf = Pdf("<div style='margin-top:200px;height:100px;filter:grayscale(0)'><div style='position:fixed;top:10px;left:10px;width:20px;height:20px;background:blue'></div></div>");
        White(At(pdf, 20, 20));
        Near(0, 0, 255, At(pdf, 20, 220));
        Assert.Single(Regex.Matches(AllContent(pdf), " re f"));
    }
}
