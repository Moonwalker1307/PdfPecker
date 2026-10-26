using PdfPecker.Css;

namespace PdfPecker.Tests;

/// <summary>
/// The old -webkit- syntax that Chrome still takes: prefixed property names, the legacy flexible box and the legacy
/// gradients. Each draws what its standard counterpart draws.
/// </summary>
public class PrefixedCssTests
{
    private const string Head = "<!doctype html><style>@page{size:200px 200px;margin:0} body{margin:0;font:12px 'Liberation Sans'}"
                                + " .b{width:40px;height:30px;background:#e80} .c{background:#48c}</style>";

    private static byte[] Pixels(string body)
        => PdfRenderer.Open(HtmlToPdf.Convert(Head + body)).RenderPage(0, new ImageOptions { Dpi = 96 }).Rgba;

    /// <summary>Asserts that two pages render alike, within <paramref name="tolerance"/> levels per channel.</summary>
    private static void SameAs(string expected, string actual, int tolerance = 0)
    {
        var a = Pixels(expected);
        var b = Pixels(actual);
        Assert.Equal(a.Length, b.Length);
        int worst = 0;
        for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        Assert.True(worst <= tolerance, $"pixels differ by up to {worst}");
        // A page that draws nothing would pass too, so make sure there is something on it.
        Assert.Contains(a, v => v != 255);
    }

    [Theory]
    [InlineData("transform:rotate(30deg) translate(20px,10px);margin:20px", "-webkit-transform:rotate(30deg) translate(20px,10px);margin:20px")]
    [InlineData("border-radius:15px 5px;width:80px", "-webkit-border-radius:15px 5px;width:80px")]
    [InlineData("box-shadow:6px 6px 0 #48c;margin:10px", "-webkit-box-shadow:6px 6px 0 #48c;margin:10px")]
    [InlineData("filter:grayscale(1);margin:10px", "-webkit-filter:grayscale(1);margin:10px")]
    [InlineData("clip-path:circle(15px)", "-webkit-clip-path:circle(15px)")]
    [InlineData("box-sizing:border-box;padding:10px;border:5px solid #48c", "-webkit-box-sizing:border-box;padding:10px;border:5px solid #48c")]
    [InlineData("margin-inline-start:30px;padding-block-start:10px", "-webkit-margin-start:30px;-webkit-padding-before:10px")]
    [InlineData("border-block-start:3px solid red;border-inline-end:6px solid blue;inline-size:80px",
        "-webkit-border-before:3px solid red;-webkit-border-end:6px solid blue;-webkit-logical-width:80px")]
    public void A_webkit_property_name_draws_like_the_standard_one(string standard, string prefixed)
        => SameAs($"<div class=b style='{standard}'></div>", $"<div class=b style='{prefixed}'></div>");

    [Fact]
    public void Webkit_flex_and_its_properties_lay_out_a_flex_container()
        => SameAs("<div style='display:flex;justify-content:space-between;align-items:flex-end;height:60px;flex-flow:row wrap'>"
                  + "<div class=b style='flex:1'></div><div class='b c' style='order:-1;align-self:center'></div></div>",
            "<div style='display:-webkit-flex;-webkit-justify-content:space-between;-webkit-align-items:flex-end;height:60px;-webkit-flex-flow:row wrap'>"
            + "<div class=b style='-webkit-flex:1'></div><div class='b c' style='-webkit-order:-1;-webkit-align-self:center'></div></div>");

    [Fact]
    public void Webkit_columns_and_column_breaks_lay_out_columns()
        => SameAs("<div style='columns:2;column-gap:10px;column-rule:2px solid red'>One two three<div style='break-before:column'>four</div></div>",
            "<div style='-webkit-columns:2;-webkit-column-gap:10px;-webkit-column-rule:2px solid red'>One two three<div style='-webkit-column-break-before:always'>four</div></div>");

    [Fact]
    public void A_webkit_name_and_the_standard_name_are_one_property_in_the_cascade()
    {
        SameAs("<div class=b></div>", "<div class=b style='transform:rotate(45deg);-webkit-transform:none'></div>");
        SameAs("<div class=b style='border-radius:9px'></div>", "<div class=b style='-webkit-border-radius:2px;border-radius:9px'></div>");
    }

    [Fact]
    public void Supports_knows_the_webkit_names_and_the_legacy_box()
    {
        Assert.True(CssSupports.Evaluate("(-webkit-transform: rotate(1deg))"));
        Assert.True(CssSupports.Evaluate("(display: -webkit-box)"));
        Assert.True(CssSupports.Evaluate("(-webkit-box-orient: vertical)"));
        Assert.False(CssSupports.Evaluate("(-webkit-text-decorations-in-effect: none)"));
    }

