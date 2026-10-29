using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Fonts;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// font-kerning, font-synthesis, font-size-adjust, font-variant-position and -east-asian, and @font-face font-stretch
/// matching. The fonts are subsets of Inter, Work Sans and Noto Sans SC (SIL Open Font License, see Fixtures/fonts).
/// </summary>
public class FontPropertyTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));

    private static string Face(string family, string file, string descriptors = "")
        => $"@font-face {{ font-family: {family}; src: url('data:font/ttf;base64,{Convert.ToBase64String(Fixture(file))}'); {descriptors} }}";

    private static readonly string InterFace = "<style>" + Face("I", "Inter-Position.otf") + " p { font-family: I; margin: 0 }</style>";

    /// <summary>The width of the red background behind each span, in page order.</summary>
    private static List<float> SpanWidths(byte[] pdf)
        => Regex.Matches(AllContent(pdf), @"(?:1 0 0 rg\n)?[\d.]+ [\d.]+ ([\d.]+) [\d.]+ re f")
            .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    private static string Span(string style, string text) => $"<p><span style=\"background:#ff0000; {style}\">{text}</span></p>";

    // ---- font-kerning

    [Fact]
    public void Font_kerning_none_drops_pair_kerning()
    {
        Assert.NotNull(FontProgram.Load(Fixture("Inter-Position.otf"))[0].Kerning);
        var pdf = ConvertUncompressed(InterFace + Span("", "AVATAR To") + Span("font-kerning: none", "AVATAR To") + Span("font-kerning: normal", "AVATAR To")
            + Span("font-feature-settings: 'kern' 0", "AVATAR To") + Span("font-kerning: none; font-feature-settings: 'kern' 1", "AVATAR To"));
        var w = SpanWidths(pdf);
        Assert.Equal(5, w.Count);
        Assert.True(w[1] > w[0] + 2, $"unkerned text is wider: {w[1]} vs {w[0]}");
        Assert.Equal(w[0], w[2], 0.01f);
        Assert.Equal(w[1], w[3], 0.01f);
        Assert.Equal(w[0], w[4], 0.01f); // font-feature-settings wins, being later
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Font_kerning_is_inherited_and_applies_to_standard_fonts()
    {
        var pdf = ConvertUncompressed("<style>p { margin: 0; font-family: Helvetica }</style>" + Span("", "AVATAR To") + "<div style='font-kerning: none'>" + Span("", "AVATAR To")
            + "<div style='font-kerning: auto'>" + Span("", "AVATAR To") + "</div></div>");
        var w = SpanWidths(pdf);
        Assert.True(w[1] > w[0] + 2, $"{w[1]} vs {w[0]}");
        Assert.Equal(w[0], w[2], 0.01f);
    }

    [Fact]
    public void Unkerned_text_is_drawn_without_kerning_adjustments()
    {
        var kerned = Latin1(ConvertUncompressed(InterFace + "<p>AVAV</p>"));
        var plain = Latin1(ConvertUncompressed(InterFace + "<p style='font-kerning: none'>AVAV</p>"));
        Assert.Contains("] TJ", kerned);
        Assert.DoesNotContain("] TJ", plain);
    }

    // ---- font-synthesis

    private static readonly string RegularOnly = "<style>" + Face("R", "Inter-Position.otf") + " p { font-family: R; margin: 0 }</style>";

    [Fact]
    public void Font_synthesis_none_leaves_missing_bold_and_italic_plain()
    {
        string content = AllContent(ConvertUncompressed(RegularOnly + "<p><b>Bold</b></p>"));
        Assert.Matches(@"\b2 Tr", content); // stroked as well as filled
        content = AllContent(ConvertUncompressed(RegularOnly + "<p><i>Italic</i></p>"));
        Assert.Contains("1 0 0.2 1 ", content);

        content = AllContent(ConvertUncompressed(RegularOnly + "<p style='font-synthesis: none'><b>Bold</b> <i>Italic</i></p>"));
        Assert.DoesNotMatch(@"\b2 Tr", content);
        Assert.DoesNotContain("1 0 0.2 1 ", content);
    }

    [Theory]
    [InlineData("font-synthesis: weight", true, false)]
    [InlineData("font-synthesis: style", false, true)]
    [InlineData("font-synthesis: weight style small-caps", true, true)]
    [InlineData("font-synthesis-weight: none", false, true)]
    [InlineData("font-synthesis-style: none", true, false)]
    [InlineData("font-synthesis: none; font-synthesis-weight: auto", true, false)]
    [InlineData("font-synthesis: bogus", true, true)]
    public void Font_synthesis_picks_the_faces_that_may_be_made_up(string style, bool bold, bool italic)
    {
        string content = AllContent(ConvertUncompressed(RegularOnly + $"<p style='{style}'><b>Bold</b> <i>Italic</i></p>"));
        Assert.Equal(bold, Regex.IsMatch(content, @"\b2 Tr"));
        Assert.Equal(italic, content.Contains("1 0 0.2 1 "));
    }

    [Fact]
    public void Font_synthesis_does_not_stop_a_real_bold_face()
    {
        var pdf = ConvertUncompressed("<style>" + Face("W", "WorkSans-Regular.ttf") + Face("W", "WorkSans-Bold.ttf", "font-weight: bold;")
            + " p { font-family: W; font-synthesis: none }</style><p><b>Bold</b></p>");
        var text = Latin1(pdf);
        Assert.Contains("WorkSans-Bold", text);
        Assert.DoesNotMatch(@"\b2 Tr", AllContent(pdf));
    }

    [Fact]
    public void Small_caps_use_the_fonts_own_glyphs_and_are_only_made_up_when_synthesis_allows()
    {
        // Inter has smcp and c2sc: the glyphs come from the font, so font-synthesis has no say.
        var real = SpanWidths(ConvertUncompressed(InterFace + Span("", "hello") + Span("font-variant-caps: small-caps", "hello")
            + Span("font-variant-caps: small-caps; font-synthesis: none", "hello") + Span("font-variant-caps: all-small-caps", "HELLO") + Span("", "HELLO")));
        Assert.True(Math.Abs(real[1] - real[0]) > 1, $"other glyphs: {real[1]} vs {real[0]}");
        Assert.Equal(real[1], real[2], 0.01f);
        Assert.True(Math.Abs(real[3] - real[4]) > 1);

        // Work Sans has none: they are made from capitals, unless font-synthesis turns that off.
        string face = "<style>" + Face("W", "WorkSans-Regular.ttf") + " p { font-family: W; margin: 0 }</style>";
        var made = SpanWidths(ConvertUncompressed(face + Span("", "hello") + Span("font-variant-caps: small-caps", "hello")
            + Span("font-variant-caps: small-caps; font-synthesis: none", "hello") + Span("font-variant-caps: small-caps; font-synthesis: weight style", "hello")
            + Span("font-variant-caps: small-caps; font-synthesis: small-caps", "hello") + Span("font-variant-caps: small-caps; font-synthesis-small-caps: none", "hello")));
        Assert.True(Math.Abs(made[1] - made[0]) > 1, $"{made[1]} vs {made[0]}");
        Assert.Equal(made[0], made[2], 0.01f);
        Assert.Equal(made[0], made[3], 0.01f);
        Assert.Equal(made[1], made[4], 0.01f);
        Assert.Equal(made[0], made[5], 0.01f);
    }

    // ---- font-size-adjust

    [Fact]
    public void Font_size_adjust_scales_text_to_hold_the_x_height_proportion()
    {
        // Inter's x-height is 1118 of 2048 units, 0.546 em.
        float natural = 1118 / 2048f;
        var pdf = ConvertUncompressed(InterFace + Span("", "xxxxxxxx") + Span($"font-size-adjust: {natural.ToString(CultureInfo.InvariantCulture)}", "xxxxxxxx")
            + Span("font-size-adjust: 0.273", "xxxxxxxx") + Span("font-size-adjust: from-font", "xxxxxxxx") + Span("font-size-adjust: ex-height 0.273", "xxxxxxxx")
            + Span("font-size-adjust: none", "xxxxxxxx") + Span("font-size-adjust: bogus 3 4", "xxxxxxxx"));
        var w = SpanWidths(pdf);
        Assert.Equal(7, w.Count);
        Assert.Equal(w[0], w[1], 0.5f);
        Assert.Equal(w[0] / 2, w[2], 1f);
        Assert.Equal(w[0], w[3], 0.01f);
        Assert.Equal(w[2], w[4], 0.01f);
        Assert.Equal(w[0], w[5], 0.01f);
        Assert.Equal(w[0], w[6], 0.01f);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Font_size_adjust_leaves_em_units_alone_and_is_inherited()
    {
        var pdf = ConvertUncompressed(InterFace + "<div style='font-size-adjust: 0.273'><div style='width: 10em; height: 10px; background: #ff0000'></div>" + Span("", "xxxxxxxx")
            + "<div style='font-size-adjust: none'>" + Span("", "xxxxxxxx") + "</div></div>");
        var w = SpanWidths(pdf);
        Assert.Equal(12 * 10, w[0], 0.01f); // 10em of the computed font size
        Assert.Equal(w[2] / 2, w[1], 1f);
    }

    [Theory]
    [InlineData("none", null)]
    [InlineData("from-font", null)]
    [InlineData("0.5", "XHeight 0.5")]
    [InlineData("ex-height 0.5", "XHeight 0.5")]
    [InlineData("cap-height 0.7", "CapHeight 0.7")]
    [InlineData("ch-width 0.6", "ZeroWidth 0.6")]
    [InlineData("ic-width 1", "IdeographWidth 1")]
    [InlineData("cap-height from-font", null)]
    public void Font_size_adjust_values(string value, string? expected)
    {
        var parsed = FontSizeAdjust.Parse(value);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value.IsNone ? null : $"{parsed.Value.Metric} {parsed.Value.Value.ToString(CultureInfo.InvariantCulture)}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("1 2")]
    [InlineData("ex-height")]
    [InlineData("ex-height 1 2")]
    [InlineData("width 1")]
    public void Font_size_adjust_rejects_invalid_values(string value) => Assert.Null(FontSizeAdjust.Parse(value));

    [Fact]
    public void Font_size_adjust_cannot_blow_text_up()
    {
        var pdf = ConvertUncompressed(InterFace + Span("", "xxxxxxxx") + Span("font-size-adjust: 1e30", "xxxxxxxx") + Span("font-size-adjust: 0.0000001", "xxxxxxxx"));
        var w = SpanWidths(pdf);
        Assert.InRange(w[1], w[0], w[0] * 21);
        Assert.InRange(w[2], 0, w[0]);
    }

    // ---- font-variant-position and font-variant-east-asian

    [Fact]
    public void Font_variant_position_uses_the_fonts_sub_and_superscript_glyphs()
    {
        var pdf = ConvertUncompressed(InterFace + Span("", "x1234") + Span("font-variant-position: sub", "x1234") + Span("font-variant-position: super", "x1234")
            + Span("font-feature-settings: 'sups'", "x1234") + Span("font-variant: super", "x1234") + Span("font-variant-position: super; font-variant-position: normal", "x1234"));
        var w = SpanWidths(pdf);
        Assert.True(w[1] < w[0] * 0.8f && w[2] < w[0] * 0.8f, $"narrower glyphs: {string.Join(", ", w)}");
        Assert.Equal(w[2], w[3], 0.01f);
        Assert.Equal(w[2], w[4], 0.01f);
        Assert.Equal(w[0], w[5], 0.01f);
    }

    [Fact]
    public void Font_variant_position_changes_nothing_for_a_font_without_the_glyphs()
    {
        var pdf = ConvertUncompressed("<style>" + Face("W", "WorkSans-Regular.ttf") + " p { font-family: W; margin: 0 }</style>" + Span("", "x1234") + Span("font-variant-position: super", "x1234"));
        var w = SpanWidths(pdf);
        Assert.Equal(w[0], w[1], 0.01f);
    }

    [Fact]
    public void Font_variant_east_asian_full_width_widens_latin_letters()
    {
        string style = "<style>" + Face("N", "NotoSansSC-Subset.woff2") + " p { font-family: N; margin: 0 }</style>";
        var pdf = ConvertUncompressed(style + Span("", "ABC") + Span("font-variant-east-asian: full-width", "ABC") + Span("font-variant: full-width", "ABC")
            + Span("font-feature-settings: 'fwid'", "ABC") + Span("font-variant-east-asian: full-width ruby", "ABC") + Span("font-variant-east-asian: full-width full-width", "ABC"));
        var w = SpanWidths(pdf);
        Assert.True(w[1] > w[0] * 1.5f, $"{w[1]} vs {w[0]}");
        Assert.Equal(w[1], w[2], 0.01f);
        Assert.Equal(w[1], w[3], 0.01f);
        Assert.Equal(w[1], w[4], 0.01f);
        Assert.Equal(w[0], w[5], 0.01f); // two widths in one declaration are invalid
    }

    [Theory]
    [InlineData(null, null, "jis04", null, "jp04")]
    [InlineData(null, null, "jis78 full-width ruby", null, "fwid,jp78,ruby")]
    [InlineData(null, null, "simplified", null, "smpl")]
    [InlineData(null, null, "traditional proportional-width", null, "pwid,trad")]
    [InlineData(null, null, null, "sub", "subs")]
    [InlineData(null, null, null, "super", "sups")]
    [InlineData("tabular-nums", "'sups' 0", null, "super", "tnum")]
    public void East_asian_and_position_feature_keys(string? numeric, string? settings, string? eastAsian, string? position, string expected)
        => Assert.Equal(expected, FontFeatures.Key(numeric, settings, eastAsian, position));

    [Theory]
    [InlineData("jis04", true)]
    [InlineData("jis04 full-width ruby", true)]
    [InlineData("ruby jis78", true)]
    [InlineData("jis04 jis78", false)]
    [InlineData("full-width proportional-width", false)]
    [InlineData("ruby ruby", false)]
    [InlineData("bogus", false)]
    [InlineData("", false)]
    public void East_asian_values(string value, bool valid) => Assert.Equal(valid, FontFeatures.ValidEastAsian(value));

    // ---- @font-face font-stretch

    private static string StretchFaces(string tail = "")
        => "<style>" + Face("F", "WorkSans-Regular.ttf", "font-stretch: 75%; font-display: swap;") + Face("F", "Inter-Position.otf", "font-stretch: 100%; font-display: optional;")
           + Face("F", "NotoSansSC-Subset.woff2", "font-stretch: 125%;") + " p { font-family: F; margin: 0 } </style>" + tail;

    [Theory]
    [InlineData("", "Inter")]
    [InlineData("font-stretch: condensed", "WorkSans")]
    [InlineData("font-stretch: ultra-condensed", "WorkSans")]
    [InlineData("font-stretch: expanded", "NotoSansSC")]
    [InlineData("font-stretch: ultra-expanded", "NotoSansSC")]
    [InlineData("font-stretch: 90%", "WorkSans")]
    [InlineData("font-stretch: 80%", "WorkSans")]
    [InlineData("font-stretch: 112%", "NotoSansSC")]
    [InlineData("font-stretch: 110%", "NotoSansSC")]
    [InlineData("font-width: condensed", "WorkSans")]
    [InlineData("font-width: 125%", "NotoSansSC")]
    [InlineData("font: condensed 12px F", "WorkSans")]
    public void Font_face_font_stretch_picks_the_closest_width(string style, string expected)
    {
        var pdf = ConvertUncompressed(StretchFaces($"<p style='{style}'>Hello</p>"));
        var fonts = Regex.Matches(Latin1(pdf), @"/BaseFont /[A-Z]{6}\+([A-Za-z]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains(expected, fonts);
        Assert.Single(fonts);
    }

    [Fact]
    public void Font_width_is_the_font_face_descriptor_and_supports_query_too()
    {
        var pdf = ConvertUncompressed("<style>" + Face("H", "WorkSans-Regular.ttf", "font-width: 75%;") + Face("H", "Inter-Position.otf", "font-width: 100%;")
            + " p { font-family: H; margin: 0 }</style><p style='font-width: 75%'>Hello</p>");
        Assert.Contains("WorkSans", Latin1(pdf));
        Assert.DoesNotContain("Inter", Latin1(pdf));
        Assert.True(PdfPecker.Css.CssSupports.Evaluate("(font-width: condensed)"));
    }

    [Fact]
    public void Font_face_stretch_ranges_cover_the_widths_between()
    {
        var pdf = ConvertUncompressed("<style>" + Face("G", "WorkSans-Regular.ttf", "font-stretch: 50% 90%;") + Face("G", "Inter-Position.otf", "font-stretch: 110% 150%;")
            + " p { font-family: G; margin: 0 }</style><p style='font-stretch: 60%'>Hello</p>");
        Assert.Contains("WorkSans", Latin1(pdf));
        pdf = ConvertUncompressed("<style>" + Face("G", "WorkSans-Regular.ttf", "font-stretch: 50% 90%;") + Face("G", "Inter-Position.otf", "font-stretch: 110% 150%;")
            + " p { font-family: G; margin: 0 }</style><p style='font-stretch: 140%'>Hello</p>");
        Assert.Contains("Inter", Latin1(pdf));
    }

    [Fact]
    public void Font_face_without_a_stretch_still_serves_every_width()
    {
        var pdf = ConvertUncompressed("<style>" + Face("S", "WorkSans-Regular.ttf") + " p { font-family: S; font-stretch: condensed }</style><p>Hello</p>");
        Assert.Contains("WorkSans", Latin1(pdf));
    }

    [Theory]
    [InlineData("font-stretch: bogus")]
    [InlineData("font-stretch: -5%")]
    [InlineData("font-stretch: 0%")]
    [InlineData("font-stretch: 50% 60% 70%")]
    [InlineData("font-display: bogus")]
    public void Invalid_font_face_descriptors_are_ignored(string descriptor)
    {
        var pdf = ConvertUncompressed("<style>" + Face("S", "WorkSans-Regular.ttf", descriptor + ";") + " p { font-family: S }</style><p>Hello</p>");
        Assert.Contains("WorkSans", Latin1(pdf));
    }
}

/// <summary>Font property values that are long, extreme or nonsense.</summary>
[Collection(HostileInputCollection.Name)]
public class FontPropertyHostileInputTests
{
    private static byte[] Bounded(string html)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return HtmlToPdf.Convert(html, null, cts.Token);
    }

    [Fact]
    public void Huge_and_malformed_font_property_values_stay_bounded()
    {
        string manyWords = string.Join(" ", Enumerable.Repeat("weight", 100_000));
        string manyEastAsian = string.Join(" ", Enumerable.Repeat("jis04", 100_000));
        string manyMetrics = string.Join(" ", Enumerable.Repeat("ex-height", 100_000));
        var html = "<style>@font-face { font-family: F; src: local(nothing); font-stretch: " + manyWords + "; font-display: " + manyWords + " }</style>"
            + $"<p style='font-synthesis: {manyWords}; font-variant-east-asian: {manyEastAsian}; font-size-adjust: {manyMetrics}'>a</p>"
            + "<p style='font-size-adjust: 1e308; font-kerning: 1e999; font-synthesis-weight: 1e999; font-variant-position: ;font-variant-east-asian: ;font-size-adjust: NaN'>b</p>"
            + "<p style='font-size-adjust: 1e-45; font-size: 1e9px'>c</p>"
            + "<p style='font: 12px/1 serif; font-variant: sub jis04 full-width small-caps; font-size-adjust: cap-height 5'>d</p>";
        PdfTestHelpers.AssertStructurallyValid(Bounded(html));
    }
}
