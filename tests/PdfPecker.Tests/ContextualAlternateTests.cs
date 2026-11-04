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
    public void Lines_break_by_the_width_alternates_across_spaces_draw()
    {
        // Shaped as one run, "3 x 4 x 3 x 4" is 6222 units against 5862 for its words: 117.25px holds the words but not
        // the run, so the last "4" goes to the next line, as in Chrome, rather than past the box's right edge.
        var pdf = ConvertUncompressed(
            "<!doctype html><style>@page{margin:0} body{margin:0} @font-face{font-family:I;src:url(data:font/ttf;base64," +
            System.Convert.ToBase64String(Inter) + ")} div{width:117.25px;font:20px I}</style><div>3 x 4 x 3 x 4</div>");
        var lines = Regex.Matches(AllContent(pdf), @"1 0 0 1 (-?[\d.]+) (-?[\d.]+) Tm");
        Assert.Equal(2, lines.Count);
        Assert.Equal("3 x 4 x 3 x\n4", PdfText.Extract(pdf).Trim().ReplaceLineEndings("\n"));
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

    // Inter's calt draws the x of "3x4" with the glyph of its own multiplication sign, U+00D7. The real sign must still
    // copy as itself and the x as an x, whichever comes first, also in PDF/A-2u, which maps every glyph to Unicode.
    [Theory]
    [InlineData("3x4 and 2×2", PdfConformance.None)]
    [InlineData("2×2 and 3x4", PdfConformance.None)]
    [InlineData("3x4 and 2×2", PdfConformance.PdfA2u)]
    [InlineData("2×2 and 3x4", PdfConformance.PdfA2u)]
    public void A_substituted_glyph_with_a_character_of_its_own_copies_as_both(string text, PdfConformance conformance)
    {
        var html = "<!doctype html><style>@font-face{font-family:I;src:url(data:font/ttf;base64," + System.Convert.ToBase64String(Inter)
            + ")} p{font:20px I}</style><p>" + text + "</p>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Conformance = conformance });
        Assert.Equal(text, PdfText.Extract(pdf).Trim());
        AssertStructurallyValid(pdf);
    }

    // Words that Inter's contextual alternates change by their neighbours across spaces (the x between digits, the
    // arrows, the colons and the brackets next to capitals), words they leave alone, and words with ligatures.
    private static readonly string[] Words =
        ["3", "x", "4", "->", "<-", "10:30", "Go", "(A)", "[b]", "{C}", "--", "==", "i", "office", "Ab", "a-b", ":", "x4", "+", "-", "×", "*", "@", "~",
         "quick", "3x4", "A", "(", ")", "–", "—", "é", "ñ", "1:", ":2", "->>", "a=b", "IJ", "fi", "B:", "12%", "3,400"];

    [Fact]
    public void Text_shaped_a_word_at_a_time_is_shaped_as_a_whole()
    {
        var font = Font();
        var program = font.Program;
        Assert.True(font.ShapesInContext);
        Assert.NotNull(program.WordContext);
        var random = new Random(5);
        List<ShapedGlyph> words = [], whole = [];
        for (int n = 0; n < 3000; n++)
        {
            var text = new System.Text.StringBuilder();
            if (random.Next(8) == 0) text.Append(' ');
            for (int w = 0, count = 1 + random.Next(9); w < count; w++)
            {
                if (w > 0) text.Append(random.Next(6) == 0 ? "  " : " ");
                text.Append(Words[random.Next(Words.Length)]);
            }
            if (random.Next(8) == 0) text.Append(' ');
            string s = text.ToString();
            // A prefix keeps the text from starting at 0, as a line's run does within its paragraph.
            string source = "pre " + s;
            if (!font.ShapeWords(source, 4, s.Length, words)) continue;
            ComplexShaper.Shape(program, source, 4, s.Length, false, whole);
            Assert.True(whole.Count == words.Count, $"'{s}': {whole.Count} glyphs whole, {words.Count} a word at a time");
            for (int k = 0; k < whole.Count; k++)
            {
                var (a, b) = (whole[k], words[k]);
                Assert.True(a.Gid == b.Gid && a.Start == b.Start && a.Length == b.Length && a.Advance == b.Advance && a.Dx == b.Dx && a.Dy == b.Dy,
                    $"'{s}': glyph {k} is {a.Gid}@{a.Start}+{a.Advance} whole and {b.Gid}@{b.Start}+{b.Advance} a word at a time");
            }
        }
    }

    [Fact]
    public void Bundled_fonts_keep_no_shaped_text()
    {
        // A bundled font lives as long as the process, so text it shapes, here Latin with combining marks, is not kept:
        // a stream of documents must not make it grow.
        var font = BundledFonts.Get(StandardFont.All[0])!;
        var glyphs = new List<ShapedGlyph>();
        for (int n = 0; n < 2000; n++)
        {
            string word = "e\u0301" + n.ToString(CultureInfo.InvariantCulture);
            Assert.True(font.UsesShaper(word, 0, word.Length));
            font.MeasureUnits(word, 0, word.Length);
            font.Shape(word, 0, word.Length, glyphs, rtl: false);
        }
        Assert.Equal(0, font.CachedGlyphs);

        // A font of one conversion whose text all goes through the shaper keeps what it shapes.
        var inter = Font();
        inter.MeasureUnits("3 x 4", 0, 5);
        Assert.True(inter.CachedGlyphs > 0);
    }

    [Fact]
    public void The_shape_cache_stays_within_its_bound()
    {
        var cache = new ShapeCache();
        List<ShapedGlyph> none = [], three = [new(1, 0, 1, 500, 0, 0), new(2, 1, 1, 500, 0, 0), new(3, 2, 1, 500, 0, 0)];
        // Entries without glyphs count too.
        for (int n = 0; n < 100; n++)
        {
            string empty = "e" + n.ToString(CultureInfo.InvariantCulture);
            cache.Add(empty, 0, empty.Length, false, none);
        }
        Assert.Equal(100, cache.Count);
        for (int n = 0; n < ShapeCache.MaxGlyphs; n++)
        {
            string s = n.ToString(CultureInfo.InvariantCulture);
            cache.Add(s, 0, s.Length, n % 3 == 0, n % 2 == 0 ? none : three);
            cache.AddWord(n % 4, s, 0, s.Length, three, 0, free: true);
            cache.AddPlain(s, 0, s.Length, plain: true);
            Assert.InRange(cache.Count, 1, ShapeCache.MaxGlyphs);
        }
    }

    [Fact]
    public void Text_is_shaped_whole_when_a_space_kerns_with_the_next_word()
    {
        // Roboto kerns a space with the glyph after it, which shaping a word at a time would leave out.
        var roboto = Assert.Single(FontProgram.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "Roboto-Liga.ttf"))));
        Assert.Null(roboto.WordContext);
    }
}
