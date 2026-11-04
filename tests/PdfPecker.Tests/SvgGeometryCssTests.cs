namespace PdfPecker.Tests;

/// <summary>
/// SVG geometry properties (cx, cy, r, rx, ry, x, y, width, height, d) and the gradient stop properties, set from
/// style sheets: a rule wins over the attribute, as in Chrome, in inline SVG and in an SVG image with its own style.
/// </summary>
public class SvgGeometryCssTests
{
    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

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
    private static byte[] Svg(string style, string content)
        => HtmlToPdf.Convert($"<style>{style}</style><body style='margin:0'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    private static readonly (int, int, int) White = (255, 255, 255);

    [Fact]
    public void A_rule_sets_the_circle_geometry_over_the_attributes()
    {
        var pdf = Svg("circle.dot { r: 60px; cx: 200px; cy: 100px }", "<circle class='dot' r='5' cx='10' cy='10' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 200, 100));
        Color(255, 0, 0, At(pdf, 200, 150));
        Color(255, 255, 255, At(pdf, 10, 10));
    }

    [Fact]
    public void Plain_numbers_percentages_and_em_units_are_lengths()
    {
        var pdf = Svg("#a { r: 40; cx: 60; cy: 60 } #b { r: 10%; cx: 50%; cy: 50%; font-size: 10px } #c { r: 2em; cx: 100px; cy: 300px; font-size: 20px }",
            "<circle id='a' fill='#f00'/><circle id='b' fill='#0a0'/><circle id='c' fill='#00f'/>");
        Color(255, 0, 0, At(pdf, 60, 90));
        // 10% of the diagonal, which is 400 here, around the centre.
        Color(0, 170, 0, At(pdf, 200, 230));
        Color(0, 0, 255, At(pdf, 100, 335));
    }

    [Fact]
    public void A_negative_radius_is_ignored_and_the_attribute_stays()
    {
        var pdf = Svg("circle { r: -20px }", "<circle r='50' cx='100' cy='100' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 100, 140));
    }

    [Fact]
    public void Auto_takes_the_other_radius_of_a_rect()
    {
        var pdf = Svg("rect { rx: 100px; ry: auto }", "<rect width='200' height='200' rx='0' fill='#f00'/>");
        // A fully rounded square is a circle: its corner is empty and its middle is not.
        Color(255, 255, 255, At(pdf, 4, 4));
        Color(255, 0, 0, At(pdf, 100, 100));
    }

    [Fact]
    public void A_rule_sets_the_position_and_the_size_of_a_rect()
    {
        var pdf = Svg("rect { x: 100px; y: 50px; width: 80px; height: 40px }", "<rect width='10' height='10' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 170, 80));
        Color(255, 255, 255, At(pdf, 5, 5));
    }

    [Fact]
    public void A_rule_sets_the_position_of_a_use()
    {
        var pdf = Svg("use { x: 200px; y: 200px }", "<defs><rect id='r' width='50' height='50' fill='#f00'/></defs><use href='#r'/>");
        Color(255, 0, 0, At(pdf, 220, 220));
        Color(255, 255, 255, At(pdf, 20, 20));
    }

    [Fact]
    public void A_rule_sets_the_path_data()
    {
        var pdf = Svg("path { d: path('M20 20 H220 V220 H20 Z') }", "<path d='M0 0 L5 5' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 120, 120));
    }

    [Fact]
    public void D_none_draws_nothing_and_an_invalid_value_leaves_the_attribute()
    {
        Color(255, 255, 255, At(Svg("path { d: none }", "<path d='M0 0 H200 V200 H0Z' fill='#f00'/>"), 100, 100));
        Color(255, 0, 0, At(Svg("path { d: 'M0 0 H200 V200 H0Z' }", "<path d='M0 0 H200 V200 H0Z' fill='#f00'/>"), 100, 100));
        Color(255, 0, 0, At(Svg("path { d: path(evenodd, 'M0 0 H5 V5 Z') }", "<path d='M0 0 H200 V200 H0Z' fill='#f00'/>"), 100, 100));
    }

    [Fact]
    public void Path_data_from_a_rule_keeps_the_parser_limits()
    {
        // Damaged data makes the declaration invalid, so nothing is drawn here, and it cannot make conversion fail.
        var pdf = Svg("path { d: path('M20 20 H220 V220 H20 Z L x y garbage') }", "<path fill='#f00'/>");
        Color(255, 255, 255, At(pdf, 120, 120));
        var many = Svg("path { d: path('" + string.Concat(Enumerable.Repeat("M0 0 L1 1 ", 2000)) + "') }", "<path fill='#f00'/>");
        Assert.NotEmpty(many);
    }

    [Fact]
    public void Gradient_stops_take_their_colour_and_opacity_from_rules()
    {
        var pdf = Svg(".a { stop-color: #f00 } .b { stop-color: #f00; stop-opacity: 0 }",
            "<linearGradient id='g'><stop class='a' offset='0'/><stop class='b' offset='1'/></linearGradient><rect width='400' height='400' fill='url(#g)'/>");
        Color(255, 0, 0, At(pdf, 4, 200));
        // The right end is transparent red over white.
        Color(255, 255, 255, At(pdf, 396, 200));
    }

    [Fact]
    public void A_stop_rule_wins_over_the_attribute()
    {
        var pdf = Svg("stop { stop-color: #00f }",
            "<linearGradient id='g'><stop offset='0' stop-color='#f00'/><stop offset='1' stop-color='#f00'/></linearGradient><rect width='400' height='400' fill='url(#g)'/>");
        Color(0, 0, 255, At(pdf, 200, 200));
    }

    [Fact]
    public void A_stop_colour_may_be_current_colour_and_a_variable()
    {
        var pdf = Svg(":root { --c: #0a0 } stop { stop-color: var(--c) } .k { stop-color: currentColor; color: #f00 }",
            "<linearGradient id='g'><stop offset='0'/><stop offset='1'/></linearGradient><rect width='200' height='400' fill='url(#g)'/>" +
            "<linearGradient id='h'><stop class='k' offset='0'/><stop class='k' offset='1'/></linearGradient><rect x='200' width='200' height='400' fill='url(#h)'/>");
        Color(0, 170, 0, At(pdf, 100, 200));
        Color(255, 0, 0, At(pdf, 300, 200));
    }

    [Fact]
    public void An_svg_image_with_its_own_style_takes_them_too()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'><style>.s{stop-color:#00f}circle{r:40px;cx:50px;cy:50px;fill:url(%23g)}</style>" +
                           "<linearGradient id='g'><stop class='s' offset='0'/><stop class='s' offset='1'/></linearGradient><circle r='1'/></svg>";
        var pdf = HtmlToPdf.Convert($"<body style='margin:0'><img width='100' height='100' style='display:block' src=\"data:image/svg+xml;utf8,{svg}\"></body>", NoMargins);
        Color(0, 0, 255, Pixel(pdf, 37, 37));
        Assert.Equal(White, Pixel(pdf, 2, 2));
    }

    [Fact]
    public void A_negative_percentage_radius_is_invalid_like_a_negative_length()
    {
        var pdf = Svg("circle { r: -10% }", "<circle r='50' cx='100' cy='100' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 100, 140));
    }

    [Fact]
    public void A_path_with_an_error_is_an_invalid_declaration_and_the_attribute_stays()
    {
        var pdf = Svg("path { d: path('M10 10 L') }", "<path d='M0 0 H200 V200 H0Z' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 100, 100));
    }

    [Fact]
    public void Initial_and_unset_give_the_initial_value_instead_of_the_attribute()
    {
        // cx and cy go back to 0, so the circle sits in the corner and the attribute's place stays empty.
        var pdf = Svg("circle { cx: initial; cy: unset }", "<circle r='50' cx='200' cy='200' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 10, 10));
        Color(255, 255, 255, At(pdf, 200, 200));
        var stops = Svg("stop { stop-color: initial }",
            "<linearGradient id='g'><stop offset='0' stop-color='#f00'/><stop offset='1' stop-color='#f00'/></linearGradient><rect width='400' height='400' fill='url(#g)'/>");
        Color(0, 0, 0, At(stops, 200, 200));
        var unset = Svg("stop { stop-color: unset }",
            "<linearGradient id='g'><stop offset='0' stop-color='#f00'/><stop offset='1' stop-color='#f00'/></linearGradient><rect width='400' height='400' fill='url(#g)'/>");
        Color(0, 0, 0, At(unset, 200, 200));
    }

    [Fact]
    public void Inherit_takes_the_value_of_the_parent()
    {
        var pdf = Svg("g { cx: 100px; cy: 100px } circle { cx: inherit; cy: inherit; r: 30px }", "<g><circle cx='300' cy='300' fill='#f00'/></g>");
        Color(255, 0, 0, At(pdf, 100, 100));
        Color(255, 255, 255, At(pdf, 300, 300));
        var stops = Svg("linearGradient { stop-color: #00f } stop { stop-color: inherit }",
            "<linearGradient id='g'><stop offset='0'/><stop offset='1'/></linearGradient><rect width='400' height='400' fill='url(#g)'/>");
        Color(0, 0, 255, At(stops, 200, 200));
    }

    [Fact]
    public void A_calc_that_comes_out_negative_is_a_radius_of_zero()
    {
        var pdf = Svg("circle { r: calc(10px - 15px) }", "<circle r='50' cx='100' cy='100' fill='#f00'/>");
        Color(255, 255, 255, At(pdf, 100, 100));
    }

    [Fact]
    public void Supports_knows_the_properties()
    {
        var pdf = HtmlToPdf.Convert("<style>@supports (stop-color: red) and (cx: 5px) and (d: none) { p { color: #f00 } }</style><p>x</p>", NoMargins);
        Assert.Contains("1 0 0 rg", System.Text.Encoding.Latin1.GetString(pdf));
    }
}
