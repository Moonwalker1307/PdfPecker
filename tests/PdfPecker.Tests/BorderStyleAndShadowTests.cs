using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// Groove, ridge, inset and outset borders, inset box shadows, text-overflow strings, calc() table widths and
/// ::first-letter and ::first-line.
/// </summary>
public class BorderStyleAndShadowTests
{
    private static (int R, int G, int B) Pixel(string html, int x, int y)
    {
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Margins = new PageMargins(0) });
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Near(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 12)
        => Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    private const string Body = "<body style='margin:0'>";

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    // ---- hr ----

    [Fact]
    public void The_default_hr_is_an_inset_rule_on_a_light_base_as_in_Chrome()
    {
        // Chrome shades the rule from #eee, not from its gray text colour: a mid-gray top edge over a light bottom edge,
        // whatever color the page gives hr.
        foreach (var style in new[] { "margin:0", "margin:0;color:red" })
        {
            var html = $"{Body}<hr style='{style}'>";
            Near(154, 154, 154, Pixel(html, 20, 0), 3);
            Near(238, 238, 238, Pixel(html, 20, 1), 3);
            Near(255, 255, 255, Pixel(html, 20, 2), 3);
        }
    }

    [Fact]
    public void An_hr_border_color_or_color_attribute_replaces_the_light_base()
    {
        Near(171, 0, 0, Pixel($"{Body}<hr style='margin:0;border-color:red'>", 20, 0), 3);
        Near(255, 0, 0, Pixel($"{Body}<hr style='margin:0;border-color:red'>", 20, 1), 3);
        // The color attribute and noshade make a flat bar: red, or gray without a color.
        Near(255, 0, 0, Pixel($"{Body}<hr color='red' style='margin:0'>", 20, 0), 3);
        Near(255, 0, 0, Pixel($"{Body}<hr color='red' style='margin:0'>", 20, 1), 3);
        Near(128, 128, 128, Pixel($"{Body}<hr noshade style='margin:0'>", 20, 0), 3);
        Near(128, 128, 128, Pixel($"{Body}<hr noshade style='margin:0'>", 20, 1), 3);
    }

    [Fact]
    public void An_hr_with_another_border_style_draws_in_its_text_colour()
    {
        // Only the inset and outset shades start from #eee; a solid or dashed rule is currentColor, gray by default.
        Near(128, 128, 128, Pixel($"{Body}<hr style='margin:0;border-style:solid'>", 20, 0), 3);
        Near(128, 128, 128, Pixel($"{Body}<hr style='margin:0;border-style:solid'>", 20, 1), 3);
        Near(255, 0, 0, Pixel($"{Body}<hr style='margin:0;border-style:solid;color:red'>", 20, 0), 3);
    }

    [Fact]
    public void The_hr_size_attribute_is_the_height_of_the_rule_with_its_borders()
    {
        var html = $"{Body}<hr size='6' style='margin:0'>";
        Near(154, 154, 154, Pixel(html, 20, 0), 3);
        Near(255, 255, 255, Pixel(html, 20, 3), 3);
        Near(238, 238, 238, Pixel(html, 20, 5), 3);
        Near(255, 255, 255, Pixel(html, 20, 6), 3);
    }

    // ---- 3D border colours ----

    [Theory]
    // Measured in Chrome 153: the dark side of an inset border, then its light side.
    [InlineData(0xCC0000u, 0x780000u, 0xFF0000u)]
    [InlineData(0x000000u, 0x545454u, 0xA8A8A8u)]
    [InlineData(0xFFFFFFu, 0xABABABu, 0xFFFFFFu)]
    [InlineData(0xC0C0C0u, 0x6C6C6Cu, 0xFFFFFFu)]
    [InlineData(0x333333u, 0x000000u, 0x878787u)]
    [InlineData(0x404040u, 0x000000u, 0x949494u)]
    [InlineData(0x7F0000u, 0x2B0000u, 0xD30000u)]
    [InlineData(0x0000C0u, 0x00006Cu, 0x0000FFu)]
    [InlineData(0x4A90D9u, 0x2D5885u, 0x57A9FFu)]
    [InlineData(0x202020u, 0x747474u, 0xC8C8C8u)] // too dark to darken: lightened first
    [InlineData(0x222222u, 0x000000u, 0x767676u)]
    [InlineData(0xEEEEEEu, 0x9A9A9Au, 0xEEEEEEu)] // near white: the light side keeps the colour
    [InlineData(0xEBEBEBu, 0x979797u, 0xFFFFFFu)]
    public void Inset_sides_are_shaded_as_in_Chrome(uint color, uint dark, uint light)
    {
        uint c = 0xFF000000 | color;
        Assert.Equal(0xFF000000 | dark, BoxPainter.Shade3D(BorderStyle.Inset, 0, c));
        Assert.Equal(0xFF000000 | dark, BoxPainter.Shade3D(BorderStyle.Inset, 3, c));
        Assert.Equal(0xFF000000 | light, BoxPainter.Shade3D(BorderStyle.Inset, 2, c));
        Assert.Equal(0xFF000000 | light, BoxPainter.Shade3D(BorderStyle.Inset, 1, c));
        // Outset is the reverse.
        Assert.Equal(0xFF000000 | light, BoxPainter.Shade3D(BorderStyle.Outset, 0, c));
        Assert.Equal(0xFF000000 | dark, BoxPainter.Shade3D(BorderStyle.Outset, 2, c));
    }

    [Fact]
    public void Inset_and_outset_borders_draw_two_shades()
    {
        string Box(string style) => Body + $"<div style='width:40px;height:40px;border:20px {style} #c00'></div>";
        Near(0x78, 0, 0, Pixel(Box("inset"), 40, 5));   // top
        Near(0x78, 0, 0, Pixel(Box("inset"), 5, 40));   // left
        Near(0xFF, 0, 0, Pixel(Box("inset"), 40, 75));  // bottom
        Near(0xFF, 0, 0, Pixel(Box("inset"), 75, 40));  // right
        Near(0xFF, 0, 0, Pixel(Box("outset"), 40, 5));
        Near(0x78, 0, 0, Pixel(Box("outset"), 40, 75));
    }

    [Fact]
    public void Groove_and_ridge_borders_split_each_side_in_two_halves()
    {
        string Box(string style) => Body + $"<div style='width:40px;height:40px;border:20px {style} #c00'></div>";
        // A groove's top is dark outside and light inside; a ridge the other way round.
        Near(0x78, 0, 0, Pixel(Box("groove"), 40, 4));
        Near(0xFF, 0, 0, Pixel(Box("groove"), 40, 15));
        Near(0xFF, 0, 0, Pixel(Box("groove"), 40, 75));
        Near(0x78, 0, 0, Pixel(Box("groove"), 40, 65));
        Near(0xFF, 0, 0, Pixel(Box("ridge"), 40, 4));
        Near(0x78, 0, 0, Pixel(Box("ridge"), 40, 15));
    }

    [Fact]
    public void A_two_pixel_groove_has_both_shades_as_in_Chrome()
    {
        // Chrome splits a groove from two pixels up: the default fieldset border is a dark and a light line, not one dark one.
        string Box(string style) => Body + $"<div style='width:40px;height:40px;border:2px {style} #c00'></div>";
        Near(0x78, 0, 0, Pixel(Box("groove"), 20, 0));
        Near(0xFF, 0, 0, Pixel(Box("groove"), 20, 1));
        Near(0x78, 0, 0, Pixel(Box("groove"), 20, 42));
        Near(0xFF, 0, 0, Pixel(Box("groove"), 20, 43));
        Near(0xFF, 0, 0, Pixel(Box("ridge"), 0, 20));
        Near(0x78, 0, 0, Pixel(Box("ridge"), 1, 20));
    }

    [Theory]
    [InlineData("groove")]
    [InlineData("ridge")]
    public void A_one_pixel_groove_or_ridge_is_drawn_unshaded(string style)
    {
        // Too thin for two shades, Chrome draws it in its own colour on every side.
        var html = Body + $"<div style='width:40px;height:40px;border:1px {style} #c00'></div>";
        Near(0xCC, 0, 0, Pixel(html, 20, 0));
        Near(0xCC, 0, 0, Pixel(html, 0, 20));
        Near(0xCC, 0, 0, Pixel(html, 20, 41));
        Near(0xCC, 0, 0, Pixel(html, 41, 20));
    }

    [Fact]
    public void A_collapsed_one_pixel_groove_is_drawn_unshaded()
    {
        var html = Body + "<table style='border-collapse:collapse'><tr><td style='width:40px;height:40px;padding:0;border:1px groove #c00'></td></tr></table>";
        // Sampled inside each side, wherever the collapsed border's half-pixel edges land.
        Near(0xCC, 0, 0, Pixel(html, 20, 1));
        Near(0xCC, 0, 0, Pixel(html, 0, 20));
        Near(0xCC, 0, 0, Pixel(html, 41, 20));
    }

    [Fact]
    public void Three_d_borders_with_rounded_corners_are_shaded()
    {
        var html = Body + "<div style='width:60px;height:40px;border:12px outset #c00;border-radius:16px'></div>";
        Near(0xFF, 0, 0, Pixel(html, 42, 5));
        Near(0x78, 0, 0, Pixel(html, 42, 58));
    }

    [Fact]
    public void Three_d_border_styles_are_parsed()
    {
        var doc = HtmlParser.Parse("<div style='border-style: groove ridge inset outset'></div>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var s = Descendants(doc).First(e => e.Name == "div").Style;
        Assert.Equal(BorderStyle.Groove, s.BorderTopStyle);
        Assert.Equal(BorderStyle.Ridge, s.BorderRightStyle);
        Assert.Equal(BorderStyle.Inset, s.BorderBottomStyle);
        Assert.Equal(BorderStyle.Outset, s.BorderLeftStyle);
    }

    // ---- form controls styled by the page ----

    private static ComputedStyle Control(string html)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return Descendants(doc).First(e => e.Name is "input" or "textarea" or "select" or "button").Style;
    }

    private static bool Square(ComputedStyle s) => s.BorderRadii == null || s.BorderRadii.All(r => r.Value == 0);

    [Fact]
    public void An_unstyled_text_field_keeps_its_rounded_native_look()
    {
        var s = Control("<input value='x'>");
        Assert.False(Square(s));
        Assert.Equal(BorderStyle.Solid, s.BorderTopStyle);
        Assert.Equal(0.75f, s.BorderTop);
    }

    [Fact]
    public void A_text_field_with_a_border_from_the_page_is_square_with_Chromes_padding()
    {
        var s = Control("<input style='border:1px solid #888' value='x'>");
        Assert.True(Square(s));
        Assert.Equal(0.75f, s.BorderTop);
        Assert.Equal(0.75f, s.PaddingTop.Value);   // 1px 2px, as in Chrome's user-agent sheet
        Assert.Equal(1.5f, s.PaddingLeft.Value);
    }

    [Fact]
    public void A_text_field_with_a_background_gets_Chromes_inset_border()
    {
        var s = Control("<input style='background:yellow' value='x'>");
        Assert.True(Square(s));
        Assert.Equal(BorderStyle.Inset, s.BorderTopStyle);
        Assert.Equal(1.5f, s.BorderTop);
        Assert.Equal(0xFF767676u, s.BorderTopColor);
    }

    [Fact]
    public void A_styled_button_gets_Chromes_outset_black_border()
    {
        var s = Control("<button style='background:yellow'>Send</button>");
        Assert.True(Square(s));
        Assert.Equal(BorderStyle.Outset, s.BorderLeftStyle);
        Assert.Equal(1.5f, s.BorderLeft);
        Assert.Equal(0xFF000000u, s.BorderLeftColor);
    }

    [Theory]
    [InlineData("<textarea style='border-color:red'>x</textarea>")]
    [InlineData("<select style='border:2px solid red'><option>x</option></select>")]
    public void Styled_text_areas_and_drop_downs_are_square(string html) => Assert.True(Square(Control(html)));

    [Fact]
    public void The_pages_own_radius_and_border_still_win()
    {
        var s = Control("<input style='border:3px dashed red;border-radius:6px' value='x'>");
        Assert.False(Square(s));
        Assert.Equal(BorderStyle.Dashed, s.BorderTopStyle);
        Assert.Equal(2.25f, s.BorderTop);
    }

    [Fact]
    public void A_styled_check_box_keeps_its_rounded_corners()
        => Assert.False(Square(Control("<input type='checkbox' style='background:yellow'>")));

    // ---- inset box-shadow ----

    [Fact]
    public void An_inset_spread_shadow_paints_inside_the_padding_box()
    {
        var html = Body + "<div style='width:100px;height:60px;background:#ff0;box-shadow:inset 0 0 0 10px #00f'></div>";
        Near(0, 0, 255, Pixel(html, 5, 30));
        Near(0, 0, 255, Pixel(html, 50, 55));
        Near(255, 255, 0, Pixel(html, 50, 30));
    }

    [Fact]
    public void An_offset_inset_shadow_lies_along_the_top_and_left_edges_inside_the_border()
    {
        var html = Body + "<div style='width:100px;height:60px;border:4px solid #000;box-shadow:inset 8px 8px 0 #f00'></div>";
        Near(0, 0, 0, Pixel(html, 2, 30));      // the border stays on top
        Near(255, 0, 0, Pixel(html, 8, 30));    // the shadow just inside the left border
        Near(255, 0, 0, Pixel(html, 50, 8));    // and the top border
        Near(255, 255, 255, Pixel(html, 100, 60));
    }

    [Fact]
    public void A_blurred_inset_shadow_fades_toward_the_middle()
    {
        var html = Body + "<div style='width:120px;height:120px;box-shadow:inset 0 0 20px #000'></div>";
        var edge = Pixel(html, 1, 60);
        var inner = Pixel(html, 15, 60);
        var middle = Pixel(html, 60, 60);
        Assert.True(edge.R < inner.R && inner.R < middle.R, $"{edge} {inner} {middle}");
        Near(255, 255, 255, middle, 4);
    }

    [Fact]
    public void A_translucent_inset_shadow_lets_the_background_show_through()
    {
        var html = Body + "<div style='width:100px;height:60px;background:#0f0;box-shadow:inset 0 0 0 10px rgba(255,0,0,.5)'></div>";
        Near(128, 128, 0, Pixel(html, 5, 30), 20);
    }

    // ---- blurred outer box-shadow ----

    [Fact]
    public void A_blurred_shadow_lets_the_background_show_through()
    {
        // Black at half alpha over blue darkens the blue; flattened on white it would be grey.
        var html = Body + "<div style='background:#0000ff;padding:40px'><div style='width:100px;height:60px;background:#fff;box-shadow:0 0 16px 8px rgba(0,0,0,.5)'></div></div>";
        var near = Pixel(html, 36, 70); // 4px outside the box, inside the spread
        Assert.True(near.R < 30 && near.G < 30 && near.B > 90 && near.B < 210, $"{near}");
        var far = Pixel(html, 8, 70); // past the blur: only the background
        Near(0, 0, 255, far, 16);
    }

    [Fact]
    public void A_blurred_shadow_over_an_image_keeps_the_image_visible()
    {
        string png = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(10, 10, 255, 255, 0));
        var html = Body + $"<div style='background:url({png}) 0 0 / 10px 10px;padding:40px'><div style='width:100px;height:60px;background:#fff;box-shadow:0 0 12px rgba(255,0,0,.6)'></div></div>";
        var p = Pixel(html, 39, 70);
        Assert.True(p.R > 230 && p.G > 60 && p.G < 210 && p.B < 30, $"red over yellow, got {p}");
    }

    [Fact]
    public void A_colour_filter_changes_a_blurred_shadows_colour_but_not_its_mask()
    {
        // invert(1) turns the black shadow white; the mask's grey levels stay, so the background around the shadow
        // stays blue instead of being painted over.
        var html = Body + "<div style='background:#0000ff;padding:40px'><div style='width:100px;height:60px;background:#fff;box-shadow:0 0 16px 8px #000;filter:invert(1)'></div></div>";
        var near = Pixel(html, 37, 70);
        Assert.True(near.R > 150 && near.G > 150 && near.B > 240, $"a white shadow, got {near}");
        Near(0, 0, 255, Pixel(html, 6, 70), 16);
        Near(0, 0, 255, Pixel(html, 90, 8), 16);
    }

    [Fact]
    public void A_blurred_shadow_split_by_a_page_break_is_masked_on_both_pages()
    {
        var html = "<style>@page { size: 300px 200px; margin: 0 }</style><body style='margin:0;background:#0000ff'><div style='height:150px'></div>"
            + "<div style='margin-left:40px;width:150px;height:100px;background:#fff;box-shadow:0 0 16px rgba(0,0,0,.8)'></div></body>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Compress = false, Accessibility = PdfAccessibility.Tagged });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        Assert.Equal(2, PdfTestHelpers.PageCount(pdf));
        var renderer = PdfRenderer.Open(pdf);
        foreach (var (page, y) in new[] { (0, 170), (1, 20) })
        {
            var img = renderer.RenderPage(page, new ImageOptions { Dpi = 96 });
            int i = (y * img.Width + 38) * 4; // just left of the box: the shadow over blue
            Assert.True(img.Rgba[i] < 20 && img.Rgba[i + 2] > 100 && img.Rgba[i + 2] < 215, $"page {page}: {img.Rgba[i]}, {img.Rgba[i + 1]}, {img.Rgba[i + 2]}");
        }
    }

    [Fact]
    public void A_sharp_shadow_is_written_as_before()
    {
        // Without a blur there is one band, still flattened on white: no alpha state is added for it.
        var pdf = HtmlToPdf.Convert(Body + "<div style='width:100px;height:60px;box-shadow:4px 4px 0 rgba(0,0,0,.5)'></div>", new PdfOptions { Compress = false });
        Assert.DoesNotContain("/ca ", PdfTestHelpers.Latin1(pdf));
    }

    [Fact]
    public void A_blurred_shadows_mask_is_an_isolated_group()
    {
        // Poppler drew a thin line along the faded outer edge of a non-isolated mask, over a tinted background.
        var pdf = HtmlToPdf.Convert(Body + "<div style='width:100px;height:60px;transform:scale(1.03);box-shadow:0 8px 20px rgba(37,99,235,.18)'></div>",
            new PdfOptions { Compress = false });
        Assert.Contains("/Group << /S /Transparency /CS /DeviceRGB /I true >>", PdfTestHelpers.Latin1(pdf));
    }

    // ---- text-overflow ----

    private static List<TextOp> TextOps(string html, string css = "")
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(css, sheet);
        new StyleResolver(sheet, 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        var list = new List<TextOp>();
        void Add(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                if (o is TextOp t) list.Add(t);
                if (o is GroupOp g) Add(g.Ops);
            }
        }
        Add(engine.Ops);
        return list;
    }

    private const string Cut = "width:120px;overflow:hidden;white-space:nowrap;";

    [Fact]
    public void Text_overflow_takes_a_custom_string()
    {
        var texts = TextOps($"<div style='{Cut}text-overflow:\" [more]\"'>The quick brown fox jumps over the lazy dog</div>");
        Assert.EndsWith(" [more]", texts[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(texts, t => t.Text.Contains("lazy"));
    }

    [Fact]
    public void Text_overflow_clip_cuts_without_a_mark()
    {
        var texts = TextOps($"<div style='{Cut}text-overflow:clip'>The quick brown fox jumps over the lazy dog</div>");
        Assert.DoesNotContain(texts, t => t.Text.Contains('\u2026'));
        var ellipsis = TextOps($"<div style='{Cut}text-overflow:ellipsis'>The quick brown fox jumps over the lazy dog</div>");
        Assert.EndsWith("\u2026", ellipsis[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_overflow_strings_decode_escapes_and_take_the_end_value()
    {
        var doc = HtmlParser.Parse("<div id=a style='text-overflow: clip \"\\2192\"'></div><div id=b style='text-overflow: ellipsis'></div>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var divs = Descendants(doc).Where(e => e.Name == "div").ToList();
        Assert.Equal("\u2192", divs[0].Style.TextOverflow);
        Assert.Equal("\u2026", divs[1].Style.TextOverflow);
    }

    // ---- calc() table widths ----

    [Fact]
    public void A_cell_width_mixing_a_percentage_with_a_length_is_auto_as_in_Chrome()
    {
        // Chrome sizes these cells as if they had no width: two cells with the same content share the table equally.
        var html = Body + "<table style='width:400px;border-spacing:0'><tr>"
                   + "<td style='width:calc(50% - 40px);padding:0;background:#f00'>a</td><td style='padding:0;background:#00f'>a</td></tr></table>";
        Near(255, 0, 0, Pixel(html, 195, 2));
        Near(0, 0, 255, Pixel(html, 205, 2));
    }

    // ---- ::first-letter ----

    [Fact]
    public void First_letter_styles_the_first_letter_only()
    {
        var texts = TextOps("<p>Once upon a time</p>", "p::first-letter { color: #a00; font-size: 30px }");
        var o = texts.First(t => t.Text.Trim().Length > 0);
        Assert.Equal("O", o.Text);
        Assert.Equal(0xFFAA0000u, o.Color);
        Assert.Equal(22.5f, o.Size);
        Assert.StartsWith("nce", texts[1].Text, StringComparison.Ordinal);
        Assert.NotEqual(0xFFAA0000u, texts[1].Color);
    }

    [Fact]
    public void First_letter_takes_the_punctuation_before_it_and_looks_inside_inline_and_block_children()
    {
        Assert.Equal("\"Q", TextOps("<p>\"Quoted\" text</p>", "p:first-letter { color: blue }").First().Text);
        var nested = TextOps("<div><p><b>Bold</b> text</p><p>Second</p></div>", "div::first-letter { color: green }");
        Assert.Equal("B", nested[0].Text);
        Assert.Equal(0xFF008000u, nested[0].Color);
        Assert.DoesNotContain(nested.Skip(1), t => t.Color == 0xFF008000u);
    }

    [Fact]
    public void A_floated_first_letter_is_a_drop_cap()
    {
        var texts = TextOps("<p style='width:300px'>Once upon a time there was a small library that turned HTML into PDF without help.</p>",
            "p::first-letter { float: left; font-size: 48px; line-height: 1 }");
        var cap = texts.First(t => t.Text == "O");
        var second = texts.Where(t => t.Text != "O").ToList();
        // The first two lines of text start to the right of the cap.
        Assert.True(second[0].X > cap.X + 20, $"{second[0].X} vs {cap.X}");
        var line2 = second.First(t => t.Baseline > second[0].Baseline + 1);
        Assert.True(line2.X > cap.X + 20);
    }

    [Fact]
    public void First_letter_is_put_back_before_styles_are_resolved_again()
    {
        var doc = HtmlParser.Parse("<p>Once upon a time</p>");
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet("p::first-letter { color: red }", sheet);
        new StyleResolver(sheet, 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        new StyleResolver(sheet, 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var p = Descendants(doc).First(e => e.Name == "p");
        Assert.Single(p.Children.OfType<Element>(), e => e.Name == "::first-letter");
        Assert.Equal("Once upon a time", string.Concat(Descendants(p).SelectMany(e => e.Children).Concat(p.Children).OfType<TextNode>().Select(t => t.Text)));
    }

    [Fact]
    public void There_is_no_first_letter_after_an_image_or_in_a_line_of_punctuation()
    {
        Assert.DoesNotContain(TextOps("<p><img src='x.png' width=10 height=10>Text</p>", "p::first-letter { color: red }"), t => t.Color == 0xFFFF0000u);
        Assert.DoesNotContain(TextOps("<p>... ok</p>", "p::first-letter { color: red }"), t => t.Color == 0xFFFF0000u);
    }

    [Theory]
    [InlineData("<p style='text-transform: uppercase'>Straße</p>", "STRASSE")]
    [InlineData("<p lang='tr' style='text-transform: uppercase'>istanbul</p>", "\u0130STANBUL")]
    [InlineData("<p lang='az-Latn' style='text-transform: capitalize'>istanbul</p>", "\u0130stanbul")]
    [InlineData("<p lang='tr' style='text-transform: lowercase'>ISPARTA</p>", "\u0131sparta")]
    [InlineData("<p lang='de' style='text-transform: uppercase'>istanbul</p>", "ISTANBUL")]
    public void Text_transform_uses_the_full_case_mapping_and_the_language(string html, string expected)
        => Assert.Equal(expected, string.Concat(TextOps(html).Select(t => t.Text)).Trim());

    // ---- ::first-line ----

    private const string Long = "The first line of this paragraph is styled while the rest of the text keeps the normal style it had.";

    [Fact]
    public void First_line_styles_only_the_text_on_the_first_line()
    {
        var texts = TextOps($"<p style='width:300px'>{Long}</p>", "p::first-line { color: #a00; text-transform: uppercase }");
        float first = texts[0].Baseline;
        Assert.All(texts.Where(t => t.Baseline == first && t.Text.Trim().Length > 0), t =>
        {
            Assert.Equal(0xFFAA0000u, t.Color);
            Assert.Equal(t.Text.ToUpperInvariant(), t.Text);
        });
        var rest = texts.Where(t => t.Baseline > first && t.Text.Trim().Length > 0).ToList();
        Assert.NotEmpty(rest);
        Assert.All(rest, t => Assert.NotEqual(0xFFAA0000u, t.Color));
        Assert.Contains(rest, t => t.Text != t.Text.ToUpperInvariant());
    }

    [Fact]
    public void First_line_keeps_what_inline_elements_set_themselves()
    {
        var texts = TextOps("<p style='width:600px'>Some <em style='color:blue'>blue</em> words</p>", "p::first-line { color: red }");
        Assert.Equal(0xFF0000FFu, texts.Single(t => t.Text.Contains("blue")).Color);
        Assert.Equal(0xFFFF0000u, texts.First(t => t.Text.Contains("Some")).Color);
    }

    [Fact]
    public void A_larger_first_line_is_broken_with_its_own_font_size()
    {
        var plain = TextOps($"<p style='width:300px'>{Long}</p>");
        var big = TextOps($"<p style='width:300px'>{Long}</p>", "p::first-line { font-size: 24px }");
        int Words(List<TextOp> t) => string.Join("", t.Where(o => o.Baseline == t[0].Baseline).Select(o => o.Text)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(Words(big) < Words(plain));
        Assert.Equal(18f, big[0].Size);
        // The second line is back to the paragraph's size.
        Assert.Equal(12f, big.First(t => t.Baseline > big[0].Baseline).Size);
    }

    [Fact]
    public void First_line_of_a_block_reaches_into_its_first_child_block_and_the_first_letter_inherits_it()
    {
        var texts = TextOps($"<div style='width:300px'><p>{Long}</p></div>", "div::first-line { color: green } div::first-letter { font-size: 2em }");
        Assert.Equal("T", texts[0].Text);
        Assert.Equal(0xFF008000u, texts[0].Color);
        Assert.Equal(24f, texts[0].Size);
        Assert.Equal(0xFF008000u, texts[1].Color);
    }

    // Small capitals cut aBא into A|Bא and bidi into aB|א, and hyphenation of a lower-case first line breaks a word the
    // usual items keep whole: the first line still ends where its own items say, and the next starts right after it.
    [Theory]
    [InlineData("width:120px", "lorem ipsum dolor sit amet consectetur adipiscing elit aBא", "font-variant: small-caps")]
    [InlineData("width:120px; overflow-wrap:anywhere", "loremipsumdolorsitametconsecteturadipiscingelit aBא lorem", "font-variant: small-caps")]
    [InlineData("width:120px; direction:rtl", "lorem <bdi>ipsum aBא</bdi> <span dir=rtl>dolor aBא</span> sit amet consectetur aBא", "font-variant: small-caps")]
    [InlineData("width:300px", "lorem aBא<br>ipsum aBא dolor", "font-variant: small-caps; letter-spacing: 2px; word-spacing: 5px")]
    [InlineData("width:150px; hyphens:auto", "Hyphenation Extraordinarily Complicated Hyphenation Extraordinarily", "text-transform: lowercase")]
    public void A_first_line_cut_into_other_pieces_keeps_its_style_and_all_the_text(string style, string html, string firstLine)
    {
        var texts = TextOps($"<p lang=en style='{style}'>{html}</p>", $"p::first-line {{ {firstLine}; color: red }}");
        float first = texts.Min(t => t.Baseline);
        Assert.All(texts.Where(t => t.Baseline == first), t => Assert.Equal(0xFFFF0000u, t.Color));
        Assert.Contains(texts, t => t.Baseline > first);
        Assert.All(texts.Where(t => t.Baseline > first), t => Assert.NotEqual(0xFFFF0000u, t.Color));
        // Nothing is lost or drawn twice where the first line's items give way to the usual ones.
        static string Letters(string s) => string.Concat(s.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-').Order());
        Assert.Equal(Letters(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", "")), Letters(string.Concat(texts.Select(t => t.Text))));
    }
}
