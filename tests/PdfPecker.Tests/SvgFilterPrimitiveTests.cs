using System.Text;

namespace PdfPecker.Tests;

/// <summary>feTurbulence, feImage, feTile, feConvolveMatrix, feDisplacementMap, lighting, primitive subregions, and the text in filtered content.</summary>
public class SvgFilterPrimitiveTests
{
    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    // An SVG of 400 × 400 px drawn in 300 × 300 pt; At takes user units.
    private static byte[] Svg(string content)
        => HtmlToPdf.Convert($"<body style='margin:0'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int R, int G, int B) At(byte[] pdf, float x, float y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = ((int)(y * 0.75f) * img.Width + (int)(x * 0.75f)) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 30)
        => Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    private static string Filtered(string primitives, string shape = "<rect width='400' height='400' fill='red' filter='url(#f)'/>")
        => $"<filter id='f' x='0' y='0' width='400' height='400' filterUnits='userSpaceOnUse'>{primitives}</filter>{shape}";

    [Fact]
    public void A_primitive_subregion_limits_its_result()
    {
        var pdf = Svg(Filtered("<feFlood flood-color='blue' x='100' y='100' width='100' height='50'/>"));
        Color(0, 0, 255, At(pdf, 150, 120));
        Color(255, 255, 255, At(pdf, 50, 50));
        Color(255, 255, 255, At(pdf, 250, 120));
        Color(255, 255, 255, At(pdf, 150, 200));
    }

    [Fact]
    public void A_subregion_defaults_to_the_union_of_the_inputs()
    {
        // The offset keeps the flood's subregion, so the moved part outside it is cut off.
        var pdf = Svg(Filtered("<feFlood flood-color='blue' width='100' height='100' result='a'/><feOffset in='a' dx='50'/>"));
        Color(0, 0, 255, At(pdf, 75, 50));
        Color(255, 255, 255, At(pdf, 25, 50));
        Color(255, 255, 255, At(pdf, 125, 50));
    }

    [Fact]
    public void FeTile_fills_the_filter_region_by_default()
    {
        var pdf = Svg(Filtered("<feFlood flood-color='blue' width='50' height='50'/><feTile/>"));
        Color(0, 0, 255, At(pdf, 375, 375));
    }

    [Fact]
    public void FeTile_repeats_its_input_subregion()
    {
        var pdf = Svg(Filtered("""
            <feFlood flood-color='blue' x='0' y='0' width='50' height='50' result='b'/>
            <feFlood flood-color='lime' x='50' y='0' width='50' height='50' result='g'/>
            <feMerge result='tile'><feMergeNode in='b'/><feMergeNode in='g'/></feMerge>
            <feFlood flood-opacity='0' x='0' y='0' width='100' height='50' result='empty'/>
            <feMerge x='0' y='0' width='100' height='50'><feMergeNode in='empty'/><feMergeNode in='tile'/></feMerge>
            <feTile x='0' y='0' width='400' height='400'/>
            """));
        Color(0, 0, 255, At(pdf, 25, 25));
        Color(0, 255, 0, At(pdf, 75, 25));
        Color(0, 0, 255, At(pdf, 225, 125));
        Color(0, 255, 0, At(pdf, 375, 375));
    }

    [Fact]
    public void FeTurbulence_makes_seeded_noise()
    {
        string Noise(int seed) => Filtered($"<feTurbulence type='fractalNoise' baseFrequency='0.05' numOctaves='2' seed='{seed}'/>");
        var a = Svg(Noise(1));
        var b = Svg(Noise(1));
        var c = Svg(Noise(2));
        var samples = Enumerable.Range(0, 10).Select(i => At(a, 20 + i * 37, 30 + i * 31)).ToList();
        Assert.Equal(samples, Enumerable.Range(0, 10).Select(i => At(b, 20 + i * 37, 30 + i * 31)).ToList());
        Assert.True(samples.Distinct().Count() > 5, "The noise varies over the image");
        Assert.NotEqual(samples, Enumerable.Range(0, 10).Select(i => At(c, 20 + i * 37, 30 + i * 31)).ToList());
    }

    [Fact]
    public void FeTurbulence_with_zero_frequency_is_flat()
    {
        // Noise at lattice points is zero: turbulence gives transparent black, fractal noise 50 % grey at 50 % alpha.
        var pdf = Svg(Filtered("<feTurbulence type='fractalNoise' baseFrequency='0' color-interpolation-filters='sRGB'/>"));
        var p = At(pdf, 200, 200);
        Color(191, 191, 191, p, 10); // grey 0.5 at alpha 0.5 over white
    }

    [Fact]
    public void FeImage_draws_an_element_and_a_file()
    {
        var png = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", "red-blue.png")));
        var pdf = Svg($"""
            <defs><rect id='r' x='0' y='0' width='100' height='100' fill='lime'/></defs>
            <filter id='f' x='0' y='0' width='400' height='400' filterUnits='userSpaceOnUse'>
              <feImage href='#r' result='a'/>
              <feImage href='data:image/png;base64,{png}' x='200' y='200' width='200' height='100' preserveAspectRatio='none' result='b'/>
              <feMerge><feMergeNode in='a'/><feMergeNode in='b'/></feMerge>
            </filter>
            <rect width='400' height='400' fill='black' filter='url(#f)'/>
            """);
        Color(0, 255, 0, At(pdf, 50, 50));
        Color(255, 0, 0, At(pdf, 250, 250));
        Color(0, 0, 255, At(pdf, 350, 250));
        Color(255, 255, 255, At(pdf, 250, 350));
    }

    [Fact]
    public void FeConvolveMatrix_applies_its_kernel()
    {
        // A kernel that takes the pixel to the left: a red half-plane moves right by one pixel; an identity kernel with
        // a bias of 1 makes everything opaque white.
        var shift = Svg(Filtered("<feConvolveMatrix order='3' kernelMatrix='0 0 0 0 0 1 0 0 0' edgeMode='none'/>",
            "<rect width='200' height='400' fill='red' filter='url(#f)'/>"));
        Color(255, 0, 0, At(shift, 100, 100));
        Color(255, 255, 255, At(shift, 300, 100));
        var bias = Svg(Filtered("<feConvolveMatrix kernelMatrix='0 0 0 0 1 0 0 0 0' bias='1' color-interpolation-filters='sRGB'/>",
            "<rect width='200' height='400' fill='red' filter='url(#f)'/>"));
        Color(255, 255, 255, At(bias, 300, 100));
        // An invalid kernel makes the result transparent.
        var invalid = Svg(Filtered("<feConvolveMatrix kernelMatrix='1 2'/>"));
        Color(255, 255, 255, At(invalid, 200, 200));
    }

    [Fact]
    public void FeDisplacementMap_moves_pixels_by_the_map()
    {
        // Left half red. A map of red 1.0 moves every pixel by scale / 2 = 50 user units: the red edge moves left.
        var pdf = Svg(Filtered("""
            <feFlood flood-color='rgb(255,128,128)' result='map'/>
            <feDisplacementMap in='SourceGraphic' in2='map' scale='100' xChannelSelector='R' yChannelSelector='G' color-interpolation-filters='sRGB'/>
            """, "<rect width='200' height='400' fill='red' filter='url(#f)'/>"));
        Color(255, 0, 0, At(pdf, 130, 200));
        Color(255, 255, 255, At(pdf, 170, 200));
    }

    [Fact]
    public void Diffuse_and_specular_lighting_light_the_alpha_surface()
    {
        // A light straight above a flat surface: diffuse gives the light colour times the constant.
        var diffuse = Svg(Filtered("<feDiffuseLighting lighting-color='rgb(0,0,255)' diffuseConstant='1' color-interpolation-filters='sRGB'><feDistantLight elevation='90'/></feDiffuseLighting>"));
        Color(0, 0, 255, At(diffuse, 200, 200));
        var lowLight = Svg(Filtered("<feDiffuseLighting lighting-color='white' color-interpolation-filters='sRGB'><feDistantLight elevation='30'/></feDiffuseLighting>"));
        Color(128, 128, 128, At(lowLight, 200, 200), 10); // sin 30° = 0.5
        // Specular from a point light: brightest under the light, dark far away, transparent where dark.
        var specular = Svg(Filtered("<feSpecularLighting specularExponent='20' lighting-color='white' color-interpolation-filters='sRGB'><fePointLight x='100' y='100' z='100'/></feSpecularLighting>"));
        Color(255, 255, 255, At(specular, 100, 100), 10);
        Color(255, 255, 255, At(specular, 390, 390), 10);
        // A spot light lights only inside its cone.
        var spot = Svg(Filtered("<feDiffuseLighting lighting-color='white' color-interpolation-filters='sRGB'><feSpotLight x='200' y='200' z='100' pointsAtX='200' pointsAtY='200' pointsAtZ='0' limitingConeAngle='20'/></feDiffuseLighting>"));
        Color(255, 255, 255, At(spot, 200, 200), 10);
        Color(0, 0, 0, At(spot, 350, 200), 10);
    }

    [Fact]
    public void Lighting_follows_the_shape_of_the_alpha()
    {
        // A blurred disc lit from the left: its left side is brighter than its right side.
        var pdf = Svg(Filtered("""
            <feGaussianBlur in='SourceAlpha' stdDeviation='20'/>
            <feDiffuseLighting surfaceScale='20' lighting-color='white' color-interpolation-filters='sRGB'><feDistantLight azimuth='180' elevation='45'/></feDiffuseLighting>
            """, "<circle cx='200' cy='200' r='100' fill='black' filter='url(#f)'/>"));
        Assert.True(At(pdf, 110, 200).R > At(pdf, 290, 200).R + 40);
    }

    [Fact]
    public void Text_under_a_filter_stays_selectable()
    {
        var pdf = Svg(Filtered("<feGaussianBlur stdDeviation='1'/>", "<text x='20' y='100' font-size='40' filter='url(#f)'>Blurred words</text>"));
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains(" 3 Tr ", text);
        Assert.Contains("(Blurred words)", text);
    }

    [Fact]
    public void An_element_drawing_itself_through_feImage_does_not_loop()
    {
        var pdf = Svg("""
            <filter id='f'><feImage href='#me'/></filter>
            <rect id='me' width='100' height='100' fill='red' filter='url(#f)'/>
            """);
        Assert.NotEmpty(pdf);
    }
}
