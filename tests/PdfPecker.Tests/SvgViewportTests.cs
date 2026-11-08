namespace PdfPecker.Tests;

/// <summary>Lengths with units in SVG attributes, and the viewport of a nested svg element.</summary>
public class SvgViewportTests
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
    private static byte[] Svg(string content)
        => HtmlToPdf.Convert($"<body style='margin:0'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    private static readonly (int, int, int) White = (255, 255, 255);

    [Fact]
    public void Geometry_attributes_take_absolute_and_font_units()
    {
        // 1in is 96 px, 1cm about 37.8 px, 72pt 96 px, 6pc 96 px and 2em 32 px at the default 16px.
        var pdf = Svg("<rect x='1cm' y='0' width='1in' height='72pt' fill='#f00'/>"
            + "<circle cx='200' cy='6pc' r='2em' fill='#00f'/>"
            + "<line x1='0' y1='300' x2='25.4mm' y2='300' stroke='#0a0' stroke-width='10'/>");
        Color(255, 0, 0, At(pdf, 40, 5));
        Color(255, 0, 0, At(pdf, 130, 90));
        Assert.Equal(White, At(pdf, 136, 50));
        Color(0, 0, 255, At(pdf, 200, 125));
        Assert.Equal(White, At(pdf, 200, 132));
        Color(0, 170, 0, At(pdf, 90, 300));
        Assert.Equal(White, At(pdf, 100, 300));
    }

    [Fact]
    public void A_path_length_with_a_unit_is_not_a_length()
    {
        // pathLength is a plain number: with a unit it is invalid and left out, so the dashes are in user units.
        var pdf = Svg("<line x1='0' y1='10' x2='400' y2='10' stroke='#000' stroke-width='10' stroke-dasharray='100' pathLength='4mm'/>");
        Color(0, 0, 0, At(pdf, 50, 10));
        Assert.Equal(White, At(pdf, 150, 10));
    }

    [Fact]
    public void A_nested_svg_with_units_draws_at_its_size()
    {
        var pdf = Svg("<svg x='0' y='0' width='1in' height='0.5in' viewBox='0 0 2 1'><rect width='2' height='1' fill='#088'/></svg>");
        Color(0, 136, 136, At(pdf, 90, 40));
        Assert.Equal(White, At(pdf, 100, 40));
    }

    [Fact]
    public void A_nested_svg_aligns_its_view_box_as_preserve_aspect_ratio_says()
    {
        var pdf = Svg("<svg width='100' height='50' viewBox='0 0 10 10'><rect width='10' height='10' fill='#f00'/></svg>"
            + "<svg x='150' width='100' height='50' viewBox='0 0 10 10' preserveAspectRatio='none'><rect width='10' height='10' fill='#00f'/></svg>"
            + "<svg y='100' width='100' height='50' viewBox='0 0 10 10' preserveAspectRatio='xMaxYMin meet'><rect width='10' height='10' fill='#0a0'/></svg>");
        // Centred by default, stretched with none, at the right with xMax.
        Assert.Equal(White, At(pdf, 10, 25));
        Color(255, 0, 0, At(pdf, 50, 25));
        Color(0, 0, 255, At(pdf, 160, 25));
        Color(0, 0, 255, At(pdf, 240, 25));
        Assert.Equal(White, At(pdf, 40, 125));
        Color(0, 170, 0, At(pdf, 90, 125));
    }

    [Fact]
    public void A_nested_svg_clips_to_its_viewport_unless_its_overflow_is_visible()
    {
        var pdf = Svg("<svg width='100' height='100' viewBox='0 0 10 10' preserveAspectRatio='xMinYMin slice'><rect width='10' height='30' fill='#f00'/></svg>"
            + "<svg x='200' width='100' height='100' overflow='visible'><rect width='100' height='200' fill='#00f'/></svg>"
            + "<svg x='300' width='50' height='100' style='overflow: visible'><rect width='100' height='100' fill='#0a0'/></svg>");
        Color(255, 0, 0, At(pdf, 50, 90));
        Assert.Equal(White, At(pdf, 50, 150));
        Color(0, 0, 255, At(pdf, 250, 150));
        Color(0, 170, 0, At(pdf, 375, 50));
    }

    [Fact]
    public void A_nested_svg_takes_percentages_of_the_viewport_and_fills_it_without_a_size()
    {
        var pdf = Svg("<svg x='50%' y='25%' width='25%' height='25%' viewBox='0 0 10 10'><rect width='10' height='10' fill='#f00'/></svg>"
            + "<svg viewBox='0 0 400 400'><rect x='0' y='350' width='50' height='50' fill='#00f'/></svg>");
        Color(255, 0, 0, At(pdf, 250, 150));
        Assert.Equal(White, At(pdf, 190, 150));
        Assert.Equal(White, At(pdf, 310, 150));
        Color(0, 0, 255, At(pdf, 25, 375));
    }

    [Fact]
    public void Css_overflow_auto_is_visible_in_svg_and_scroll_clips()
    {
        // In SVG, auto means visible, which Chrome follows for the property as for the attribute. A marker takes its
        // overflow from CSS too.
        var pdf = Svg("<svg x='0' y='0' width='100' height='100' style='overflow:auto'><rect width='150' height='150' fill='#f00'/></svg>"
            + "<svg x='200' y='0' width='100' height='100' style='overflow:scroll'><rect width='150' height='150' fill='#00f'/></svg>"
            + "<svg x='0' y='200' width='100' height='100' style='overflow:hidden auto'><rect width='150' height='150' fill='#0a0'/></svg>"
            + "<marker id='m' markerWidth='10' markerHeight='10' style='overflow:visible'><rect width='30' height='30' fill='#808'/></marker>"
            + "<marker id='h' markerWidth='10' markerHeight='10'><rect width='30' height='30' fill='#f80'/></marker>"
            + "<path d='M200 200 L210 200' stroke='#000' marker-start='url(#m)'/><path d='M300 200 L310 200' stroke='#000' marker-start='url(#h)'/>");
        Color(255, 0, 0, At(pdf, 125, 125));
        Color(0, 0, 255, At(pdf, 250, 50));
        Assert.Equal(White, At(pdf, 325, 125));
        // overflow-x decides, as for the attribute's single value.
        Assert.Equal(White, At(pdf, 125, 225));
        Color(136, 0, 136, At(pdf, 225, 225));
        Color(255, 136, 0, At(pdf, 305, 205));
        Assert.Equal(White, At(pdf, 325, 225));
    }

    [Fact]
    public void Text_positions_take_units_and_percentages()
    {
        // 1in is 96 px, 2cm about 75.6 px, 10% of the height 40 px and 10em at 20px 200 px; Chrome places them alike.
        var pdf = Svg("<text x='1in' y='1in' font-size='20'>Inch</text>"
            + "<text x='10' y='250' dx='1in' font-size='20'>Dx<tspan dy='0.5in'>Dy</tspan></text>"
            + "<text x='2cm' y='10%' font-size='20'>Cm</text>"
            + "<text x='10' y='350' font-size='20'>em<tspan x='10em' dy='12pt'>ten</tspan></text>");
        var content = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("1 0 0 -1 96 96 Tm (Inch)", content);
        Assert.Contains("1 0 0 -1 106 250 Tm (Dx)", content);
        Assert.Matches(@"1 0 0 -1 [\d.]+ 298 Tm \(Dy\)", content);
        Assert.Contains("1 0 0 -1 75.591 40 Tm (Cm)", content);
        Assert.Contains("1 0 0 -1 200 366 Tm (ten)", content);
    }
}
