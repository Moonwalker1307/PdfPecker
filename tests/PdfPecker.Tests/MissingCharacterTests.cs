namespace PdfPecker.Tests;

public class MissingCharacterTests
{
    private static List<string> Missing(string html, PdfOptions? options = null)
    {
        var found = new List<string>();
        options ??= new PdfOptions();
        options.MissingCharacter = found.Add;
        HtmlToPdf.Convert(html, options);
        return found;
    }

    [Fact]
    public void Reports_each_character_the_standard_fonts_lack_once()
    {
        var missing = Missing("<p>Hello æøå € 漢字 ⏰ ★ Ł</p><p>漢 again ⏰</p>");
        Assert.Equal(new[] { "漢", "字", "⏰" }, missing); // the bundled fallback fonts have ★ and Ł
    }

    [Fact]
    public void Checks_embedded_fonts_glyph_by_glyph()
    {
        var missing = Missing("<p>Ærø € ő Ω 漢 😀</p>", new PdfOptions { EmbedStandardFonts = true });
        Assert.DoesNotContain("€", missing);
        Assert.DoesNotContain("ő", missing); // Liberation covers Latin Extended-A and Greek, which WinAnsi does not
        Assert.DoesNotContain("Ω", missing);
        Assert.Contains("漢", missing);
        Assert.Contains("😀", missing);
    }

    [Fact]
    public void Ignores_spaces_and_invisible_characters()
    {
        Assert.Empty(Missing("<p>soft­hyphen zero​width nbsp thin﻿</p>"));
    }

    [Fact]
    public void Covers_headers_watermarks_and_svg_text()
    {
        var options = new PdfOptions { Watermark = new PdfWatermark { Text = "草稿" } };
        var missing = Missing("<p>Body</p><svg width='100' height='20'><text x='0' y='15'>⏰ ✔</text></svg>", options);
        Assert.Contains("草", missing);
        Assert.Contains("⏰", missing);
        Assert.DoesNotContain("✔", missing);
    }

    [Fact]
    public void Throwing_fails_the_conversion()
    {
        var options = new PdfOptions { MissingCharacter = ch => throw new InvalidOperationException("missing " + ch) };
        var ex = Assert.Throws<InvalidOperationException>(() => HtmlToPdf.Convert("<p>OK ⏰</p>", options));
        Assert.Equal("missing ⏰", ex.Message);
        HtmlToPdf.Convert("<p>OK</p>", options); // nothing missing, nothing thrown
    }
}
