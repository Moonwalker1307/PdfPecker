using System.Text;
using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>Embedded Type 1 fonts (built here from cleartext, eexec and charstring encryption) and Type 3 fonts.</summary>
public class RendererFontTests
{
    // ---------------------------------------------------------------- Type 1 font builder

    private static byte[] Encrypt(byte[] plain, int r, int skip)
    {
        const int c1 = 52845, c2 = 22719;
        var lead = new byte[skip];
        var all = lead.Concat(plain).ToArray();
        var o = new byte[all.Length];
        for (int i = 0; i < all.Length; i++)
        {
            int c = all[i] ^ (r >> 8);
            r = ((c + r) * c1 + c2) & 0xFFFF;
            o[i] = (byte)c;
        }
        return o;
    }

    private static void Number(List<byte> o, int v)
    {
        if (v >= -107 && v <= 107) o.Add((byte)(v + 139));
        else if (v >= 108 && v <= 1131) { v -= 108; o.Add((byte)(247 + (v >> 8))); o.Add((byte)v); }
        else if (v >= -1131 && v <= -108) { v = -v - 108; o.Add((byte)(251 + (v >> 8))); o.Add((byte)v); }
        else throw new ArgumentOutOfRangeException(nameof(v));
    }

    /// <summary>Assembles a charstring from numbers and operator names.</summary>
    private static byte[] CharString(string program)
    {
        var o = new List<byte>();
        foreach (var token in program.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out int n)) { Number(o, n); continue; }
            o.Add(token switch
            {
                "hlineto" => 6, "vlineto" => 7, "rrcurveto" => 8, "closepath" => 9, "callsubr" => 10, "return" => 11, "hsbw" => 13,
                "endchar" => 14, "rmoveto" => 21, "rlineto" => 5, "hmoveto" => 22, "vmoveto" => 4,
                _ => throw new ArgumentException(token),
            });
        }
        return o.ToArray();
    }

    private static byte[] Type1Program(string cleartextEncoding, (string Name, string Program)[] glyphs, string[]? subrs = null)
    {
        var clear = "%!PS-AdobeFont-1.0: TestType1 1.0\n12 dict begin\n/FontInfo 1 dict dup begin /FullName (TestType1) def end readonly def\n" +
                    "/FontName /TestType1 def\n/PaintType 0 def\n/FontType 1 def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n" +
                    cleartextEncoding + "\n/FontBBox {0 0 1000 1000} readonly def\ncurrentdict end\ncurrentfile eexec\n";
        var priv = new List<byte>();
        void Text(string s) => priv.AddRange(Encoding.Latin1.GetBytes(s));
        void Binary(string name, string intro, byte[] charstring, string outro)
        {
            var enc = Encrypt(charstring, 4330, 4);
            Text($"{intro}{enc.Length} RD ");
            priv.AddRange(enc);
            Text($" {outro}\n");
            _ = name;
        }
        Text("dup /Private 8 dict dup begin\n/RD {string currentfile exch readstring pop} executeonly def\n/ND {noaccess def} executeonly def\n" +
             "/NP {noaccess put} executeonly def\n/BlueValues [] def\n/password 5839 def\n");
        if (subrs != null)
        {
            Text($"/Subrs {subrs.Length} array\n");
            for (int i = 0; i < subrs.Length; i++) Binary("", $"dup {i} ", CharString(subrs[i]), "NP");
            Text("ND\n");
        }
        Text($"2 index /CharStrings {glyphs.Length + 1} dict dup begin\n");
        Binary(".notdef", "/.notdef ", CharString("0 500 hsbw endchar"), "ND");
        foreach (var (name, program) in glyphs) Binary(name, $"/{name} ", CharString(program), "ND");
        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        var encrypted = Encrypt(priv.ToArray(), 55665, 4);
        return Encoding.Latin1.GetBytes(clear).Concat(encrypted).ToArray();
    }

    private const string BuiltinEncoding =
        "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 65 /A put\ndup 66 /B put\ndup 67 /C put\nreadonly def";

    // A square, a diamond built with rlineto, and a circle built from four Bezier curves; the square calls a subroutine.
    private static readonly (string, string)[] TestGlyphs =
    {
        ("A", "0 600 hsbw 100 100 rmoveto 0 callsubr 500 vlineto -500 hlineto closepath endchar"),
        ("B", "0 600 hsbw 300 100 rmoveto 200 200 rlineto -200 200 rlineto -200 -200 rlineto closepath endchar"),
        ("C", "0 600 hsbw 100 300 rmoveto 0 110 90 90 110 0 rrcurveto 110 0 90 -90 0 -110 rrcurveto " +
              "0 -110 -90 -90 -110 0 rrcurveto -110 0 -90 90 0 110 rrcurveto closepath endchar"),
    };

    private static byte[] Type1Page(string text, string fontEntries, string cleartextEncoding = BuiltinEncoding, string prefix = "")
    {
        var pdf = new FixturePdf();
        var program = Type1Program(cleartextEncoding, TestGlyphs, new[] { "500 hlineto return" });
        int file = pdf.AddStream($"/Length1 {Encoding.Latin1.GetString(program).IndexOf("eexec\n", StringComparison.Ordinal) + 6} /Length2 {program.Length}", program);
        int desc = pdf.Add($"<< /Type /FontDescriptor /FontName /TestType1 /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent 0 /CapHeight 1000 /StemV 80 /FontFile {file} 0 R >>");
        int font = pdf.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /TestType1 /FirstChar 65 /LastChar 67 /Widths [600 600 600] /FontDescriptor {desc} 0 R {fontEntries} >>");
        return pdf.Build(72, 40, $"{prefix} BT /F1 40 Tf 0 0 Td {text} Tj ET", $"/Font << /F1 {font} 0 R >>");
    }

    [Fact]
    public void Type1_font_glyphs_cover_the_expected_pixels()
    {
        var img = Render(Type1Page("(ABC)", ""));
        // A: the square 100..600 at size 40 covers x and y from 4 to 24.
        AssertAt(img, 14, 14, 0, 0, 0);
        AssertAt(img, 2, 14, 255, 255, 255);
        AssertAt(img, 14, 26, 255, 255, 255);
        // B: the diamond centred at (300,300) in glyph space, offset by the 24 point advance of A.
        AssertAt(img, 36, 12, 0, 0, 0);
        AssertAt(img, 29, 5, 255, 255, 255); // inside the bounding box but outside the diamond
        // C: the circle of radius 200 centred at (300,300), offset by two advances.
        AssertAt(img, 60, 12, 0, 0, 0);
        AssertAt(img, 66, 12, 0, 0, 0);
        AssertAt(img, 69, 12, 255, 255, 255);
        AssertAt(img, 53, 5, 255, 255, 255);
    }

    [Fact]
    public void Type1_glyph_advance_comes_from_the_pdf_widths()
    {
        // The widths array says 600 per glyph, so the second glyph starts 24 points after the first regardless of hsbw.
        var img = Render(Type1Page("(AA)", ""));
        AssertAt(img, 14, 14, 0, 0, 0);
        AssertAt(img, 38, 14, 0, 0, 0);
        AssertAt(img, 26, 14, 255, 255, 255); // gap between the squares: 24..28
    }

    [Fact]
    public void Type1_font_uses_the_pdf_encoding_differences_over_the_builtin_one()
    {
        // Code 200 is remapped to /C, and the builtin encoding is not consulted for it.
        var img = Render(Type1Page("(\\310)", "/Encoding << /Type /Encoding /Differences [200 /C] >> /FirstChar 200 /LastChar 200"));
        AssertAt(img, 12, 12, 0, 0, 0);
        AssertAt(img, 5, 5, 255, 255, 255);
    }

    [Fact]
    public void Type1_font_with_standard_encoding_maps_codes_to_names()
    {
        var img = Render(Type1Page("(B)", "", "/Encoding StandardEncoding def"));
        AssertAt(img, 12, 12, 0, 0, 0);
        AssertAt(img, 5, 5, 255, 255, 255);
    }

    [Fact]
    public void Type1_font_fill_color_follows_the_graphics_state()
    {
        var img = Render(Type1Page("(A)", "", BuiltinEncoding, "0 0 1 rg"));
        AssertAt(img, 14, 14, 0, 0, 255);
    }

    // ---------------------------------------------------------------- Type 3

    private static RenderedImage Type3(string text, int width = 80)
    {
        var pdf = new FixturePdf();
        int sq = pdf.AddStream("", "1000 0 0 0 1000 1000 d1 1 0 0 rg 100 100 800 800 re f");
        int col = pdf.AddStream("", "1000 0 d0 1 0 0 rg 100 100 800 800 re f");
        int tri = pdf.AddStream("", "1000 0 0 0 1000 1000 d1 100 100 m 900 100 l 100 900 l f");
        int bits = pdf.AddStream("", "1000 0 0 0 1000 1000 d1 q 1000 0 0 1000 0 0 cm BI /IM true /W 2 /H 2 /BPC 1 /D [1 0] ID \u0080@ EI Q");
        int font = pdf.Add($"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
                           $"/CharProcs << /sq {sq} 0 R /col {col} 0 R /tri {tri} 0 R /bits {bits} 0 R >> " +
                           "/Encoding << /Type /Encoding /Differences [97 /sq /col /tri /bits] >> /FirstChar 97 /LastChar 100 /Widths [1000 1000 1000 1000] >>");
        return Render(pdf.Build(width, 30, $"0 0 1 rg BT /F1 20 Tf 0 0 Td {text} Tj ET", $"/Font << /F1 {font} 0 R >>"));
    }

    [Fact]
    public void Type3_uncolored_glyph_uses_the_text_fill_color_and_ignores_color_operators()
    {
        var img = Type3("(a)");
        AssertAt(img, 10, 10, 0, 0, 255); // the procedure says red, but d1 makes it uncolored
        AssertAt(img, 1, 10, 255, 255, 255);
        AssertAt(img, 10, 19, 255, 255, 255);
    }

    [Fact]
    public void Type3_colored_glyph_uses_its_own_colors()
    {
        var img = Type3("(ab)");
        AssertAt(img, 30, 10, 255, 0, 0); // d0 keeps the procedure's red
        AssertAt(img, 10, 10, 0, 0, 255);
    }

    [Fact]
    public void Type3_glyph_path_is_drawn_from_its_procedure_and_advances_by_the_widths()
    {
        var img = Type3("(abc)");
        // The triangle is the third glyph (origin at 40): vertices (42,2), (58,2), (42,18).
        AssertAt(img, 45, 5, 0, 0, 255);
        AssertAt(img, 56, 16, 255, 255, 255);
        AssertAt(img, 41, 10, 255, 255, 255);
    }

    [Fact]
    public void Type3_glyph_can_paint_an_inline_image_mask()
    {
        var img = Type3("(d)");
        AssertAt(img, 5, 15, 0, 0, 255); // top left quadrant
        AssertAt(img, 15, 15, 255, 255, 255); // top right
        AssertAt(img, 5, 5, 255, 255, 255); // bottom left
        AssertAt(img, 15, 5, 0, 0, 255); // bottom right
    }
}
