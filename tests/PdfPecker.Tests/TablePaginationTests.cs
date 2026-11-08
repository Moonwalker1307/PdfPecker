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
        // Rows that break inside break below their text, which each row has once, on one page.
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
    [InlineData(1.5)]
    [InlineData(4.5)]
    [InlineData(10)]
    public void A_header_is_repeated_when_the_border_spacing_before_a_row_reaches_past_the_page_end(double spacing)
    {
        // The box before the table puts the bottom of the sixth row half the spacing before the end of the 300-point
        // page, so the seventh row would start just past it. As in Chrome, the spacing is dropped at the break and the
        // row starts the next page under the repeated header, which sits at the top of the page. Rows are placed on
        // whole pixels, so positions are compared to within a pixel.
        double before = 100 - 7.5 * spacing;
        string s = spacing.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string b = before.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pages = Pages($"<div style='height: {b}pt'>Before</div><table style='border-spacing: 0 {s}pt'><thead><tr><th style='height: 20pt'>Head</th></tr></thead><tbody>"
                          + string.Concat(Enumerable.Range(1, 9).Select(i => $"<tr><td style='height: 30pt'>Row {i}</td></tr>")) + "</tbody></table>");
        Assert.Equal(2, pages.Count);
        Assert.Equal("Row 6", pages[0].Lines[^1].Text);
        Assert.Equal(new[] { "Head", "Row 7", "Row 8", "Row 9" }, pages[1].Lines.Select(l => l.Text));
        Assert.Equal(pages[0].Lines[1].Y - before - spacing, pages[1].Lines[0].Y, 1.0);
        Assert.Equal(pages[0].Lines[2].Y - pages[0].Lines[1].Y, pages[1].Lines[1].Y - pages[1].Lines[0].Y, 1.0);
    }

    [Fact]
    public void Border_spacing_that_reaches_past_the_page_end_is_dropped_there()
    {
        // The sixth row ends 5 points before the page end and the spacing is 10 points: as in Chrome, the seventh row
        // starts at the top of the next page, as a row of a table without spacing would.
        var pages = Pages("<div style='height: 25pt'>Before</div><table style='border-spacing: 0 10pt'><tbody>"
                          + string.Concat(Enumerable.Range(1, 8).Select(i => $"<tr><td style='height: 35pt'>Row {i}</td></tr>")) + "</tbody></table>");
        var unspaced = Pages("<table style='border-spacing: 0'><tr><td style='height: 35pt'>Row 7</td></tr></table>");
        Assert.Equal("Row 6", pages[0].Lines[^1].Text);
        Assert.Equal("Row 7", pages[1].Lines[0].Text);
        Assert.Equal(unspaced[0].Lines[0].Y, pages[1].Lines[0].Y, 0.5);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("<p style='margin: 0'>After</p>", 2)]
    public void A_table_whose_bottom_spacing_reaches_past_the_page_end_ends_at_the_break(string after, int pageCount)
    {
        // The last row ends 5 points before the page end: the spacing under it is dropped, as in Chrome, so it adds no
        // blank page, and what follows starts at the top of the next page.
        var pages = Pages("<div style='height: 25pt'>Before</div><table style='border-spacing: 0 10pt'><tbody>"
                          + string.Concat(Enumerable.Range(1, 6).Select(i => $"<tr><td style='height: 35pt'>Row {i}</td></tr>"))
                          + "</tbody></table>" + after);
        Assert.Equal(pageCount, pages.Count);
        Assert.Equal("Row 6", pages[0].Lines[^1].Text);
        if (pageCount == 2) Assert.True(pages[1].Lines[0].Y < 2);
    }

    [Theory]
    [InlineData(265, 10)]
    [InlineData(280, 0)]
    public void A_table_moves_with_its_header_when_its_first_row_starts_the_next_page(int before, int spacing)
    {
        // The header ends 5 points before the page end with 10 points of spacing under it, or right at the end without
        // spacing: the first row would start the next page and leave the header behind on its own, so the whole table
        // moves, as in Chrome.
        var pages = Pages($"<div style='height: {before}pt'>Before</div><table style='border-spacing: 0 {spacing}pt'>"
                          + "<thead><tr><th style='height: 20pt'>Head</th></tr></thead><tbody><tr><td style='height: 30pt'>Row 1</td></tr>"
                          + "<tr><td style='height: 30pt'>Row 2</td></tr></tbody></table>");
        Assert.Equal(new[] { "Before" }, pages[0].Lines.Select(l => l.Text));
        Assert.Equal(new[] { "Head", "Row 1", "Row 2" }, pages[1].Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_table_whose_first_row_does_not_fit_starts_on_the_next_page()
    {
        // 260 points of the 300-point page are used, so the header fits but the first 30-point row's text does not
        // above the footer: as in Chrome, the header and footer move with it instead of staying behind on their own.
        var pages = Pages($"<div style='height: 260pt'>Before</div>{Table(3)}");
        Assert.Equal(new[] { "Before" }, pages[0].Lines.Select(l => l.Text));
        Assert.Equal("Head", pages[1].Lines[0].Text);
        Assert.Equal("Row 1", pages[1].Lines[1].Text);
    }

    [Fact]
    public void A_row_whose_text_fits_breaks_inside_with_the_rest_of_its_height_on_the_next_page()
    {
        // Without a footer, the first row's text fits under the header: as in Chrome, the row breaks below it, and the
        // rest of its 30 points goes on under the header repeated on the next page.
        var pages = Pages($"<div style='height: 260pt'>Before</div>{Table(3, foot: false)}");
        Assert.Equal(new[] { "Before", "Head", "Row 1" }, pages[0].Lines.Select(l => l.Text));
        Assert.Equal(new[] { "Head", "Row 2", "Row 3" }, pages[1].Lines.Select(l => l.Text));
        Assert.True(pages[1].Lines[1].Y > pages[1].Lines[0].Y + 20);
    }

    [Theory]
    [InlineData("", new[] { "Before", "L1", "L2", "L3", "L4" })]
    [InlineData("tr { break-inside: avoid }", new[] { "Before" })]
    [InlineData("td:first-child { padding-top: 60pt }", new[] { "Before" })]
    public void A_row_breaks_between_the_lines_of_its_cells_unless_a_cell_has_none_before_the_break(string css, string[] firstPage)
    {
        // As in Chrome, a row that does not fit breaks inside, between its cells' lines, unless it may not or a cell
        // would have nothing before the break: then it moves to the next page whole.
        var pages = Pages($"<style>{css}</style><div style='height: 240pt'>Before</div><table><tr><td>L1<br>L2<br>L3<br>L4<br>L5<br>L6</td>"
                          + "<td>Side</td></tr></table>");
        Assert.Equal(firstPage, pages[0].Lines.Select(l => l.Text.Replace(" Side", "", StringComparison.Ordinal)).Where(t => t != "Side"));
        Assert.Contains("L6", pages[1].Lines.Select(l => l.Text));
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

    [Fact]
    public void A_row_whose_spanning_cell_would_run_past_the_page_end_moves_to_the_next_page()
    {
        // The first row of the span fits, but the spanning cell's ten lines do not: before, the row stayed and the cell's
        // text ran on past the page end, under the repeated header. Now the row moves and the span stays together.
        var html = "<table style='border-spacing: 0'><thead><tr><th>Head</th></tr></thead><tbody>"
                   + string.Concat(Enumerable.Range(1, 8).Select(i => $"<tr><td style='height: 30pt'>Row {i}</td><td></td></tr>"))
                   + "<tr><td rowspan='2'>" + string.Join("<br>", Enumerable.Range(1, 10).Select(i => "Span " + i)) + "</td><td>First</td></tr>"
                   + "<tr><td>Second</td></tr></tbody></table>";
        var pages = Pages(html);
        Assert.Equal(2, pages.Count);
        var second = pages[1].Lines.Select(l => l.Text).ToList();
        Assert.Equal("Head", second[0]);
        Assert.StartsWith("Span 1", second[1], StringComparison.Ordinal);
        Assert.Contains(second, l => l.Contains("Span 10", StringComparison.Ordinal));
        Assert.DoesNotContain(pages[0].Lines, l => l.Text.Contains("Span", StringComparison.Ordinal));
    }

    [Fact]
    public void A_spanning_cell_whose_content_fits_still_lets_its_rows_break_between_them()
    {
        // As in Chrome, a short group label spanning many rows does not keep them all together.
        var html = "<table style='border-spacing: 0'><tbody>"
                   + string.Concat(Enumerable.Range(1, 8).Select(i => $"<tr><td style='height: 30pt'>Row {i}</td><td></td></tr>"))
                   + "<tr><td rowspan='4'>Group</td><td style='height: 30pt'>Item 1</td></tr>"
                   + string.Concat(Enumerable.Range(2, 3).Select(i => $"<tr><td style='height: 30pt'>Item {i}</td></tr>")) + "</tbody></table>";
        var pages = Pages(html);
        Assert.Contains(pages[0].Lines, l => l.Text.Contains("Group", StringComparison.Ordinal));
        Assert.Contains(pages[1].Lines, l => l.Text.Contains("Item 4", StringComparison.Ordinal));
    }
}