    [Fact]
    public void The_legacy_box_lays_out_as_a_flex_container_from_its_box_properties()
    {
        SameAs("<div style='display:flex;justify-content:space-between;align-items:center;height:60px;width:150px'><div class=b></div><div class='b c'></div></div>",
            "<div style='display:-webkit-box;-webkit-box-pack:justify;-webkit-box-align:center;height:60px;width:150px'><div class=b></div><div class='b c'></div></div>");
        SameAs("<div style='display:flex;flex-direction:column-reverse;justify-content:flex-end;height:100px'><div class=b></div><div class='b c'></div></div>",
            "<div style='display:-webkit-box;-webkit-box-orient:vertical;-webkit-box-direction:reverse;-webkit-box-pack:end;height:100px'><div class=b></div><div class='b c'></div></div>");
    }

    [Fact]
    public void Legacy_box_items_flex_and_order_by_their_box_properties_alone()
    {
        SameAs("<div style='display:flex;width:150px'><div class=b style='flex:1 1 auto'></div><div class='b c' style='flex:2 2 auto'></div></div>",
            "<div style='display:-webkit-box;width:150px'><div class=b style='-webkit-box-flex:1;flex-grow:0'></div><div class='b c' style='-webkit-box-flex:2'></div></div>");
        // An ordinal group below 1 is invalid, so the last item stays in group 1 and comes first.
        SameAs("<div style='display:flex;width:150px'><div class=b style='width:10px'></div><div class='b c'></div><div class=b></div></div>",
            "<div style='display:-webkit-box;width:150px;justify-content:flex-end'><div class=b style='-webkit-box-ordinal-group:3'></div><div class='b c' style='-webkit-box-ordinal-group:2'></div>"
            + "<div class=b style='width:10px;-webkit-box-ordinal-group:0;order:-5'></div></div>");
    }

