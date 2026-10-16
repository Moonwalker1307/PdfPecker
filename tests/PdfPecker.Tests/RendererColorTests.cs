using System.Globalization;
using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>
/// Colour spaces, PDF functions and shadings, rendered from tiny hand-built PDFs. Each test fills a page (one pixel per
/// point) and asserts the colour at chosen coordinates.
/// </summary>
public class RendererColorTests
{
    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- functions through a Separation tint transform

    private static (int R, int G, int B, int A) Tint(Func<FixturePdf, int> function, string alternate, double tint)
    {
        var pdf = new FixturePdf();
        int fn = function(pdf);
        var page = pdf.Build(40, 40, $"/CS cs {F(tint)} scn 0 0 40 40 re f",
            $"/ColorSpace << /CS [/Separation /Spot {alternate} {fn} 0 R] >>");
        return At(Render(page), 20, 20);
    }

    private static Func<FixturePdf, int> PostScript(string code, int outputs = 3) =>
        pdf => pdf.AddStream($"/FunctionType 4 /Domain [0 1] /Range [{string.Join(' ', Enumerable.Repeat("0 1", outputs))}]", code);

    [Fact]
    public void Postscript_function_with_ifelse_computes_the_tint_transform()
    {
        var fn = PostScript("{ dup 0.5 gt { pop 1 0 0 } { 2 mul 0 0 } ifelse }");
        AssertColor(Tint(fn, "/DeviceRGB", 0.25), 128, 0, 0);
        AssertColor(Tint(fn, "/DeviceRGB", 0.75), 255, 0, 0);
    }

    [Fact]
    public void Postscript_function_with_nested_ifelse_takes_the_right_branch()
    {
        var fn = PostScript("{ dup 0.3 lt { pop 0 0 1 } { dup 0.6 lt { pop 0 1 0 } { pop 1 0 0 } ifelse } ifelse }");
        AssertColor(Tint(fn, "/DeviceRGB", 0.1), 0, 0, 255);
        AssertColor(Tint(fn, "/DeviceRGB", 0.5), 0, 255, 0);
        AssertColor(Tint(fn, "/DeviceRGB", 0.9), 255, 0, 0);
    }

    [Fact]
    public void Postscript_if_without_else_skips_its_block_when_false()
    {
        var fn = PostScript("{ dup 0.5 gt { 1 exch sub } if 0 0 }");
        AssertColor(Tint(fn, "/DeviceRGB", 0.25), 64, 0, 0);
        AssertColor(Tint(fn, "/DeviceRGB", 0.75), 64, 0, 0);
    }

    [Fact]
    public void Postscript_stack_operators_roll_index_and_copy()
    {
        AssertColor(Tint(PostScript("{ 0.25 0.5 3 -1 roll }"), "/DeviceRGB", 1), 64, 128, 255); // t .25 .5 rolled down by one
        AssertColor(Tint(PostScript("{ 0.25 0.5 3 1 roll }"), "/DeviceRGB", 1), 128, 255, 64); // rolled up by one
        AssertColor(Tint(PostScript("{ 0.5 0.25 2 index }"), "/DeviceRGB", 1), 128, 64, 255); // copies t from depth two
        AssertColor(Tint(PostScript("{ 0.5 exch 2 copy }"), "/DeviceRGB", 1), 255, 128, 255); // .5 t .5 t
    }

