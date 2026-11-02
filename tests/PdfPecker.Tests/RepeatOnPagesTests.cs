namespace PdfPecker.Tests;

/// <summary>-pdfpecker-repeat: each-page, which draws a box again on each later page its parent continues on.</summary>
public class RepeatOnPagesTests
{
    private static readonly PdfOptions Fixed = new() { CreationDate = DateTimeOffset.UnixEpoch };

    private const string Page = "<style>@page { size: 300pt 300pt; margin: 20pt } p { margin: 0; height: 20pt }</style>";

    private static string Lines(int count)
        => string.Concat(Enumerable.Range(1, count).Select(i => $"<p>Line {i}</p>"));

    private static IReadOnlyList<PdfTextPage> Pages(string html) => PdfText.ExtractPages(HtmlToPdf.Convert(html, Fixed));

    private static int Count(PdfTextPage page, string text) => page.Lines.Count(l => l.Text.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void A_box_without_a_parent_on_later_pages_is_drawn_once()
    {
        const string html = Page + "<div><div style='-pdfpecker-repeat: each-page'>Label</div><p>Text</p></div>";
        Assert.Equal(HtmlToPdf.Convert(html.Replace("-pdfpecker-repeat: each-page", "", StringComparison.Ordinal), Fixed), HtmlToPdf.Convert(html, Fixed));
    }

    [Fact]
    public void A_label_beside_a_long_text_is_drawn_on_every_page_of_its_row()
    {
        var pages = Pages(Page + $@"<div style='display: flex'><div style='width: 80pt'><div style='-pdfpecker-repeat: each-page'>Label</div></div>
            <div style='flex: 1'>{Lines(30)}</div></div><p>After</p>");
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.Equal(1, Count(p, "Label")));
        // On the later pages the copy starts the page, level with the text that continues there.
        var label = pages[1].Lines.Single(l => l.Text.Contains("Label", StringComparison.Ordinal));
        var line = pages[1].Lines.First(l => l.Text.Contains("Line", StringComparison.Ordinal));
        Assert.Equal(line.Y, label.Y, 1.0);
    }

    [Fact]
    public void The_copies_stop_where_the_parent_ends()
    {
        var pages = Pages(Page + $@"<div style='display: flex'><div style='width: 80pt'><div style='-pdfpecker-repeat: each-page'>Label</div></div>
            <div style='flex: 1'>{Lines(14)}</div></div>{Lines(20)}");
        Assert.Equal(1, Count(pages[0], "Label"));
        Assert.Equal(1, Count(pages[1], "Label"));
        Assert.All(pages.Skip(2), p => Assert.Equal(0, Count(p, "Label")));
    }

    [Fact]
    public void A_box_in_a_table_cell_split_across_pages_repeats()
    {
        var pages = Pages(Page + $@"<table style='width: 100%; border-collapse: collapse'><tr>
            <td style='width: 80pt'><div style='-pdfpecker-repeat: each-page'>Label</div></td><td>{Lines(30)}</td></tr></table>");
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.Equal(1, Count(p, "Label")));
    }

    [Fact]
    public void A_box_in_a_parent_with_a_height_repeats_down_that_height()
    {
        var pages = Pages(Page + "<div style='height: 700pt'><div style='-pdfpecker-repeat: each-page'>Label</div></div>");
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.Equal(1, Count(p, "Label")));
    }

    [Fact]
    public void The_copies_are_artifacts_in_a_tagged_pdf()
    {
        string html = Page + $@"<div style='display: flex'><div style='width: 80pt'><div style='-pdfpecker-repeat: each-page'>Label</div></div>
            <div style='flex: 1'>{Lines(30)}</div></div>";
        var options = new PdfOptions { CreationDate = DateTimeOffset.UnixEpoch, Accessibility = PdfAccessibility.Tagged };
        static int Count(string text, string what) => System.Text.RegularExpressions.Regex.Count(text, what);
        string repeated = PdfTestHelpers.AllContent(HtmlToPdf.Convert(html, options));
        string once = PdfTestHelpers.AllContent(HtmlToPdf.Convert(html.Replace("-pdfpecker-repeat: each-page", "", StringComparison.Ordinal), options));
        // The two copies add marked content of their own, as artifacts, and no tagged content.
        Assert.Equal(Count(once, "/MCID"), Count(repeated, "/MCID"));
        Assert.True(Count(repeated, "/Artifact") > Count(once, "/Artifact"));
    }

    [Fact]
    public void The_property_is_not_inherited()
    {
        var pages = Pages(Page + $@"<div style='display: flex'><div style='width: 80pt; -pdfpecker-repeat: each-page'><div>Label</div></div>
            <div style='flex: 1'>{Lines(30)}</div></div>");
        // The item itself spans the pages of its row, so neither it nor its child is drawn again.
        Assert.Equal(1, pages.Sum(p => Count(p, "Label")));
    }
}
