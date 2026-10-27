using System.Diagnostics;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>CSS background images: url() layers, repeat, position, size, origin, clip and the background shorthand.</summary>
public class BackgroundImageTests
{
    private const float Width = 500, PageHeight = 800;

    // 40 x 20 px = 30 x 15 pt, and 20 x 20 px = 15 x 15 pt.
    private static readonly string Wide = DataUri(SolidPng(40, 20, 255, 0, 0));
    private static readonly string Square = DataUri(SolidPng(20, 20, 255, 0, 0));

    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
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

    /// <summary>The background layers of a div styled with <paramref name="style"/>, in paint order, relative to its border box.</summary>
    private static List<BackgroundOp> Layers(string style)
    {
        var doc = Styled($"<div style='margin:0; {style}'></div>");
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        var ops = Flatten(engine.Ops).OfType<BackgroundOp>().ToList();
        return ops;
    }

    private static BackgroundOp Layer(string style) => Assert.Single(Layers(style));

    private static ComputedStyle StyleOf(string style)
    {
        var doc = Styled($"<div id='x' style='{style}'></div>");
        return doc.FindFirst("div")!.Style!;
    }

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}");

    private static void Tile(BackgroundOp op, float x, float y, float w, float h)
    {
        Near(x, op.TileX - op.ClipX);
        Near(y, op.TileY - op.ClipY);
        Near(w, op.TileW);
        Near(h, op.TileH);
    }

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y, int page = 0)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual)
        => Assert.True(Math.Abs(actual.R - r) < 40 && Math.Abs(actual.G - g) < 40 && Math.Abs(actual.B - b) < 40,
            $"Expected ({r}, {g}, {b}), got {actual}");

    // ---- parsing ----

    [Fact]
    public void Shorthand_sets_every_longhand_per_layer()
    {
        var s = StyleOf($"background: url({Wide}) right 10pt bottom / 50% auto no-repeat content-box padding-box, linear-gradient(red, blue) #00ff00");
        Assert.NotNull(s.BackgroundImages);
        Assert.Equal(2, s.BackgroundImages!.Length);
        Assert.StartsWith("data:image/png;base64,", s.BackgroundImages[0]!.Url);
        Assert.NotNull(s.BackgroundImages[1]!.Gradient);
        Assert.Equal(0xFF00FF00u, s.BackgroundColor);

        Assert.True(s.BackgroundPositionsX![0].FromEnd);
        Assert.Equal(10, s.BackgroundPositionsX[0].Offset.Value);
        Assert.True(s.BackgroundPositionsY![0].FromEnd);
        Assert.Equal(50, s.BackgroundSizes![0].Width.Value);
        Assert.True(s.BackgroundSizes[0].Height.IsAuto);
        Assert.Equal((BackgroundRepeat.NoRepeat, BackgroundRepeat.NoRepeat), s.BackgroundRepeats![0]);
        Assert.Equal(BackgroundBox.ContentBox, s.BackgroundOrigins![0]);
        Assert.Equal(BackgroundBox.PaddingBox, s.BackgroundClips![0]);

        // The second layer gets the initial values.
        Assert.Equal((BackgroundRepeat.Repeat, BackgroundRepeat.Repeat), s.BackgroundRepeats[1]);
        Assert.Equal(BackgroundBox.PaddingBox, s.BackgroundOrigins[1]);
        Assert.Equal(BackgroundBox.BorderBox, s.BackgroundClips[1]);
        Assert.True(s.BackgroundSizes[1].Width.IsAuto);
    }

    [Fact]
    public void Shorthand_keeps_data_uris_with_semicolons_and_commas_whole()
    {
        var s = StyleOf($"background: #fff url({Wide}) repeat-x");
        Assert.Equal(Wide, Assert.Single(s.BackgroundImages!)!.Url);
        Assert.Equal((BackgroundRepeat.Repeat, BackgroundRepeat.NoRepeat), s.BackgroundRepeats![0]);
        Assert.Equal(0xFFFFFFFFu, s.BackgroundColor);
    }

    [Theory]
    [InlineData("center", 50, false, 50, false)]
    [InlineData("top", 50, false, 0, false)]
    [InlineData("bottom right", 0, true, 0, true)]
    [InlineData("25% 75%", 25, false, 75, false)]
    [InlineData("right 10pt top 5pt", 10, true, 5, false)]
    [InlineData("left 10pt center", 10, false, 50, false)]
    public void Position_values_parse(string value, float x, bool xEnd, float y, bool yEnd)
    {
        var p = CssBackground.Position(value, 12, 12, 5, 8);
        Assert.NotNull(p);
        Assert.Equal(x, p!.Value.X.Offset.Value);
        Assert.Equal(xEnd, p.Value.X.FromEnd);
        Assert.Equal(y, p.Value.Y.Offset.Value);
        Assert.Equal(yEnd, p.Value.Y.FromEnd);
    }

    [Theory]
    [InlineData("left right")]
    [InlineData("top 10pt bottom 5pt")]
    [InlineData("nonsense")]
    public void Invalid_positions_are_rejected(string value) => Assert.Null(CssBackground.Position(value, 12, 12, 5, 8));

    [Fact]
    public void An_invalid_longhand_keeps_the_previous_value()
    {
        var s = StyleOf("background-repeat: no-repeat; background-repeat: sideways");
        Assert.Equal((BackgroundRepeat.NoRepeat, BackgroundRepeat.NoRepeat), s.BackgroundRepeats![0]);
    }

    // ---- layout ----

    [Fact]
    public void No_repeat_image_is_positioned_in_the_padding_box()
    {
        var op = Layer($"width:200pt; height:100pt; background: url({Wide}) no-repeat center");
        Tile(op, 85, 42.5f, 30, 15);
        Assert.False(op.RepeatX || op.RepeatY);
        Assert.NotNull(op.Image);

        Tile(Layer($"width:200pt; height:100pt; background: url({Wide}) no-repeat right 10pt bottom 5pt"), 160, 80, 30, 15);
        Tile(Layer($"width:200pt; height:100pt; background: url({Wide}) no-repeat; background-position: 100% 0"), 170, 0, 30, 15);
    }

    [Fact]
    public void Sizes_cover_contain_and_lengths()
    {
        const string box = "width:200pt; height:100pt; background-repeat:no-repeat; ";
        Tile(Layer(box + $"background-image:url({Square}); background-size:cover"), 0, 0, 200, 200);
        Tile(Layer(box + $"background-image:url({Square}); background-size:cover; background-position:center"), 0, -50, 200, 200);
        Tile(Layer(box + $"background-image:url({Square}); background-size:contain"), 0, 0, 100, 100);
        Tile(Layer(box + $"background-image:url({Wide}); background-size:60pt"), 0, 0, 60, 30);
        Tile(Layer(box + $"background-image:url({Wide}); background-size:auto 50%"), 0, 0, 100, 50);
        Tile(Layer(box + $"background-image:url({Wide}); background-size:10pt 40pt"), 0, 0, 10, 40);
    }

    [Fact]
    public void Space_and_round_fit_whole_tiles()
    {
        var space = Layer($"width:100pt; height:15pt; background: url({Square}) space no-repeat");
        Assert.True(space.RepeatX);
        Assert.False(space.RepeatY);
        Near(17, space.StepX); // 6 tiles of 15pt with 5 gaps of 2pt
        Near(0, space.TileX - space.ClipX);

        var round = Layer($"width:100pt; height:100pt; background: url({Wide}) round");
        Near(100f / 3, round.TileW); // 30pt tiles: 3.33 rounds to 3
        Near(100f / 7, round.TileH); // 15pt tiles: 6.67 rounds to 7
    }

    [Fact]
    public void Origin_and_clip_use_the_border_padding_and_content_boxes()
    {
        const string box = "width:100pt; height:50pt; padding:10pt; border:6pt solid black; background-repeat:no-repeat; ";
        var initial = Layer(box + $"background-image:url({Wide})");
        Tile(initial, 6, 6, 30, 15); // origin padding-box, clip border-box
        Near(132, initial.ClipW);

        var content = Layer(box + $"background-image:url({Wide}); background-origin:content-box; background-clip:content-box");
        Tile(content, 0, 0, 30, 15);
        Near(100, content.ClipW);
        Near(50, content.ClipH);

        var border = Layer(box + $"background-image:url({Wide}); background-origin:border-box; background-position:right bottom");
        Tile(border, 102, 67, 30, 15);
    }

    [Fact]
    public void Background_color_follows_the_bottom_layer_clip()
    {
        var doc = Styled($"<div style='margin:0; width:100pt; height:50pt; padding:10pt; background: url({Wide}) no-repeat content-box #00ff00'></div>");
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        var rect = Assert.Single(Flatten(engine.Ops).OfType<RectOp>());
        Near(100, rect.W);
        Near(50, rect.H);
    }

    [Fact]
    public void Layers_paint_bottom_first_and_none_layers_are_skipped()
    {
        var ops = Layers($"height:50pt; background: url({Square}) no-repeat, none, url({Wide})");
        Assert.Equal(2, ops.Count);
        Assert.True(ops[0].RepeatX);  // the last layer is painted first
        Assert.False(ops[1].RepeatX);
    }

    [Fact]
    public void Missing_images_paint_nothing()
    {
        Assert.Empty(Layers("height:50pt; background: url(missing.png) no-repeat"));
        Assert.Empty(Layers("height:50pt; background-image: url()"));
        Assert.Empty(Layers($"height:50pt; background: url({Square}); background-size: 0 0"));
    }

    [Fact]
    public void Sized_gradients_repeat_as_tiles()
    {
        var op = Layer("width:100pt; height:100pt; background: linear-gradient(red, blue) 0 0 / 20pt 10pt");
        Assert.NotNull(op.Gradient);
        Tile(op, 0, 0, 20, 10);
        Assert.True(op.RepeatX && op.RepeatY);
    }

    // ---- PDF output ----

    [Fact]
    public void Single_image_is_clipped_and_drawn_once()
    {
        var html = $"<body style='margin:0'><div style='width:200pt; height:100pt; background: url({DataUri(SolidPng(40, 40, 255, 0, 0))}) no-repeat center'></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        AssertStructurallyValid(pdf);
        var content = AllContent(pdf);
        Assert.Contains(" re W n ", content);
        Assert.Contains("/Im1 Do", content);
        Assert.DoesNotContain("/PatternType 1", Latin1(pdf));
        Color(255, 0, 0, Pixel(pdf, 100, 50));
        Color(255, 255, 255, Pixel(pdf, 20, 20));
    }

    [Fact]
    public void Fixed_backgrounds_are_placed_on_the_paper_of_every_page()
    {
        // As Chrome prints it: 40px 40px from the paper's corner, not the box's, on each page the box reaches.
        var html = "<style>@page { size: 300pt 200pt; margin: 20pt } body { margin: 0 }</style>"
                   + $"<div style='height: 480pt; background: yellow url({Square}) no-repeat fixed 40px 40px'></div>"
                   + $"<div style='margin-left: 100pt; height: 100pt; background: yellow url({Square}) no-repeat fixed'></div>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Compress = false });
        AssertStructurallyValid(pdf);
        for (int page = 0; page < 3; page++)
        {
            Color(255, 0, 0, Pixel(pdf, 37, 37, page));
            Color(255, 255, 0, Pixel(pdf, 25, 25, page));
            Color(255, 255, 0, Pixel(pdf, 50, 50, page));
        }
        // The second box is far from the paper's corner, so its layer does not show.
        Color(255, 255, 0, Pixel(pdf, 125, 80, 3));
        Color(255, 255, 255, Pixel(pdf, 10, 10, 3));
    }

    [Fact]
    public void Repeated_image_is_a_tiling_pattern()
    {
        var html = $"<body style='margin:0'><div style='width:100pt; height:15pt; background: url({Square}) space no-repeat'></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        AssertStructurallyValid(pdf);
        Assert.Contains("/PatternType 1 /PaintType 1 /TilingType 1", Latin1(pdf));
        Assert.Contains("/Pattern cs /T1 scn", AllContent(pdf));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Latin1(pdf), "/Subtype /Image"));
        Color(255, 0, 0, Pixel(pdf, 7, 7));
        Color(255, 255, 255, Pixel(pdf, 16, 7)); // the 2pt gap between the first two tiles
        Color(255, 0, 0, Pixel(pdf, 24, 7));
        Color(255, 0, 0, Pixel(pdf, 92, 7));
        Color(255, 255, 255, Pixel(pdf, 50, 25)); // below the box
    }

    [Fact]
    public void Transparent_pixels_show_the_background_colour()
    {
        var png = DataUri(SolidPng(20, 20, 255, 0, 0, 0));
        var pdf = HtmlToPdf.Convert($"<body style='margin:0'><div style='height:50pt; background: #00ff00 url({png})'></div></body>", NoMargins);
        Assert.Contains("/SMask", Latin1(pdf));
        Color(0, 255, 0, Pixel(pdf, 10, 10));
    }

    [Theory]
    [InlineData("linear-gradient(to right, #ff0000 50%, transparent 50%)")]
    [InlineData("conic-gradient(from 180deg, #ff0000 50%, rgba(0,0,255,0) 50%)")]
    public void Repeated_gradient_tiles_with_translucent_stops_show_what_is_below(string gradient)
    {
        // The tile is soft-masked by the stops' alphas, so the parent's blue shows through the transparent half of
        // every tile, where it used to be flattened on the box's own (white) backdrop.
        var html = $"<body style='margin:0'><div style='background:#0000ff'><div style='width:100pt; height:20pt; background: {gradient} 0 0 / 20pt 20pt'></div></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("/PatternType 1", text);
        Assert.Contains("/SMask << /Type /Mask /S /Luminosity /G ", text);
        Color(255, 0, 0, Pixel(pdf, 5, 10));
        Color(0, 0, 255, Pixel(pdf, 15, 10));
        Color(255, 0, 0, Pixel(pdf, 85, 10));
        Color(0, 0, 255, Pixel(pdf, 95, 10));
    }

    [Fact]
    public void Repeated_opaque_gradient_tiles_keep_their_own_resources()
    {
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><div style='width:100pt; height:20pt; background: linear-gradient(red, blue) 0 0 / 20pt 20pt'></div></body>", NoMargins);
        var text = Latin1(pdf);
        Assert.Contains("/Resources << /Shading << /Sh1 ", text);
        Assert.DoesNotContain("/SMask", text);
    }

    [Fact]
    public void Repeated_gradient_tiles_render()
    {
        var html = "<body style='margin:0'><div style='width:100pt; height:20pt; background: linear-gradient(to right, #ff0000 50%, #0000ff 50%) 0 0 / 20pt 20pt'></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        AssertStructurallyValid(pdf);
        Assert.Contains("/Shading << /Sh1 << /ShadingType 2", Latin1(pdf));
        Color(255, 0, 0, Pixel(pdf, 5, 10));
        Color(0, 0, 255, Pixel(pdf, 15, 10));
        Color(255, 0, 0, Pixel(pdf, 25, 10));
        Color(0, 0, 255, Pixel(pdf, 95, 10));
    }

    [Fact]
    public void A_background_split_by_a_page_break_continues_on_the_next_page()
    {
        var html = $"<body style='margin:0'><div style='height:1200pt; background: url({Square})'></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        Assert.Equal(2, PageCount(pdf));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(AllContent(pdf), "/Pattern cs /T\\d+ scn").Count);
        Color(255, 0, 0, Pixel(pdf, 50, 50, page: 1));
    }

    [Fact]
    public void Background_images_work_in_pdfa_and_encrypted_files()
    {
        var html = $"<div style='height:50pt; background: url({Square}) repeat-x, url({Wide}) no-repeat center'></div>";
        var pdfa = HtmlToPdf.Convert(html, new PdfOptions { Conformance = PdfConformance.PdfA2b, Compress = false });
        AssertStructurallyValid(pdfa);
        Assert.Contains("/PatternType 1", Latin1(pdfa));
        var encrypted = HtmlToPdf.Convert(html, new PdfOptions { Security = new PdfSecurity { UserPassword = "a" } });
        Assert.Equal(1, PdfRenderer.Open(encrypted, "a").PageCount);
    }

    // ---- resources ----

    [Fact]
    public void Urls_in_linked_stylesheets_are_relative_to_the_stylesheet()
    {
        var root = Directory.CreateTempSubdirectory("pecker-bg-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "css"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "img"));
            File.WriteAllBytes(Path.Combine(root.FullName, "img", "dot.png"), SolidPng(4, 4, 255, 0, 0));
            File.WriteAllText(Path.Combine(root.FullName, "css", "site.css"), "div { height: 20pt; background: url(../img/dot.png) no-repeat; }");
            var pdf = HtmlToPdf.Convert("<link rel='stylesheet' href='css/site.css'><div></div>",
                new PdfOptions { BaseDirectory = root.FullName, Compress = false });
            Assert.Contains("/Subtype /Image", Latin1(pdf));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Background_urls_follow_the_file_access_rules()
    {
        var root = Directory.CreateTempSubdirectory("pecker-bg-");
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(root.FullName, "doc"));
            File.WriteAllBytes(Path.Combine(root.FullName, "outside.png"), SolidPng(4, 4, 255, 0, 0));
            const string html = "<div style='height:20pt; background: url(../outside.png)'></div>";
            Assert.DoesNotContain("/Subtype /Image", Latin1(HtmlToPdf.Convert(html, new PdfOptions { BaseDirectory = sub.FullName, Compress = false })));
            Assert.Contains("/Subtype /Image", Latin1(HtmlToPdf.Convert(html,
                new PdfOptions { BaseDirectory = sub.FullName, AllowFileAccess = true, Compress = false })));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rebasing_leaves_absolute_urls_alone()
    {
        var css = "a{background:url(img/x.png)} b{background:url('data:image/png;base64,AA==')} c{background:url(\"/abs.png\")} "
                  + "d{background:url( 'y z.png' )} e{background:url(https://example.com/a.png)} f{fill:url(#g)}";
        var rebased = HtmlToPdf.RebaseUrls(css, "css/site.css");
        Assert.Contains("url(\"css/img/x.png\")", rebased);
        Assert.Contains("url('data:image/png;base64,AA==')", rebased);
        Assert.Contains("url(\"/abs.png\")", rebased);
        Assert.Contains("url(\"css/y z.png\")", rebased);
        Assert.Contains("url(https://example.com/a.png)", rebased);
        Assert.Contains("url(#g)", rebased);
        Assert.Same(css, HtmlToPdf.RebaseUrls(css, "site.css"));
    }

}
