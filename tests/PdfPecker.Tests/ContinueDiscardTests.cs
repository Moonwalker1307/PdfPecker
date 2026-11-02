namespace PdfPecker.Tests;

/// <summary>continue: discard, which keeps a box's first fragment and leaves out the content after it.</summary>
public class ContinueDiscardTests
{
    private static readonly PdfOptions Fixed = new() { CreationDate = DateTimeOffset.UnixEpoch };

    private static string Lines(int count, string prefix = "Line")
        => string.Concat(Enumerable.Range(1, count).Select(i => $"<p style='margin: 0; height: 20pt; line-height: 20pt'>{prefix} {i}</p>"));

    private static IReadOnlyList<PdfTextPage> Pages(string html) => PdfText.ExtractPages(HtmlToPdf.Convert(html, Fixed));

    private static IEnumerable<string> Texts(PdfTextPage page) => page.Lines.Select(l => l.Text.Trim());

    [Fact]
    public void Content_that_fits_is_drawn_as_without_the_property()
    {
        const string html = "<div style='border: 1pt solid black{0}'><p>Short text</p><p>More</p></div><p>After</p>";
        Assert.Equal(HtmlToPdf.Convert(string.Format(html, ""), Fixed), HtmlToPdf.Convert(string.Format(html, "; continue: discard"), Fixed));
    }

    [Fact]
    public void Content_past_the_end_of_the_page_is_left_out()
    {
        var pages = Pages($"<style>@page {{ size: 300pt 300pt; margin: 20pt }}</style><div style='continue: discard'>{Lines(40)}</div><p>After</p>");
        var texts = Texts(pages[0]).ToList();
        // 260pt of page holds 13 lines of 20pt; the rest is gone, and the next paragraph follows on the next page.
        Assert.Contains("Line 13", texts);
        Assert.DoesNotContain("Line 14", texts);
        Assert.Equal(2, pages.Count);
        Assert.Equal(["After"], Texts(pages[1]));
        Assert.DoesNotContain(pages, p => Texts(p).Contains("Line 40"));
    }

    [Fact]
    public void The_height_of_the_box_ends_its_content()
    {
        var pages = Pages($"<div style='height: 100pt; continue: discard'>{Lines(20)}</div><p style='margin: 0'>After</p>");
        Assert.Single(pages);
        var texts = Texts(pages[0]).ToList();
        Assert.Equal(["Line 1", "Line 2", "Line 3", "Line 4", "Line 5", "After"], texts);
        // The next paragraph starts below the box's 100pt.
        var after = pages[0].Lines.Single(l => l.Text.Contains("After", StringComparison.Ordinal));
        var first = pages[0].Lines.Single(l => l.Text.Contains("Line 1", StringComparison.Ordinal));
        Assert.InRange(after.Y - first.Y, 95, 106);
    }

    [Fact]
    public void A_line_that_crosses_the_height_is_left_out_whole()
    {
        var pages = Pages($"<div style='height: 50pt; continue: discard'>{Lines(5)}</div>");
        Assert.Equal(["Line 1", "Line 2"], Texts(pages[0]));
    }

    [Fact]
    public void Max_height_ends_the_content_of_a_box_with_an_auto_height()
    {
        var pages = Pages($"<div style='max-height: 60pt; continue: discard'>{Lines(10)}</div>");
        Assert.Equal(["Line 1", "Line 2", "Line 3"], Texts(pages[0]));
    }

    [Fact]
    public void A_box_whose_height_runs_past_the_page_ends_at_the_page()
    {
        var pages = Pages($"<style>@page {{ size: 300pt 300pt; margin: 20pt }}</style><div style='height: 1000pt; background: #eee; continue: discard'>{Lines(60)}</div><p>After</p>");
        Assert.Equal(2, pages.Count);
        Assert.Contains("Line 13", Texts(pages[0]));
        Assert.Equal(["After"], Texts(pages[1]));
    }

    [Fact]
    public void A_box_with_no_room_left_on_its_page_moves_to_the_next()
    {
        var pages = Pages($"<style>@page {{ size: 300pt 300pt; margin: 20pt }}</style>{Lines(12, "Before")}<div style='continue: discard'><p style='margin: 0; line-height: 40pt'>Inside</p></div>");
        Assert.Equal(2, pages.Count);
        Assert.DoesNotContain("Inside", Texts(pages[0]));
        Assert.Equal(["Inside"], Texts(pages[1]));
    }

    [Fact]
    public void Its_bottom_border_stays_on_the_page()
    {
        var pdf = HtmlToPdf.Convert($"<style>@page {{ size: 300pt 300pt; margin: 20pt }}</style><div style='border-bottom: 10pt solid black; continue: discard'>{Lines(40)}</div>", Fixed);
        Assert.Equal(1, PdfTestHelpers.PageCount(pdf));
        // The border leaves room for 12 lines of 20pt above it.
        var texts = Texts(PdfText.ExtractPages(pdf)[0]).ToList();
        Assert.Contains("Line 12", texts);
        Assert.DoesNotContain("Line 13", texts);
    }

    [Fact]
    public void A_flex_item_does_not_stretch_its_line_onto_the_next_page()
    {
        var pages = Pages($@"<style>@page {{ size: 300pt 300pt; margin: 20pt }}</style>
            <div style='display: flex'><div style='width: 80pt; border: 1pt solid black'>Label</div>
            <div style='flex: 1; continue: discard'>{Lines(40)}</div></div><p style='margin: 0'>After</p>");
        Assert.Equal(2, pages.Count);
        Assert.Contains("Line 13", Texts(pages[0]));
        Assert.DoesNotContain("Line 14", Texts(pages[0]));
        // The label's border ends on the first page: the paragraph after the row starts the second.
        var after = pages[1].Lines.Single(l => l.Text.Contains("After", StringComparison.Ordinal));
        Assert.True(after.Y < 40, $"After at {after.Y}pt");
    }

    [Fact]
    public void Discard_is_not_inherited()
    {
        var pages = Pages($"<style>@page {{ size: 300pt 300pt; margin: 20pt }}</style><div style='continue: discard'><div style='continue: auto'>{Lines(20)}</div></div>");
        Assert.Single(pages);
        var plain = Pages($"<style>@page {{ size: 300pt 300pt; margin: 20pt }}</style><div><div style='continue: inherit'>{Lines(20)}</div></div>");
        Assert.Equal(2, plain.Count);
    }

    [Fact]
    public void Supports_knows_the_property()
    {
        var pages = Pages("<style>p { display: none } @supports (continue: discard) { p { display: block } }</style><p>Shown</p>");
        Assert.Equal(["Shown"], Texts(pages[0]));
    }
}
