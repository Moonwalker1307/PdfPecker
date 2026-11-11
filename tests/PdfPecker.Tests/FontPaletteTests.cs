using System.Text.RegularExpressions;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// font-palette and @font-palette-values: the palette a colour font draws in, as in Chrome. The COLRv1 test glyphs have
/// three palettes, the second marked for dark backgrounds and the third for light ones; Nabla has five, unmarked.
/// </summary>
public class FontPaletteTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));

    private static readonly byte[] TestGlyphs = Fixture("COLRv1-TestGlyphs.ttf");
    private static readonly byte[] Nabla = Fixture("Nabla-Subset.ttf");

    // linear_repeat_0_1: a square filled with a gradient of the first palette entries.
    private static readonly string Square = char.ConvertFromUtf32(0xF0100);

    private static PdfOptions Options(byte[] font, string family = "E", bool compress = true) => new()
    {
        Margins = new PageMargins(0),
        Compress = compress,
        Fonts = new FontCollection().Add(font, family),
        CreationDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static byte[] Pixels(string html, byte[]? font = null, string family = "E")
        => PdfRenderer.Open(HtmlToPdf.Convert(html, Options(font ?? TestGlyphs, family))).RenderPage(0, new ImageOptions { Dpi = 48 }).Rgba;

    private static string Text(string palette, string content = "") => $"<style>{palette}</style><p style='margin:0;font:100px E'>{content}</p>";

    private static string Styled(string css, string style) => Text(css, $"<span style='{style}'>{Square}</span>");

    [Theory]
    [InlineData("light", 2)]
    [InlineData("dark", 1)]
    public void Light_and_dark_pick_the_palette_the_font_marks_for_them(string keyword, int palette)
    {
        var picked = Pixels(Styled("", "font-palette:" + keyword));
        Assert.NotEqual(Pixels(Styled("", "")), picked);
        Assert.Equal(Pixels(Styled($"@font-palette-values --p {{ font-family: E; base-palette: {palette} }}", "font-palette:--p")), picked);
    }

    [Fact]
    public void A_font_without_palette_types_draws_its_first_palette_for_light_and_dark()
    {
        string abc = "<p style='margin:0;font:100px N'>ABC</p>";
        var normal = Pixels(abc, Nabla, "N");
        Assert.Equal(normal, Pixels(abc.Replace("100px N'", "100px N;font-palette:light'"), Nabla, "N"));
        Assert.Equal(normal, Pixels(abc.Replace("100px N'", "100px N;font-palette:dark'"), Nabla, "N"));
    }

    [Fact]
    public void Override_colors_replace_palette_entries()
    {
        const string red = "@font-palette-values --red { font-family: E; override-colors: 0 red, 1 red, 2 red, 3 red, 4 red }";
        var img = PdfRenderer.Open(HtmlToPdf.Convert(Styled(red, "font-palette:--red"), Options(TestGlyphs))).RenderPage(0, new ImageOptions { Dpi = 72 });
        // The middle of the square, which is otherwise a blend of red and blue.
        int i = (img.Width * 40 + 40) * 4;
        Assert.True(img.Rgba[i] > 250 && img.Rgba[i + 1] < 5 && img.Rgba[i + 2] < 5, $"{img.Rgba[i]} {img.Rgba[i + 1]} {img.Rgba[i + 2]}");
    }

    [Theory]
    [InlineData("@font-palette-values --p { font-family: Other; base-palette: 1 }", "font-palette:--p")] // another family
    [InlineData("", "font-palette:--missing")]                                                          // no such rule
    [InlineData("@font-palette-values --p { font-family: E; base-palette: 9 }", "font-palette:--p")]    // no such palette
    [InlineData("@font-palette-values --p { font-family: E; override-colors: 0 currentcolor }", "font-palette:--p")]
    [InlineData("@font-palette-values --p { font-family: E; override-colors: 99 red }", "font-palette:--p")]
    [InlineData("", "font-palette:normal")]
    [InlineData("@font-palette-values p { font-family: E; base-palette: 1 }", "font-palette:p")]        // not a dashed name
    public void Palettes_that_change_nothing_draw_the_fonts_own(string css, string style)
    {
        string html = Styled(css, style);
        var plain = HtmlToPdf.Convert(Styled("", ""), Options(TestGlyphs, compress: false));
        var styled = HtmlToPdf.Convert(html, Options(TestGlyphs, compress: false));
        Assert.Equal(1, Regex.Count(Latin1(styled), "/Subtype /Type3"));
        Assert.Equal(Pixels(Styled("", "")), Pixels(html));
        Assert.Equal(plain, styled);
    }

    [Fact]
    public void The_last_rule_of_a_name_wins_and_the_palette_inherits()
    {
        const string rules = "@font-palette-values --p { font-family: E; base-palette: 2 } @font-palette-values --p { font-family: E; base-palette: 1 }";
        string html = $"<style>{rules}</style><p style='margin:0;font:100px E;font-palette:--p'><span>{Square}</span></p>";
        Assert.Equal(Pixels(Styled("", "font-palette:dark")), Pixels(html));
    }

    [Fact]
    public void Each_palette_is_a_font_of_its_own_and_the_text_stays_text()
    {
        string html = Text("@font-palette-values --p { font-family: E; base-palette: 1 }",
            $"{Square}<span style='font-palette:--p'>{Square}</span><span style='font-palette:light'>{Square}</span><span style='font-palette:--p'>{Square}</span>");
        var pdf = HtmlToPdf.Convert(html, Options(TestGlyphs, compress: false));
        Assert.Equal(3, Regex.Count(Latin1(pdf), "/Subtype /Type3"));
        Assert.Equal(string.Concat(Enumerable.Repeat(Square, 4)), PdfText.Extract(pdf).Trim());
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void A_palette_rule_names_the_family_of_the_font_face()
    {
        // The rule names the @font-face family, not the font's own name, which is how Chrome matches it.
        var css = "@font-face { font-family: Mine; src: url(data:font/ttf;base64," + Convert.ToBase64String(TestGlyphs) + ") }"
            + "@font-palette-values --p { font-family: Mine; base-palette: 1 }";
        string html = $"<style>{css}</style><p style='margin:0;font:100px Mine'><span style='font-palette:--p'>{Square}</span></p>";
        var picked = PdfRenderer.Open(HtmlToPdf.Convert(html, new PdfOptions { Margins = new PageMargins(0) })).RenderPage(0, new ImageOptions { Dpi = 48 }).Rgba;
        Assert.Equal(Pixels(Styled("", "font-palette:dark")), picked);
    }

    [Fact]
    public void Supports_knows_font_palette()
    {
        var pdf = HtmlToPdf.Convert("<style>p { display: none } @supports (font-palette: dark) { p { display: block } }</style><p>yes</p>");
        Assert.Equal("yes", PdfText.Extract(pdf).Trim());
    }
}
