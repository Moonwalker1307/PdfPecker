using System.Text.RegularExpressions;
using PdfPecker.Fonts;

namespace PdfPecker.Tests;

/// <summary>
/// Indic, Thai and Lao shaping, compared glyph for glyph with HarfBuzz on subsets of the Noto fonts of each script
/// (SIL Open Font License 1.1, see Fixtures/fonts). The texts are in tools/ShapingReference/corpus.txt and HarfBuzz's
/// glyphs in Fixtures/shaping/harfbuzz.txt, which tools/ShapingReference/generate.py writes.
/// </summary>
public class IndicShapingTests
{
    private static readonly Dictionary<string, FontProgram> Fonts = new();

    private static FontProgram Font(string name)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(name, out var font))
                Fonts[name] = font = Assert.Single(FontProgram.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name + ".woff2"))));
            return font;
        }
    }

    // A text in <U+XXXX ...> form spells out its code points.
    private static string TextOf(string field)
    {
        var m = Regex.Match(field, "^<(.*)>$");
        return m.Success ? string.Concat(m.Groups[1].Value.Split(' ').Select(u => char.ConvertFromUtf32(Convert.ToInt32(u[2..], 16)))) : field;
    }

    public static TheoryData<string, string, string> HarfBuzzCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shaping", "harfbuzz.txt")))
        {
            if (line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            data.Add(fields[0], fields[1], fields[2]);
        }
        return data;
    }

    // Glyphs as "gid@advance,dx,dy" in font units, as HarfBuzz gives them.
    private static string Shape(FontProgram font, string text)
    {
        var glyphs = new List<ShapedGlyph>();
        ComplexShaper.Shape(font, text, 0, text.Length, rtl: false, glyphs);
        float units = font.UnitsPerEm / 1000f;
        return string.Join(" ", glyphs.Select(g => $"{g.Gid}@{MathF.Round(g.Advance * units)},{MathF.Round(g.Dx * units)},{MathF.Round(g.Dy * units)}"));
    }

    [Theory]
    [MemberData(nameof(HarfBuzzCases))]
    public void Shapes_like_HarfBuzz(string font, string text, string expected)
        => Assert.Equal(expected, Shape(Font(font), TextOf(text)));

    [Theory]
    [InlineData("NotoSansDevanagari-Subset", "Price: ", "हिन्दी", " (Hindi)")]
    [InlineData("NotoSansThai-Subset", "Thai ", "ภาษาไทย", " 2026")]
    [InlineData("NotoSansTamil-Subset", "(", "தமிழ்", ")")]
    public void Latin_around_a_complex_script_is_shaped_apart(string font, string before, string text, string after)
    {
        // Each script is shaped on its own, so Latin letters and punctuation around Indic or Thai text come out as
        // they do alone, and the Indic text as it does alone.
        var program = Font(font);
        string expected = string.Join(" ", new[] { before, text, after }.Select(part => Shape(program, part)));
        Assert.Equal(expected, Shape(program, before + text + after));
    }

    [Theory]
    [InlineData("NotoSansDevanagari-Subset", "परीक्षा")]
    [InlineData("NotoSansBengali-Subset", "বাংলা ক্ষমা")]
    [InlineData("NotoSansThai-Subset", "ภาษาไทย")]
    public void Text_extracted_from_the_pdf_is_the_text_written(string font, string text)
    {
        // A conjunct is one glyph, which the PDF maps back to the characters it was made of. (A vowel sign drawn before
        // its consonant is extracted where it is drawn, as the documented limitations say.)
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", font + ".woff2"));
        string html = "<style>@font-face{font-family:F;src:url(data:font/woff2;base64," + Convert.ToBase64String(data) + ")}"
            + "body{font-family:F}</style><p>" + text + "</p>";
        Assert.Equal(text, PdfText.Extract(HtmlToPdf.Convert(html)).Trim());
    }
}
