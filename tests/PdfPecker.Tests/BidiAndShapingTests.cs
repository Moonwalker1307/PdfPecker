using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Text;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Right-to-left text: the Unicode Bidirectional Algorithm, Arabic and Hebrew shaping, and right-to-left layout.
/// Expected glyphs and positions were produced by HarfBuzz with the same fonts.
/// </summary>
public class BidiAndShapingTests
{
    // Subsets of Noto Sans Arabic and Noto Sans Hebrew (SIL Open Font License 1.1, see Fixtures/fonts) with all their
    // OpenType layout features.
    private static readonly byte[] Arabic = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansArabic-Subset.ttf"));
    private static readonly byte[] Hebrew = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansHebrew-Subset.ttf"));

    private static string FontFaces =>
        "@font-face{font-family:Ar;src:url(data:font/ttf;base64," + Convert.ToBase64String(Arabic) + ")}" +
        "@font-face{font-family:He;src:url(data:font/ttf;base64," + Convert.ToBase64String(Hebrew) + ")}";

    // Glyphs in visual order (left to right), as "gid@dx,dy+advance" in font units (both fonts have 1000 units per em).
    private static string ShapeVisual(byte[] fontData, string text)
    {
        var font = new EmbeddedFont(Assert.Single(FontProgram.Load(fontData)), false, false);
        var glyphs = new List<ShapedGlyph>();
        font.Shape(text, 0, text.Length, glyphs, rtl: true);
        glyphs.Reverse();
        return string.Join(" ", glyphs.Select(g => $"{g.Gid}@{MathF.Round(g.Dx)},{MathF.Round(g.Dy)}+{MathF.Round(g.Advance)}"));
    }

