using System.Text;

namespace PdfPecker.Tests;

/// <summary>Text extraction from PDFs this library makes, hand-written PDFs and the fixture PDFs.</summary>
public class TextExtractionTests
{
    private static byte[] Font(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));

    private static string Face(string family, string file)
        => "@font-face{font-family:" + family + ";src:url(data:font/ttf;base64," + Convert.ToBase64String(Font(file)) + ")}";

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Paragraphs_come_out_in_reading_order_with_their_words()
    {
        var pdf = HtmlToPdf.Convert(
            "<h1>Quarterly report</h1><p>The first paragraph has <b>bold</b> and <i>italic</i> words.</p>" +
            "<p>Second paragraph, H<sub>2</sub>O and x<sup>2</sup>.</p>");
        var page = Assert.Single(PdfText.ExtractPages(pdf));
        Assert.Equal(new[] { "Quarterly report", "The first paragraph has bold and italic words.", "Second paragraph, H2O and x2." }, Lines(page.Text));
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(595.3, page.Width, 0);

        var line = page.Lines[1];
        Assert.Equal(8, line.Words.Count);
        Assert.Equal("bold", line.Words[4].Text);
        Assert.Equal("Times-Bold", line.Words[4].FontName);
        Assert.Equal("Times-Italic", line.Words[6].FontName);
        Assert.Equal(12, line.Words[0].FontSize, 1);
        Assert.Equal(24, page.Lines[0].Words[0].FontSize, 1);
        // Words go left to right on one line, inside the page, below the heading.
        for (int i = 1; i < line.Words.Count; i++) Assert.True(line.Words[i].X > line.Words[i - 1].X + line.Words[i - 1].Width);
        Assert.True(line.Y > page.Lines[0].Y + page.Lines[0].Height);
        Assert.False(line.RightToLeft);
        Assert.Equal(0, line.Angle);
        // The page text separates paragraphs with an empty line, and Extract gives the same text.
        Assert.Contains("words.\n\nSecond", page.Text);
        Assert.Equal(page.Text, PdfText.Extract(pdf));
    }

    [Fact]
    public void Table_cells_on_a_row_make_one_line()
    {
        var pdf = HtmlToPdf.Convert(
            "<table style='border-collapse:collapse'><tr><th>Item</th><th>Qty</th><th>Price</th></tr>" +
            "<tr><td>Apples</td><td>3</td><td>1.50</td></tr><tr><td>Pears</td><td>12</td><td>4.25</td></tr></table>");
        var page = PdfText.ExtractPage(pdf, 1);
        Assert.Equal(new[] { "Item Qty Price", "Apples 3 1.50", "Pears 12 4.25" }, page.Lines.Select(l => l.Text));
        // Each column lines up.
        Assert.Equal(page.Lines[1].Words[1].X, page.Lines[2].Words[1].X, 1);
    }

    [Fact]
    public void Embedded_fonts_and_ligatures()
    {
        var pdf = HtmlToPdf.Convert("<style>" + Face("R", "Roboto-Liga.ttf") + Face("W", "WorkSans-Regular.ttf") + "</style>" +
            "<p style='font:20px R'>difficult office flow</p><p style='font:16px W'>Work Sans text, 42%</p>");
        var page = PdfText.ExtractPage(pdf, 1);
        Assert.Equal(new[] { "difficult office flow", "Work Sans text, 42%" }, Lines(page.Text));
        Assert.Equal("Roboto-Regular", page.Lines[0].Words[0].FontName);
        Assert.Equal("WorkSans-Regular", page.Lines[1].Words[0].FontName);
    }

    [Fact]
    public void Right_to_left_text_comes_out_in_logical_order()
    {
        var pdf = HtmlToPdf.Convert("<style>" + Face("Ar", "NotoSansArabic-Subset.ttf") + Face("He", "NotoSansHebrew-Subset.ttf") + "</style>" +
            "<p dir='rtl' style='font:20px Ar'>لا سلام</p>" +
            "<div dir='rtl' style='font:16px Ar'><p>بِسْمِ اللَّهِ ١٢٣ <span style='font-family:He'>שָׁלוֹם</span> PDF</p></div>" +
            "<p style='font:20px He'>זהו משפט</p>");
        var page = PdfText.ExtractPage(pdf, 1);
        // Vowel signs and points are kept with their letters, and the Latin word and the digits read left to right.
        Assert.Equal(new[] { "لا سلام", "بِسْمِ اللَّهِ ١٢٣ שָׁלוֹם PDF", "זהו משפט" }, Lines(page.Text));
        Assert.All(page.Lines, l => Assert.True(l.RightToLeft));
        // Words are in reading order, so the first is the rightmost.
        Assert.True(page.Lines[0].Words[0].X > page.Lines[0].Words[1].X);
    }

    [Fact]
    public void Mixed_left_to_right_line_with_a_hebrew_phrase()
    {
        var pdf = HtmlToPdf.Convert("<style>" + Face("He", "NotoSansHebrew-Subset.ttf") + "</style>" +
            "<p style='font:16px He,Helvetica'>The words זהו משפט are Hebrew</p>");
        var line = Assert.Single(PdfText.ExtractPage(pdf, 1).Lines);
        Assert.Equal("The words זהו משפט are Hebrew", line.Text);
        Assert.False(line.RightToLeft);
    }

    [Fact]
    public void Form_field_values_are_included_unless_annotations_are_left_out()
    {
        var pdf = HtmlToPdf.Convert("<form><p><input name=a value='Typed value'></p><p><select name=c><option>One</option><option selected>Two</option></select></p></form>");
        var text = PdfText.Extract(pdf);
        Assert.Contains("Typed value", text);
        Assert.Contains("Two", text);
        Assert.Equal("", PdfText.Extract(pdf, new PdfTextOptions { IncludeAnnotations = false }));
    }

    [Fact]
    public void Encrypted_pdfs_need_their_password()
    {
        var pdf = HtmlToPdf.Convert("<p>Confidential figures</p>", new PdfOptions { Security = new PdfSecurity { UserPassword = "open", OwnerPassword = "admin" } });
        Assert.Throws<PdfPasswordException>(() => PdfText.Extract(pdf));
        Assert.Throws<PdfPasswordException>(() => PdfText.Extract(pdf, new PdfTextOptions { Password = "wrong" }));
        Assert.Equal("Confidential figures", PdfText.Extract(pdf, new PdfTextOptions { Password = "open" }));
        Assert.Equal("Confidential figures", PdfText.Extract(pdf, new PdfTextOptions { Password = "admin" }));
    }

    [Theory]
    [InlineData("encrypted-aes128.pdf", "user", "Chapter one\n\nFirst page text.\n\nName Jane Doe")]
    [InlineData("encrypted-rc4-40.pdf", "user", "Chapter one\n\nFirst page text.\n\nName Jane Doe")]
    [InlineData("password.pdf", "secret", "Shapes, strokes and colors")]
    [InlineData("objstm-cairo.pdf", null, "Hello")]
    public void Fixture_pdfs(string name, string? password, string start)
    {
        var text = PdfText.Extract(Fixture(name), new PdfTextOptions { Password = password });
        Assert.StartsWith(start, text);
    }

    [Fact]
    public void Fixture_with_a_type3_font_optional_content_and_a_rotated_page()
    {
        var pages = PdfText.ExtractPages(Fixture("features.pdf"));
        Assert.Equal("Patterns and shadings", pages[0].Lines[0].Text);
        Assert.Contains(pages[0].Lines, l => l.Text == "Ab");
        // The Type 3 glyphs have names without a character ("tri", "dot"), so their ASCII codes stand for them.
        Assert.Equal("ABABAAA", pages[2].Lines[1].Text);
        var last = pages[^1];
        Assert.StartsWith("Optional content, annotations, rotate 180, crop box", last.Text);
        Assert.Contains("Stamp AP", last.Text);
    }

    [Fact]
    public void Turned_pages_read_along_the_text()
    {
        var pdf = PdfPages.Rotate(HtmlToPdf.Convert("<p>Turned page</p>"), 90);
        var page = PdfText.ExtractPage(pdf, 1);
        var line = Assert.Single(page.Lines);
        Assert.Equal("Turned page", line.Text);
        // The page turns clockwise, so its text runs down the displayed page, near its right edge.
        Assert.Equal(270, line.Angle);
        Assert.True(line.Height > line.Width);
        Assert.True(line.X > page.Width - 100);
    }

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
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] HandWritten()
    {
        const string content =
            "BT /F1 12 Tf 72 700 Td [(Hello)-250(world)] TJ ET\n" +       // a word gap made by positioning, not a space
            "BT /F1 12 Tf 72 680 Td [(Ker)20(ned)] TJ ET\n" +             // kerning is no gap
            "BT /F2 12 Tf 72 660 Td (\\001\\002) Tj ET\n" +               // ligature glyph names from Differences
            "BT 3 Tr /F1 12 Tf 72 640 Td (Invisible) Tj ET\n" +             // invisible text, as on scanned pages
            "/Span << /ActualText (Replaced) >> BDC BT /F1 12 Tf 72 620 Td (XYZ) Tj ET EMC\n" +
            "/OC /oc1 BDC BT /F1 12 Tf 72 600 Td (Hidden) Tj ET EMC\n" +    // optional content that is off
            "BT /F1 12 Tf 2 Tc 72 580 Td (Spaced out) Tj ET\n" +            // letter spacing is no word gap
            "BT /F1 12 Tf 0 1 -1 0 300 100 Tm (Upward) Tj ET\n";
        return BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> /Properties << /oc1 6 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Differences [1 /fi /f_f] >> >>",
            "<< /Type /OCG /Name (Layer) >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream");
    }

    [Fact]
    public void Hand_written_pdf_with_positioned_words_invisible_text_and_actual_text()
    {
        var page = PdfText.ExtractPage(HandWritten(), 1);
        Assert.Equal(new[] { "Hello world", "Kerned", "fiff", "Invisible", "Replaced", "Spaced out", "Upward" }, page.Lines.Select(l => l.Text));
        Assert.Equal(90, page.Lines[^1].Angle);
        Assert.Equal("Hello", page.Lines[0].Words[0].Text);
        Assert.Equal(72, page.Lines[0].Words[0].X, 1);
        Assert.Equal(792 - 700 - 0.8 * 12, page.Lines[0].Words[0].Y, 1);
        // Without expansion the ligature character comes out as the PDF says it.
        var raw = PdfText.ExtractPage(HandWritten(), 1, new PdfTextOptions { ExpandLigatures = false });
        Assert.Equal("ﬁff", raw.Lines[2].Text);
    }

    [Fact]
    public void Pages_overloads_and_limits()
    {
        var pdf = HtmlToPdf.Convert("<p>One</p><p style='page-break-before:always'>Two</p><p style='page-break-before:always'>Three</p>");
        Assert.Equal("One\fTwo\fThree", PdfText.Extract(pdf));
        Assert.Equal("Three\fOne", PdfText.Extract(pdf, new PdfTextOptions { Pages = PdfPageRange.Parse("3,1") }));
        Assert.Equal(new[] { 2, 3 }, PdfText.ExtractPages(pdf, new PdfTextOptions { Pages = PdfPageRange.Parse("2-") }).Select(p => p.PageNumber));
        Assert.Equal("Two", PdfText.ExtractPage(new MemoryStream(pdf), 2).Text);
        Assert.Equal("One\fTwo\fThree", PdfText.Extract(new MemoryStream(pdf)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfText.ExtractPage(pdf, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfText.ExtractPage(pdf, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfText.Extract(pdf, new PdfTextOptions { Pages = PdfPageRange.Parse("5") }));
        Assert.Throws<PdfLimitException>(() => PdfText.Extract(pdf, new PdfTextOptions { MaxPages = 2 }));
        Assert.Throws<InvalidDataException>(() => PdfText.Extract(Encoding.ASCII.GetBytes("not a pdf")));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfText.Extract(pdf, null, new CancellationToken(true)));

        var path = Path.Combine(Path.GetTempPath(), "pdfpecker-text-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            Assert.Equal("One\fTwo\fThree", PdfText.ExtractFile(path));
            Assert.Equal(3, PdfText.ExtractPagesFromFile(path).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
