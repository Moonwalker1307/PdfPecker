using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Fonts;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Contextual alternates (GSUB calt), which browsers apply to all horizontal text unless font-variant-ligatures,
/// font-feature-settings or letter-spacing turns them off. Expected positions were measured in Chromium's PDF output.
/// </summary>
public class ContextualAlternateTests
{
    // A subset of Inter with its calt and kern features (SIL Open Font License, see Fixtures/fonts). Its calt draws
    // "->" as an arrow, the x between digits as a multiplication sign and a colon between digits raised.
    private static readonly byte[] Inter = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "Inter-Calt.ttf"));

    private const string Text = "Go -&gt; 3x4 x 10:30";

    private static byte[] Convert(string style, string text = Text, string inner = "")
        => ConvertUncompressed(
            "<!doctype html><style>@page{margin:0} body{margin:0} @font-face{font-family:I;src:url(data:font/ttf;base64," +
            System.Convert.ToBase64String(Inter) + ")} div{width:400px;font:20px I;text-align:right}</style>" +
            $"<div style=\"{style}\">" + (inner.Length > 0 ? $"<span style=\"{inner}\">{text}</span>" : text) + "</div>");

    // The x of the first text, in CSS pixels.
    private static float FirstTextX(byte[] pdf)
    {
        var m = Regex.Match(AllContent(pdf), @"1 0 0 1 (-?[\d.]+) -?[\d.]+ Tm");
        Assert.True(m.Success);
        return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 0.75f;
    }

    private static EmbeddedFont Font() => new(Assert.Single(FontProgram.Load(Inter)), false, false);

    [Theory]
    [InlineData("", "", 227.16f)]                                     // the arrow, the multiplication sign and the raised colon
    [InlineData("font-variant-ligatures:no-contextual", "", 228.62f)]
    [InlineData("font-variant-ligatures:none", "", 228.62f)]
    [InlineData("font-variant-ligatures:no-common-ligatures", "", 227.16f)]
    [InlineData("font-variant:no-contextual", "", 228.62f)]
    [InlineData("font-feature-settings:'calt' 0", "", 228.62f)]
    [InlineData("letter-spacing:1px", "", 211.62f)]                   // letter-spaced text has no contextual alternates
    [InlineData("font-variant-ligatures:no-contextual", "font-variant-ligatures:normal", 227.16f)]
    [InlineData("font-variant-ligatures:no-contextual", "font-variant-ligatures:common-ligatures", 227.16f)]
    [InlineData("font-variant-ligatures:no-contextual", "font-variant-ligatures:bogus", 228.62f)]
    [InlineData("font-feature-settings:'calt' 0", "font-feature-settings:'tnum'", 227.16f)]
    [InlineData("font-variant-ligatures:no-contextual", "font-feature-settings:'calt' 1", 227.16f)]
    public void Right_aligned_text_starts_where_chrome_puts_it(string style, string inner, float x)
    {
        float actual = FirstTextX(Convert(style, inner: inner));
        Assert.True(Math.Abs(x - actual) <= 0.1f, $"Expected {x} but got {actual}");
    }

    [Theory]
    [InlineData("", 215.91f)]
    [InlineData("text-align:center", 107.95f)]
    public void Lines_are_aligned_by_alternates_that_reach_across_spaces(string style, float x)
    {
        // The x of "3 x 4" becomes a multiplication sign only in the run of words, which is wider than its words.
        float actual = FirstTextX(Convert(style, "Go -&gt; 3 x 4 x 10:30"));
        Assert.True(Math.Abs(x - actual) <= 0.1f, $"Expected {x} but got {actual}");
    }

    [Fact]
    public void Shaping_substitutes_in_context_and_can_be_turned_off()
    {
        var font = Font();
        Assert.True(font.Program.ContextualFeatures.HasFlag(ContextualFeatures.Calt));
        Assert.True(font.ShapesInContext);
        var glyphs = new List<ShapedGlyph>();
        font.Shape("3x4", 0, 3, glyphs);
        Assert.Equal(3, glyphs.Count);
        Assert.NotEqual(font.Program.GlyphId('x'), glyphs[1].Gid);
        // A lone x is the letter.
        font.Shape("a x", 0, 3, glyphs);
        Assert.Equal(font.Program.GlyphId('x'), glyphs[2].Gid);

        var off = font.WithoutContextual();
        Assert.NotSame(font, off);
        Assert.Same(off, font.WithoutContextual());
        Assert.False(off.ShapesInContext);
        ((EmbeddedFont)off).Shape("3x4", 0, 3, glyphs);
        Assert.Equal(font.Program.GlyphId('x'), glyphs[1].Gid);
        // Turning ligatures off leaves contextual alternates on.
        var noLigatures = (EmbeddedFont)font.WithoutLigatures();
        Assert.True(noLigatures.ShapesInContext);
        Assert.Same(off, ((EmbeddedFont)off).WithoutContextual());
    }

    [Fact]
    public void Fonts_without_contextual_alternates_are_not_shaped()
    {
        var workSans = Assert.Single(FontProgram.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf"))));
        Assert.Equal(ContextualFeatures.None, workSans.ContextualFeatures);
        var font = new EmbeddedFont(workSans, false, false);
        Assert.False(font.ShapesInContext);
        Assert.False(font.UsesShaper("office", 0, 6));
        Assert.Same(font, font.WithoutContextual());

        // Neither do the bundled fonts, which draw text when the document names no font of its own.
        foreach (var standard in StandardFont.All)
            if (BundledFonts.Get(standard) is { } bundled) Assert.False(bundled.ShapesInContext, standard.BaseFont);
        Assert.False(BundledFonts.Symbols(false, false)!.ShapesInContext);
    }

    [Fact]
    public void Substituted_glyphs_copy_as_the_original_characters()
    {
        var pdf = Convert("");
        Assert.Equal("Go -> 3x4 x 10:30", PdfText.Extract(pdf).Trim());
        AssertStructurallyValid(pdf);
    }
}