    [Fact]
    public void Postscript_arithmetic_and_math_operators()
    {
        AssertColor(Tint(PostScript("{ dup 0.25 add exch dup mul 0 }"), "/DeviceRGB", 0.5), 191, 64, 0); // t + .25 and t squared
        AssertColor(Tint(PostScript("{ 100 mul cvi 7 mod 7 div 0 0 }"), "/DeviceRGB", 0.5), 36, 0, 0); // 50 mod 7 = 1
        AssertColor(Tint(PostScript("{ 4 mul round 4 div 0 0 }"), "/DeviceRGB", 0.6), 128, 0, 0); // 2.4 rounds to 2
        AssertColor(Tint(PostScript("{ 2 mul ceiling 2 div 0 0 }"), "/DeviceRGB", 0.6), 255, 0, 0); // 1.2 rounds up to 2
        AssertColor(Tint(PostScript("{ 1 atan 360 div 0 0 }"), "/DeviceRGB", 1), 32, 0, 0); // atan2(1, 1) = 45 degrees
        AssertColor(Tint(PostScript("{ pop 30 sin 0 0 }"), "/DeviceRGB", 0), 128, 0, 0); // sine takes degrees
        AssertColor(Tint(PostScript("{ pop 0.25 sqrt 0 0 }"), "/DeviceRGB", 0), 128, 0, 0);
        AssertColor(Tint(PostScript("{ 2 exp neg abs 0 0 }"), "/DeviceRGB", 0.5), 64, 0, 0);
        AssertColor(Tint(PostScript("{ pop 7 2 idiv 6 div 0 0 }"), "/DeviceRGB", 0), 128, 0, 0); // 3 / 6
    }

    [Fact]
    public void Postscript_boolean_bitwise_and_comparison_operators()
    {
        var either = PostScript("{ dup 0.2 lt exch 0.8 gt or { 1 } { 0 } ifelse 0 0 }");
        AssertColor(Tint(either, "/DeviceRGB", 0.1), 255, 0, 0);
        AssertColor(Tint(either, "/DeviceRGB", 0.5), 0, 0, 0);
        AssertColor(Tint(either, "/DeviceRGB", 0.9), 255, 0, 0);
        var inverted = PostScript("{ 0.5 lt not { 1 } { 0 } ifelse 0 0 }");
        AssertColor(Tint(inverted, "/DeviceRGB", 0.7), 255, 0, 0);
        AssertColor(Tint(inverted, "/DeviceRGB", 0.2), 0, 0, 0);
        AssertColor(Tint(PostScript("{ pop 1 3 bitshift 8 div 0 0 }"), "/DeviceRGB", 0), 255, 0, 0);
        AssertColor(Tint(PostScript("{ pop 16 -2 bitshift 8 div 0 0 }"), "/DeviceRGB", 0), 128, 0, 0);
        AssertColor(Tint(PostScript("{ pop 5 3 xor 8 div 0 0 }"), "/DeviceRGB", 0), 191, 0, 0);
        AssertColor(Tint(PostScript("{ pop 6 3 and 8 div 0 0 }"), "/DeviceRGB", 0), 64, 0, 0);
        AssertColor(Tint(PostScript("{ 0.5 eq { 1 } { 0 } ifelse 0 0 }"), "/DeviceRGB", 0.5), 255, 0, 0);
        AssertColor(Tint(PostScript("{ 0.5 ne { 1 } { 0 } ifelse 0 0 }"), "/DeviceRGB", 0.5), 0, 0, 0);
    }

