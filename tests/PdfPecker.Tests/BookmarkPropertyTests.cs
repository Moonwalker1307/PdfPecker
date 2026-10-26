using System.Text.RegularExpressions;
using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// bookmark-level, bookmark-label and bookmark-state (CSS Generated Content for Paged Media): which elements become PDF
/// bookmarks, with what text, and whether they start closed.
/// </summary>
public class BookmarkPropertyTests
{
    private static string Pdf(string html)
    {
        var pdf = ConvertUncompressed(html);
        AssertStructurallyValid(pdf);
        return Latin1(pdf);
    }

    private static List<string> Titles(string text)
        => Regex.Matches(text, @"/Title \(([^)]*)\)").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void Any_element_with_a_bookmark_level_becomes_a_bookmark()
    {
        var text = Pdf("<h1>Report</h1><div class=sec style='bookmark-level:2'>Summary</div><p style='bookmark-level:2'>Details</p>");
        Assert.Equal(["Report", "Summary", "Details"], Titles(text));
        Assert.Matches(@"/Title \(Report\) /Parent \d+ 0 R /First \d+ 0 R /Last \d+ 0 R /Count 2 ", text);
    }

    [Fact]
    public void Bookmark_level_none_leaves_a_heading_out_and_a_level_moves_it()
    {
        var text = Pdf("<h1>One</h1><h2 style='bookmark-level:none'>Hidden</h2><h3 style='bookmark-level:1'>Two</h3>");
        Assert.Equal(["One", "Two"], Titles(text));
        Assert.Matches(@"/Type /Outlines /First \d+ 0 R /Last \d+ 0 R /Count 2 ", text);
    }

    [Theory]
    [InlineData("'Chapter one'", "Chapter one")]
    [InlineData("'Part ' attr(data-n)", "Part 7")]
    [InlineData("content(text)", "Heading text")]
    [InlineData("content()", "Heading text")]
    public void Bookmark_label_sets_the_text(string label, string title)
        => Assert.Equal([title], Titles(Pdf($"<h1 data-n=7 style=\"bookmark-label: {label}\">Heading <b>text</b></h1>")));

    [Fact]
    public void Bookmark_label_takes_counters()
    {
        var text = Pdf("<style>body{counter-reset:ch} h1{counter-increment:ch; bookmark-label: counter(ch) \". \" content()}</style><h1>Intro</h1><h1>Method</h1>");
        Assert.Equal(["1. Intro", "2. Method"], Titles(text));
    }

    [Fact]
    public void A_closed_bookmark_hides_its_children_and_counts_them_negative()
    {
        var text = Pdf("<h1 style='bookmark-state:closed'>A</h1><h2>A1</h2><h3>A1a</h3><h2>A2</h2><h1>B</h1><h2>B1</h2>");
        // A shows nothing at first, so the outline shows A, B and B1.
        Assert.Matches(@"/Type /Outlines /First \d+ 0 R /Last \d+ 0 R /Count 3 ", text);
        Assert.Matches(@"/Title \(A\) /Parent \d+ 0 R /Next \d+ 0 R /First \d+ 0 R /Last \d+ 0 R /Count -3 ", text);
        Assert.Matches(@"/Title \(B\) /Parent \d+ 0 R /Prev \d+ 0 R /First \d+ 0 R /Last \d+ 0 R /Count 1 ", text);
    }

    [Fact]
    public void An_open_item_does_not_count_what_a_closed_child_hides()
    {
        var text = Pdf("<h1>A</h1><h2 style='bookmark-state:closed'>A1</h2><h3>A1a</h3><h3>A1b</h3><h2>A2</h2>");
        Assert.Matches(@"/Title \(A\) /Parent \d+ 0 R /First \d+ 0 R /Last \d+ 0 R /Count 2 ", text);
        Assert.Matches(@"/Title \(A1\) /Parent \d+ 0 R /Next \d+ 0 R /First \d+ 0 R /Last \d+ 0 R /Count -2 ", text);
        Assert.Matches(@"/Type /Outlines /First \d+ 0 R /Last \d+ 0 R /Count 3 ", text);
    }

    [Theory]
    [InlineData("bookmark-level: 0")]
    [InlineData("bookmark-level: -2")]
    [InlineData("bookmark-level: two")]
    public void Invalid_levels_are_dropped(string style)
        => Assert.Equal(["Kept"], Titles(Pdf($"<h2 style='{style}'>Kept</h2><div style='{style}'>Not a bookmark</div>")));

    [Fact]
    public void The_properties_are_not_inherited()
        => Assert.Equal(["Section"], Titles(Pdf("<section style='bookmark-level:1;bookmark-label:\"Section\"'><div>Inside</div></section>")));

    [Fact]
    public void Supports_knows_the_bookmark_properties()
    {
        Assert.True(CssSupports.Evaluate("(bookmark-level: 1)"));
        Assert.True(CssSupports.Evaluate("(bookmark-label: content(text))"));
        Assert.True(CssSupports.Evaluate("(bookmark-state: closed)"));
    }
}
