namespace PdfPecker.Tests;

/// <summary>Tables that continue over several pages, in normal flow and inside flex and grid containers.</summary>
public class TablePaginationTests
{
    private const string Style = "<style>@page { size: 300pt 300pt; margin: 0 } body { margin: 0; font-size: 10pt } td, th { padding: 0 }</style>";

    private static string Table(int rows, bool foot = true)
        => "<table><thead><tr><th>Head</th></tr></thead><tbody>"
           + string.Concat(Enumerable.Range(1, rows).Select(i => $"<tr><td style='height: 30pt'>Row {i}</td></tr>"))
           + "</tbody>" + (foot ? "<tfoot><tr><td>Foot</td></tr></tfoot>" : "") + "</table>";

    private static IReadOnlyList<PdfTextPage> Pages(string body) => PdfText.ExtractPages(HtmlToPdf.Convert(Style + body));

    [Theory]
    [InlineData("block")]
    [InlineData("flex")]
    [InlineData("grid")]
    public void Every_page_of_a_table_starts_with_its_header_and_ends_with_its_footer(string display)
    {
        var pages = Pages($"<div style='display: {display}'>{Table(20)}</div><p>After</p>");
        Assert.True(pages.Count >= 3);
        var rows = new List<string>();
        foreach (var page in pages)
        {
            var lines = page.Lines.Select(l => l.Text).ToList();
            Assert.Equal("Head", lines[0]);
            Assert.Equal(1, lines.Count(l => l == "Foot"));
            rows.AddRange(lines.Where(l => l.StartsWith("Row ", StringComparison.Ordinal)));
        }
        // No row is cut in half: each appears once, whole, on one page.
        Assert.Equal(Enumerable.Range(1, 20).Select(i => "Row " + i), rows);
        Assert.Equal("After", pages[^1].Lines[^1].Text);
    }