    [Fact]
    public void Sampled_function_interpolates_between_samples()
    {
        // Three samples of RGB: red at 0, green at 0.5, blue at 1.
        byte[] samples = { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        Func<FixturePdf, int> fn = pdf => pdf.AddStream("/FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] /Size [3] /BitsPerSample 8", samples);
        AssertColor(Tint(fn, "/DeviceRGB", 0), 255, 0, 0);
        AssertColor(Tint(fn, "/DeviceRGB", 0.5), 0, 255, 0);
        AssertColor(Tint(fn, "/DeviceRGB", 1), 0, 0, 255);
        AssertColor(Tint(fn, "/DeviceRGB", 0.25), 128, 128, 0);
        AssertColor(Tint(fn, "/DeviceRGB", 0.875), 0, 64, 191);
    }

    [Fact]
    public void Sampled_function_honours_bits_per_sample_encode_and_decode()
    {
        // 4-bit samples (packed two per byte): 0x0, 0xF; Encode reverses the input and Decode shrinks the output to 0.2..0.8.
        byte[] samples = { 0x0F };
        Func<FixturePdf, int> fn = pdf => pdf.AddStream(
            "/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 4 /Encode [1 0] /Decode [0.2 0.8]", samples);
        AssertColor(Tint(fn, "/DeviceGray", 0), 204, 204, 204); // encoded to sample 1 (0xF) = 0.8
        AssertColor(Tint(fn, "/DeviceGray", 1), 51, 51, 51); // sample 0 = 0.2
    }

    [Fact]
    public void Sampled_function_with_16_bit_samples_and_two_inputs()
    {
        // A 2x2 grid of gray: (0,0)=0, (1,0)=1, (0,1)=1, (1,1)=0 so the centre is 0.5; 16-bit big-endian samples.
        byte[] samples = { 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0 };
        var pdf = new FixturePdf();
        int fn = pdf.AddStream("/FunctionType 0 /Domain [0 1 0 1] /Range [0 1] /Size [2 2] /BitsPerSample 16", samples);
        var page = pdf.Build(40, 40, "/CS cs 1 0 scn 0 0 20 40 re f /CS cs 0.5 0.5 scn 20 0 20 40 re f",
            $"/ColorSpace << /CS [/DeviceN [/A /B] /DeviceGray {fn} 0 R] >>");
        var img = Render(page);
        AssertAt(img, 10, 20, 255, 255, 255);
        AssertAt(img, 30, 20, 128, 128, 128);
    }

    [Fact]
    public void Stitching_function_switches_sub_functions_at_the_bounds()
    {
        Func<FixturePdf, int> fn = pdf =>
        {
            int a = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >>");
            int b = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [0 0 1] /N 1 >>");
            return pdf.Add($"<< /FunctionType 3 /Domain [0 1] /Functions [{a} 0 R {b} 0 R] /Bounds [0.5] /Encode [0 1 0 1] >>");
        };
        AssertColor(Tint(fn, "/DeviceRGB", 0.25), 128, 128, 0);
        AssertColor(Tint(fn, "/DeviceRGB", 0.75), 0, 128, 128);
        AssertColor(Tint(fn, "/DeviceRGB", 1), 0, 0, 255);
    }

    [Fact]
    public void Stitching_function_applies_a_reversed_encode()
    {
        Func<FixturePdf, int> fn = pdf =>
        {
            int a = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >>");
            int b = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [0 0 1] /N 1 >>");
            return pdf.Add($"<< /FunctionType 3 /Domain [0 1] /Functions [{a} 0 R {b} 0 R] /Bounds [0.5] /Encode [1 0 0 1] >>");
        };
        AssertColor(Tint(fn, "/DeviceRGB", 0.125), 64, 191, 0); // local 0.25 reversed to 0.75
        AssertColor(Tint(fn, "/DeviceRGB", 0.625), 0, 191, 64);
    }

    [Fact]
    public void Exponential_function_applies_its_exponent()
    {
        Func<FixturePdf, int> fn = pdf => pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 2 >>");
        AssertColor(Tint(fn, "/DeviceGray", 0.5), 64, 64, 64);
        AssertColor(Tint(fn, "/DeviceGray", 1), 255, 255, 255);
    }

    // ---------------------------------------------------------------- colour spaces

    private static (int R, int G, int B, int A) Fill(string colorSpace, string color, Action<FixturePdf>? setup = null)
    {
        var pdf = new FixturePdf();
        setup?.Invoke(pdf);
        var page = pdf.Build(40, 40, $"/CS cs {color} sc 0 0 40 40 re f", $"/ColorSpace << /CS {colorSpace} >>");
        return At(Render(page), 20, 20);
    }

    [Fact]
    public void Devicen_runs_all_components_through_the_tint_transform()
    {
        var pdf = new FixturePdf();
        int fn = pdf.AddStream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ 0 }");
        var page = pdf.Build(40, 40, "/CS cs 0.5 1 scn 0 0 40 40 re f", $"/ColorSpace << /CS [/DeviceN [/A /B] /DeviceRGB {fn} 0 R] >>");
        AssertColor(At(Render(page), 20, 20), 128, 255, 0);
    }