    private static List<(float X, string Text)> TextRuns(byte[] pdf)
        => Regex.Matches(AllContent(pdf), @"1 0 0 1 (-?[\d.]+) -?[\d.]+ Tm \((.*?)\) Tj")
            .Select(m => (float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value)).ToList();

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("אבג", "גבא")]
    [InlineData("abc אבג def", "abc גבא def")]
    [InlineData("אבג 123 דהו", "והד 123 גבא")]                // numbers keep their order inside right-to-left text
    [InlineData("אבג (abc) דה", "הד (abc) גבא")]              // brackets are mirrored and pair up (rule N0)
    public void Visual_order_follows_the_bidi_algorithm(string logical, string visual)
    {
        // The paragraph takes the direction of its first strong character (rule P2), and each line is put in visual
        // order (rules L1 and L2) with right-to-left brackets mirrored.
        var types = logical.Select(c => UnicodeTables.Bidi(c)).ToArray();
        var cps = logical.Select(c => (int)c).ToArray();
        int para = Bidi.FirstStrongLevel(types);
        var levels = Bidi.ResolveLevels(types, cps, para);
        Bidi.ResetWhitespace(types, levels, para);
        string actual = string.Concat(Bidi.VisualOrder(levels).Select(k => (levels[k] & 1) != 0 ? (char)UnicodeTables.Mirror(logical[k]) : logical[k]));
        Assert.Equal(visual, actual);
    }

    [Fact]
    public void Levels_of_mixed_text_with_isolates()
    {
        // "abc ⁧אב⁩ d" in a right-to-left paragraph: the Latin text and the isolate (a neutral between Latin letters)
        // are level 2, and the Hebrew inside the isolate is level 3.
        string s = "abc ⁧אב⁩ d";
        var types = s.Select(c => UnicodeTables.Bidi(c)).ToArray();
        var levels = Bidi.ResolveLevels(types, s.Select(c => (int)c).ToArray(), 1);
        Assert.Equal(new byte[] { 2, 2, 2, 2, 2, 3, 3, 2, 2, 2 }, levels);
    }

    [Fact]
    public void Arabic_letters_join_like_harfbuzz()
    {
        // سلام: initial seen, medial lam, final alef and isolated meem.
        Assert.Equal("57@0,0+484 6@0,0+379 54@0,0+220 26@0,0+784", ShapeVisual(Arabic, "سلام"));
    }

    // Noto Sans Arabic draws lam-alef as a special lam and alef pair rather than one ligature glyph.
    [Fact]
    public void Lam_alef_takes_its_special_forms()
        => Assert.Equal("5@0,0+363 56@0,0+219", ShapeVisual(Arabic, "لا"));

    [Fact]
    public void Arabic_harakat_are_placed_on_their_letters()
        => Assert.Equal("138@139,0+0 60@0,0+562 137@301,-126+0 25@0,0+850 138@-35,-191+0 124@31,-3+0 13@0,0+269",
            ShapeVisual(Arabic, "بِسْمِ"));

    [Fact]
    public void Hebrew_points_are_placed_on_their_letters()
        => Assert.Equal("8@0,0+684 19@82,0+0 52@-10,0+291 23@0,0+522 32@227,0+0 42@539,0+0 41@0,0+730",
            ShapeVisual(Hebrew, "שָׁלוֹם"));

    [Fact]
    public void Right_to_left_paragraphs_are_right_aligned()
    {
        var runs = TextRuns(ConvertUncompressed("<!doctype html><style>@page{margin:0} body{margin:0;font:20px Helvetica}</style>" +
            "<p dir=\"rtl\" style=\"width:400px\">abc</p><p style=\"width:400px\">abc</p>"));
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].X > 250, $"rtl text starts at {runs[0].X}");
        Assert.Equal(0, runs[1].X);
    }

    [Fact]
    public void Right_to_left_tables_put_the_first_column_on_the_right()
    {
        var runs = TextRuns(ConvertUncompressed("<!doctype html><style>@page{margin:0} body{margin:0;font:20px Helvetica}</style>" +
            "<table dir=\"rtl\" style=\"width:400px\"><tr><td>A</td><td>B</td></tr></table>"));
        float a = runs.Single(r => r.Text == "A").X, b = runs.Single(r => r.Text == "B").X;
        Assert.True(a > b, $"A at {a}, B at {b}");
    }

    [Fact]
    public void Dir_auto_takes_the_direction_of_the_first_strong_character()
    {
        Assert.Equal(TextAlign.Right, ComputedStyle.Resolve(TextAlign.Start, rtl: true));
        Assert.Equal(TextAlign.Left, ComputedStyle.Resolve(TextAlign.End, rtl: true));
        Assert.Equal(TextAlign.Left, ComputedStyle.Resolve(TextAlign.Start, rtl: false));

        var runs = TextRuns(ConvertUncompressed("<!doctype html><style>@page{margin:0} body{margin:0;font:20px Helvetica}</style>" +
            "<p dir=\"auto\" style=\"width:400px\">1 א</p><p dir=\"auto\" style=\"width:400px\">1 a</p>"));
        Assert.True(runs[0].X > 250, $"the Hebrew paragraph starts at {runs[0].X}");
        Assert.Equal(0, runs[^1].X);
    }

    [Fact]
    public void Shaped_arabic_text_extracts_as_its_characters()
    {
        var pdf = Latin1(ConvertUncompressed("<!doctype html><style>" + FontFaces + "</style>" +
            "<p dir=\"rtl\" style=\"font:20px Ar\">لا سلام</p>"));
        // The joining forms map back to their base letters, so copied text is unchanged.
        Assert.Matches(new Regex(@"<[0-9A-F]{4}> <0644>"), pdf);
        Assert.Matches(new Regex(@"<[0-9A-F]{4}> <0627>"), pdf);
        Assert.Matches(new Regex(@"<[0-9A-F]{4}> <0633>"), pdf);
        Assert.Matches(new Regex(@"<[0-9A-F]{4}> <0645>"), pdf);
    }

    [Fact]
    public void Arabic_and_hebrew_documents_are_valid_pdfs()
    {
        var pdf = ConvertUncompressed("<!doctype html><style>" + FontFaces + "</style>" +
            "<div dir=\"rtl\" style=\"font:16px Ar\"><p>بِسْمِ اللَّهِ ١٢٣ <span style=\"font-family:He\">שָׁלוֹם</span> PDF</p>" +
            "<ul><li>عربي</li></ul><div style=\"display:flex\"><span>ا</span><span>ب</span></div></div>");
        AssertStructurallyValid(pdf);
    }

    private static float EllipsisX(byte[] pdf)
    {
        var m = Regex.Match(AllContent(pdf), "1 0 0 1 (-?[\\d.]+) -?[\\d.]+ Tm \\(\u0085\\) Tj");
        Assert.True(m.Success, "no ellipsis drawn");
        return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("white-space:nowrap;overflow:hidden;text-overflow:ellipsis")]
    [InlineData("overflow:hidden;line-clamp:1")]
    public void The_ellipsis_of_right_to_left_text_is_on_the_left(string style)
    {
        string Html(string dir) => "<!doctype html><style>@page{margin:0} body{margin:0} " + FontFaces + "</style>" +
            $"<div dir=\"{dir}\" style=\"width:200px;font:20px He,Helvetica;{style}\">זהו משפט ארוך בעברית שלא נכנס לתיבה הקטנה</div>";
        // The box is 150pt wide: right to left the text is cut at its left end, left to right at its right end.
        Assert.True(EllipsisX(ConvertUncompressed(Html("rtl"))) < 2);
        Assert.True(EllipsisX(ConvertUncompressed(Html("ltr"))) > 130);
    }

    [Fact]
    public void Svg_text_with_arabic_is_shaped_and_right_aligned_at_its_start()
    {
        var pdf = ConvertUncompressed("<!doctype html><style>" + FontFaces + "</style>" +
            "<svg width=\"300\" height=\"60\"><text x=\"290\" y=\"40\" direction=\"rtl\" font-family=\"Ar\" font-size=\"20\">بِسْمِ</text></svg>");
        string content = AllContent(pdf);
        // Drawn with the registered font, its harakat raised and lowered with text rise (Ts).
        Assert.Contains("/E1 ", content);
        Assert.Contains(" Ts ", content);
        Assert.Matches(new Regex(@"<[0-9A-F]{4}> <0628>"), Latin1(pdf));
    }

    [Fact]
    public void Spaces_are_drawn_with_the_font_that_measured_them()
    {
        // Latin words in an Arabic font fall back to Helvetica, but the spaces between them are the Arabic font's, as
        // in browsers, and as the layout measured them.
        var arabic = new EmbeddedFont(Assert.Single(FontProgram.Load(Arabic)), false, false);
        var helvetica = StandardFont.Get(FontFamily.Helvetica, false, false);
        var chain = new FontChain(arabic, _ => helvetica);
        var runs = new List<FontRun>();
        chain.Split("Widget Pro", runs);
        Assert.Equal(new[] { (0, 6), (6, 1), (7, 3) }, runs.Select(r => (r.Start, r.Length)));
        Assert.Same(arabic, runs[1].Font);
    }

    [Fact]
    public void Arabic_extended_c_letters_outside_the_bmp_join()
    {
        // U+10EC3 is a dual-joining Arabic letter in the supplementary planes, written in UTF-16 as a surrogate pair.
        // Drawn with beh's glyph, three of them take beh's initial, medial and final forms, each with its dot, as three
        // behs do (in visual order: final 10, medial 11 and initial 13).
        const string Supplementary = "\U00010EC3\U00010EC3\U00010EC3";
        var font = WithCodePointAs(Arabic, 0x10EC3, 'ب');
        const string Joined = "124@387,-23+0 10@0,0+1093 124@94,-3+0 11@0,0+343 124@31,-3+0 13@0,0+269";
        Assert.Equal(Joined, ShapeVisual(font, "ببب"));
        Assert.Equal(Joined, ShapeVisual(font, Supplementary));
        Assert.True(ComplexShaper.GuessRtl(Supplementary, 0, Supplementary.Length));
    }

    // The font with a Unicode cmap of format 12 that maps a supplementary code point to the glyph of a BMP character,
    // and every BMP character to its own glyph as before.
    private static byte[] WithCodePointAs(byte[] fontData, int codePoint, char like)
    {
        var program = Assert.Single(FontProgram.Load(fontData));
        var groups = Enumerable.Range(0, 0x10000).Select(c => (Code: c, Gid: program.GlyphId(c))).Where(m => m.Gid != 0)
            .Append((Code: codePoint, Gid: program.GlyphId(like))).ToList();
        var cmap = new List<byte>();
        void U16(int v) { cmap.Add((byte)(v >> 8)); cmap.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        U16(0); U16(1); U16(3); U16(10); U32(12); // one encoding record: Windows, full Unicode, at 12
        U16(12); U16(0); U32(16 + groups.Count * 12); U32(0); U32(groups.Count);
        foreach (var (code, gid) in groups) { U32(code); U32(code); U32(gid); }
        var tables = program.Tables.Select(t => (t.Tag, t.Tag == FontProgram.Tag("cmap") ? cmap.ToArray() : program.Data.AsSpan(t.Offset, t.Length).ToArray())).ToList();
        return SfntBuilder.Build(0x00010000, tables);
    }
}
