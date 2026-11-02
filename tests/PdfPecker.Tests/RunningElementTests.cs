namespace PdfPecker.Tests;

/// <summary>
/// position: running(name) and element(name) in page-margin boxes, as Prince and WeasyPrint draw them: the element
/// leaves the flow, and each page's margin box shows the running element of that name the policy picks for the page.
/// </summary>
public class RunningElementTests
{
    private const string Pages = "<p>one</p><p style='break-before:page'>two</p><p style='break-before:page'>three</p>";

    private static string[] TextOfPages(string css, string body)
    {
        var html = "<html><head><style>@page { size: 400px 300px; margin: 60px 30px; " + css + " } body { margin: 0 }</style></head><body>"
                   + body + "</body></html>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { EmbedStandardFonts = false });
        return PdfText.ExtractPages(pdf).Select(p => p.Text).ToArray();
    }

    [Fact]
    public void A_running_element_leaves_the_flow_and_shows_in_the_margin_of_every_page()
    {
        var pages = TextOfPages("@top-center { content: element(head) }",
            "<div style='position: running(head)'>Annual report</div>" + Pages);
        Assert.Equal(3, pages.Length);
        foreach (var page in pages) Assert.Single(page.Split("Annual report").Skip(1));
        Assert.True(pages[0].IndexOf("Annual report", StringComparison.Ordinal) < pages[0].IndexOf("one", StringComparison.Ordinal));
    }

    [Fact]
    public void A_later_running_element_of_the_same_name_takes_over_from_its_page()
    {
        var pages = TextOfPages("@top-center { content: element(head) }",
            "<div style='position: running(head)'>First</div><p>one</p>"
            + "<p style='break-before:page'>two</p><div style='position: running(head)'>Second</div><p>more</p>"
            + "<p style='break-before:page'>three</p>");
        Assert.Contains("First", pages[0]);
        Assert.Contains("Second", pages[1]);
        Assert.DoesNotContain("First", pages[1]);
        Assert.Contains("Second", pages[2]);
    }

    [Theory]
    [InlineData("first", "Second")]
    [InlineData("last", "Second")]
    [InlineData("start", "First")]
    [InlineData("first-except", "")]
    public void Policies_pick_the_element_of_the_page(string policy, string expected)
    {
        var pages = TextOfPages($"@bottom-center {{ content: element(foot, {policy}) }}",
            "<div style='position: running(foot)'>First</div><p>one</p>"
            + "<p style='break-before:page'>two</p><span style='position: running(foot)'>Second</span><p>more</p>");
        var second = pages[1];
        if (expected.Length == 0)
        {
            Assert.DoesNotContain("First", second);
            Assert.DoesNotContain("Second", second);
        }
        else Assert.Contains(expected, second);
    }

    [Fact]
    public void Page_counters_in_a_running_element_count_the_page_that_shows_it()
    {
        var pages = TextOfPages("@bottom-center { content: element(foot) }",
            "<style>.n::after { content: 'Page ' counter(page) ' of ' counter(pages) }</style>"
            + "<div style='position: running(foot)'><span class=n></span></div>" + Pages);
        Assert.Contains("Page 1 of 3", pages[0]);
        Assert.Contains("Page 2 of 3", pages[1]);
        Assert.Contains("Page 3 of 3", pages[2]);
    }

    [Fact]
    public void A_running_element_no_margin_box_shows_is_not_drawn()
    {
        var pages = TextOfPages("", "<div style='position: running(head)'>Hidden</div><p>one</p>");
        Assert.DoesNotContain("Hidden", pages[0]);
    }

    [Fact]
    public void Supports_knows_running()
    {
        Assert.True(Css.CssSupports.Evaluate("(position: running(head))"));
        Assert.False(Css.CssSupports.Evaluate("(position: running())"));
    }
}
