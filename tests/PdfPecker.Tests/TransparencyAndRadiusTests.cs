using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Translucent colours, opacity, translucent gradients and border-radius.</summary>
public class TransparencyAndRadiusTests
{
    private const float Width = 500, PageHeight = 800;

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
    }

    private static ComputedStyle StyleOf(string style) => Styled($"<div style='{style}'></div>").FindFirst("div")!.Style!;

    private static List<DrawOp> Ops(string html)
    {
        var doc = Styled(html);
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        var list = new List<DrawOp>();
        void Add(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                list.Add(o);
                if (o is GroupOp g) Add(g.Ops);
            }
        }
        Add(engine.Ops);
        return list;
    }

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 12)
        => Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    private static byte[] Pdf(string body) => HtmlToPdf.Convert($"<body style='margin:0'>{body}</body>", NoMargins);

    // ---- colours ----

    [Fact]
    public void Translucent_colours_keep_their_alpha()
    {
        var s = StyleOf("color: rgba(0, 0, 255, 0.5); background-color: hsla(0, 100%, 50%, 0.25); border: 1px solid #00ff0080");
        Assert.Equal(0x800000FFu, s.Color);
        Assert.Equal(0x40FF0000u, s.BackgroundColor);
        Assert.Equal(0x8000FF00u, s.BorderTopColor);
    }

    [Fact]
    public void Translucent_background_blends_with_what_is_below()
    {
        var pdf = Pdf("<div style='background:#0000ff; padding:20pt'><div style='height:40pt; background: rgba(255, 0, 0, 0.5)'></div></div>");
        AssertStructurallyValid(pdf);
        Assert.Matches(@"/a128 << /ca 0\.50?2? >>", Latin1(pdf));
        Assert.Contains("/a128 gs", AllContent(pdf));
        Color(128, 0, 128, Pixel(pdf, 100, 40)); // red at half over blue
        Color(0, 0, 255, Pixel(pdf, 100, 5));
    }

    [Fact]
    public void Opaque_documents_have_no_graphics_states()
    {
        var pdf = Pdf("<p style='color:#333; background:#eee'>Plain</p>");
        Assert.DoesNotContain("/ExtGState", Latin1(pdf));
        Assert.DoesNotContain(" gs", AllContent(pdf));
    }

    [Fact]
    public void Translucent_text_and_borders_use_alpha()
    {
        var pdf = Pdf("<p style='color: rgba(0,0,0,0.5); border: 2px dashed rgba(0,0,0,0.25)'>Faded</p>");
        var content = AllContent(pdf);
        Assert.Contains("/a128 gs", content);
        Assert.Contains("/A64 gs", content);
    }

    // ---- opacity ----

    [Fact]
    public void Opacity_paints_the_box_as_one_transparency_group()
    {
        var pdf = Pdf("<div style='opacity:0.5; background:#ff0000; height:60pt'><div style='background:#00ff00; height:20pt'></div></div>");
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("/Group << /S /Transparency >>", text);
        Assert.Contains("/g128 << /ca 0.502 /CA 0.502 >>", text);
        Assert.Contains("/g128 gs /Fx1 Do", AllContent(pdf));
        // The green child covers the red box inside the group, so it is not tinted red.
        Color(128, 255, 128, Pixel(pdf, 100, 10));
        Color(255, 128, 128, Pixel(pdf, 100, 40));
    }

    [Fact]
    public void Links_inside_a_translucent_box_stay_clickable()
    {
        var pdf = Pdf("<div style='opacity:0.5'><a href='https://example.com/'>link</a></div>");
        Assert.Contains("/URI (https://example.com/)", Latin1(pdf));
    }

    [Fact]
    public void Translucent_groups_render_when_encrypted()
    {
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><div style='opacity:0.5; height:40pt; background:#000'></div></body>",
            new PdfOptions { Margins = new PageMargins(0), Security = new PdfSecurity { UserPassword = "pw" } });
        var img = PdfRenderer.Open(pdf, "pw").RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (20 * img.Width + 50) * 4;
        Color(128, 128, 128, (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]));
    }

    [Fact]
    public void Opacity_makes_a_layer()
    {
        var layer = Ops("<div style='opacity:0.3'>x</div>").OfType<LayerOp>().Single();
        Assert.Equal(0.3f, layer.Opacity, 3);
        Assert.True(layer.StackingContext);
    }

    [Fact]
    public void Opacity_on_an_inline_span_fades_its_text()
    {
        var text = Ops("<p>a <span style='opacity:0.5; color:#ff0000'>b <b>c</b></span></p>").OfType<TextOp>().ToList();
        Assert.Equal(0xFF000000u, text.First(t => t.Text.StartsWith("a")).Color);
        Assert.Equal(0x80FF0000u, text.First(t => t.Text.StartsWith("b")).Color);
        Assert.Equal(0x80FF0000u, text.First(t => t.Text.StartsWith("c")).Color);
    }

    // ---- gradients ----

    [Fact]
    public void Gradient_to_transparent_uses_a_soft_mask()
    {
        var pdf = Pdf("<div style='background:#0000ff'><div style='height:50pt; width:200pt; background: linear-gradient(to right, red, transparent)'></div></div>");
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("/SMask << /Type /Mask /S /Luminosity /G ", text);
        Assert.Contains("/ColorSpace /DeviceGray", text);
        Color(255, 0, 0, Pixel(pdf, 2, 25), 30);
        Color(0, 0, 255, Pixel(pdf, 198, 25), 30);
        Color(128, 0, 128, Pixel(pdf, 100, 25), 30); // red, not darkened, at half over blue
    }

    // ---- border-radius ----

    [Fact]
    public void Border_radius_shorthand_expands_to_corners()
    {
        var s = StyleOf("border-radius: 10px 20px / 5px");
        var r = s.BorderRadii!;
        Assert.Equal(7.5f, r[0].Value);  // top-left horizontal
        Assert.Equal(3.75f, r[1].Value); // top-left vertical
        Assert.Equal(15f, r[2].Value);   // top-right horizontal
        Assert.Equal(7.5f, r[4].Value);  // bottom-right horizontal
        Assert.Equal(15f, r[6].Value);   // bottom-left horizontal

        var p = StyleOf("border-radius: 50%").BorderRadii!;
        Assert.True(p[0].IsPercent);
        Assert.Null(StyleOf("border-radius: 0").BorderRadii);
    }

    [Fact]
    public void Border_width_style_and_colour_shorthands_set_each_side()
    {
        var s = StyleOf("border-style: solid dashed; border-width: 4px 2px; border-color: red green blue orange");
        Assert.Equal(BorderStyle.Solid, s.BorderTopStyle);
        Assert.Equal(BorderStyle.Dashed, s.BorderLeftStyle);
        Assert.Equal(3f, s.BorderTopWidth);
        Assert.Equal(1.5f, s.BorderRightWidth);
        Assert.Equal(0xFF0000FFu, s.BorderBottomColor);
    }

    [Fact]
    public void Radii_are_scaled_down_when_they_overlap()
    {
        var r = CornerRadii.Fit(100, 100, 100, 100, 0, 0, 0, 0, 100, 300)!.Value;
        Assert.Equal(50, r.TLx, 3);
        Assert.Equal(50, r.TRy, 3);
        Assert.Null(CornerRadii.Fit(0, 0, 0, 0, 0, 0, 0, 0, 100, 100));
    }

    [Fact]
    public void Rounded_box_is_painted_with_curves()
    {
        var ops = Ops("<div style='width:100pt; height:50pt; background:#00f; border:4px solid red; border-radius:12px'></div>");
        var fills = ops.OfType<RoundRectOp>().ToList();
        Assert.Equal(2, fills.Count); // background, border ring
        Assert.False(fills[0].HasHole);
        Assert.True(fills[1].HasHole);
        Assert.Equal(6, fills[1].HoleR.TLx, 3); // inner radius: 9pt - 3pt

        var pdf = Pdf("<div style='width:100pt; height:50pt; background:#00f; border-radius:20pt'></div>");
        Assert.Contains(" c ", AllContent(pdf));
        Color(255, 255, 255, Pixel(pdf, 1, 1));   // outside the curve
        Color(0, 0, 255, Pixel(pdf, 50, 25));
        Color(0, 0, 255, Pixel(pdf, 1, 25));
    }

    [Fact]
    public void Sides_of_different_colours_are_cut_at_the_corners()
    {
        var ops = Ops("<div style='height:50pt; border-style:solid; border-width:4pt; border-color:red green blue orange; border-radius:12pt'></div>");
        var sides = ops.OfType<RoundRectOp>().ToList();
        Assert.Equal(4, sides.Count);
        Assert.All(sides, s => Assert.NotNull(s.Clip));
    }

    [Fact]
    public void Dashed_rounded_border_is_stroked()
    {
        var ring = Ops("<div style='height:50pt; border:2px dashed red; border-radius:12pt'></div>").OfType<RoundRectOp>().Single();
        Assert.Equal(1.5f, ring.StrokeWidth);
        Assert.Equal(BorderStyle.Dashed, ring.Style);
    }

    [Fact]
    public void Background_layers_and_images_are_clipped_to_the_curve()
    {
        var bg = Ops("<div style='height:50pt; border-radius:10pt; background: linear-gradient(red, blue)'></div>").OfType<BackgroundOp>().Single();
        Assert.NotNull(bg.ClipRadii);

        string png = "data:image/png;base64," + Convert.ToBase64String(SolidPng(10, 10, 255, 0, 0));
        var pdf = Pdf($"<img src='{png}' style='display:block; width:100pt; height:100pt; border-radius:50%'>");
        Assert.Matches(@"q [\d. ]+ m .* c .*h\nW n [\d. ]+ 0 0 [\d. ]+ cm /Im1 Do", AllContent(pdf));
        Color(255, 255, 255, Pixel(pdf, 3, 3));
        Color(255, 0, 0, Pixel(pdf, 50, 50));
    }

    [Fact]
    public void Rounded_inline_image_is_clipped()
    {
        string png = "data:image/png;base64," + Convert.ToBase64String(SolidPng(10, 10, 255, 0, 0));
        var img = Ops($"<p><img src='{png}' style='width:40pt; height:40pt; border-radius:8pt'></p>").OfType<ImageOp>().Single();
        Assert.NotNull(img.Radii);
    }

    [Fact]
    public void Inline_image_draws_its_border_padding_and_background()
    {
        // Chrome draws the box of an image that stays inline: the picture sits inside the padding, inside the border.
        string png = "data:image/png;base64," + Convert.ToBase64String(SolidPng(10, 10, 0, 0, 255));
        var ops = Ops($"<p>a <img src='{png}' style='width:40pt; height:40pt; padding:4pt; background:#ff0; border:3pt solid red'> b</p>");
        var img = ops.OfType<ImageOp>().Single();
        var yellow = ops.OfType<RectOp>().Single(r => r.Color == 0xFFFFFF00);
        Assert.Equal(40, img.W, 0.05f);
        Assert.Equal(yellow.X + 7, img.X, 0.05f);
        Assert.Equal(yellow.Y + 7, img.Y, 0.05f);
        Assert.Equal(54, yellow.W, 0.05f);
        Assert.Equal(3, ops.OfType<BorderRectOp>().Single().Width, 0.05f);
    }

    [Fact]
    public void Box_shadow_follows_the_radius()
    {
        var ops = Ops("<div style='height:50pt; border-radius:10pt; box-shadow: 0 4px 8px rgba(0,0,0,.3)'></div>");
        Assert.Contains(ops.OfType<RoundRectOp>(), r => r.HasHole);
        Assert.Empty(ops.OfType<RectOp>());
    }

    [Fact]
    public void Rounded_box_split_by_a_page_break_is_sliced()
    {
        var pdf = HtmlToPdf.Convert("<div style='height:1500pt; background:#ccc; border:1pt solid #000; border-radius:20pt'></div>", new PdfOptions { Compress = false });
        AssertStructurallyValid(pdf);
        Assert.Equal(2, PageCount(pdf));
        Assert.Contains(" re W n", AllContent(pdf));
    }

    [Fact]
    public void Transparency_and_radius_are_allowed_in_pdfa()
    {
        var pdf = HtmlToPdf.Convert("<div style='opacity:.5; border-radius:8px; background: linear-gradient(red, transparent); color: rgba(0,0,0,.5)'>PDF/A</div>",
            new PdfOptions { Conformance = PdfConformance.PdfA2b, Compress = false });
        AssertStructurallyValid(pdf);
        Assert.Contains("/S /Transparency", Latin1(pdf));
    }

    // ---- overflow ----

    [Fact]
    public void Overflow_values_set_each_axis()
    {
        var s = StyleOf("overflow: hidden");
        Assert.True(s.ClipsX && s.ClipsY && s.FormattingRoot);
        s = StyleOf("overflow-x: clip");
        Assert.True(s.ClipsX);
        Assert.False(s.ClipsY);
        Assert.False(s.FormattingRoot);
        s = StyleOf("overflow: visible auto"); // visible next to auto computes to auto
        Assert.True(s.ClipsX && s.ClipsY);
        Assert.False(StyleOf("overflow: visible").ClipsX);
    }

    [Fact]
    public void Overflow_hidden_cuts_content_at_the_padding_box()
    {
        var pdf = Pdf("<div style='width:100pt; height:40pt; overflow:hidden; border:5pt solid #00f'><div style='width:200pt; height:200pt; background:red'></div></div>");
        AssertStructurallyValid(pdf);
        Color(255, 0, 0, Pixel(pdf, 50, 25));
        Color(0, 0, 255, Pixel(pdf, 50, 47));      // the bottom border, not covered
        Color(255, 255, 255, Pixel(pdf, 50, 80));  // below the box
        Color(255, 255, 255, Pixel(pdf, 150, 25)); // beside it

        var visible = Pdf("<div style='width:100pt; height:40pt'><div style='width:200pt; height:200pt; background:red'></div></div>");
        Color(255, 0, 0, Pixel(visible, 150, 80));
    }

    [Fact]
    public void Overflow_hidden_follows_the_rounded_corners()
    {
        var ops = Ops("<div style='box-sizing:border-box; width:100pt; height:100pt; overflow:hidden; border:8px solid #000; border-radius:50%'><p>x</p></div>");
        var clip = ops.OfType<ClipOp>().Single();
        Assert.InRange(clip.W, 87.9f, 88.1f);                // the padding box: 100pt - 2 × 6pt
        Assert.InRange(clip.Radii!.Value.TLx, 43.9f, 44.1f); // the inner border edge: 50pt - 6pt

        var pdf = Pdf("<div style='width:100pt; height:100pt; overflow:hidden; border-radius:50%'><div style='height:100pt; background:red'></div></div>");
        Assert.Matches(@"q [\d. ]+ m .* c .*h\nW n", AllContent(pdf));
        Color(255, 255, 255, Pixel(pdf, 4, 4)); // outside the circle
        Color(255, 0, 0, Pixel(pdf, 50, 50));
        Color(255, 0, 0, Pixel(pdf, 2, 50));
    }

    [Fact]
    public void Overflow_clip_on_one_axis_leaves_the_other_alone()
    {
        var pdf = Pdf("<div style='width:100pt; height:20pt; overflow-x:clip'><div style='width:200pt; height:60pt; background:red'></div></div>");
        Color(255, 255, 255, Pixel(pdf, 150, 10));
        Color(255, 0, 0, Pixel(pdf, 50, 50));
    }

    [Fact]
    public void Hidden_overflow_adds_no_pages()
    {
        var pdf = Pdf("<div style='height:100pt; overflow:hidden'><div style='height:3000pt; background:red'></div></div>");
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void Clipped_box_split_by_a_page_break_shows_on_both_pages()
    {
        var pdf = HtmlToPdf.Convert("<div style='height:1500pt; overflow:hidden; border-radius:10pt'><p>first</p><p style='margin-top:1200pt'>second <a href='https://example.com'>link</a></p></div>",
            new PdfOptions { Compress = false });
        AssertStructurallyValid(pdf);
        Assert.Equal(2, PageCount(pdf));
        Assert.Contains("/URI", Latin1(pdf));
    }

    [Fact]
    public void Positioned_children_are_sorted_inside_the_clip()
    {
        var ops = Ops("<div style='height:50pt; overflow:hidden'><div style='position:relative; z-index:2'>a</div><div>b</div></div>");
        var clip = ops.OfType<ClipOp>().Single();
        Assert.IsType<LayerOp>(clip.Ops[^1]);
    }

    [Fact]
    public void Dashed_sides_of_a_mixed_rounded_border_stay_dashed()
    {
        var sides = Ops("<div style='height:50pt; border:2pt solid red; border-top-style:dashed; border-radius:12pt'></div>").OfType<RoundRectOp>().ToList();
        Assert.Equal(4, sides.Count);
        var top = Assert.Single(sides, s => s.StrokeWidth > 0);
        Assert.Equal(BorderStyle.Dashed, top.Style);
        Assert.NotNull(top.Clip);

        var pdf = Pdf("<div style='height:50pt; border:2pt solid red; border-top-style:dotted; border-radius:12pt'></div>");
        AssertStructurallyValid(pdf);
        // Round dots, spread evenly around the whole curve.
        Assert.Matches(@"1 J \[0 [\d.]+\] 0 d", AllContent(pdf));
    }
}
