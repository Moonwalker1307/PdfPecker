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
}
