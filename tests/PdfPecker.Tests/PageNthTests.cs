using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// The @page :nth(An+B) selector and page groups, :nth(An+B of name) (CSS GCPM 3, 4.1): they style the margin boxes
/// and the background of the pages they match.
/// </summary>
public class PageNthTests
{
    private static string Body(int sections, string extra = "")
        => string.Concat(Enumerable.Range(1, sections).Select(i => $"<section>Body {i}</section>")) + extra;

    private static byte[] Convert(string css, string body)
        => HtmlToPdf.Convert($"<style>@page {{ size: 200pt 150pt; margin: 30pt }} section {{ break-after: page }} body {{ margin: 0 }} {css}</style>{body}",
            new PdfOptions { Compress = false });

    private static string[] PageTexts(byte[] pdf) => PdfText.ExtractPages(pdf).Select(p => p.Text).ToArray();

    [Fact]
    public void Selectors_parse_to_a_canonical_form()
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet("@page :nth(odd), :NTH( -n + 3 ), :nth(5), :nth(2n of Chapter) { margin: 1cm }", sheet);
        Assert.Equal(new[] { ":nth(2n+1)", ":nth(-1n+3)", ":nth(0n+5)", ":nth(2n+0 of Chapter)" }, sheet.PageRules.Select(r => r.Selector));
        Assert.Equal(new[] { 2, 2, 2, 6 }, sheet.PageRules.Select(r => r.Specificity));
    }

    [Theory]
    [InlineData("@page :nth() {}")]
    [InlineData("@page :nth(x) {}")]
    [InlineData("@page :nth(2 of) {}")]
    [InlineData("@page :nth(of chapter) {}")]
    [InlineData("@page :nth(2 of auto) {}")]
    [InlineData("@page :nth(2 of a b) {}")]
    [InlineData("@page :nth(2n+1 {}")]
    public void Invalid_selectors_drop_the_rule(string css)
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(css, sheet);
        Assert.Empty(sheet.PageRules);
    }

    [Theory]
    [InlineData(":nth(2)", new[] { false, true, false, false, false })]
    [InlineData(":nth(odd)", new[] { true, false, true, false, true })]
    [InlineData(":nth(3n)", new[] { false, false, true, false, false })]
    [InlineData(":nth(-n+2)", new[] { true, true, false, false, false })]
    [InlineData(":nth(n+4)", new[] { false, false, false, true, true })]
    [InlineData(":nth(-2n+5)", new[] { true, false, true, false, true })]
    public void Margin_boxes_appear_on_the_matching_pages(string selector, bool[] expected)
    {
        var pages = PageTexts(Convert($"@page {selector} {{ @top-center {{ content: 'Marked' }} }}", Body(5)));
        Assert.Equal(expected.Length, pages.Length);
        for (int i = 0; i < pages.Length; i++) Assert.Equal(expected[i], pages[i].Contains("Marked", StringComparison.Ordinal));
    }

    [Fact]
    public void Nth_overrides_the_plain_rule_and_keeps_the_page_counter()
    {
        var pages = PageTexts(Convert("@page { @bottom-center { content: 'Page ' counter(page) } } @page :nth(2) { @bottom-center { content: 'Second ' counter(page) } }", Body(3)));
        Assert.Contains("Page 1", pages[0]);
        Assert.Contains("Second 2", pages[1]);
        Assert.Contains("Page 3", pages[2]);
    }

    [Fact]
    public void Of_name_counts_the_pages_of_the_named_run()
    {
        var css = "@page chapter { @top-center { content: 'Chapter' } } @page :nth(1 of chapter) { @top-center { content: 'Opening' } }";
        var pages = PageTexts(Convert(css + " .c { page: chapter }", Body(2, "<div class=c>" + Body(2) + "</div>")));
        Assert.Equal(4, pages.Length);
        Assert.DoesNotContain("Opening", pages[0]);
        Assert.DoesNotContain("Opening", pages[1]);
        Assert.Contains("Opening", pages[2]);
        Assert.Contains("Chapter", pages[3]);
    }

    [Fact]
    public void Nth_gives_a_page_its_background_without_moving_the_content()
    {
        var plain = Convert("", Body(3));
        var pdf = Convert("@page :nth(2) { background: #ff0000; margin: 0; size: a4 }", Body(3));
        var pages = AllContent(pdf);
        Assert.Equal(3, PageCount(pdf));
        // Only the second page is filled; size and margins of an :nth() rule do not apply.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(pages, "1 0 0 rg\n0 0 200 150 re f"));
        Assert.Equal(PdfText.ExtractPages(plain).Select(p => p.Text), PdfText.ExtractPages(pdf).Select(p => p.Text));
        Assert.Contains("/MediaBox [0 0 200 150]", Latin1(pdf));
    }

    [Fact]
    public void Later_rules_of_equal_specificity_win_the_background()
    {
        var pdf = Convert("@page :nth(2) { background: #ff0000 } @page :first { background: #00ff00 } @page :nth(1) { background: #0000ff }", Body(2));
        var content = AllContent(pdf);
        Assert.Contains("0 0 1 rg\n0 0 200 150 re f", content);
        Assert.Contains("1 0 0 rg\n0 0 200 150 re f", content);
        Assert.DoesNotContain("0 1 0 rg", content);
    }
}
