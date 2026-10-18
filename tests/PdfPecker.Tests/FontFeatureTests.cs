using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Fonts;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>font-variant-numeric and font-feature-settings: OpenType features that substitute glyphs.</summary>
public class FontFeatureTests
{
    // A subset of Inter with its tnum, pnum and frac features (SIL Open Font License, see Fixtures/fonts).
    private static readonly byte[] Inter = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "Inter-Features.ttf"));
    private static readonly string Face = "<style>@font-face { font-family: I; src: url('data:font/ttf;base64," + Convert.ToBase64String(Inter) + "'); } p { font-family: I; margin: 0 }</style>";

    /// <summary>The width of the red background behind each span, in page order.</summary>
    private static List<float> SpanWidths(byte[] pdf)
        => Regex.Matches(AllContent(pdf), @"1 0 0 rg\n[\d.]+ [\d.]+ ([\d.]+) [\d.]+ re f")
            .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    private static string Span(string style, string text) => $"<p><span style=\"background:#ff0000; {style}\">{text}</span></p>";

    [Fact]
    public void Tabular_nums_give_every_digit_the_same_width()
    {
        var pdf = ConvertUncompressed(Face + Span("", "1111") + Span("", "0000") + Span("font-variant-numeric: tabular-nums", "1111")
            + Span("font-variant-numeric: tabular-nums", "0000"));
        var w = SpanWidths(pdf);
        Assert.Equal(4, w.Count);
        Assert.True(w[0] < w[1] * 0.8f, $"proportional ones are narrow: {w[0]} vs {w[1]}");
        Assert.Equal(w[2], w[3], 0.01f);
        // Inter's tabular figures are 1328 of 2048 units wide: 4 of them at 12pt.
        Assert.Equal(4 * 1328 / 2048f * 12, w[2], 0.5f); // the background snaps to whole pixels
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Font_feature_settings_and_the_font_variant_shorthand_turn_features_on()
    {
        var pdf = ConvertUncompressed(Face + Span("font-feature-settings: 'tnum'", "1111") + Span("font-variant: tabular-nums", "1111")
            + Span("font-feature-settings: 'tnum' on, 'liga' 0", "1111") + Span("font-feature-settings: 'tnum' 0", "1111")
            + Span("font-variant-numeric: tabular-nums; font-feature-settings: 'tnum' off", "1111"));
        var w = SpanWidths(pdf);
        Assert.Equal(w[0], w[1], 0.01f);
        Assert.Equal(w[0], w[2], 0.01f);
        Assert.True(w[3] < w[0] * 0.8f);
        Assert.Equal(w[3], w[4], 0.01f); // font-feature-settings wins over font-variant-numeric
    }

    [Fact]
    public void Features_are_inherited_and_reset_by_the_font_shorthand()
    {
        var pdf = ConvertUncompressed(Face + "<div style='font-variant-numeric: tabular-nums'>" + Span("", "1111")
            + "<div style='font: 12pt I'>" + Span("", "1111") + "</div></div>");
        var w = SpanWidths(pdf);
        Assert.True(w[1] < w[0] * 0.8f, $"{w[1]} vs {w[0]}");
    }

    [Fact]
    public void Tabular_figures_are_embedded_and_copy_as_digits()
    {
        var pdf = ConvertUncompressed(Face + "<p style='font-variant-numeric: tabular-nums'>2026</p><p>2026</p>");
        var text = Latin1(pdf);
        // Two subsets of the font (a Type0 font and its CIDFont each), one with each set of figures, each mapping its
        // glyphs back to the digits.
        Assert.Equal(4, Regex.Matches(text, @"/BaseFont /[A-Z]{6}\+Inter").Count);
        Assert.Equal(2, Regex.Matches(text, @"/ToUnicode \d+ 0 R").Count);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Feature_variants_of_one_font_are_capped()
    {
        // Bundled fonts live as long as the process, so every new feature setting a server saw used to keep a copy of
        // the font's character map forever.
        var font = FontProgram.Load(Inter)[0];
        var variants = Enumerable.Range(0, 200).Select(i => font.WithFeatures("tnum,ss01=" + (i + 2))).Distinct(ReferenceEqualityComparer.Instance).Count();
        Assert.InRange(variants, 2, 65);
    }

    [Fact]
    public void Contextual_features_and_missing_features_change_nothing()
    {
        var font = FontProgram.Load(Inter)[0];
        Assert.Same(font, font.WithFeatures("frac")); // fractions need context, which is not applied
        Assert.Same(font, font.WithFeatures("onum")); // the font has no old-style figures
        var tnum = font.WithFeatures("tnum");
        Assert.NotSame(font, tnum);
        Assert.Same(tnum, font.WithFeatures("tnum"));
        Assert.NotEqual(font.GlyphId('1'), tnum.GlyphId('1'));
        Assert.Equal(font.GlyphId('A'), tnum.GlyphId('A'));
        Assert.Equal(1328, tnum.Advance(tnum.GlyphId('1')));
    }

    [Theory]
    [InlineData("tabular-nums slashed-zero", null, "tnum,zero")]
    [InlineData("oldstyle-nums", "'onum' 0, 'ss01', \"salt\" 2", "salt=2,ss01")]
    [InlineData(null, "'liga' 1, 'kern'", null)]
    [InlineData(null, "'bad', 'toolong' 1, 'tnum' x", null)]
    public void Feature_keys(string? numeric, string? settings, string? expected)
        => Assert.Equal(expected, FontFeatures.Key(numeric, settings));
}