    [Fact]
    public void Devicen_with_cmyk_alternate_converts_through_cmyk()
    {
        var pdf = new FixturePdf();
        // Spot inks A and B drive black (K) only: K = max(a, b).
        int fn = pdf.AddStream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1 0 1]", "{ 2 copy lt { exch } if pop 0 0 0 4 -1 roll }");
        var page = pdf.Build(40, 40, "/CS cs 0 1 scn 0 0 20 40 re f /CS cs 0 0 scn 20 0 20 40 re f",
            $"/ColorSpace << /CS [/DeviceN [/A /B] /DeviceCMYK {fn} 0 R] >>");
        var img = Render(page);
        var black = At(img, 10, 20);
        Assert.True(black.R < 90 && black.G < 90 && black.B < 90, $"Full K should be dark, got {black}");
        AssertAt(img, 30, 20, 255, 255, 255);
    }

    [Fact]
    public void Separation_none_paints_nothing_and_all_is_a_tint_of_the_alternate()
    {
        var pdf = new FixturePdf();
        int fn = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>");
        var page = pdf.Build(40, 40, "/None cs 1 scn 0 0 20 40 re f /All cs 1 scn 20 0 20 40 re f",
            $"/ColorSpace << /None [/Separation /None /DeviceGray {fn} 0 R] /All [/Separation /All /DeviceGray {fn} 0 R] >>");
        var img = Render(page);
        AssertAt(img, 10, 20, 255, 255, 255);
        AssertAt(img, 30, 20, 0, 0, 0);
    }

    [Fact]
    public void Lab_lightness_maps_to_srgb_gray()
    {
        const string lab = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]";
        AssertColor(Fill(lab, "50 0 0"), 119, 119, 119, 3);
        AssertColor(Fill(lab, "100 0 0"), 255, 255, 255, 3);
        AssertColor(Fill(lab, "0 0 0"), 0, 0, 0, 3);
        AssertColor(Fill(lab, "75 0 0"), 185, 185, 185, 3);
    }

    [Fact]
    public void Lab_chroma_axes_point_towards_red_green_yellow_and_blue()
    {
        const string lab = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]";
        var red = Fill(lab, "50 80 0");
        Assert.True(red.R > red.G + 80 && red.R > red.B, $"+a should be reddish: {red}");
        var green = Fill(lab, "50 -80 0");
        Assert.True(green.G > green.R + 80 && green.G > green.B, $"-a should be greenish: {green}");
        var yellow = Fill(lab, "80 0 80");
        Assert.True(yellow.R > yellow.B + 100 && yellow.G > yellow.B + 100, $"+b should be yellowish: {yellow}");
        var blue = Fill(lab, "40 0 -80");
        Assert.True(blue.B > blue.R + 80 && blue.B > blue.G + 40, $"-b should be bluish: {blue}");
    }

    [Fact]
    public void Lab_components_are_clamped_to_the_declared_range()
    {
        const string narrow = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-10 10 -10 10] >>]";
        const string wide = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]";
        var clamped = Fill(narrow, "50 100 0");
        var expected = Fill(wide, "50 10 0");
        AssertColor(clamped, expected.R, expected.G, expected.B, 2);
    }

    [Fact]
    public void Calrgb_with_srgb_primaries_and_gamma_renders_like_devicergb()
    {
        const string cal = "[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [2.2 2.2 2.2] /Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>]";
        AssertColor(Fill(cal, "1 0 0"), 255, 0, 0, 4);
        AssertColor(Fill(cal, "0.5 0.5 0.5"), 128, 128, 128, 6);
    }

    [Fact]
    public void Calgray_renders_as_gray()
    {
        const string cal = "[/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >>]";
        AssertColor(Fill(cal, "0.5"), 128, 128, 128, 6);
        AssertColor(Fill(cal, "1"), 255, 255, 255, 2);
    }

    private const string SrgbMatrix = "/Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505]";

    [Fact]
    public void Calgray_with_gamma_one_is_linear_light()
    {
        // CalGray with Gamma 1 gives luminance Y = A, so half luminance is sRGB 188, not 128 (ISO 32000-1, 8.6.5.2).
        AssertColor(Fill("[/CalGray << /WhitePoint [0.9505 1 1.089] >>]", "0.5"), 188, 188, 188, 3);
        AssertColor(Fill("[/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 1 >>]", "0"), 0, 0, 0, 2);
    }

    [Fact]
    public void Calrgb_with_gamma_one_and_srgb_primaries_is_linear_srgb()
    {
        string cal = $"[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [1 1 1] {SrgbMatrix} >>]";
        AssertColor(Fill(cal, "0.5 0.5 0.5"), 188, 188, 188, 3);
        AssertColor(Fill(cal, "0 1 0"), 0, 255, 0, 4);
    }

    [Fact]
    public void Calrgb_adapts_a_d50_white_point_to_srgb_white()
    {
        // White in the document's white point must come out as sRGB white, whatever that white point is. The matrix is the
        // sRGB primaries adapted to D50.
        const string cal = "[/CalRGB << /WhitePoint [0.9642 1 0.8249] /Gamma [1.8 1.8 1.8] /Matrix [0.4361 0.2225 0.0139 0.3851 0.7169 0.0971 0.1431 0.0606 0.7141] >>]";
        AssertColor(Fill(cal, "1 1 1"), 255, 255, 255, 3);
        var mid = Fill(cal, "0.5 0.5 0.5");
        Assert.True(Math.Abs(mid.R - mid.G) <= 3 && Math.Abs(mid.G - mid.B) <= 3, $"Neutral input should stay neutral, got {mid}");
    }

    [Fact]
    public void Calrgb_with_the_default_identity_matrix_keeps_black_and_white()
    {
        // Without /Matrix the components are X, Y and Z directly; white and black must still come out near white and black.
        const string cal = "[/CalRGB << /WhitePoint [0.9505 1 1.089] >>]";
        AssertColor(Fill(cal, "0 0 0"), 0, 0, 0, 2);
        var white = Fill(cal, "1 1 1");
        Assert.True(white.R >= 230 && white.G >= 230 && white.B >= 230, $"Expected near white, got {white}");
    }

    [Fact]
    public void Calrgb_image_matches_the_same_calrgb_fill()
    {
        // 8-bit CalRGB images take a table-driven path; it must agree with the per-colour conversion.
        string cal = $"[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [1 1.8 2.2] {SrgbMatrix} >>]";
        var pdf = new FixturePdf();
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace {cal}", new byte[] { 128, 64, 200 });
        var img = Render(pdf.Build(4, 4, "q 4 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        var fill = Fill(cal, $"{F(128 / 255.0)} {F(64 / 255.0)} {F(200 / 255.0)}");
        AssertColor(At(img, 2, 2), fill.R, fill.G, fill.B, 2);
        AssertColor(Fill(cal, "0.5 0 0"), 188, 0, 0, 3);
    }

    [Fact]
    public void Iccbased_uses_its_alternate_space()
    {
        // The profile bytes are not a real ICC profile; the alternate (Lab here) must decide the colour.
        const string lab = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]";
        var result = Fill("[/ICCBased 5 0 R]", "50 0 0", pdf => pdf.AddStream($"/N 3 /Alternate {lab}", "not a profile"));
        AssertColor(result, 119, 119, 119, 3);
    }

    [Fact]
    public void Iccbased_with_four_components_falls_back_to_cmyk()
    {
        var black = Fill("[/ICCBased 5 0 R]", "0 0 0 1", pdf => pdf.AddStream("/N 4 /Alternate /DeviceCMYK", "x"));
        Assert.True(black.R < 90 && black.G < 90 && black.B < 90, $"Full K should be dark, got {black}");
        var white = Fill("[/ICCBased 5 0 R]", "0 0 0 0", pdf => pdf.AddStream("/N 4", "x"));
        AssertColor(white, 255, 255, 255, 2);
        var gray = Fill("[/ICCBased 5 0 R]", "0.5", pdf => pdf.AddStream("/N 1", "x"));
        AssertColor(gray, 128, 128, 128, 2);
    }

    [Fact]
    public void Indexed_color_space_looks_up_the_palette_for_fills()
    {
        const string indexed = "[/Indexed /DeviceRGB 2 <FF0000 00FF00 0000FF>]";
        AssertColor(Fill(indexed, "0"), 255, 0, 0, 1);
        AssertColor(Fill(indexed, "1"), 0, 255, 0, 1);
        AssertColor(Fill(indexed, "2"), 0, 0, 255, 1);
    }

    [Fact]
    public void Indexed_color_space_with_a_stream_lookup_and_a_lab_base()
    {
        // The lookup bytes are scaled over the Lab ranges: entry 0 is L=0 and entry 1 is L=100, with a and b at 128 (zero).
        const string lab = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]";
        var result = Fill("[/Indexed " + lab + " 1 5 0 R]", "1", pdf =>
            pdf.AddStream("", new byte[] { 0, 128, 128, 255, 128, 128 }));
        Assert.True(Math.Abs(result.R - 255) < 12 && Math.Abs(result.G - 255) < 12 && Math.Abs(result.B - 255) < 12, $"Index 1 should be about white, got {result}");
    }

    // ---------------------------------------------------------------- axial and radial shadings

    private static RenderedImage Shade(string shadingEntries, Action<FixturePdf>? setup = null, byte[]? meshData = null)
    {
        var pdf = new FixturePdf();
        setup?.Invoke(pdf);
        int shading = meshData == null ? pdf.Add($"<< {shadingEntries} >>") : pdf.AddStream(shadingEntries, meshData);
        return Render(pdf.Build(40, 40, "/Sh sh", $"/Shading << /Sh {shading} 0 R >>"));
    }

    private static string RedToBlue(FixturePdf pdf) => $"{pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>")} 0 R";

    [Theory]
    [InlineData("false false", false, false)]
    [InlineData("true false", true, false)]
    [InlineData("false true", false, true)]
    [InlineData("true true", true, true)]
    public void Axial_shading_extends_only_where_asked(string extend, bool start, bool end)
    {
        var pdf = new FixturePdf();
        var fn = RedToBlue(pdf);
        int sh = pdf.Add($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [10 0 30 0] /Function {fn} /Extend [{extend}] >>");
        var img = Render(pdf.Build(40, 40, "/Sh sh", $"/Shading << /Sh {sh} 0 R >>"));
        AssertAt(img, 20, 20, 122, 0, 133, 8); // inside the axis: halfway
        if (start) AssertAt(img, 4, 20, 255, 0, 0); else AssertAt(img, 4, 20, 255, 255, 255);
        if (end) AssertAt(img, 36, 20, 0, 0, 255); else AssertAt(img, 36, 20, 255, 255, 255);
    }

    [Fact]
    public void Axial_shading_follows_a_diagonal_axis_and_a_domain()
    {
        var pdf = new FixturePdf();
        int fn = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>");
        int sh = pdf.Add($"<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 40 40] /Domain [0 0.5] /Function {fn} 0 R /Extend [true true] >>");
        var img = Render(pdf.Build(40, 40, "/Sh sh", $"/Shading << /Sh {sh} 0 R >>"));
        // Along the diagonal t runs 0..0.5 over the axis, so the middle is gray 0.25; the far corner is extended at 0.5.
        AssertAt(img, 20, 20, 64, 64, 64, 6);
        AssertAt(img, 2, 2, 0, 0, 0, 8);
        AssertAt(img, 38, 38, 128, 128, 128, 6);
        // Perpendicular to the axis the colour does not change.
        AssertAt(img, 10, 30, 64, 64, 64, 6);
    }

    [Theory]
    [InlineData("false false", false, false)]
    [InlineData("true false", true, false)]
    [InlineData("false true", false, true)]
    [InlineData("true true", true, true)]
    public void Radial_shading_extends_only_where_asked(string extend, bool inner, bool outer)
    {
        var pdf = new FixturePdf();
        var fn = RedToBlue(pdf);
        int sh = pdf.Add($"<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [20 20 5 20 20 10] /Function {fn} /Extend [{extend}] >>");
        var img = Render(pdf.Build(40, 40, "/Sh sh", $"/Shading << /Sh {sh} 0 R >>"));
        AssertAt(img, 27, 20, 126, 0, 129, 8); // r about 7.5: halfway between the circles
        if (inner) AssertAt(img, 20, 20, 255, 0, 0); else AssertAt(img, 20, 20, 255, 255, 255);
        if (outer) AssertAt(img, 35, 20, 0, 0, 255); else AssertAt(img, 35, 20, 255, 255, 255);
    }

    [Fact]
    public void Function_based_shading_maps_x_and_y_through_the_function()
    {
        // A Type 1 (function-based) shading: colour = (x, y, 0) over the domain 0..1 mapped to 0..40 points.
        var pdf = new FixturePdf();
        int fn = pdf.AddStream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ 0 }");
        int sh = pdf.Add($"<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [40 0 0 40 0 0] /Function {fn} 0 R >>");
        var img = Render(pdf.Build(40, 40, "/Sh sh", $"/Shading << /Sh {sh} 0 R >>"));
        AssertAt(img, 10, 30, 64, 191, 0, 8);
        AssertAt(img, 30, 10, 191, 64, 0, 8);
    }

    // ---------------------------------------------------------------- mesh shadings

    private const string MeshEntries8 = "/ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /Decode [0 255 0 255 0 1 0 1 0 1]";

    private readonly record struct V(int X, int Y, int R, int G, int B);

    private static (int R, int G, int B) Barycentric(V a, V b, V c, double px, double py)
    {
        double d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        double wa = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / d;
        double wb = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / d;
        double wc = 1 - wa - wb;
        return ((int)Math.Round(wa * a.R + wb * b.R + wc * c.R), (int)Math.Round(wa * a.G + wb * b.G + wc * c.G), (int)Math.Round(wa * a.B + wb * b.B + wc * c.B));
    }

    private static void AssertBarycentric(RenderedImage img, V a, V b, V c, int px, int py)
    {
        var e = Barycentric(a, b, c, px + 0.5, py + 0.5);
        AssertAt(img, px, py, e.R, e.G, e.B, 10);
    }

    [Fact]
    public void Free_form_triangle_mesh_interpolates_vertex_colors()
    {
        V r = new(4, 4, 255, 0, 0), g = new(36, 4, 0, 255, 0), b = new(20, 36, 0, 0, 255);
        byte[] data = { 0, 4, 4, 255, 0, 0, 0, 36, 4, 0, 255, 0, 0, 20, 36, 0, 0, 255 };
        var img = Shade("/ShadingType 4 /BitsPerFlag 8 " + MeshEntries8, null, data);
        AssertBarycentric(img, r, g, b, 20, 15);
        AssertBarycentric(img, r, g, b, 8, 6);
        AssertBarycentric(img, r, g, b, 30, 8);
        AssertBarycentric(img, r, g, b, 20, 30);
        AssertAt(img, 2, 36, 255, 255, 255); // outside the triangle
        AssertAt(img, 36, 36, 255, 255, 255);
    }

    [Fact]
    public void Free_form_triangle_mesh_continues_strips_with_edge_flags()
    {
        V va = new(4, 4, 0, 255, 0), vb = new(36, 4, 0, 255, 0), vc = new(4, 20, 0, 255, 0), vd = new(36, 20, 0, 0, 255), ve = new(20, 36, 255, 0, 0);
        byte[] data =
        {
            0, 4, 4, 0, 255, 0,
            0, 36, 4, 0, 255, 0,
            0, 4, 20, 0, 255, 0,
            1, 36, 20, 0, 0, 255, // flag 1: the triangle vb, vc and the new vertex
            2, 20, 36, 255, 0, 0, // flag 2: the previous triangle's va, vc and the new vertex
        };
        var img = Shade("/ShadingType 4 /BitsPerFlag 8 " + MeshEntries8, null, data);
        // Second triangle: (36,4) green, (4,20) green, (36,20) blue.
        AssertBarycentric(img, vb, vc, vd, 30, 14);
        // Third triangle: the second's va (36,4) green, vc (36,20) blue and the new (20,36) red.
        AssertBarycentric(img, vb, vd, ve, 33, 22);
        _ = va;
    }

    [Fact]
    public void Lattice_triangle_mesh_interpolates_across_rows()
    {
        // Two rows of three vertices; the colour changes only along x so the diagonal split does not matter.
        byte[] data =
        {
            4, 4, 255, 0, 0, 20, 4, 0, 255, 0, 36, 4, 0, 0, 255,
            4, 36, 255, 0, 0, 20, 36, 0, 255, 0, 36, 36, 0, 0, 255,
        };
        var img = Shade("/ShadingType 5 /VerticesPerRow 3 " + MeshEntries8, null, data);
        AssertAt(img, 12, 20, 128, 128, 0, 10);
        AssertAt(img, 28, 10, 0, 128, 128, 10);
        AssertAt(img, 2, 20, 255, 255, 255);
    }

    private static byte[] Patch(bool tensor)
    {
        var bytes = new List<byte> { 0 };
        // Boundary of a square (8,8)-(32,32), started at the lower left and going up the left edge first.
        int[] boundary = { 8, 8, 8, 16, 8, 24, 8, 32, 16, 32, 24, 32, 32, 32, 32, 24, 32, 16, 32, 8, 24, 8, 16, 8 };
        foreach (int v in boundary) bytes.Add((byte)v);
        if (tensor) foreach (int v in new[] { 16, 16, 16, 24, 24, 24, 24, 16 }) bytes.Add((byte)v);
        // Corner colors: lower left red, upper left green, upper right blue, lower right yellow.
        bytes.AddRange(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 });
        return bytes.ToArray();
    }

    private static (int R, int G, int B) Bilinear(double x, double y)
    {
        double u = (x - 8) / 24, v = (y - 8) / 24;
        double r = (1 - u) * (1 - v) * 255 + (1 - u) * v * 0 + u * v * 0 + u * (1 - v) * 255;
        double g = (1 - u) * (1 - v) * 0 + (1 - u) * v * 255 + u * v * 0 + u * (1 - v) * 255;
        double b = (1 - u) * (1 - v) * 0 + (1 - u) * v * 0 + u * v * 255 + u * (1 - v) * 0;
        return ((int)Math.Round(r), (int)Math.Round(g), (int)Math.Round(b));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void Patch_meshes_blend_the_corner_colors(int type, bool tensor)
    {
        var img = Shade($"/ShadingType {type} /BitsPerFlag 8 " + MeshEntries8, null, Patch(tensor));
        foreach (var (x, y) in new[] { (20, 20), (12, 12), (28, 12), (12, 28), (28, 28), (10, 20) })
        {
            var e = Bilinear(x + 0.5, y + 0.5);
            AssertAt(img, x, y, e.R, e.G, e.B, 12);
        }
        AssertAt(img, 4, 4, 255, 255, 255);
        AssertAt(img, 36, 36, 255, 255, 255);
    }

    [Fact]
    public void Triangle_mesh_with_a_function_maps_the_single_component_through_it()
    {
        var pdf = new FixturePdf();
        int fn = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>");
        // One triangle whose vertices carry t = 0, 1 and 0.5.
        byte[] data = { 0, 4, 4, 0, 0, 36, 4, 255, 0, 20, 36, 128 };
        int sh = pdf.AddStream($"/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 255 0 255 0 1] /Function {fn} 0 R", data);
        var img = Render(pdf.Build(40, 40, "/Sh sh", $"/Shading << /Sh {sh} 0 R >>"));
        AssertAt(img, 6, 6, 247, 0, 8, 25); // near the t = 0 vertex: red
        AssertAt(img, 34, 6, 12, 0, 243, 25); // near the t = 1 vertex: blue
    }

    [Fact]
    public void Mesh_shading_works_as_a_pattern_fill()
    {
        var pdf = new FixturePdf();
        byte[] data = { 0, 0, 0, 255, 0, 0, 0, 40, 0, 0, 255, 0, 0, 0, 40, 0, 0, 255 };
        int sh = pdf.AddStream("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 255 0 255 0 1 0 1 0 1]", data);
        int pat = pdf.Add($"<< /PatternType 2 /Shading {sh} 0 R >>");
        var img = Render(pdf.Build(40, 40, "/Pattern cs /P scn 0 0 20 20 re f", $"/Pattern << /P {pat} 0 R >>"));
        // Only the 20x20 square is painted, with the gradient inside it; the rest stays white.
        AssertBarycentric(img, new V(0, 0, 255, 0, 0), new V(40, 0, 0, 255, 0), new V(0, 40, 0, 0, 255), 2, 2);
        AssertAt(img, 30, 30, 255, 255, 255);
    }
}
