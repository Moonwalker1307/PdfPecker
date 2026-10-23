using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Html;

namespace PdfPecker.Tests;

/// <summary>PDF to HTML: page images, the invisible text layer, links, options and limits.</summary>
public class PdfToHtmlTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static byte[] BuildPdf(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R /Info 2 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>A letter-size page with Helvetica words, upward text and links of every kind, then a second page.</summary>
    private static byte[] HandWritten(string annots = "[8 0 R 9 0 R 10 0 R 11 0 R 12 0 R]")
    {
        const string content =
            "BT /F1 12 Tf 72 700 Td (Hello) Tj ET\n" +
            "BT /F1 20 Tf 72 600 Td (a<b & \"c\") Tj ET\n" +
            "BT /F1 12 Tf 0 1 -1 0 300 100 Tm (Upward) Tj ET\n";
        return BuildPdf(
            "<< /Type /Catalog /Pages 3 0 R /Lang (da-DK) /Names << /Dests 14 0 R >> >>",
            "<< /Title (Report <2026>) >>",
            "<< /Type /Pages /Kids [4 0 R 13 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R /Annots " + annots + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 690 112 710] /A << /S /URI /URI (https://example.com/a b) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 650 112 670] /A << /S /URI /URI (javascript:alert\\(1\\)) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 610 112 630] /Dest [13 0 R /Fit] >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 570 112 590] /A << /S /GoTo /D (chapter) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 530 112 550] /F 2 /A << /S /URI /URI (https://hidden.example) >> >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 300 400] /Contents 7 0 R >>",
            "<< /Names [(chapter) [13 0 R /XYZ 0 400 0]] >>");
    }

    private static string Html(byte[] pdf, PdfHtmlOptions? options = null) => PdfToHtml.Convert(pdf, options);

    private static List<string> Spans(string html) => Regex.Matches(html, "<span style=\"([^\"]*)\">([^<]*)</span>").Select(m => m.Value).ToList();

    [Fact]
    public void Pages_become_boxes_with_an_image_and_a_text_layer()
    {
        var html = Html(HandWritten());
        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"da-DK\">", html);
        Assert.Contains("<title>Report &lt;2026&gt;</title>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<div class=\"pdf-page\" id=\"page-1\" style=\"width:612pt;aspect-ratio:612/792;page:p1\">", html);
        Assert.Contains("<div class=\"pdf-page\" id=\"page-2\" style=\"width:300pt;aspect-ratio:300/400;page:p2\">", html);
        Assert.Contains("@page p1{size:612pt 792pt}", html);
        Assert.Contains("@page p2{size:300pt 400pt}", html);
        // Images at the default 150 dpi, as lossless WebP.
        var img = Regex.Match(html, "<img src=\"data:image/webp;base64,([^\"]+)\" width=\"(\\d+)\" height=\"(\\d+)\"");
        Assert.True(img.Success);
        Assert.Equal("1275", img.Groups[2].Value);
        Assert.Equal("1650", img.Groups[3].Value);
        var webp = Convert.FromBase64String(img.Groups[1].Value);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(webp, 8, 4));
        // The second page is empty, so it has no text layer.
        Assert.Single(Regex.Matches(html, "class=\"pdf-text\""));
    }

    [Fact]
    public void Words_cover_their_glyphs()
    {
        var html = Html(HandWritten());
        // "Hello" in 12 pt Helvetica at (72, 700): its baseline is 92 pt from the top. The line is laid out ten times
        // larger and scaled down, and the layer font's ascent is 0.8 em. Each layer character is one em wide, so the
        // five letters, 27.336 pt in Helvetica, are drawn closer together with letter spacing.
        double spacing = ((722 + 556 + 222 + 222 + 556) * 12 / 1000.0 - 5 * 12) / 5 / 12;
        string expected = string.Create(CultureInfo.InvariantCulture,
            $"<span style=\"left:11.7647%;top:{Math.Round((92 - 96) / 792.0 * 100, 4)}%;font-size:19.6078cqw;transform:scale(0.1)\">" +
            $"<span style=\"letter-spacing:{Math.Round(spacing, 4)}em\">Hello</span></span><br>");
        Assert.Contains(expected, html);
        // Text is escaped, and the words of a line are spans in one line box, separated by a space at the end of each but
        // the last, so a browser finds a phrase in it.
        Assert.Matches("<span style=\"[^\"]*\">a&lt;b </span><span style=\"[^\"]*\">&amp; </span><span style=\"[^\"]*\">\"c\"</span></span><br>", html);
        // Text running up the page is turned about the start of its baseline.
        Assert.Matches("<span style=\"[^\"]*;transform:rotate\\(-90deg\\) scale\\(0.1\\)\"><span[^>]*>Upward</span></span>", html);
    }

    [Fact]
    public void Words_that_letter_spacing_cannot_stretch_are_scaled()
    {
        // Browsers put no letter spacing between Arabic letters, which join, nor between a letter and its marks.
        var pdf = HtmlToPdf.Convert("<p style='font-family:Arabic'>سلام world e\u0301te</p>",
            new PdfOptions { Fonts = new FontCollection().AddFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansArabic-Subset.ttf"), "Arabic") });
        var spans = Regex.Matches(Html(pdf), "<span( style=\"([^\"]*)\")?>([^<]*)</span>").Select(m => (Style: m.Groups[2].Value, Text: m.Groups[3].Value)).ToList();
        Assert.Contains(spans, s => s.Text.StartsWith("سلام", StringComparison.Ordinal) && s.Style.Contains("display:inline-block;transform:scaleX(", StringComparison.Ordinal));
        Assert.Contains(spans, s => s.Text.StartsWith("world", StringComparison.Ordinal) && !s.Style.Contains("scaleX", StringComparison.Ordinal));
        Assert.Contains(spans, s => s.Text.StartsWith("e\u0301te", StringComparison.Ordinal) && s.Style.Contains("scaleX", StringComparison.Ordinal));
    }

    [Fact]
    public void Links_go_to_web_addresses_and_converted_pages()
    {
        var html = Html(HandWritten());
        var links = Regex.Matches(html, "<a class=\"pdf-link\" href=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        // A space is escaped, javascript: and the hidden link are dropped, and an explicit and a named destination go to page 2.
        Assert.Equal(new[] { "https://example.com/a%20b", "#page-2", "#page-2" }, links);
        Assert.Contains("href=\"https://example.com/a%20b\" rel=\"noopener noreferrer\" style=\"left:11.7647%;top:10.3535%;width:6.5359%;height:2.5253%\"></a>", html);

        // A link to a page that is not converted is left out, and so are all links when asked.
        var first = Html(HandWritten(), new PdfHtmlOptions { Pages = PdfPageRange.Parse("1") });
        Assert.DoesNotContain("#page-2", first);
        Assert.Contains("https://example.com", first);
        Assert.DoesNotContain("pdf-link\"", Html(HandWritten(), new PdfHtmlOptions { IncludeLinks = false }));
    }

    [Theory]
    [InlineData("https://pdfpecker.net/docs/", "https://pdfpecker.net/docs/")]
    [InlineData("HTTP://Example.com", "http://example.com/")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    [InlineData("tel:+4512345678", "tel:+4512345678")]
    [InlineData(" https://example.com/x?a=1&b=\"2\" ", "https://example.com/x?a=1&b=%222%22")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("JaVaScRiPt:alert(1)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("vbscript:x", null)]
    [InlineData("page2.html", null)]
    [InlineData("//example.com", null)]
    [InlineData("\\\\\u202E", null)] // System.Uri throws IndexOutOfRangeException for these two
    [InlineData("//\u200F", null)]
    [InlineData("", null)]
    public void Only_web_mail_and_phone_addresses_are_kept(string uri, string? expected)
        => Assert.Equal(expected, PageLinks.WebAddress(Encoding.UTF8.GetBytes(uri)));

    [Fact]
    public void Copied_text_reads_as_extracted_text()
    {
        var pdf = HtmlToPdf.Convert("<h1>Quarterly report</h1><p>The first paragraph has <b>bold</b> words.</p>" +
            "<table><tr><td>Apples</td><td>3</td></tr></table>", new PdfOptions { Title = "Q3" });
        var html = Html(pdf);
        Assert.Contains("<title>Q3</title>", html);
        // The text of the layer, as a browser copies it: spans in order, and a line break after each line.
        var layer = Regex.Match(html, "<div class=\"pdf-text\">(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;
        var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(layer, "<br>\n?", "\n"), "<[^>]+>", ""));
        var expected = string.Join("\n", PdfText.ExtractPage(pdf, 1).Lines.Select(l => l.Text)) + "\n";
        Assert.Equal(expected, text);
        Assert.Contains("bold words.\nApples 3\n", text);
    }

    [Fact]
    public void Options_choose_pages_formats_and_text()
    {
        var pdf = HtmlToPdf.Convert("<p>One</p><p style='page-break-before:always'>Two</p><p style='page-break-before:always'>Three</p>");
        var html = Html(pdf, new PdfHtmlOptions { Pages = PdfPageRange.Parse("3,1,1"), ImageFormat = PdfImageFormat.Png, Dpi = 72 });
        // Pages in the order asked for; only the first copy of a page has its id.
        Assert.Equal(new[] { " id=\"page-3\"", " id=\"page-1\"", "" }, Regex.Matches(html, "<div class=\"pdf-page\"( id=\"[^\"]*\")?").Select(m => m.Groups[1].Value));
        Assert.Equal(3, Regex.Matches(html, "data:image/png;base64,").Count);
        Assert.Contains("width=\"595\" height=\"842\"", html);
        Assert.Contains("data:image/jpeg;base64,", Html(pdf, new PdfHtmlOptions { ImageFormat = PdfImageFormat.Jpeg, Dpi = 36 }));
        var noText = Html(pdf, new PdfHtmlOptions { IncludeText = false, Dpi = 36 });
        Assert.DoesNotContain("class=\"pdf-text\"", noText);
        Assert.Contains("<title>PDF document</title>", noText);
        Assert.Contains("<title>Mine &amp; yours</title>", Html(pdf, new PdfHtmlOptions { Title = "Mine & yours", Dpi = 36 }));
        // MaxDimension caps the image, whatever the resolution.
        Assert.Contains("width=\"141\" height=\"200\"", Html(pdf, new PdfHtmlOptions { Pages = PdfPageRange.Parse("1"), MaxDimension = 200 }));
    }

    [Fact]
    public void Output_is_the_same_every_time_and_from_every_overload()
    {
        var pdf = HtmlToPdf.Convert("<p>Same <a href='https://example.com'>every</a> time</p>");
        var options = new PdfHtmlOptions { Dpi = 36 };
        var html = PdfToHtml.Convert(pdf, options);
        Assert.Equal(html, PdfToHtml.Convert(pdf, options));
        Assert.Equal(html, PdfToHtml.Convert(new MemoryStream(pdf), options));
        Assert.Equal(html, PdfToHtml.Convert(pdf, new PdfHtmlOptions { Dpi = 36, MaxDegreeOfParallelism = 1 }));
        var output = new MemoryStream();
        PdfToHtml.Convert(pdf, output, options);
        Assert.Equal(html, new UTF8Encoding(false).GetString(output.ToArray()));
        Assert.True(output.CanWrite); // the stream is left open

        var dir = Path.Combine(Path.GetTempPath(), "pdfpecker-html-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "in.pdf"), pdf);
            PdfToHtml.ConvertFile(Path.Combine(dir, "in.pdf"), Path.Combine(dir, "out.html"), options);
            Assert.Equal(html, File.ReadAllText(Path.Combine(dir, "out.html")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Bad_input_and_limits_fail_with_documented_exceptions()
    {
        var pdf = HtmlToPdf.Convert("<p>One</p><p style='page-break-before:always'>Two</p>");
        Assert.Throws<ArgumentNullException>(() => PdfToHtml.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => PdfToHtml.Convert(pdf, (Stream)null!));
        Assert.Throws<ArgumentException>(() => PdfToHtml.Convert(pdf, new PdfHtmlOptions { ImageFormat = PdfImageFormat.Tiff }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfToHtml.Convert(pdf, new PdfHtmlOptions { Dpi = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfHtmlOptions { Quality = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfToHtml.Convert(pdf, new PdfHtmlOptions { Pages = PdfPageRange.Parse("3") }));
        Assert.Throws<PdfLimitException>(() => PdfToHtml.Convert(pdf, new PdfHtmlOptions { MaxPages = 1 }));
        Assert.Throws<InvalidDataException>(() => PdfToHtml.Convert(Encoding.ASCII.GetBytes("not a pdf")));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfToHtml.Convert(pdf, null, new CancellationToken(true)));

        var locked = HtmlToPdf.Convert("<p>Secret</p>", new PdfOptions { Security = new PdfSecurity { UserPassword = "open" } });
        Assert.Throws<PdfPasswordException>(() => PdfToHtml.Convert(locked));
        Assert.Contains(">Secret</span>", PdfToHtml.Convert(locked, new PdfHtmlOptions { Password = "open", Dpi = 36 }));
    }

    [Fact]
    public void Third_party_pdfs_convert()
    {
        foreach (var name in new[] { "chromium.pdf", "ghostscript.pdf", "libreoffice.pdf", "reportlab.pdf" })
        {
            var pdf = Fixture(Path.Combine("third-party", name));
            var html = Html(pdf, new PdfHtmlOptions { Dpi = 36 });
            Assert.Equal(PdfToImage.GetPageCount(pdf), Regex.Matches(html, "class=\"pdf-page\"").Count);
            var firstWord = PdfText.ExtractPage(pdf, 1).Lines[0].Words[0].Text;
            Assert.Contains(">" + System.Net.WebUtility.HtmlEncode(firstWord).Replace("&#39;", "'", StringComparison.Ordinal), html);
        }
    }

    [Fact]
    public void Text_layer_font_has_a_wide_and_a_zero_width_blank_glyph()
    {
        var bytes = TextLayerFont.Build();
        var font = Assert.Single(FontProgram.Load(bytes));
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(3, font.NumGlyphs);
        Assert.Equal(1000, font.Advance(1));
        Assert.Equal(0, font.Advance(2));
        // Format 13 groups map all of Unicode, starting with the controls and Latin up to U+02FF on the wide glyph.
        var group = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0xAC, 0, 0, 0, 1 }; // U+0000 to U+00AC; U+00AD, a soft hyphen, has no width
        Assert.True(bytes.AsSpan().IndexOf(group) > 0);
        Assert.StartsWith("data:font/ttf;base64,", TextLayerFont.DataUri);
        Assert.True(bytes.Length < 16 * 1024);
    }

    [Theory]
    // The widths Chromium gives these words in the text layer font, in ems: marks and format characters take no room,
    // composed and decomposed accents are alike, and Arabic, Thai and Indic shaping keep one glyph per character.
    [InlineData("abc def", 7)]
    [InlineData("a\u0301bc", 3)]
    [InlineData("\u00e9", 1)]
    [InlineData("سلام", 4)]
    [InlineData("لا", 2)]
    [InlineData("بِسْمِ", 3)]
    [InlineData("اللَّهِ", 4)]
    [InlineData("שָׁלוֹם", 4)]
    [InlineData("नमस्ते", 4)]
    [InlineData("क्षत्रिय", 6)]
    [InlineData("हिंदी", 4)]
    [InlineData("สวัสดี", 4)]
    [InlineData("น้ำ", 2)]
    [InlineData("ผู้ใหญ่", 4)]
    [InlineData("中文。", 3)]
    [InlineData("한국어", 3)]
    [InlineData("\U0001F600", 1)]
    [InlineData("a\u200Db", 2)]
    public void Text_layer_widths_match_the_browser(string text, int ems) => Assert.Equal(ems, TextLayerFont.Ems(text));

    [Fact]
    public void Damaged_text_and_links_are_dropped_not_written()
    {
        // Control characters and lone surrogates, which damaged ToUnicode maps produce, never reach the HTML.
        var sb = new StringBuilder();
        HtmlPageWriter.AppendEscaped(sb, "a\u0001b\u0085c\ud800d\udc00e\U0001F600f￾g<\"", attribute: true);
        Assert.Equal("abcde\U0001F600fg&lt;&quot;", sb.ToString());
        Assert.Equal("0", HtmlPageWriter.Number(double.NaN));
        Assert.Equal("0", HtmlPageWriter.Number(-0.00001));
        Assert.Equal("12.3457", HtmlPageWriter.Number(12.345678));

        // Links with a rectangle of no area or too few numbers are left out, and a page keeps at most PageLinks.MaxPerPage.
        var annots = new StringBuilder("[<< /Subtype /Link /Rect [72 72 72 90] /Dest [4 0 R /Fit] >> << /Subtype /Link /Rect [1 2 3] /Dest [4 0 R /Fit] >>");
        for (int i = 0; i < PageLinks.MaxPerPage + 10; i++) annots.Append(" << /Subtype /Link /Rect [0 0 10 10] /Dest [4 0 R /Fit] >>");
        annots.Append(']');
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 3 0 R >>",
            "<< >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 100 100] /Annots " + annots + " >>");
        var html = Html(pdf, new PdfHtmlOptions { Dpi = 36 });
        Assert.Equal(PageLinks.MaxPerPage, Regex.Matches(html, "<a class=\"pdf-link\" href=\"#page-1\" style=\"left:0%;top:90%;width:10%;height:10%\">").Count);
    }
}