    [Fact]
    public void The_line_clamp_idiom_still_clamps_as_a_block()
    {
        var pdf = HtmlToPdf.Convert(Head + "<div style='display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;overflow:hidden;width:120px'>"
                                    + "One two three four five six seven eight nine ten eleven twelve thirteen fourteen</div>");
        var lines = PdfText.ExtractPages(pdf)[0].Lines;
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("…", lines[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_and_inline_elements_in_a_legacy_box_share_one_item()
    {
        var pdf = HtmlToPdf.Convert(Head + "<div style='display:-webkit-box'>Text <b>bold</b> end<div class=b></div></div>");
        Assert.Equal("Text bold end", PdfText.ExtractPages(pdf)[0].Lines[0].Text);
    }

    [Theory]
    [InlineData("-webkit-linear-gradient(top, red, blue)", "linear-gradient(to bottom, red, blue)")]
    [InlineData("-webkit-linear-gradient(left, red, yellow 30%, blue)", "linear-gradient(to right, red, yellow 30%, blue)")]
    [InlineData("-webkit-linear-gradient(30deg, red, blue)", "linear-gradient(60deg, red, blue)")]
    [InlineData("-webkit-linear-gradient(red, blue)", "linear-gradient(red, blue)")]
    [InlineData("-webkit-repeating-linear-gradient(left, red, red 10px, blue 10px, blue 20px)", "repeating-linear-gradient(to right, red, red 10px, blue 10px, blue 20px)")]
    [InlineData("-webkit-radial-gradient(30% 40%, circle cover, red, blue)", "radial-gradient(circle farthest-corner at 30% 40%, red, blue)")]
    [InlineData("-webkit-radial-gradient(20% 30%, ellipse contain, red, blue)", "radial-gradient(closest-side at 20% 30%, red, blue)")]
    [InlineData("-webkit-radial-gradient(circle, red, blue)", "radial-gradient(circle, red, blue)")]
    [InlineData("-webkit-radial-gradient(center, 60px 30px, red, blue)", "radial-gradient(60px 30px, red, blue)")]
    [InlineData("-webkit-gradient(linear, left top, left bottom, from(red), to(blue))", "linear-gradient(red, blue)")]
    [InlineData("-webkit-gradient(linear, 0 0, 100% 0, from(red), color-stop(80%, blue), color-stop(0.2, yellow))", "linear-gradient(to right, red, yellow 20%, blue 80%)")]
    [InlineData("-webkit-gradient(linear, left top, right top, from(red), color-stop(0.5, red), color-stop(0.5, blue), to(blue))", "linear-gradient(to right, red 50%, blue 50%)")]
    public void A_legacy_gradient_draws_like_its_standard_form(string legacy, string standard)
        => SameAs($"<div style='width:150px;height:100px;background:{standard}'></div>", $"<div style='width:150px;height:100px;background:{legacy}'></div>", 3);

    [Fact]
    public void A_webkit_gradient_without_stops_is_a_transparent_layer()
        => SameAs("<div style='width:150px;height:100px;background:red'></div>",
            "<div style='width:150px;height:100px;background:-webkit-gradient(linear, 0 0, 10 10) red'></div>");

    [Fact]
    public void A_prefixed_gradient_towards_a_corner_runs_along_the_diagonal()
        // In a square box the diagonal is the direction of the standard corner gradient.
        => SameAs("<div style='width:100px;height:100px;background:linear-gradient(to bottom right, red, blue)'></div>",
            "<div style='width:100px;height:100px;background:-webkit-linear-gradient(left top, red, blue)'></div>", 3);

    [Fact]
    public void A_webkit_gradient_between_two_circles_draws_both()
    {
        SameAs("<div style='width:100px;height:100px;background:radial-gradient(circle 40px at 50px 50px, red, blue)'></div>",
            "<div style='width:100px;height:100px;background:-webkit-gradient(radial, 50 50, 0, 50 50, 40, from(red), to(blue))'></div>", 3);
        var rgba = Pixels("<div style='width:100px;height:100px;background:-webkit-gradient(radial, 30 30, 5, 70 40, 50, from(red), to(blue))'></div>");
        // Inside the start circle the colour is the first stop's, far outside the end circle the last stop's.
        Assert.Equal(255, rgba[(30 * 200 + 30) * 4]);
        Assert.Equal(255, rgba[(95 * 200 + 5) * 4 + 2]);
    }

    [Theory]
    [InlineData("-webkit-gradient(conic, 0 0, 1 1, from(red))")]
    [InlineData("-webkit-gradient(linear, 0 0, from(red), to(blue))")]
    [InlineData("-webkit-gradient(linear, 0 0, 10 10, red, blue)")]
    [InlineData("-webkit-gradient(radial, 0 0, -5, 10 10, 5, from(red))")]
    [InlineData("-webkit-linear-gradient(to right, red, blue)")]
    [InlineData("-webkit-radial-gradient(center, circle 10px, red, blue)")]
    [InlineData("-webkit-gradient(linear, 0% 0%, 10px 10%, from(red))")]
    [InlineData("-webkit-gradient(linear, top left, 10 10, from(red))")]
    public void An_invalid_legacy_gradient_draws_nothing(string legacy)
        => Assert.DoesNotContain(Pixels($"<div style='width:150px;height:100px;background:{legacy}'></div><p>x</p>").Take(150 * 4 * 10), v => v != 255);
}

/// <summary>Legacy gradients and box properties with huge, tiny, many or malformed values.</summary>
[Collection(HostileInputCollection.Name)]
public class PrefixedCssHostileInputTests
{
    [Fact]
    public void Extreme_legacy_values_stay_bounded()
    {
        string stops = string.Join(",", Enumerable.Range(0, 5000).Select(i => $"color-stop({i % 7 * 0.2 - 0.3},red)"));
        var html = "<div style='height:50px;background:-webkit-gradient(radial,1e30 -1e30,1e30,NaN 0,Infinity,from(red),to(blue))'></div>"
                   + $"<div style='height:50px;background:-webkit-gradient(linear,0 0,0 0,{stops})'></div>"
                   + "<div style='height:50px;background:-webkit-gradient(linear,left top,1e38 1e38,from(red),color-stop(1e30%,blue),to((()'></div>"
                   + "<div style='height:50px;background:-webkit-radial-gradient(1e30px -1e30px,1e30px 1e-30px,red,blue)'></div>"
                   + "<div style='height:50px;background:-webkit-repeating-linear-gradient(1e30deg,red,blue 1e-30px)'></div>"
                   + "<div style='display:-webkit-box;-webkit-box-flex:1e38;-webkit-box-ordinal-group:2147483647'>"
                   + "<div style='-webkit-box-flex:3.4e38;-webkit-box-ordinal-group:99999999999'>a</div><div style='-webkit-box-flex:-1'>b</div></div>";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        PdfTestHelpers.AssertStructurallyValid(HtmlToPdf.Convert(html, null, cts.Token));
    }
}