    [Fact]
    public void A_header_is_repeated_when_the_row_before_ends_exactly_at_the_page_end()
    {
        // Ten rows of 30 points fill the 300-point page exactly, so the eleventh starts the next page without moving.
        var html = "<table style='border-spacing: 0'><thead><tr><th style='height: 30pt'>Head</th></tr></thead><tbody>"
                   + string.Concat(Enumerable.Range(1, 15).Select(i => $"<tr><td style='height: 30pt'>Row {i}</td></tr>")) + "</tbody></table>";
        var pages = Pages(html);
        Assert.Equal("Head", pages[1].Lines[0].Text);
        Assert.Equal("Row 10", pages[1].Lines[1].Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_table_whose_first_row_does_not_fit_starts_on_the_next_page(bool foot)
    {
        // 260 points of the 300-point page are used, so the header fits but the first 30-point row does not: as in
        // Chrome, the header and footer move with it instead of staying behind on their own.
        var pages = Pages($"<div style='height: 260pt'>Before</div>{Table(3, foot)}");
        Assert.Equal(new[] { "Before" }, pages[0].Lines.Select(l => l.Text));
        Assert.Equal("Head", pages[1].Lines[0].Text);
        Assert.Equal("Row 1", pages[1].Lines[1].Text);
    }

    [Theory]
    [InlineData("<tr style='break-before: page'><td>Row 3</td></tr>", "")]
    [InlineData("<tr><td>Row 3</td></tr>", "break-after: page")]
    [InlineData("<tr style='page-break-before: always'><td>Row 3</td></tr>", "")]
    public void A_forced_break_between_table_rows_starts_a_new_page_with_the_header(string third, string secondStyle)
    {
        var pages = Pages("<table><thead><tr><th>Head</th></tr></thead><tbody><tr><td>Row 1</td></tr>"
                          + $"<tr style='{secondStyle}'><td>Row 2</td></tr>{third}<tr><td>Row 4</td></tr></tbody></table><p>After</p>");
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { "Head", "Row 1", "Row 2" }, pages[0].Lines.Select(l => l.Text));
        Assert.Equal(new[] { "Head", "Row 3", "Row 4", "After" }, pages[1].Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_forced_break_on_a_row_group_breaks_between_the_groups()
    {
        var pages = Pages("<table><tbody><tr><td>One</td></tr></tbody><tbody style='break-before: page'><tr><td>Two</td></tr></tbody>"
                          + "<tbody><tr><td>Three</td></tr></tbody></table>");
        Assert.Equal(new[] { "One" }, pages[0].Lines.Select(l => l.Text));
        Assert.Equal(new[] { "Two", "Three" }, pages[1].Lines.Select(l => l.Text));
    }

    [Fact]
    public void Forced_breaks_before_the_first_row_or_after_the_last_are_ignored_as_in_Chrome()
    {
        var pages = Pages("<p>Before</p><table><tr style='break-before: page'><td>One</td></tr>"
                          + "<tr style='break-after: page'><td>Two</td></tr></table><p>After</p>");
        Assert.Single(pages);
        Assert.Equal(new[] { "Before", "One", "Two", "After" }, pages[0].Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_column_break_on_a_table_row_moves_it_to_the_next_column()
    {
        var page = Pages("<div style='columns: 2; column-gap: 0'><table><tr><td>One</td></tr><tr style='break-before: column'><td>Two</td></tr>"
                         + "<tr style='break-before: page'><td>Three</td></tr></table></div>").Single();
        // Two goes to the top of the second column, beside One; the page break inside the columns is ignored, as in
        // Chrome, so Three follows Two there.
        Assert.Equal(new[] { "One Two", "Three" }, page.Lines.Select(l => l.Text));
        Assert.True(page.Lines[1].X > 100);
    }

    [Theory]
    [InlineData("middle")]
    [InlineData("bottom")]
    public void A_cell_split_across_pages_keeps_its_content_at_the_top(string align)
    {
        // As in Chrome, vertical-align does not apply to a cell split across pages: centred in the whole cell, the short
        // cell started on the second page and the heading that stays with the table moved there too, leaving the
        // first page empty.
        var lines = string.Concat(Enumerable.Range(1, 40).Select(i => $"<div>Line {i}</div>"));
        var pages = Pages($"<h2>Heading</h2><table><tr><td style='vertical-align: {align}'>Short</td><td>{lines}</td></tr></table>");
        var first = pages[0].Lines.Select(l => l.Text).ToList();
        Assert.Equal("Heading", first[0]);
        Assert.Contains(first, l => l.StartsWith("Short", StringComparison.Ordinal));
    }

    [Fact]
    public void A_box_keeps_its_top_margin_after_a_forced_break_before_it()
    {
        const string h = "<style>h1 { margin: 40pt 0 0; font-size: 10pt }</style>";
        var before = Pages(h + "<p>One</p><h1 style='break-before: page'>Two</h1>");
        var after = Pages(h + "<p style='break-after: page'>One</p><h1>Two</h1>");
        var both = Pages(h + "<p style='break-after: page'>One</p><h1 style='break-before: page'>Two</h1>");
        Assert.Equal(2, before.Count);
        Assert.Equal(2, both.Count);
        Assert.Equal(after[1].Lines[0].Y, before[1].Lines[0].Y, 0.01);
        Assert.Equal(after[1].Lines[0].Y, both[1].Lines[0].Y, 0.01);
        // The margin is there: the heading is lower than text at the top of the page would be.
        Assert.True(before[1].Lines[0].Y - after[0].Lines[0].Y > 20);
    }

    [Theory]
    [InlineData("collapse")]
    [InlineData("separate")]
    public void A_row_split_across_pages_has_the_header_and_footer_on_every_page(string model)
    {
        // As in Chrome, the header repeats at the top of each page the row goes on to and the footer at the bottom of
        // each page it goes on from, and the row's lines go on below the header rather than under it.
        var lines = string.Concat(Enumerable.Range(1, 40).Select(i => $"<div>Line {i}</div>"));
        var pages = Pages($"<table style='border-collapse: {model}'><thead><tr><th>Head</th></tr></thead>"
                          + $"<tfoot><tr><td>Foot</td></tr></tfoot><tbody><tr><td>{lines}</td></tr></tbody></table>");
        Assert.True(pages.Count >= 2);
        var rows = new List<string>();
        foreach (var page in pages)
        {
            var text = page.Lines.Select(l => l.Text).ToList();
            Assert.Equal("Head", text[0]);
            Assert.Equal("Foot", text[^1]);
            rows.AddRange(text.Where(l => l.StartsWith("Line ", StringComparison.Ordinal)));
        }
        Assert.Equal(Enumerable.Range(1, 40).Select(i => "Line " + i), rows);
    }

    [Fact]
    public void A_box_in_a_split_row_goes_on_below_the_repeated_header()
    {
        // A 400-point box on 300-point pages under a 30-point header: 270 points fit on the first page, and the other
        // 130 go on below the header repeated on the second, so the row after it starts 160 points down, as in Chrome,
        // not 130.
        var pages = Pages("<table style='border-spacing: 0'><thead><tr><th style='height: 30pt'>Head</th></tr></thead><tbody>"
                          + "<tr><td><div style='height: 400pt'></div></td></tr><tr><td>After</td></tr></tbody></table>");
        Assert.Equal(2, pages.Count);
        var after = pages[1].Lines.Single(l => l.Text == "After");
        var head = pages[1].Lines.Single(l => l.Text == "Head");
        Assert.InRange(after.Y - head.Y, 145, 155);
    }
}
