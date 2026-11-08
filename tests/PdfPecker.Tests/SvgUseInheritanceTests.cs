namespace PdfPecker.Tests;

/// <summary>
/// What a use element draws inherits from the use element, as its copy in the use element's shadow tree does, not
/// from where it is defined. MathJax draws every glyph of a formula with use, under a group painted currentColor.
/// </summary>
public class SvgUseInheritanceTests
{
    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    /// <summary>The RGB of a pixel of the first page rendered at 72 dpi (1 px = 1 pt).</summary>
    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual)
        => Assert.True(Math.Abs(actual.R - r) < 40 && Math.Abs(actual.G - g) < 40 && Math.Abs(actual.B - b) < 40,
            $"Expected ({r}, {g}, {b}), got {actual}");

    // An SVG of 400 × 400 px drawn in 300 × 300 pt; At takes user units.
    private static byte[] Svg(string content, string style = "")
        => HtmlToPdf.Convert($"<body style='margin:0;{style}'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    [Fact]
    public void A_shape_without_paint_takes_the_fill_of_the_use_element()
    {
        var pdf = Svg("<defs><rect id='r' width='100' height='100'/></defs><g fill='#f00'><use href='#r'/></g><use href='#r' x='200' fill='#00f'/>");
        Color(255, 0, 0, At(pdf, 50, 50));
        Color(0, 0, 255, At(pdf, 250, 50));
    }

    [Fact]
    public void Current_color_reaches_glyphs_drawn_by_use()
    {
        // The shape of MathJax's SVG output: glyph paths in defs, drawn by use elements under a currentColor group.
        var pdf = Svg("<defs><path id='g' d='M0 0H100V100H0Z'/></defs><g fill='currentColor' stroke='currentColor' stroke-width='0'>"
            + "<g transform='translate(150,0)'><use xlink:href='#g'/></g></g>", "color:#0a0");
        Color(0, 170, 0, At(pdf, 200, 50));
    }

    [Fact]
    public void The_paint_where_a_shape_is_defined_does_not_reach_its_use()
    {
        var pdf = Svg("<defs fill='#f00'><g fill='#0f0'><rect id='r' width='100' height='100'/></g></defs><use href='#r' fill='#00f'/>");
        Color(0, 0, 255, At(pdf, 50, 50));
    }

    [Fact]
    public void A_shape_keeps_its_own_fill_and_its_children_theirs()
    {
        var pdf = Svg("<defs><rect id='own' width='100' height='100' fill='#0a0'/>"
            + "<g id='group' fill='#f00'><rect width='100' height='100'/></g>"
            + "<symbol id='s'><rect width='100' height='100'/></symbol></defs>"
            + "<use href='#own' fill='#00f'/><use href='#group' x='150' fill='#00f'/><use href='#s' y='150' width='100' height='100' fill='#00f'/>");
        Color(0, 170, 0, At(pdf, 50, 50));
        Color(255, 0, 0, At(pdf, 200, 50));
        Color(0, 0, 255, At(pdf, 50, 200));
    }

    [Fact]
    public void A_use_inside_a_used_group_inherits_from_its_own_use()
    {
        var pdf = Svg("<defs><rect id='r' width='100' height='100'/><g id='twice'><use href='#r'/><use href='#r' x='150' fill='#0a0'/></g></defs>"
            + "<use href='#twice' fill='#00f'/>");
        Color(0, 0, 255, At(pdf, 50, 50));
        Color(0, 170, 0, At(pdf, 200, 50));
    }

    [Fact]
    public void Current_color_in_a_sprite_symbol_is_the_colour_where_it_is_used()
    {
        // The icon-sprite pattern: a hidden sprite of symbols painted currentColor, used in text of different colours.
        var pdf = HtmlToPdf.Convert("<body style='margin:0'>"
            + "<svg style='display:none'><symbol id='i' viewBox='0 0 10 10'><path fill='currentColor' d='M0 0H10V10H0Z'/></symbol></svg>"
            + "<div style='color:#f00'><svg width='40' height='40' style='display:block'><use href='#i'/></svg></div>"
            + "<div style='color:#00f'><svg width='40' height='40' style='display:block'><use href='#i'/></svg></div></body>", NoMargins);
        Color(255, 0, 0, Pixel(pdf, 15, 15));
        Color(0, 0, 255, Pixel(pdf, 15, 45));
    }

    [Fact]
    public void Every_way_of_setting_the_colour_of_a_use_reaches_current_color()
    {
        var pdf = Svg("<defs><symbol id='s' viewBox='0 0 10 10'><rect width='10' height='10' fill='currentColor'/></symbol>"
            + "<rect id='r' width='50' height='50' fill='currentColor'/><rect id='p' width='50' height='50'/></defs>"
            + "<use href='#s' width='50' height='50' style='color:#f00'/>"
            + "<use href='#s' x='60' width='50' height='50' color='#00f'/>"
            + "<g style='color:#0a0'><use href='#s' x='120' width='50' height='50'/></g>"
            + "<use href='#r' x='180' style='color:#f80'/>"
            + "<use href='#p' x='240' style='fill:currentColor;color:#808'/>", "color:#888");
        Color(255, 0, 0, At(pdf, 25, 25));
        Color(0, 0, 255, At(pdf, 85, 25));
        Color(0, 170, 0, At(pdf, 145, 25));
        Color(255, 136, 0, At(pdf, 205, 25));
        Color(136, 0, 136, At(pdf, 265, 25));
    }

    [Fact]
    public void A_used_shape_that_sets_its_own_colour_keeps_it()
    {
        var pdf = Svg("<defs><symbol id='s' viewBox='0 0 10 10'><g color='#0a0'><rect width='10' height='10' fill='currentColor'/></g>"
            + "<rect width='5' height='5' fill='none' stroke='currentColor' stroke-width='2'/></symbol></defs>"
            + "<use href='#s' width='100' height='100' style='color:#f00'/>");
        Color(0, 170, 0, At(pdf, 75, 75));
        Color(255, 0, 0, At(pdf, 50, 25));
    }

    [Fact]
    public void Current_color_is_inherited_as_the_keyword()
    {
        // fill: currentColor on the svg is resolved where each shape is drawn, against that shape's own colour.
        var html = HtmlToPdf.Convert("<body style='margin:0;color:#00f'><svg width='400' height='400' fill='currentColor' style='display:block'>"
            + "<g style='color:#0a0'><rect width='100' height='100'/></g><rect x='150' width='100' height='100'/></svg></body>", NoMargins);
        Color(0, 170, 0, At(html, 50, 50));
        Color(0, 0, 255, At(html, 200, 50));
    }

    [Fact]
    public void Text_drawn_by_use_inherits_colour_paint_and_font_from_the_use_element()
    {
        // Text and its tspans go through the use element's inheritance as shapes do. Chrome draws the four lines blue,
        // green, orange and bold purple at 40px.
        var pdf = Svg("<defs><g id='t'><text x='10' y='40' font-size='30' fill='currentColor'>Hello<tspan>World</tspan></text></g>"
            + "<symbol id='sy'><text x='10' y='40' font-size='30' fill='currentColor'>Sym<tspan fill='currentColor'>bol</tspan></text></symbol>"
            + "<text id='plain' x='10' y='40' font-size='30'>Plain</text><g id='fs'><text x='10' y='40'>Font</text></g></defs>"
            + "<use href='#t' style='color:#00f'/><use href='#sy' y='60' color='#080'/><use href='#plain' y='120' fill='#f80'/>"
            + "<use href='#fs' y='200' fill='#808' font-size='40' font-weight='bold'/>", "color:#f00");
        var content = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("1 0 0 rg", content);
        Assert.Matches(@"0 0 1 rg\s+BT /F\d+ 30 Tf[^\n]*\(Hello\) Tj ET\s+BT[^\n]*orld", content);
        Assert.Matches(@"0 0\.533 0 rg\s+BT[^\n]*\(Sym\) Tj ET\s+BT[^\n]*\(bol\) Tj", content);
        Assert.Matches(@"1 0\.533 0 rg\s+BT[^\n]*\(Plain\)", content);
        Assert.Matches(@"0\.533 0 0\.533 rg\s+BT /F\d+ 40 Tf[^\n]*\(Font\)", content);
        // The bold word is in a font of its own.
        var regular = System.Text.RegularExpressions.Regex.Match(content, @"/(F\d+) 30 Tf[^\n]*\(Plain\)").Groups[1].Value;
        var bold = System.Text.RegularExpressions.Regex.Match(content, @"/(F\d+) 40 Tf[^\n]*\(Font\)").Groups[1].Value;
        Assert.NotEqual(regular, bold);
    }

    [Fact]
    public void Text_drawn_by_use_keeps_what_it_sets_itself()
    {
        var pdf = Svg("<defs><text id='t' x='10' y='40' font-size='20' fill='#0a0'>Own<tspan font-size='30' fill='#00f'>Span</tspan></text></defs>"
            + "<use href='#t' fill='#f00' font-size='50'/>");
        var content = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("1 0 0 rg", content);
        Assert.Matches(@"0 0\.667 0 rg\s+BT /F\d+ 20 Tf[^\n]*\(Own\)", content);
        Assert.Matches(@"0 0 1 rg\s+BT /F\d+ 30 Tf[^\n]*\(Span\)", content);
    }
}
