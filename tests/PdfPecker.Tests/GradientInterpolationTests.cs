using PdfPecker.Css;
using PdfPecker.Pdf;

namespace PdfPecker.Tests;

/// <summary>Gradients with a colour interpolation method (CSS Images 4): <c>in oklch</c>, <c>in hsl longer hue</c> and so on.</summary>
public class GradientInterpolationTests
{
    private static CssGradient? Parse(string css) => CssGradient.Parse(css, CssColor.Black, 12, 12, 5, 8);

    private static Gradient Resolve(string css) => Parse(css)!.Resolve(200, 100, 0xFFFFFFFF);

    private static uint Mix(string mix) => CssColor.TryParse(mix, CssColor.Black, out uint c) ? c : throw new ArgumentException(mix);

    /// <summary>The colour at the middle stop: a pair of stops is split into sixteen steps.</summary>
    private static uint Middle(Gradient g) => g.Colors[Array.IndexOf(g.Offsets, 0.5f)];

    [Theory]
    [InlineData("linear-gradient(in oklch, red, blue)")]
    [InlineData("linear-gradient(to right in oklab, red, blue)")]
    [InlineData("linear-gradient(in oklab to right, red, blue)")]
    [InlineData("linear-gradient(90deg in hsl longer hue, red, blue)")]
    [InlineData("linear-gradient(in lch decreasing hue 45deg, red, blue)")]
    [InlineData("linear-gradient(IN OKLCH, red, blue)")]
    [InlineData("repeating-linear-gradient(in srgb-linear, red, blue 20%)")]
    [InlineData("radial-gradient(circle in oklch, red, blue)")]
    [InlineData("radial-gradient(in lch circle at 10% 20%, red, blue)")]
    [InlineData("radial-gradient(in display-p3, red, blue)")]
    [InlineData("conic-gradient(from 10deg in oklch, red, blue)")]
    [InlineData("conic-gradient(in hwb increasing hue at 20% 30%, red, blue)")]
    public void Interpolation_methods_parse_in_every_gradient(string css) => Assert.NotNull(Parse(css));

    [Theory]
    [InlineData("linear-gradient(in nonsense, red, blue)")]
    [InlineData("linear-gradient(in, red, blue)")]
    [InlineData("linear-gradient(in oklab longer hue, red, blue)")]
    [InlineData("linear-gradient(in oklch sideways hue, red, blue)")]
    [InlineData("linear-gradient(to in oklch right, red, blue)")]
    [InlineData("linear-gradient(in oklch in oklab, red, blue)")]
    [InlineData("radial-gradient(circle in oklch at center, red, blue)")]
    public void Invalid_interpolation_methods_make_the_gradient_invalid(string css) => Assert.Null(Parse(css));

    [Theory]
    [InlineData("oklch")]
    [InlineData("oklab")]
    [InlineData("lch")]
    [InlineData("hsl")]
    [InlineData("srgb-linear")]
    [InlineData("xyz")]
    public void The_middle_of_two_stops_is_their_color_mix(string space)
    {
        var g = Resolve($"linear-gradient(to right in {space}, blue, yellow)");
        Assert.Equal(17, g.Offsets.Length);
        Assert.Equal(Mix($"color-mix(in {space}, blue, yellow)"), Middle(g));
    }

    [Fact]
    public void Hue_methods_take_the_way_round_they_name()
    {
        // Red to blue the short way passes magenta; the long way passes green.
        uint shorter = Middle(Resolve("linear-gradient(in hsl, red, blue)"));
        uint longer = Middle(Resolve("linear-gradient(in hsl longer hue, red, blue)"));
        Assert.Equal(0xFFFF00FFu, shorter);
        Assert.Equal(0xFF00FF00u, longer);
        // The longer hue between two stops of one colour goes once round the circle.
        Assert.Equal(0xFF00FFFFu, Middle(Resolve("linear-gradient(in hsl longer hue, red, red)")));
    }

    [Fact]
    public void Srgb_and_hard_edges_add_no_stops()
    {
        Assert.Equal(2, Resolve("linear-gradient(in srgb, red, blue)").Offsets.Length);
        Assert.Equal(2, Resolve("linear-gradient(red, blue)").Offsets.Length);
        Assert.Equal(Resolve("linear-gradient(red 50%, blue 50%, blue)").Offsets, Resolve("linear-gradient(in oklch, red 50%, blue 50%, blue)").Offsets);
    }

    [Fact]
    public void Many_stops_stay_within_the_limit()
    {
        var stops = string.Join(", ", Enumerable.Range(0, 3000).Select(i => i % 2 == 0 ? "red" : "blue"));
        var g = Resolve($"linear-gradient(in oklch longer hue, {stops})");
        Assert.InRange(g.Offsets.Length, 2, 3001);
        var some = string.Join(", ", Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? "red" : "blue"));
        Assert.InRange(Resolve($"linear-gradient(in oklch, {some})").Offsets.Length, 200, 1225);
    }

    [Fact]
    public void A_page_draws_an_oklch_gradient_through_its_hues()
    {
        var pdf = HtmlToPdf.Convert("<style>@page{margin:0;size:200pt 20pt} body{margin:0}</style>"
            + "<div style='height:20pt;background:linear-gradient(to right in oklch, blue, yellow)'></div>");
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (10 * img.Width + 100) * 4;
        // sRGB would give grey here; oklch passes through cyan and green.
        Assert.True(img.Rgba[i + 1] > 150 && img.Rgba[i] < 100, $"({img.Rgba[i]}, {img.Rgba[i + 1]}, {img.Rgba[i + 2]})");
    }
}
