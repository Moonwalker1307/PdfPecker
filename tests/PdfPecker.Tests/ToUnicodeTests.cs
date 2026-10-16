using System.Text.RegularExpressions;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Every glyph code drawn with an embedded font has a ToUnicode entry, as PDF/A level U (6.2.11.7.2) and PDF/UA require,
/// including the glyphs that shaping substitutes: contextual forms, ligatures, alternates and decomposed characters.
/// </summary>
public class ToUnicodeTests
{
    private static byte[] Font(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));

    private static string Face(string family, string file)
        => "@font-face{font-family:" + family + ";src:url(data:font/ttf;base64," + Convert.ToBase64String(Font(file)) + ")}";

    // The right-to-left sample of the conformance checks, where veraPDF found two Arabic glyphs without a mapping.
    private static string RtlSample =>
        "<html lang='en'><head><title>Scripts</title><style>" + Face("NotoArabic", "NotoSansArabic-Subset.ttf") +
        Face("NotoHebrew", "NotoSansHebrew-Subset.ttf") +
        ".ar { font-family: NotoArabic; direction: rtl; font-size: 18pt } .he { font-family: NotoHebrew; direction: rtl; font-size: 18pt }" +
        "</style></head><body><h1>Mixed scripts</h1>" +
        "<p class='ar' lang='ar'>مرحبا بالعالم 123</p>" +
        "<p class='he' lang='he'>שלום עולם 123</p>" +
        "<p>Latin text with <span class='ar' lang='ar'>عربي</span> inside, and 日本語のテキスト.</p></body></html>";

    /// <summary>
    /// The codes drawn with each embedded font that its ToUnicode CMap does not map, as "resource:code". Reads the
    /// uncompressed output with regular expressions, which is enough for the files this library writes.
    /// </summary>
    private static List<string> UnmappedCodes(byte[] pdf, out int drawn)
    {
        var text = Latin1(pdf);
        string Obj(int id)
        {
            var m = Regex.Match(text, @"(?:^|\n)" + id + @" 0 obj\n");
            Assert.True(m.Success, "Object " + id + " not found");
            int end = text.IndexOf("\nendobj", m.Index + m.Length, StringComparison.Ordinal);
            return text.Substring(m.Index + m.Length, end - m.Index - m.Length);
        }

        var maps = new Dictionary<string, HashSet<int>>();
        foreach (Match r in Regex.Matches(text, @"/(E\d+) (\d+) 0 R"))
        {
            if (maps.ContainsKey(r.Groups[1].Value)) continue;
            var font = Obj(int.Parse(r.Groups[2].Value));
            var tu = Regex.Match(font, @"/ToUnicode (\d+) 0 R");
            Assert.True(tu.Success, r.Groups[1].Value + " has no ToUnicode");
            var cmap = Obj(int.Parse(tu.Groups[1].Value));
            var codes = new HashSet<int>();
            foreach (Match b in Regex.Matches(cmap, @"beginbfchar\n(.*?)endbfchar", RegexOptions.Singleline))
                // An empty destination is a mapping too: the extra glyphs of a shaped cluster stand for no characters.
                // U+0000, U+FEFF and U+FFFE are not allowed as Unicode values.
                foreach (Match e in Regex.Matches(b.Groups[1].Value, @"<([0-9A-F]{4})> <((?:[0-9A-F]{4})*)>"))
                    if (!Regex.IsMatch(e.Groups[2].Value, "^(?:.{4})*?(0000|FEFF|FFFE)")) codes.Add(Convert.ToInt32(e.Groups[1].Value, 16));
            Assert.DoesNotContain("bfrange", cmap);
            maps[r.Groups[1].Value] = codes;
        }

        // Walks the content streams: Tf selects the font, and the hex strings outside dictionaries (marked-content
        // properties may hold an ActualText hex string) are the codes it draws.
        var missing = new List<string>();
        drawn = 0;
        string content = AllContent(pdf);
        string? current = null;
        int depth = 0;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '<' && i + 1 < content.Length && content[i + 1] == '<') { depth++; i++; continue; }
            if (c == '>' && i + 1 < content.Length && content[i + 1] == '>') { depth--; i++; continue; }
            if (c == '(')
            {
                // A literal string: skipped, honouring escapes and nested parentheses.
                int nest = 1;
                for (i++; i < content.Length && nest > 0; i++)
                {
                    if (content[i] == '\\') i++;
                    else if (content[i] == '(') nest++;
                    else if (content[i] == ')') nest--;
                }
                i--;
                continue;
            }
            if (c == '/' && depth == 0)
            {
                var m = Regex.Match(content.Substring(i, Math.Min(40, content.Length - i)), @"^/(\w+) [\d.]+ Tf");
                if (m.Success) current = m.Groups[1].Value;
                continue;
            }
            if (c == '<' && depth == 0)
            {
                int end = content.IndexOf('>', i);
                string hex = content.Substring(i + 1, end - i - 1);
                i = end;
                if (current == null || !maps.TryGetValue(current, out var codes)) continue;
                for (int k = 0; k + 4 <= hex.Length; k += 4)
                {
                    int code = Convert.ToInt32(hex.Substring(k, 4), 16);
                    drawn++;
                    if (!codes.Contains(code)) missing.Add(current + ":" + code);
                }
            }
        }
        return missing;
    }

    private static void AssertAllMapped(byte[] pdf)
    {
        var missing = UnmappedCodes(pdf, out int drawn);
        Assert.True(drawn > 0, "No embedded-font glyphs were found in the content");
        Assert.True(missing.Count == 0, "Glyph codes without a ToUnicode entry: " + string.Join(", ", missing.Distinct()));
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u, PdfAccessibility.None)]
    [InlineData(PdfConformance.None, PdfAccessibility.PdfUA1)]
    [InlineData(PdfConformance.PdfA4, PdfAccessibility.PdfUA2)]
    public void Shaped_arabic_and_hebrew_glyphs_all_map_to_unicode(PdfConformance conformance, PdfAccessibility accessibility)
    {
        var pdf = ConvertUncompressed(RtlSample, new PdfOptions
        {
            Conformance = conformance,
            Accessibility = accessibility,
            EmbedStandardFonts = true,
            Title = "Conformance sample",
            Language = "en",
        });
        AssertAllMapped(pdf);
        // Text extraction reads the ToUnicode map, so the shaped words come back as they were written.
        var lines = PdfText.Extract(pdf).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("مرحبا بالعالم 123", lines);
        Assert.Contains("שלום עולם 123", lines);
        Assert.Contains(lines, l => l.Contains("عربي", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NotoSansArabic-Subset.ttf", "rtl", "لا سلام بِسْمِ اللَّهِ ١٢٣ لأ لإ لآ")]
    [InlineData("NotoSansHebrew-Subset.ttf", "rtl", "שָׁלוֹם זהו משפט בּ שׁ")]
    [InlineData("Roboto-Liga.ttf", "ltr", "difficult office flow fi fl ffi")]
    [InlineData("Inter-Features.ttf", "ltr", "Office 0123 fi")]
    public void Every_drawn_glyph_maps_to_unicode(string file, string dir, string text)
    {
        var pdf = ConvertUncompressed("<style>" + Face("F", file) + "</style>" +
            $"<p dir='{dir}' style='font:20px F'>{text}</p>" +
            $"<p dir='{dir}' style='font:20px F;font-variant:small-caps'>{text}</p>" +
            $"<p dir='{dir}' style=\"font:20px F;font-feature-settings:'ss01','ss02','salt','zero','tnum','case','dlig','smcp','c2sc'\">{text}</p>",
            new PdfOptions { Conformance = PdfConformance.PdfA2u });
        AssertAllMapped(pdf);
        if (dir == "rtl")
        {
            // The letters whose dots shaping draws as separate glyphs come back once each, and the letters with hamza or
            // madda that it draws in two parts come back decomposed, which is canonically the same text.
            var first = PdfText.Extract(pdf).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.Equal(text.Normalize(), first.Normalize());
        }
    }

    [Fact]
    public void Variable_font_instances_map_every_glyph()
    {
        var pdf = ConvertUncompressed("<style>" + Face("V", "RobotoFlex-Subset.ttf") + Face("I", "Inter-Variable-Subset.ttf") + "</style>" +
            "<p style='font:20px V;font-weight:800;font-stretch:75%'>Variable office fi</p>" +
            "<p style=\"font:20px I;font-variation-settings:'wght' 300\">Variable office fi</p>",
            new PdfOptions { Conformance = PdfConformance.PdfA2u });
        AssertAllMapped(pdf);
    }
}
