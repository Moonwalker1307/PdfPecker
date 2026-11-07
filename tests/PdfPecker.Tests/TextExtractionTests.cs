using PdfPecker.Rendering;
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
    public void Small_lines_beside_a_large_heading_keep_their_letters_apart()
    {
        // Two small lines in a corner sit within the large heading's line height; joining both to the heading's line
        // used to interleave their letters ("RequDeasttee d2 1b.y1").
        var pdf = HtmlToPdf.Convert(
            "<div style='display:flex;justify-content:space-between;align-items:flex-end'><h1 style='margin:0;font-size:24pt'>Purchase order</h1>" +
            "<div style='font-size:8pt;text-align:right'>Date 21.10.2026<br>Requested by J. Doe</div></div><p>Body text.</p>");
        var lines = PdfText.ExtractPage(pdf, 1).Lines.Select(l => l.Text).ToList();
        Assert.Contains(lines, l => l.Contains("Date 21.10.2026", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Requested by J. Doe", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Purchase order", StringComparison.Ordinal));
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
    public void Type3_font_drawn_at_its_own_scale_keeps_its_words_whole()
    {
        // As a real PDF does: FontMatrix [1 0 0 1 0 0] with glyphs about 72 units high, used at 0.24 points, and each
        // letter group placed on its own with a small gap. The size seen is that of the glyphs, about 17 points, so
        // the gaps are kerning and only the wide one is a word break.
        const string content =
            "BT /F1 0.24 Tf 1 0 0 1 100 700 Tm (ab) Tj 1 0 0 1 120.7 700 Tm (ab) Tj 1 0 0 1 145 700 Tm (ab) Tj ET\n";
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontMatrix [1 0 0 1 0 0] /FontBBox [0 -14 64 58] /CharProcs 5 0 R " +
            "/Encoding << /Differences [97 /a /b] >> /FirstChar 97 /LastChar 98 /Widths [40 40] >>",
            "<< /a 7 0 R /b 7 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Length 33 >>\nstream\n40 0 0 0 40 58 d1 0 0 40 58 re f\nendstream");
        var line = Assert.Single(PdfText.ExtractPage(pdf, 1).Lines);
        Assert.Equal("abab ab", line.Text);
        Assert.Equal(17.3, line.Words[0].FontSize, 1);
    }

    [Fact]
    public void Standard_symbol_fonts_without_a_descriptor_use_their_built_in_encodings()
    {
        // As ReportLab writes them: no FontDescriptor, so no flag says the font is symbolic, and no Encoding.
        const string content =
            "BT /F1 12 Tf 72 700 Td (abgdp \\345\\245) Tj ET\n" +
            "BT /F2 12 Tf 72 680 Td (3456 *) Tj ET\n" +
            "BT /F3 12 Tf 72 660 Td (W) Tj ET\n";
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R /F3 6 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol,Bold >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream");
        var lines = PdfText.ExtractPage(pdf, 1).Lines.Select(l => l.Text);
        Assert.Equal(new[] { "\u03b1\u03b2\u03b3\u03b4\u03c0 \u2211\u221e", "\u2713\u2714\u2715\u2716 \u261b", "\u03a9" }, lines);
    }

    private const string Column1 = "The first column starts the story and runs on for a few lines of text before it ends here.";
    private const string Column2 = "The second column carries the story on to the end with more words than one line can hold.";

    [Fact]
    public void Side_by_side_columns_are_read_one_after_the_other()
    {
        var pdf = HtmlToPdf.Convert(
            "<h1>Two columns</h1><div style='column-count:2;column-gap:40px;width:360px;text-align:left'>" +
            "<p style='margin:0'>" + Column1 + "</p><p style='margin:0;break-before:column'>" + Column2 + "</p></div>" +
            "<p>A last paragraph under both columns.</p>");
        var page = PdfText.ExtractPage(pdf, 1);
        var text = page.Text.Replace('\n', ' ').Replace("  ", " ");
        Assert.Contains(Column1, text);
        Assert.Contains(Column2, text);
        Assert.True(text.IndexOf(Column1, StringComparison.Ordinal) < text.IndexOf(Column2, StringComparison.Ordinal));
        Assert.StartsWith("Two columns\n\n", page.Text);
        Assert.EndsWith("\n\nA last paragraph under both columns.", page.Text);
        // A new column starts a paragraph, and each line holds the words of one column only.
        Assert.Contains("ends here.\n\nThe second", page.Text);
        Assert.Equal(page.Text.Replace("\n\n", "\n"), string.Join("\n", page.Lines.Select(l => l.Text)));
        foreach (var line in page.Lines.Skip(1).SkipLast(1)) Assert.True(line.Width < 180, line.Text);
        Assert.Equal(page.Text, PdfText.Extract(pdf));

        // Read from the top down, the lines run across both columns, and every word is there either way.
        var across = PdfText.ExtractPage(pdf, 1, new PdfTextOptions { Order = PdfTextOrder.TopToBottom });
        Assert.StartsWith("The first column", across.Lines[1].Text);
        Assert.Contains("The second column", across.Lines[1].Text);
        Assert.Equal(across.Lines.SelectMany(l => l.Words).Select(w => w.Text).Order(StringComparer.Ordinal),
            page.Lines.SelectMany(l => l.Words).Select(w => w.Text).Order(StringComparer.Ordinal));
        var split = page.Lines.First(l => l.Text.StartsWith("The second column", StringComparison.Ordinal));
        var whole = across.Lines[1];
        Assert.Equal(whole.Y, split.Y, 1);
        Assert.Equal(whole.Words.First(w => w.Text == "second").X, split.Words[1].X, 3);
        Assert.Equal(split.Words[^1].X + split.Words[^1].Width, split.X + split.Width, 3);
    }

    private static readonly string[] Story =
    [
        "The first trams will run in the spring of 2029, if the contractors keep to the schedule that was agreed last month. The route follows the old railway embankment for most of its length, which keeps the cost of buying land low and avoids the narrow streets of the old town.",
        "Residents along the embankment have asked for noise barriers and for the trees that line the path to be kept. The planning office says that most of the trees can stay, because the tracks need less room than the railway did, and that new trees will be planted where old ones must go.",
        "Who pays for it",
        "The national government pays forty percent of the construction cost under its programme for public transport in medium sized towns. The rest comes from the municipality and the regional health authority, which wants a faster connection for patients and staff.",
        "Ticket prices will match the bus fares, and a single ticket will be valid on both for ninety minutes. Season passes bought before the opening keep their price for the first year.",
        "What happens next",
        "Archaeologists will survey the eastern section during the summer, where a medieval road is thought to cross the embankment. Construction of the depot starts in the autumn, and the first rails are laid the following year.",
        "The council will hold three public meetings before the detailed plans are approved. The first is at the library on the second of March, and the plans can be read online and at the town hall until then.",
        "A smaller second line to the harbour is being studied as well, but no money has been set aside for it yet. Its fate depends on how many passengers the first line carries in its first two years.",
    ];

    private static void AssertStoryInOrder(string text)
    {
        text = text.Replace('\n', ' ').Replace("  ", " ");
        int at = -1;
        foreach (var paragraph in Story)
        {
            int next = text.IndexOf(paragraph, StringComparison.Ordinal);
            Assert.True(next > at, $"Out of order or missing: {paragraph[..Math.Min(30, paragraph.Length)]}\n{text}");
            at = next;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Columns_whose_paragraphs_do_not_line_up_are_read_one_after_the_other(bool tagged)
    {
        // The columns flow freely, so paragraphs run on from one column to the next and the two columns start their
        // paragraphs at different heights, which ends the layout's group of side-by-side lines part of the way down.
        var pdf = HtmlToPdf.Convert(
            "<style>@page{size:A4;margin:18mm 16mm} body{margin:0;font:10.5pt/1.4 serif} h2{font:bold 12.5pt/1.25 sans-serif;margin:10pt 0 4pt}" +
            " p{margin:0 0 6pt}</style><h1>Harbour town plans a new tram line</h1><div style='columns:2;column-gap:9mm;text-align:justify'>" +
            string.Concat(Story.Select(p => p.Length < 20 ? "<h2>" + p + "</h2>" : "<p>" + p + "</p>")) + "</div>",
            new PdfOptions { Accessibility = tagged ? PdfAccessibility.Tagged : PdfAccessibility.None });
        var text = PdfText.ExtractPage(pdf, 1).Text;
        Assert.StartsWith("Harbour town plans a new tram line\n\n", text);
        AssertStoryInOrder(text);
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    public void A_page_turned_with_Rotate_is_read_as_its_text_runs(int degrees, bool tagged)
    {
        // A page with /Rotate shows its text running down, up or upside down; read as displayed, the lines of its two
        // columns lie side by side across the reading direction and were joined into one.
        var upright = HtmlToPdf.Convert(
            "<style>@page{size:A4;margin:18mm 16mm} body{margin:0;font:10.5pt/1.4 serif} h2{font:bold 12.5pt/1.25 sans-serif;margin:10pt 0 4pt}" +
            " p{margin:0 0 6pt}</style><h1>Harbour town plans a new tram line</h1><div style='columns:2;column-gap:9mm;text-align:justify'>" +
            string.Concat(Story.Select(p => p.Length < 20 ? "<h2>" + p + "</h2>" : "<p>" + p + "</p>")) + "</div>",
            new PdfOptions { Accessibility = tagged ? PdfAccessibility.Tagged : PdfAccessibility.None });
        var pdf = PdfPages.Rotate(upright, degrees);
        var (page, uprightPage) = (PdfText.ExtractPage(pdf, 1), PdfText.ExtractPage(upright, 1));
        Assert.Equal(uprightPage.Text, page.Text);
        Assert.StartsWith("Harbour town plans a new tram line\n\n", page.Text);
        AssertStoryInOrder(page.Text);
        Assert.Equal(PdfToMarkdown.Convert(upright), PdfToMarkdown.Convert(pdf));

        // The lines and words are where the page shows them, in the direction their text runs: the title is as far
        // from the edge the top of the page has turned to, and from the one its left edge has, as on the upright page.
        var (title, uprightTitle) = (page.Lines[0], uprightPage.Lines[0]);
        var (top, left) = degrees switch
        {
            90 => (page.Width - (title.X + title.Width), title.Y),
            180 => (page.Height - (title.Y + title.Height), page.Width - (title.X + title.Width)),
            _ => (title.X, page.Height - (title.Y + title.Height)),
        };
        Assert.Equal((360 - degrees) % 360, title.Angle);
        Assert.Equal(degrees == 180 ? 595.3 : 841.9, page.Width, 0.1);
        Assert.Equal(degrees == 180 ? uprightTitle.Width : uprightTitle.Height, title.Width, 0.01);
        Assert.Equal(uprightTitle.Y, top, 0.01);
        Assert.Equal(uprightTitle.X, left, 0.01);
        var words = page.Lines.SelectMany(l => l.Words).ToList();
        var uprightWords = uprightPage.Lines.SelectMany(l => l.Words).ToList();
        Assert.Equal(uprightWords.Count, words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            var (w, u) = (words[i], uprightWords[i]);
            var (x, y) = degrees switch
            {
                90 => (uprightPage.Height - (u.Y + u.Height), u.X),
                180 => (uprightPage.Width - (u.X + u.Width), uprightPage.Height - (u.Y + u.Height)),
                _ => (u.Y, uprightPage.Width - (u.X + u.Width)),
            };
            Assert.True(Math.Abs(w.X - x) < 0.01 && Math.Abs(w.Y - y) < 0.01, $"{w.Text} at {w.X}, {w.Y} instead of {x}, {y}");
        }
    }

    [Fact]
    public void An_upright_heading_over_vertical_writing_stays_first()
    {
        // Most of the page's letters run down the page, but the page is upright and so is the heading at its top.
        var pdf = HtmlToPdf.Convert(
            "<style>@page{size:A5;margin:15mm} body{margin:0;font:11pt/1.5 serif}</style><h1>Vertical writing</h1>" +
            "<div style='writing-mode:vertical-rl;height:120mm'>" + string.Concat(Story.Take(4).Select(p => "<p>" + p + "</p>")) + "</div>");
        Assert.StartsWith("Vertical writing\n", PdfText.ExtractPage(pdf, 1).Text);
    }

    [Fact]
    public void Right_to_left_columns_are_read_from_the_right()
    {
        // Hebrew in two columns: their lines share baselines, so each column's lines are cut from lines across the
        // page, and the cut lines must stay right to left for the right column to be read first.
        string[] paragraphs =
        {
            "מועצת העיר החליטה ביום שלישי לבנות קו רכבת קלה באורך אחד עשר קילומטרים בין התחנה המרכזית לבית החולים החדש. הרכבות הראשונות ייצאו לדרך באביב 2029.",
            "הקו יעבור לאורך סוללת הרכבת הישנה, וכך תישאר עלות רכישת הקרקע נמוכה. התושבים ביקשו קירות נגד רעש ושמירה על העצים.",
            "הממשלה תממן ארבעים אחוז מעלות הבנייה, והשאר יגיע מהעירייה ומרשות הבריאות האזורית. מחיר הכרטיס יהיה זהה למחיר באוטובוס.",
            "הפגישה הציבורית הראשונה תתקיים בספרייה ב־2 במרץ, והתוכניות זמינות לעיון באתר העירייה עד אז.",
        };
        var pdf = HtmlToPdf.Convert("<html dir='rtl'><style>@page{size:A4;margin:18mm 16mm} body{margin:0;font:12pt/1.4 sans-serif}" +
            " p{margin:0 0 8pt}</style><h1>עיר הנמל בונה קו רכבת קלה</h1><div style='columns:2;column-gap:8mm'>" +
            string.Concat(paragraphs.Select(p => "<p>" + p + "</p>")) + "</div></html>");
        var text = PdfText.ExtractPage(pdf, 1).Text.Replace('\n', ' ').Replace("  ", " ");
        Assert.StartsWith("עיר הנמל בונה קו רכבת קלה ", text);
        int at = -1;
        foreach (var paragraph in paragraphs)
        {
            int next = text.IndexOf(paragraph, StringComparison.Ordinal);
            Assert.True(next > at, $"Out of order or missing: {paragraph[..20]}\n{text}");
            at = next;
        }
    }

    [Fact]
    public void Arabic_letters_joined_by_cursive_attachment_stay_one_word()
    {
        // Glyphs as Chrome places Noto Naskh Arabic at 13 pt, each on its own and in visual order: cursive attachment
        // puts the final noon of "عن" half a size under the line and leaves gaps of a third of the size before the
        // wide dad of "محاضرة" and "ومعرضاً", while the spaces between the words are drawn.
        (string Text, double X, double End, double Y)[] drawn =
        {
            ("ي", 55.50, 61.74, 89.55), ("ذ", 63.73, 67.90, 90.01), ("ل", 67.90, 70.65, 86.25), ("ا", 70.65, 73.75, 86.25),
            (" ", 73.75, 76.62, 86.25), ("م", 76.62, 82.97, 86.25), ("ا", 82.97, 86.26, 86.25), ("ع", 86.26, 91.64, 86.25),
            ("ل", 91.64, 94.40, 86.25), ("ا", 94.40, 97.49, 86.25), (" ", 97.49, 100.36, 86.25), ("ن", 103.01, 107.97, 92.94),
            ("ع", 107.97, 114.53, 86.25),
            ("اً", 382.92, 386.22, 196.35), ("ض", 390.40, 396.44, 199.65), ("ر", 396.44, 401.69, 195.75), ("ع", 401.69, 407.07, 195.75),
            ("م", 407.07, 413.00, 195.75), ("و", 412.99, 419.08, 195.75), (" ", 419.08, 421.95, 195.75), ("ة", 422.55, 427.25, 199.57),
            ("ر", 427.25, 432.50, 195.75), ("ض", 436.67, 442.72, 199.65), ("ا", 442.72, 446.01, 195.75), ("ح", 446.00, 454.66, 195.75),
            ("م", 454.66, 460.59, 195.75), (" ", 460.59, 463.46, 195.75), ("1", 463.46, 470.73, 195.75), ("4", 470.73, 478.00, 195.75),
            ("0", 478.00, 485.27, 195.75),
        };
        var glyphs = drawn.Select(g => new TextGlyph
        {
            Text = g.Text, X = g.X, Y = g.Y, EndX = g.End, EndY = g.Y, DirX = 1, Size = 13, Font = "NotoNaskhArabic-Regular", Moved = true, Mcid = -1,
        }).ToList();
        var page = TextLayout.Build(glyphs, 1, 595, 842, expandLigatures: false);
        Assert.Equal(new[] { "عن العام الذي", "140 محاضرة ومعرضاً" }, page.Lines.Select(l => l.Text));
    }

    [Fact]
    public void The_lines_of_the_longer_column_stay_in_that_column()
    {
        // Two columns of lines placed one by one, drawn across the page, where the left column has two lines more
        // than the right: those two lines have nothing beside them and end the left column, not the page.
        var left = new[]
        {
            "The first trams will run in the spring of", "2029, if the contractors keep to the", "schedule that was agreed last month. The",
            "route follows the old railway embankment for", "most of its length, which keeps the cost of", "buying land low and avoids the narrow",
            "streets of the old town. Residents along the", "embankment have asked for noise barriers and", "for the trees that line the path to be kept.",
            "The planning office says that most of the", "trees can stay.",
        };
        var right = new[]
        {
            "The national government pays forty percent", "of the construction cost under its programme", "for public transport in medium sized towns.",
            "The rest comes from the municipality and the", "regional health authority, which wants a", "faster connection for patients and staff.",
            "Ticket prices will match the bus fares, and", "a single ticket will be valid on both for", "ninety minutes.",
        };
        var html = new StringBuilder("<div style='position:relative;height:200mm;font:10.5pt serif'>");
        for (int i = 0; i < left.Length; i++)
        {
            string top = (i * 5.2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            html.Append($"<div style='position:absolute;white-space:nowrap;left:0;top:{top}mm'>{left[i]}</div>");
            if (i < right.Length) html.Append($"<div style='position:absolute;white-space:nowrap;left:92mm;top:{top}mm'>{right[i]}</div>");
        }
        html.Append("</div>");
        var page = PdfText.ExtractPage(HtmlToPdf.Convert(html.ToString()), 1);
        Assert.Equal(left.Concat(right), page.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_tagged_paragraph_that_runs_on_into_the_next_column_is_read_in_column_order()
    {
        // As word processors write two columns: the second paragraph starts at the foot of the left column and ends at
        // the top of the right one, as one tagged paragraph whose text is drawn in that order.
        var content = new StringBuilder();
        string Line(double x, double y, string text) => $"BT /F1 10 Tf {x.ToString(System.Globalization.CultureInfo.InvariantCulture)} {y.ToString(System.Globalization.CultureInfo.InvariantCulture)} Td ({text}) Tj ET\n";
        var first = new[] { "First paragraph line one", "First paragraph line two", "First paragraph line three" };
        var secondLeft = new[] { "Second paragraph begins here", "and goes on down the column" };
        var secondRight = new[] { "and ends at the top of the", "right column after the turn." };
        var third = new[] { "Third paragraph line one", "Third paragraph line two" };
        content.Append("/P <</MCID 0>> BDC\n");
        for (int i = 0; i < first.Length; i++) content.Append(Line(72, 700 - 14 * i, first[i]));
        content.Append("EMC\n/P <</MCID 1>> BDC\n");
        for (int i = 0; i < secondLeft.Length; i++) content.Append(Line(72, 700 - 14 * (first.Length + 1 + i), secondLeft[i]));
        for (int i = 0; i < secondRight.Length; i++) content.Append(Line(320, 700 - 14 * i, secondRight[i]));
        content.Append("EMC\n/P <</MCID 2>> BDC\n");
        for (int i = 0; i < third.Length; i++) content.Append(Line(320, 700 - 14 * (secondRight.Length + 1 + i), third[i]));
        content.Append("EMC");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /StructParents 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R 10 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 2 >>",
        };
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var page = PdfText.ExtractPage(Encoding.Latin1.GetBytes(pdf.ToString()), 1);
        Assert.Equal(first.Concat(secondLeft).Concat(secondRight).Concat(third), page.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Text_in_one_column_reads_as_from_the_top_down()
    {
        const string html = "<h1>Invoice</h1><p>Billed to Example Trading Ltd, 1 Harbour Street.</p>" +
            "<table style='border-collapse:collapse'><tr><th style='border:1px solid'>Item</th><th style='border:1px solid'>Amount</th></tr>" +
            "<tr><td style='border:1px solid'>Consulting</td><td style='border:1px solid'>1,200.00</td></tr></table>" +
            "<ul><li>Payment within thirty days.</li><li>Late payment adds interest.</li></ul><p>Thank you.</p>" +
            "<p style='font-size:30px'>Large text</p><p>Small text close under it, then more.</p>";
        foreach (var accessibility in new[] { PdfAccessibility.None, PdfAccessibility.Tagged })
        {
            var pdf = HtmlToPdf.Convert(html, new PdfOptions { Accessibility = accessibility });
            var reading = PdfText.ExtractPage(pdf, 1);
            var down = PdfText.ExtractPage(pdf, 1, new PdfTextOptions { Order = PdfTextOrder.TopToBottom });
            Assert.Equal(down.Text, reading.Text);
            Assert.Equal(down.Lines.Select(l => (l.Text, l.X, l.Y, l.Width, l.Height)), reading.Lines.Select(l => (l.Text, l.X, l.Y, l.Width, l.Height)));
        }
    }

    [Fact]
    public void Tagged_pdfs_are_read_in_the_order_of_their_tags()
    {
        // The second paragraph of the HTML is placed above the first, so its tag comes second but its text is higher.
        const string html = "<div style='position:relative;height:200px'>" +
            "<p style='position:absolute;top:120px;margin:0'>First in the document, lower on the page.</p>" +
            "<p style='position:absolute;top:20px;margin:0'>Second in the document, higher on the page.</p></div>";
        var tagged = HtmlToPdf.Convert(html, new PdfOptions { Accessibility = PdfAccessibility.Tagged });
        Assert.Equal("First in the document, lower on the page.\n\nSecond in the document, higher on the page.", PdfText.ExtractPage(tagged, 1).Text);
        Assert.Equal("Second in the document, higher on the page.\n\nFirst in the document, lower on the page.",
            PdfText.ExtractPage(tagged, 1, new PdfTextOptions { Order = PdfTextOrder.TopToBottom }).Text);
        // Without tags, the page is read from the top down.
        Assert.Equal("Second in the document, higher on the page.\n\nFirst in the document, lower on the page.", PdfText.ExtractPage(HtmlToPdf.Convert(html), 1).Text);
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
