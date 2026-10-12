using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Fonts;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Standard ligatures (GSUB liga). Expected positions were measured in Chromium's PDF output.</summary>
public class LigatureTests
{
    // A subset of Roboto (Apache License 2.0, see Fixtures/fonts) with its liga and kern features. Its "ffi" ligature is
    // much narrower than f, f and i.
    private static readonly byte[] Roboto = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "Roboto-Liga.ttf"));

    private static byte[] Convert(string style, string text = "difficult office")
        => ConvertUncompressed(
            "<!doctype html><style>@page{margin:0} body{margin:0} @font-face{font-family:R;src:url(data:font/ttf;base64," +
            System.Convert.ToBase64String(Roboto) + ")} div{width:400px;font:20px R;text-align:right}</style>" +
            $"<div style=\"{style}\">{text}</div>");

    private static float FirstTextX(byte[] pdf)
    {
        var m = Regex.Match(AllContent(pdf), @"1 0 0 1 (-?[\d.]+) -?[\d.]+ Tm");
        Assert.True(m.Success);
        return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("", 209.57f)]                                // ffi and ffi ligatures
    [InlineData("font-variant-ligatures:none", 207.06f)]
    [InlineData("font-variant-ligatures:no-common-ligatures", 207.06f)]
    [InlineData("font-feature-settings:'liga' 0", 207.06f)]
    [InlineData("letter-spacing:1px", 195.06f)]              // letter-spaced text has no ligatures
    public void Right_aligned_text_starts_where_chrome_puts_it(string style, float x)
    {
        float actual = FirstTextX(Convert(style));
        Assert.True(Math.Abs(x - actual) <= 0.05f, $"Expected {x} but got {actual}");
    }

    [Fact]
    public void Ligature_glyphs_are_drawn_and_map_back_to_their_characters()
    {
        var pdf = Latin1(Convert(""));
        // The ToUnicode CMap maps the ffi ligature's CID to "ffi", so copied text is unchanged.
        Assert.Matches(new Regex(@"<[0-9A-F]{4}> <006600660069>"), pdf);

        var none = Latin1(Convert("font-variant-ligatures:none"));
        Assert.DoesNotMatch(new Regex(@"<[0-9A-F]{4}> <006600660069>"), none);
    }

    [Fact]
    public void Shaping_replaces_the_characters_of_a_ligature_with_one_glyph()
    {
        var program = Assert.Single(FontProgram.Load(Roboto));
        Assert.NotNull(program.Ligatures);
        var font = new EmbeddedFont(program, false, false);
        var glyphs = new List<ShapedGlyph>();
        font.Shape("office", 0, 6, glyphs);
        Assert.Equal(4, glyphs.Count);                          // o, ffi, c, e
        Assert.Equal((1, 3), (glyphs[1].Start, glyphs[1].Length));
        Assert.NotEqual(program.GlyphId('f'), glyphs[1].Gid);

        // The width is the ligature's, and turning ligatures off gives the characters' own widths.
        Assert.True(font.MeasureUnits("office", 0, 6) < font.WithoutLigatures().MeasureUnits("office", 0, 6));
        Assert.Equal(font.WithoutLigatures().MeasureUnits("of", 0, 2), font.MeasureUnits("of", 0, 2));
    }

    [Fact]
    public void Fonts_without_ligatures_are_unchanged()
    {
        var workSans = Assert.Single(FontProgram.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf"))));
        Assert.Null(workSans.Ligatures);
        var font = new EmbeddedFont(workSans, false, false);
        Assert.Same(font, font.WithoutLigatures());
    }
}
