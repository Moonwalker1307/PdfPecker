using System.Text;
using System.Text.RegularExpressions;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// COLR colour fonts, versions 0 and 1, as Chrome prints them: Type 3 fonts whose glyphs paint their colour drawing,
/// and whose text still extracts. The fonts are Google's COLRv1 test glyphs (Apache License 2.0), subsets of Noto Color
/// Emoji and Nabla (SIL Open Font License 1.1) and ColorGrid, which tools/ColorFonts/generate.py makes.
/// </summary>
public class ColorFontTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));

    private static readonly byte[] TestGlyphs = Fixture("COLRv1-TestGlyphs.ttf");
    private static readonly byte[] Emoji = Fixture("NotoColorEmoji-Subset.ttf");
    private static readonly byte[] Nabla = Fixture("Nabla-Subset.ttf");
    private static readonly byte[] Grid = Fixture("ColorGrid.ttf");

    private static PdfOptions Options(byte[] font, string family, bool compress = true) => new()
    {
        Margins = new PageMargins(0),
        Compress = compress,
        Fonts = new FontCollection().Add(font, family),
    };

    private static RenderedImage Render(byte[] pdf) => PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });

    private static (int R, int G, int B) At(RenderedImage img, float cssX, float cssY)
    {
        int i = ((int)(cssY * 0.75f) * img.Width + (int)(cssX * 0.75f)) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    /// <summary>The distinct colours, rounded to steps of 32, in a square of CSS pixels.</summary>
    private static HashSet<(int, int, int)> Colors(RenderedImage img, float x, float y, float size)
    {
        var set = new HashSet<(int, int, int)>();
        for (float py = y; py < y + size; py += 2)
            for (float px = x; px < x + size; px += 2)
            {
                var (r, g, b) = At(img, px, py);
                set.Add((r / 32, g / 32, b / 32));
            }
        return set;
    }

    private static string Chars(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    [Fact]
    public void A_colour_glyph_is_a_Type3_glyph_that_paints_its_drawing()
    {
        var pdf = HtmlToPdf.Convert("<p style='margin:0;font:100px E'>" + Chars(0xF0E01) + "</p>", Options(TestGlyphs, "E", compress: false));
        var text = Latin1(pdf);
        Assert.Contains("/Subtype /Type3", text);
        Assert.Matches(@"\d+ 0 d0\nq [\d.]+ 0 0 [\d.]+ 0 0 cm /Fx\d+ Do Q", text);
        Assert.DoesNotContain("/FontFile2", text);
        AssertStructurallyValid(pdf);
        // colored_circles_v1: red, green and blue circles over white.
        Assert.True(Colors(Render(pdf), 0, 0, 100).Count >= 4);
    }

    [Fact]
    public void Version_0_glyphs_paint_their_layers()
    {
        // colored_circles_v0: seven rings, red to violet, and a black digit.
        var pdf = HtmlToPdf.Convert("<p style='margin:0;font:100px E'>" + Chars(0xF0E00) + "</p>", Options(TestGlyphs, "E", compress: false));
        Assert.Contains(" 0 d0\n", Latin1(pdf));
        Assert.True(Colors(Render(pdf), 0, 0, 100).Count >= 8);
    }

    [Fact]
    public void Emoji_keep_their_colours_and_their_text()
    {
        string emoji = Chars(0x1F39B, 0x1F39F, 0x1F3AB);
        var pdf = HtmlToPdf.Convert("<p style='margin:0;font:100px E'>" + emoji + "</p>", Options(Emoji, "E"));
        Assert.Equal(emoji, PdfText.Extract(pdf).Trim());
        var img = Render(pdf);
        for (int i = 0; i < 3; i++) Assert.True(Colors(img, i * 100 + 10, 10, 80).Count >= 4, $"emoji {i} has colours");
    }

    [Fact]
    public void Every_test_glyph_converts_and_draws_something()
    {
        var sb = new StringBuilder("<p style='margin:0;font:24px E;width:600px;line-height:1'>");
        int count = 0;
        foreach (var program in Fonts.FontProgram.Load(TestGlyphs))
            foreach (int cp in Enumerable.Range(0xF0000, 0x2000).Concat(Enumerable.Range(0xFE000, 0x10)))
                if (program.GlyphId(cp) != 0)
                {
                    sb.Append(char.ConvertFromUtf32(cp));
                    count++;
                }
        Assert.True(count > 200);
        var pdf = HtmlToPdf.Convert(sb.ToString(), Options(TestGlyphs, "E"));
        AssertStructurallyValid(pdf);
        Assert.True(Colors(Render(pdf), 0, 0, 600).Count > 20);
    }

    [Fact]
    public void Glyphs_that_paint_themselves_draw_nothing()
    {
        // paintcolrglyph_cycle_first refers to the second, which refers back to the first.
        var pdf = HtmlToPdf.Convert("<p style='margin:0;font:100px E'>" + Chars(0xF1100, 0xF1101) + "</p>", Options(TestGlyphs, "E"));
        Assert.Equal((7, 7, 7), Assert.Single(Colors(Render(pdf), 0, 0, 200)));
    }

    [Fact]
    public void More_than_256_colour_glyphs_switch_Type3_fonts_and_keep_their_order()
    {
        // 150 palette squares, red, green and blue in turn, then 150 squares in the text colour.
        string text = string.Concat(Enumerable.Range(0, 300).Select(i => (char)(0xE000 + i)));
        var pdf = HtmlToPdf.Convert("<style>@page { size: 3000px 400px }</style><p style='margin:0;font:10px G;color:#808080'>" + text + "</p>",
            Options(Grid, "G", compress: false));
        var pdfText = Latin1(pdf);
        Assert.Equal(2, Regex.Count(pdfText, "/Subtype /Type3"));
        Assert.Equal(text, PdfText.Extract(pdf).Trim());
        var img = Render(pdf);
        for (int i = 0; i < 300; i++)
        {
            var expected = i >= 150 ? (128, 128, 128) : (i % 3) switch { 0 => (255, 0, 0), 1 => (0, 128, 0), _ => (0, 0, 255) };
            var (r, g, b) = At(img, i * 10 + 5, 5);
            Assert.True(Math.Abs(r - expected.Item1) < 8 && Math.Abs(g - expected.Item2) < 8 && Math.Abs(b - expected.Item3) < 8,
                $"glyph {i}: ({r},{g},{b})");
        }
    }

    [Fact]
    public void Glyphs_in_the_text_colour_follow_the_colour_of_each_run()
    {
        var pdf = HtmlToPdf.Convert("<p style='margin:0;font:40px G'><span style='color:#ff0000'></span>"
                                    + "<span style='color:#0000ff'></span>A</p>", Options(Grid, "G", compress: false));
        // One font for each text colour: red, blue and the paragraph's black.
        Assert.Equal(3, Regex.Count(Latin1(pdf), "/Subtype /Type3"));
        var img = Render(pdf);
        Assert.Equal((255, 0, 0), At(img, 20, 20));
        Assert.Equal((0, 0, 255), At(img, 60, 20));
        Assert.Equal((255, 0, 0), At(img, 100, 20)); // a palette colour stays
        Assert.Equal((0, 0, 0), At(img, 140, 20));   // a glyph without colour has the text colour
    }

    [Fact]
    public void Emoji_sequences_join_into_one_glyph()
    {
        // ColorGrid joins a skin tone, a zero width joiner sequence, a flag and a subdivision flag with its ccmp lookups,
        // as colour emoji fonts do; the joiner and the tags are not drawn.
        const string text = "\uE000\U0001F3FD \uE001\u200D\uE002 \U0001F1E9\U0001F1F0 \U0001F3F4\U000E0067\U000E0062\U000E007F \U0001F3FD";
        var pdf = HtmlToPdf.Convert("<p style='margin:0;font:40px G;white-space:pre'>" + text + "</p>", Options(Grid, "G"));
        var img = Render(pdf);
        Assert.Equal((255, 255, 0), At(img, 20, 20));   // skin tone
        Assert.Equal((255, 0, 255), At(img, 100, 20));  // zero width joiner
        Assert.Equal((0, 255, 255), At(img, 180, 20));  // flag
        Assert.Equal((255, 128, 0), At(img, 260, 20));  // subdivision flag
        Assert.Equal((128, 128, 128), At(img, 340, 20)); // a skin tone alone
        Assert.Equal(text.Replace("\u200D", "", StringComparison.Ordinal), PdfText.Extract(pdf).Trim().Replace("\u200D", "", StringComparison.Ordinal));
    }

    [Fact]
    public void An_emoji_sequence_takes_the_first_face_that_has_all_of_it()
    {
        // Web fonts split by unicode-range, as Google Fonts serves Noto Color Emoji: the first face has U+E000 but not
        // the skin tone, so the sequence comes from the second face, which has both, as in Chrome.
        string url = "data:font/ttf;base64," + Convert.ToBase64String(Grid);
        var pdf = HtmlToPdf.Convert(
            $"<style>@font-face {{ font-family: G; src: url('{url}'); unicode-range: U+E000-E12B }}"
            + $"@font-face {{ font-family: G; src: url('{url}'); unicode-range: U+E000, U+1F3FD }}</style>"
            + "<p style='margin:0;font:40px G'>\uE000\U0001F3FD\uE003</p>",
            new PdfOptions { Margins = new PageMargins(0) });
        var img = Render(pdf);
        Assert.Equal((255, 255, 0), At(img, 20, 20));
        Assert.Equal((255, 0, 0), At(img, 60, 20));
    }

    [Fact]
    public void A_translucent_gradient_stays_inside_its_mask()
    {
        // Source in: the gradient, which needs a soft mask of its own for its alpha, shows only inside the small
        // square, not over the rest of the glyph.
        var img = Render(HtmlToPdf.Convert("<p style='margin:0;font:100px G'>\uE200</p>", Options(Grid, "G")));
        var (r, g, b) = At(img, 50, 40); // the middle of the small square
        Assert.InRange(r, 120, 135);
        Assert.InRange(g, 120, 135);
        Assert.Equal(255, b);
        Assert.Equal((255, 255, 255), At(img, 20, 20)); // inside the glyph's square, outside the small one
    }

    [Fact]
    public void Opacity_applies_to_a_colour_glyph_as_a_whole()
    {
        // Two overlapping layers at half opacity would show the lower one through the upper one; Chrome draws the
        // glyph and then fades it, as a group.
        var pdf = HtmlToPdf.Convert("<p style='margin:0;font:40px G;opacity:0.5'></p>", Options(Grid, "G"));
        var (r, g, b) = At(Render(pdf), 20, 20);
        Assert.InRange(r, 250, 255);
        Assert.InRange(g, 124, 131);
        Assert.InRange(b, 124, 131);
    }

    [Fact]
    public void Variable_colour_fonts_follow_their_axes()
    {
        byte[] Convert(string settings) => HtmlToPdf.Convert(
            "<p style='margin:0;font:100px N;font-variation-settings:" + settings + "'>ABC</p>", Options(Nabla, "N"));
        var light = Render(Convert("\"wght\" 100"));
        var bold = Render(Convert("\"wght\" 700"));
        Assert.NotEqual(light.Rgba, bold.Rgba);
        Assert.Equal("ABC", PdfText.Extract(Convert("\"wght\" 700")).Trim());
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u, PdfAccessibility.None)]
    [InlineData(PdfConformance.None, PdfAccessibility.PdfUA1)]
    public void Colour_fonts_in_archive_and_accessible_documents(PdfConformance conformance, PdfAccessibility accessibility)
    {
        var options = Options(Emoji, "E", compress: false);
        options.Conformance = conformance;
        options.Accessibility = accessibility;
        options.Title = "Emoji";
        var pdf = HtmlToPdf.Convert("<html lang=en><p style='font:20px E'>" + Chars(0x1F39B) + "</p></html>", options);
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("/Type /FontDescriptor", text);
        Assert.Matches(@"/Subtype /Type3 .*/ToUnicode \d+ 0 R", text);
    }
}
