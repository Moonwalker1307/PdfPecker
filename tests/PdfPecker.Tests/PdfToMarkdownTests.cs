using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>PDF to Markdown: the structure of the structured HTML, written as GitHub Flavored Markdown.</summary>
public class PdfToMarkdownTests
{
    private static byte[] Pdf(string html) => HtmlToPdf.Convert(html);

    private static string Md(string html, PdfMarkdownOptions? options = null) => PdfToMarkdown.Convert(Pdf(html), options);

    /// <summary>The text of some HTML, with tags removed and spaces collapsed.</summary>
    private static string Text(string html)
        => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html.Replace("<br>", " ", StringComparison.Ordinal), "<[^>]+>", " ")), @"\s+", " ").Trim();

    /// <summary>The text Markdown shows once a Markdown reader has turned it into HTML.</summary>
    private static string Shown(string markdown) => Text(MarkdownToHtml.Convert(markdown));

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "third-party", name);

    [Fact]
    public void Headings_paragraphs_and_emphasis_are_markdown()
    {
        var md = Md("<h1>Quarterly report</h1><h2>Summary</h2><p>Sales grew with <b>strong</b> demand and <i>fair</i> weather.</p>");
        Assert.Equal("# Quarterly report\n\n## Summary\n\nSales grew with **strong** demand and *fair* weather.\n", md);
    }

    [Fact]
    public void Lists_keep_their_bullets_and_numbers()
    {
        var md = Md("<ul><li>Apples</li><li>Pears</li></ul><p>Then:</p><ol start=\"3\"><li>Three</li><li>Four</li></ol>");
        Assert.Contains("- Apples\n- Pears\n\nThen:\n\n3. Three\n4. Four\n", md, StringComparison.Ordinal);
        var html = MarkdownToHtml.Convert(md);
        Assert.Contains("<ol start=\"3\">", html, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(html, "<ul>|<ol").Count);
    }

    [Fact]
    public void A_list_numbered_again_from_one_is_a_list_of_its_own()
    {
        var md = Md("<ol><li>One</li><li>Two</li></ol><ol><li>Again one</li></ol>");
        Assert.Equal("1. One\n2. Two\n\n1) Again one\n", md);
        Assert.Equal(2, Regex.Matches(MarkdownToHtml.Convert(md), "<ol>").Count);
    }

    [Fact]
    public void The_lines_of_a_list_item_after_a_break_stay_in_the_item()
    {
        var md = Md("<ul><li>Short<br>The second line of the item that is long enough</li><li>Next</li></ul>");
        Assert.Equal("- Short\\\n  The second line of the item that is long enough\n- Next\n", md);
        Assert.Equal(2, Regex.Matches(MarkdownToHtml.Convert(md), "<li>").Count);
    }

    [Fact]
    public void A_table_with_a_header_row_is_a_pipe_table()
    {
        var md = Md("""
            <style>td,th{border:1px solid #000;padding:4px}table{border-collapse:collapse}</style>
            <table><tr><th>Item</th><th>Qty</th><th>Price</th></tr>
            <tr><td>Apples</td><td>3</td><td>12.00</td></tr>
            <tr><td>Pears</td><td>5</td><td>7.50</td></tr></table>
            """);
        Assert.Equal("| Item | Qty | Price |\n| --- | --- | --- |\n| Apples | 3 | 12.00 |\n| Pears | 5 | 7.50 |\n", md);
        var html = MarkdownToHtml.Convert(md);
        Assert.Contains("<th>Item</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td>12.00</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_without_a_header_row_gets_an_empty_one()
    {
        var md = Md("""
            <style>td{border:1px solid #000;padding:4px}table{border-collapse:collapse}</style>
            <table><tr><td>Apples</td><td>3</td></tr><tr><td>Pears</td><td>5</td></tr></table>
            """);
        Assert.Equal("|  |  |\n| --- | --- |\n| Apples | 3 |\n| Pears | 5 |\n", md);
    }

    [Theory]
    [InlineData("td,th{border:1px solid #000;padding:1px}", "")] // the CSS default: each cell's box 2px from the next
    [InlineData("td,th{border:1px solid #000;padding:2px}", "")]
    [InlineData("td,th{border:2px solid #000;padding:1px}", "")]
    [InlineData("td,th{border:2px groove #888;padding:1px}", "")] // painted through a clip of each side's strip
    [InlineData("", " border=\"1\"")] // inset cells in an outset table, painted the same way
    public void A_table_whose_cells_draw_their_own_borders_is_a_table(string css, string attributes)
    {
        var md = Md($"<style>body{{font-family:sans-serif}} {css}</style><table{attributes}><tr><th>Name</th><th>Value</th><th>Note</th></tr>"
            + "<tr><td>Alpha</td><td>1</td><td>a*b</td></tr><tr><td>Beta</td><td>2</td><td>x</td></tr><tr><td>Gamma</td><td>3</td><td>z</td></tr></table>");
        Assert.Equal("| Name | Value | Note |\n| --- | --- | --- |\n| Alpha | 1 | a\\*b |\n| Beta | 2 | x |\n| Gamma | 3 | z |\n", md);
    }

    [Fact]
    public void A_clip_cuts_a_filled_box_down_to_the_parts_it_paints()
    {
        // The top side of an inset border: the cell's box filled through the side's half and the frame between the
        // border's outer and inner edges leaves the top strip and the tops of the left and right strips.
        var side = new PdfPecker.Rendering.ClipBoxes(new[] { new[] { (0.0, 0.0, 100.0, 20.0) }, new[] { (-1.0, -1.0, 101.0, 41.0), (1.0, 1.0, 99.0, 39.0) } });
        Assert.Equal(new[] { (0.0, 0.0, 100.0, 1.0), (0.0, 1.0, 1.0, 20.0), (99.0, 1.0, 100.0, 20.0) }, side.Pieces(0, 0, 100, 40));
        // A clip around the whole box, no clip and a clip of a shape the boxes cannot follow leave the box whole.
        Assert.Null(new PdfPecker.Rendering.ClipBoxes(new[] { new[] { (-5.0, -5.0, 105.0, 45.0) } }).Pieces(0, 0, 100, 40));
        Assert.Null(new PdfPecker.Rendering.ClipBoxes(null).Pieces(0, 0, 100, 40));
        Assert.Null(new PdfPecker.Rendering.ClipBoxes(new[] { new[] { (0.0, 0.0, 10.0, 10.0), (20.0, 0.0, 30.0, 10.0), (40.0, 0.0, 50.0, 10.0) } }).Pieces(0, 0, 100, 40));
        // A clip that misses the box leaves nothing of it.
        Assert.Empty(new PdfPecker.Rendering.ClipBoxes(new[] { new[] { (200.0, 0.0, 300.0, 40.0) } }).Pieces(0, 0, 100, 40)!);
    }

    [Fact]
    public void A_cell_spanning_columns_of_a_table_with_separate_borders_keeps_its_span()
    {
        var md = Md("""
            <style>td,th{border:1px solid #000;padding:2px}</style>
            <table><tr><th>Name</th><th>Value</th><th>Note</th></tr><tr><td colspan="2">Spans two</td><td>x</td></tr>
            <tr><td>Gamma</td><td>3</td><td>z</td></tr></table>
            """);
        Assert.Equal("| Name | Value | Note |\n| --- | --- | --- |\n| Spans two |  | x |\n| Gamma | 3 | z |\n", md);
    }

    [Fact]
    public void Boxes_in_a_row_that_draw_their_own_borders_are_not_a_table()
    {
        // Cards set a few points apart, like the cells of a table with separate borders, but in one row only.
        var md = Md("<style>.c{display:inline-block;width:30%;border:1px solid #444;padding:4px;margin:2px;vertical-align:top}</style>"
            + string.Concat(Enumerable.Range(1, 3).Select(i => $"<div class=\"c\"><b>Card {i}</b><br>Some words that describe the card and wrap onto a second line of text.</div>")));
        Assert.DoesNotContain("|", md, StringComparison.Ordinal);
        Assert.Contains("**Card 1**\n\nSome words that describe the card and wrap onto a second line of text.\n\n**Card 2**", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("table{border-spacing:6px} td,th{border:1px solid #000}")]
    [InlineData("table{border-spacing:4px} td,th{border:2px dashed #333}")]
    [InlineData("table{border-spacing:6px} td,th{border:2px inset #333}")]
    [InlineData("table{border-spacing:3px} td,th{border:3px solid #333}")]
    [InlineData("table{border-spacing:10px} td,th{border:3px solid #333}")]
    public void A_table_with_wide_border_spacing_is_a_table(string css)
    {
        var md = Md($"<style>body{{font-family:sans-serif;font-size:11pt}} td,th{{padding:2px 4px}} {css}</style><table><tr><th>Name</th><th>Value</th><th>Note</th></tr>"
            + "<tr><td>Alpha</td><td>1</td><td>first entry</td></tr><tr><td>Beta</td><td>22</td><td>second one</td></tr><tr><td>Gamma</td><td>333</td><td>third</td></tr></table>");
        Assert.Equal("| Name | Value | Note |\n| --- | --- | --- |\n| Alpha | 1 | first entry |\n| Beta | 22 | second one |\n| Gamma | 333 | third |\n", md);
    }

    [Fact]
    public void Boxes_set_apart_are_a_table_only_when_they_fill_the_grid()
    {
        // Samples in a row, set as far apart as the cells of a table with wide border spacing, with one more under the
        // first: no box stands beside it, so they are not the cells of a table.
        var md = Md("<style>body{font-family:sans-serif;font-size:9pt} .s{display:flex;flex-wrap:wrap;gap:8px;width:300px} .s div{border:2px solid #444;width:70px;height:40px;padding:2px}</style>"
            + "<div class='s'><div>round</div><div>bevel</div><div>scoop</div><div>mixed</div></div><p>After the samples.</p>");
        Assert.Equal("round bevel scoop\n\nmixed\n\nAfter the samples.\n", md);
    }

    [Fact]
    public void A_table_of_bordered_fields_keeps_its_header()
    {
        // The fields in the cells are boxes as far apart as wide border spacing sets cells; the table around them is
        // read whole, with its header row.
        var md = Md("<style>body{font-family:sans-serif;font-size:9pt} table{border-collapse:collapse;width:100%} th,td{border:0.5pt solid #aaa;padding:2pt;text-align:left} .f{border:0.5pt solid #888;padding:2pt;height:11pt}</style>"
            + "<table><tr><th>Employer</th><th>From</th><th>To</th><th>Role</th></tr><tr><td><div class='f'>Example Ltd.</div></td><td><div class='f'>2015</div></td><td><div class='f'>2019</div></td><td><div class='f'>Developer</div></td></tr>"
            + string.Concat(Enumerable.Repeat("<tr><td><div class='f'></div></td><td><div class='f'></div></td><td><div class='f'></div></td><td><div class='f'></div></td></tr>", 2)) + "</table>");
        Assert.Equal("| Employer | From | To | Role |\n| --- | --- | --- | --- |\n| Example Ltd. | 2015 | 2019 | Developer |\n|  |  |  |  |\n|  |  |  |  |\n", md);
    }

    [Fact]
    public void A_hint_under_a_field_stays_out_of_the_fields_cell()
    {
        // Rows that each draw a box, with a framed field in each: the top and bottom of a field end where its own sides
        // are, which is no sign of a column boundary in the rows around it.
        var md = Md("<style>body{font-family:sans-serif;font-size:12px} .row{display:grid;grid-template-columns:100px 1fr 40px;column-gap:4px;border:1px solid #ddd;padding:3px 6px;margin-bottom:4px;align-items:baseline} .in{border:1px solid #aaa;padding:2px 4px} .hint{grid-column:2/4;font-size:10px}</style>"
            + "<div class='row'><b>Name</b><div class='in'>Ada Lovelace</div><span>required</span></div><div class='row'><b>Organisation</b><div class='in'>Analytical Engines Ltd</div><span></span></div>"
            + "<div class='row'><b>Amount</b><div class='in'>1 024,00</div><span>kr</span><div class='hint'>The hint sits under the field and the unit.</div></div>");
        Assert.Equal("|  |  |  |\n| --- | --- | --- |\n| **Name** | Ada Lovelace | required |\n| **Organisation** | Analytical Engines Ltd |  |\n| **Amount** | 1 024,00 | kr |\n\n"
            + "The hint sits under the field and the unit.\n", md);
    }

    [Fact]
    public void Lists_counted_in_letters_side_by_side_are_read_one_after_the_other()
    {
        var md = Md("<style>body{font-family:sans-serif;font-size:10pt} .lists{display:flex;gap:4mm} .lists>*{margin:0}</style>"
            + "<div class='lists'><ol type='A'><li>type A</li><li>second</li></ol><ol type='i' start='3'><li>roman from three</li><li>four</li></ol></div>");
        Assert.Equal("A. type A\n\nB. second\n\niii. roman from three\n\niv. four\n", md);
    }

    [Fact]
    public void Each_item_of_a_list_counted_in_letters_is_a_paragraph()
    {
        var md = Md("<style>body{font-family:sans-serif;font-size:10pt}</style><p>Choose one:</p><ol type='a'><li>the first way</li><li>the second way</li><li>the third way</li></ol><p>Then go on.</p>");
        Assert.Equal("Choose one:\n\na. the first way\n\nb. the second way\n\nc. the third way\n\nThen go on.\n", md);
    }

    [Fact]
    public void A_column_of_names_whose_initials_follow_each_other_is_still_a_table()
    {
        var md = Md("<style>body{font-family:sans-serif;font-size:11pt} td,th{padding:2px 12px;text-align:left}</style><table><tr><th>Name</th><th>Score</th><th>Team</th></tr>"
            + "<tr><td>A. Smith</td><td>12</td><td>North</td></tr><tr><td>B. Jones</td><td>9</td><td>South</td></tr><tr><td>C. Brown</td><td>7</td><td>East</td></tr></table>");
        Assert.Equal("| Name | Score | Team |\n| --- | --- | --- |\n| A. Smith | 12 | North |\n| B. Jones | 9 | South |\n| C. Brown | 7 | East |\n", md);
    }

    [Fact]
    public void A_borderless_table_with_lower_case_values_is_a_table()
    {
        // A one-word value fills its column whenever it is the widest, which is no sign of text wrapping to the next row.
        var md = Md("""
            <style>body{font-family:sans-serif;font-size:11pt} td,th{padding:2px 12px;text-align:left}</style>
            <table><tr><th>Name</th><th>Status</th><th>Region</th></tr>
            <tr><td>Alice Brown</td><td>active</td><td>North</td></tr><tr><td>Bob Smith</td><td>pending</td><td>South</td></tr>
            <tr><td>Carol White</td><td>closed</td><td>East</td></tr><tr><td>Dan Green</td><td>active</td><td>West</td></tr></table>
            """);
        Assert.Equal("| Name | Status | Region |\n| --- | --- | --- |\n| Alice Brown | active | North |\n| Bob Smith | pending | South |\n"
            + "| Carol White | closed | East |\n| Dan Green | active | West |\n", md);
    }

    [Fact]
    public void Two_header_rows_are_joined_column_by_column()
    {
        // A tagged PDF says which rows are the header.
        var pdf = HtmlToPdf.Convert("""
            <table><thead><tr><th colspan="2">2024</th><th>2025</th></tr><tr><th>Q1</th><th>Q2</th><th>Q1</th></tr></thead>
            <tr><td>1</td><td>2</td><td>3</td></tr></table>
            """, new PdfOptions { Accessibility = PdfAccessibility.Tagged });
        Assert.Equal("| 2024 Q1 | 2024 Q2 | 2025 Q1 |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n", PdfToMarkdown.Convert(pdf));
    }

    [Fact]
    public void A_spanned_cell_is_in_its_first_row_and_column_and_breaks_are_br()
    {
        var md = Md("""
            <style>td{border:1px solid #000;padding:4px}table{border-collapse:collapse}</style>
            <table><tr><td colspan="2">Both</td><td>C</td></tr>
            <tr><td rowspan="2">Tall</td><td>B1</td><td>C1</td></tr>
            <tr><td>B2</td><td>Line one<br>Line two</td></tr></table>
            """);
        Assert.Contains("| Both |  | C |\n", md, StringComparison.Ordinal);
        Assert.Contains("| Tall | B1 | C1 |\n|  | B2 | Line one<br>Line two |\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_characters_in_the_text_are_escaped()
    {
        const string text = "Price *special* 5 < 6 & snake_case _x_ [x] | pipe `code` ~old~ \\ back &amp; x";
        var md = Md("<p>" + System.Net.WebUtility.HtmlEncode(text) + "</p><p># not a heading</p><p>+ not a list</p><p>2026. Not a list either</p>");
        Assert.Contains("snake_case", md, StringComparison.Ordinal);
        var shown = Shown(md);
        Assert.Contains(text, shown, StringComparison.Ordinal);
        Assert.Contains("# not a heading", shown, StringComparison.Ordinal);
        Assert.Contains("+ not a list", shown, StringComparison.Ordinal);
        Assert.Contains("2026. Not a list either", shown, StringComparison.Ordinal);
        var html = MarkdownToHtml.Convert(md);
        Assert.DoesNotContain("<h1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<em>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<ul>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<ol", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_breaks_made_on_purpose_are_hard_breaks()
    {
        var md = Md("<p>Acme Trading<br>=== End<br>The long street name 1, which is a much longer line</p>");
        Assert.Equal("Acme Trading\\\n\\=== End\\\nThe long street name 1, which is a much longer line\n", md);
        Assert.Equal("<p>Acme Trading<br />\n=== End<br />\nThe long street name 1, which is a much longer line</p>", MarkdownToHtml.Convert(md).Trim());
    }

    [Fact]
    public void Links_are_markdown_links()
    {
        var md = Md("<p>Read <a href=\"https://example.com/a_(b)\">the terms</a> first.</p>");
        Assert.Equal("Read [the terms](https://example.com/a_%28b%29) first.\n", md);
        Assert.DoesNotContain("](", Md("<p>Read <a href=\"https://example.com/\">the terms</a> first.</p>", new PdfMarkdownOptions { IncludeLinks = false }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_are_separated_and_can_say_their_number()
    {
        const string html = "<p>First page</p><div style=\"page-break-before:always\">Second page</div>";
        Assert.Equal("First page\n\nSecond page\n", Md(html));
        Assert.Equal("<!-- page 1 -->\n\nFirst page\n\n<!-- page 2 -->\n\nSecond page\n", Md(html, new PdfMarkdownOptions { IncludePageComments = true }));
        Assert.Equal("<!-- page 2 -->\n\nSecond page\n",
            Md(html, new PdfMarkdownOptions { IncludePageComments = true, Pages = PdfPageRange.Parse("2") }));
    }

    [Fact]
    public void Images_are_included_when_asked_for()
    {
        var png = Convert.ToBase64String(PdfToImage.Convert(HtmlToPdf.Convert("<div style=\"width:40px;height:40px;background:red\"></div>"),
            new ImageOptions { Dpi = 24 })[0]);
        var html = $"<p>Logo:</p><img src=\"data:image/png;base64,{png}\" style=\"width:80px;height:80px\"><p>After</p>";
        Assert.DoesNotContain("![", Md(html), StringComparison.Ordinal);
        var md = Md(html, new PdfMarkdownOptions { IncludeImages = true });
        Assert.Matches(@"\n!\[\]\(data:image/(png|jpeg);base64,[A-Za-z0-9+/=]+\)\n", md);
    }

    [Theory]
    [InlineData("chromium.pdf")]
    [InlineData("libreoffice.pdf")]
    [InlineData("reportlab.pdf")]
    [InlineData("ghostscript.pdf")]
    public void Markdown_shows_the_text_of_the_structured_html(string name)
    {
        var pdf = File.ReadAllBytes(Fixture(name));
        var html = PdfToHtml.Convert(pdf, new PdfHtmlOptions { Mode = PdfHtmlMode.Structured });
        var md = PdfToMarkdown.Convert(pdf);
        Assert.Equal(Text(html[html.IndexOf("<section", StringComparison.Ordinal)..]), Shown(md));
        // The structure is the same too.
        var shown = MarkdownToHtml.Convert(md);
        Assert.Equal(Regex.Matches(html, "<table").Count, Regex.Matches(shown, "<table").Count);
        Assert.Equal(Regex.Matches(html, "<h[1-6]").Count, Regex.Matches(shown, "<h[1-6]").Count);
        Assert.Equal(Regex.Matches(html, "<li").Count, Regex.Matches(shown, "<li").Count);
    }

    [Fact]
    public void A_tagged_pdf_is_read_from_its_tags()
    {
        var pdf = HtmlToPdf.Convert("<h1>Title</h1><p>Body text.</p><table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>",
            new PdfOptions { Accessibility = PdfAccessibility.Tagged });
        Assert.Equal("# Title\n\nBody text.\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n", PdfToMarkdown.Convert(pdf));
    }

    [Fact]
    public void Stream_and_file_overloads_write_the_same_markdown()
    {
        var pdf = Pdf("<h1>Title</h1><p>Text with æøå and €.</p>");
        var md = PdfToMarkdown.Convert(pdf);
        using (var input = new MemoryStream(pdf)) Assert.Equal(md, PdfToMarkdown.Convert(input));
        using (var output = new MemoryStream())
        {
            PdfToMarkdown.Convert(pdf, output);
            Assert.Equal(md, System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var pdfPath = Path.Combine(dir.FullName, "in.pdf");
            var mdPath = Path.Combine(dir.FullName, "out.md");
            File.WriteAllBytes(pdfPath, pdf);
            PdfToMarkdown.ConvertFile(pdfPath, mdPath);
            Assert.Equal(md, File.ReadAllText(mdPath));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Output_is_the_same_every_time()
    {
        var pdf = File.ReadAllBytes(Fixture("chromium.pdf"));
        var first = PdfToMarkdown.Convert(pdf);
        Assert.Equal(first, PdfToMarkdown.Convert(pdf, new PdfMarkdownOptions { MaxDegreeOfParallelism = 1 }));
    }

    [Fact]
    public void Bad_input_fails_with_documented_exceptions()
    {
        Assert.Throws<ArgumentNullException>(() => PdfToMarkdown.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => PdfToMarkdown.Convert(Pdf("<p>x</p>"), (Stream)null!));
        Assert.Throws<InvalidDataException>(() => PdfToMarkdown.Convert("not a pdf"u8.ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfToMarkdown.Convert(Pdf("<p>x</p>"), new PdfMarkdownOptions { Pages = PdfPageRange.Parse("5") }));
        Assert.Throws<PdfLimitException>(() => PdfToMarkdown.Convert(Pdf("<p>x</p><p style=\"page-break-before:always\">y</p>"),
            new PdfMarkdownOptions { MaxPages = 1 }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfToMarkdown.Convert(Pdf("<p>x</p>"), null, cts.Token));
    }
}
