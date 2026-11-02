using System.Globalization;
using System.Text.RegularExpressions;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>@page { size: 80mm auto }: a page as tall as its content, for receipts and other paper on a roll.</summary>
public class AutoPageHeightTests
{
    private static readonly PdfOptions Fixed = new() { CreationDate = DateTimeOffset.UnixEpoch };

    /// <summary>The page sizes in page order, in points.</summary>
    private static List<(double W, double H)> PageSizes(byte[] pdf)
        => Regex.Matches(Latin1(pdf), @"/Type /Page /Parent \d+ 0 R /MediaBox \[0 0 ([\d.]+) ([\d.]+)\]")
            .Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();

    private static string Lines(int count)
        => string.Concat(Enumerable.Range(1, count).Select(i => $"<p style='margin: 0; height: 20pt'>Line {i}</p>"));

    [Fact]
    public void The_page_is_as_tall_as_its_content_and_margins()
    {
        var pdf = HtmlToPdf.Convert($"<style>@page {{ size: 200pt auto; margin: 10pt 20pt 30pt }}</style>{Lines(5)}", Fixed);
        var size = Assert.Single(PageSizes(pdf));
        Assert.Equal(200, size.W, 0.01);
        Assert.Equal(100 + 10 + 30, size.H, 0.1);
        Assert.Equal(5, PdfText.ExtractPages(pdf)[0].Lines.Count);
    }

    [Fact]
    public void Content_taller_than_a_page_of_the_options_stays_on_one_page()
    {
        var pdf = HtmlToPdf.Convert($"<style>@page {{ size: 80mm auto; margin: 0 }}</style>{Lines(100)}", Fixed);
        var size = Assert.Single(PageSizes(pdf));
        // Heights snap to Chrome's layout units of 1/64 px, which makes each 20pt line a little shorter.
        Assert.Equal(2000, size.H, 1.0);
    }

    [Fact]
    public void A_footer_band_and_a_margin_box_go_at_the_bottom_of_the_fitted_page()
    {
        var pdf = HtmlToPdf.Convert($@"<style>@page {{ size: 200pt auto; margin: 10pt 10pt 30pt; @bottom-center {{ content: 'Thanks' }} }}</style>
            {Lines(3)}<footer data-sticky-pdf-footer>Footer</footer>", Fixed);
        var size = Assert.Single(PageSizes(pdf));
        var page = PdfText.ExtractPages(pdf)[0];
        var footer = page.Lines.Single(l => l.Text.Contains("Footer", StringComparison.Ordinal));
        var thanks = page.Lines.Single(l => l.Text.Contains("Thanks", StringComparison.Ordinal));
        var last = page.Lines.Single(l => l.Text.Contains("Line 3", StringComparison.Ordinal));
        Assert.True(footer.Y > last.Y, "the footer is below the content");
        Assert.True(thanks.Y > footer.Y && thanks.Y < size.H, "the margin box is in the bottom margin");
        Assert.True(size.H < 200, $"page is {size.H}pt tall");
    }

    [Fact]
    public void A_named_page_with_an_auto_height_fits_only_its_own_content()
    {
        var pdf = HtmlToPdf.Convert($@"<style>@page {{ size: 300pt 400pt; margin: 0 }} @page receipt {{ size: 200pt auto }}
            .r {{ page: receipt }}</style><div>{Lines(2)}</div><div class='r'>{Lines(4)}</div><div>{Lines(1)}</div>", Fixed);
        var sizes = PageSizes(pdf);
        Assert.Equal(3, sizes.Count);
        Assert.Equal((300, 400), (Math.Round(sizes[0].W), Math.Round(sizes[0].H)));
        Assert.Equal((200, 80), (Math.Round(sizes[1].W), Math.Round(sizes[1].H)));
        Assert.Equal((300, 400), (Math.Round(sizes[2].W), Math.Round(sizes[2].H)));
    }

    [Fact]
    public void An_empty_document_gets_a_small_page()
    {
        var pdf = HtmlToPdf.Convert("<style>@page { size: 200pt auto; margin: 0 }</style>", Fixed);
        var size = Assert.Single(PageSizes(pdf));
        Assert.InRange(size.H, 1, 20);
    }

    [Fact]
    public void Content_longer_than_the_tallest_page_continues_on_pages_of_that_height()
    {
        var pdf = HtmlToPdf.Convert($"<style>@page {{ size: 200pt auto; margin: 0 }}</style>{Lines(800)}", Fixed);
        var sizes = PageSizes(pdf);
        Assert.Equal(2, sizes.Count);
        Assert.All(sizes, s => Assert.Equal(14400, s.H, 0.05));
    }

    [Fact]
    public void A_later_size_rule_replaces_the_auto_height()
    {
        var pdf = HtmlToPdf.Convert("<style>@page { size: 200pt auto } @page { size: 300pt 400pt }</style><p>Text</p>", Fixed);
        Assert.Equal((300, 400), PageSizes(pdf).Select(s => (Math.Round(s.W), Math.Round(s.H))).Single());
    }
}
