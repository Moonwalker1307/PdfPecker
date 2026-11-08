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
    public void Diagonal_fractions_are_formed_from_the_digits_around_a_slash()
    {
        string[] styles =
        [
            "", "font-variant-numeric: diagonal-fractions", "font-feature-settings: 'frac'",
            "font-variant-numeric: diagonal-fractions tabular-nums",
        ];
        var w = styles.Select(style => SpanWidths(ConvertUncompressed(Face + Span(style, "1/2 3/4")))[0]).ToList();
        // The fractions are smaller figures on either side of a slash, so narrower than the plain text.
        Assert.True(w[1] < w[0] * 0.8f, $"{w[1]} vs {w[0]}");
        Assert.Equal(w[1], w[2], 0.01f);
        // Tabular figures do not widen the fractions' digits: the shaper runs fractions and tnum in the font's order.
        Assert.Equal(w[1], w[3], 0.01f);
        // Text that is not a fraction is left alone.
        var letters = SpanWidths(ConvertUncompressed(Face + Span("", "a/b")))[0];
        Assert.Equal(letters, SpanWidths(ConvertUncompressed(Face + Span("font-variant-numeric: diagonal-fractions", "a/b")))[0], 0.01f);
    }

    [Fact]
    public void A_font_keeps_its_latest_feature_variants()
    {
        // Each variant copies the character map, and bundled and registered fonts serve every conversion, so a font keeps
        // the latest 64. Past 64 settings it used to give every new one the font without features instead.
        var font = FontProgram.Load(Inter)[0];
        int tabularOne = font.WithFeatures("tnum").GlyphId('1');
        for (int i = 0; i < 200; i++) Assert.Equal(tabularOne, font.WithFeatures("tnum,ss01=" + (i + 2)).GlyphId('1'));
        Assert.Equal(64, font.Cached.Variants);
        // Settings that change nothing make no variant and push none out.
        var latest = font.WithFeatures("tnum,ss01=201");
        for (int i = 0; i < 200; i++) Assert.Same(font, font.WithFeatures("onum=" + (i + 2)));
        Assert.Same(latest, font.WithFeatures("tnum,ss01=201"));
        Assert.Equal(64, font.Cached.Variants);
    }

    [Fact]
    public void A_document_keeps_its_feature_variants_and_a_bounded_number_of_them()
    {
        var font = FontProgram.Load(Inter)[0];
        var document = new DocumentFontVariants();
        var tabular = document.WithFeatures(font, "tnum");
        for (int i = 0; i < 100; i++) font.WithFeatures("tnum,ss01=" + (i + 2)); // what other documents ask for
        Assert.NotSame(tabular, font.WithFeatures("tnum"));
        Assert.Same(tabular, document.WithFeatures(font, "tnum"));
        // Settings that change nothing do not count towards the variants one document may have.
        for (int i = 0; i < 100; i++) Assert.Same(font, document.WithFeatures(font, "onum=" + (i + 2)));
        for (int i = 1; i < DocumentFontVariants.MaxFeatureVariants; i++) Assert.NotSame(font, document.WithFeatures(font, "tnum,ss01=" + (i + 2)));
        Assert.Same(font, document.WithFeatures(font, "frac"));
    }

    [Fact]
    public void Registered_and_bundled_fonts_keep_their_features_after_many_documents()
    {
        // Registered and bundled fonts serve every conversion. After 64 feature settings, the features of every new one
        // used to be left out, so a document's output depended on the documents converted before it.
        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        PdfOptions Registered()
        {
            var options = new PdfOptions { CreationDate = date };
            options.Fonts.Add(Inter, "R");
            return options;
        }
        const string Tabular = "<p style='font-family: R; font-variant-numeric: tabular-nums'>1111</p>";
        var fresh = HtmlToPdf.Convert(Tabular, Registered());
        var shared = Registered();
        for (int i = 0; i < 70; i++) HtmlToPdf.Convert($"<p style=\"font-family: R; font-feature-settings: 'tnum', 'ss01' {i + 2}\">1</p>", shared);
        Assert.Equal(fresh, HtmlToPdf.Convert(Tabular, shared));

        // Liberation Sans stands in for Helvetica when the standard fonts are embedded, and its sups feature raises the
        // digits from four to nine; the last setting is one the font has not seen before.
        var embedded = new PdfOptions { CreationDate = date, EmbedStandardFonts = true };
        var plain = HtmlToPdf.Convert("<p style='font-family: Helvetica'>x5</p>", embedded);
        for (int i = 0; i < 70; i++) HtmlToPdf.Convert($"<p style=\"font-family: Helvetica; font-feature-settings: 'sups', 'cv01' {i + 2}\">5</p>", embedded);
        Assert.NotEqual(plain, HtmlToPdf.Convert("<p style=\"font-family: Helvetica; font-feature-settings: 'sups', 'cv01' 1000\">x5</p>", embedded));
    }

    [Fact]
    public void Missing_features_change_nothing_and_contextual_ones_leave_the_character_map_alone()
    {
        var font = FontProgram.Load(Inter)[0];
        Assert.Same(font, font.WithFeatures("onum")); // the font has no old-style figures
        // Fractions substitute in context: the variant keeps the font's character map and has the shaper apply them.
        var frac = font.WithFeatures("frac");
        Assert.NotSame(font, frac);
        Assert.Equal(["frac"], frac.ShapedFeatures!);
        Assert.Equal(font.GlyphId('1'), frac.GlyphId('1'));
        Assert.Null(font.ShapedFeatures);
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
