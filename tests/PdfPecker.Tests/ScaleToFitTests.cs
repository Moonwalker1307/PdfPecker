using System.Diagnostics;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>-pdfpecker-scale-to-fit: shrink, which draws a box's content smaller until it fits the box.</summary>
public class ScaleToFitTests
{
    private const string Words = "The quick brown fox jumps over the lazy dog. ";

    private static PdfTextPage Page(string html) => PdfText.ExtractPages(HtmlToPdf.Convert(html))[0];

    private static IEnumerable<PdfTextLine> LinesWith(PdfTextPage page, string text)
        => page.Lines.Where(l => l.Text.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Content_that_fits_is_drawn_as_without_the_property()
    {
        const string box = "<div style='width: 200pt; height: 100pt; border: 1pt solid black{0}'><p>Short text</p></div>";
        var plain = HtmlToPdf.Convert(string.Format(box, ""), new PdfOptions { CreationDate = DateTimeOffset.UnixEpoch });
        var scaled = HtmlToPdf.Convert(string.Format(box, "; -pdfpecker-scale-to-fit: shrink"), new PdfOptions { CreationDate = DateTimeOffset.UnixEpoch });
        Assert.Equal(plain, scaled);
    }

    [Fact]
    public void Text_taller_than_the_box_is_drawn_smaller_inside_it()
    {
        string text = string.Concat(Enumerable.Repeat(Words, 12));
        var page = Page($@"<div id='box' style='width: 200pt; height: 60pt; font-size: 12pt; -pdfpecker-scale-to-fit: shrink'>{text}</div>
            <p>After the box</p>");
        var lines = page.Lines.Where(l => !l.Text.Contains("After", StringComparison.Ordinal)).ToList();
        Assert.True(lines.Count > 1);
        // Smaller than 12pt text, and all of it within the box's 60pt.
        Assert.All(lines, l => Assert.True(l.Height < 11, $"line height {l.Height}"));
        double top = lines.Min(l => l.Y), bottom = lines.Max(l => l.Y + l.Height);
        Assert.True(bottom - top <= 61, $"text spans {bottom - top}pt");
        Assert.True(lines.Max(l => l.X + l.Width) - lines.Min(l => l.X) <= 201);
        // The box keeps its height: the next paragraph starts below it, not below the unscaled text.
        var after = LinesWith(page, "After the box").Single();
        Assert.True(after.Y - top < 90, $"next paragraph at {after.Y - top}pt below the text");
        Assert.Equal(text.Trim(), string.Join(" ", lines.Select(l => l.Text)).Trim());
    }

    [Fact]
    public void A_scale_is_found_close_to_the_largest_that_fits()
    {
        string text = string.Concat(Enumerable.Repeat(Words, 6));
        var page = Page($"<div style='width: 300pt; height: 50pt; font-size: 20pt; line-height: 1; -pdfpecker-scale-to-fit: shrink'>{text}</div>");
        double size = page.Lines.Max(l => l.Height);
        // At 20pt the text needs many more lines than fit in 50pt, but far less than a twentieth of the size.
        Assert.InRange(size, 4, 19);
        double span = page.Lines.Max(l => l.Y + l.Height) - page.Lines.Min(l => l.Y);
        Assert.InRange(span, 40, 51);
    }

    [Fact]
    public void Max_height_is_used_when_the_box_has_no_height()
    {
        string text = string.Concat(Enumerable.Repeat(Words, 12));
        var page = Page($"<div style='width: 200pt; max-height: 40pt; -pdfpecker-scale-to-fit: shrink'>{text}</div>");
        double span = page.Lines.Max(l => l.Y + l.Height) - page.Lines.Min(l => l.Y);
        Assert.True(span <= 41, $"text spans {span}pt");
    }

    [Fact]
    public void A_word_wider_than_the_box_is_drawn_narrower()
    {
        var page = Page("<div style='width: 100pt; height: 100pt; font-size: 20pt; -pdfpecker-scale-to-fit: shrink'>Unbreakablelongwordthatdoesnotfit</div>");
        var line = page.Lines.Single();
        Assert.True(line.Width <= 101, $"word is {line.Width}pt wide");
    }

    [Fact]
    public void Table_cells_with_a_height_scale_their_content()
    {
        string text = string.Concat(Enumerable.Repeat(Words, 10));
        var page = Page($@"<table style='border-collapse: collapse'><tr>
            <td style='width: 150pt; height: 40pt; padding: 0; -pdfpecker-scale-to-fit: shrink'>{text}</td>
            <td style='width: 150pt'>Beside</td></tr></table>");
        var cellLines = page.Lines.Where(l => !l.Text.Contains("Beside", StringComparison.Ordinal)).ToList();
        double span = cellLines.Max(l => l.Y + l.Height) - cellLines.Min(l => l.Y);
        Assert.True(span <= 42, $"cell text spans {span}pt");
    }

    [Fact]
    public void Right_to_left_content_keeps_to_the_right_edge()
    {
        string text = string.Concat(Enumerable.Repeat("שלום עולם ", 40));
        var page = Page($"<div dir='rtl' style='margin-left: 100pt; width: 200pt; height: 30pt; -pdfpecker-scale-to-fit: shrink'>{text}</div>");
        double right = page.Lines.Max(l => l.X + l.Width), left = page.Lines.Min(l => l.X);
        // The box runs from 100pt to 300pt past the left margin; the scaled text ends near its right edge.
        Assert.True(right - left <= 201);
        Assert.True(right > left + 150, "the text should still fill most of the box's width");
    }

    [Fact]
    public void Nested_boxes_and_long_content_finish_in_bounded_time()
    {
        string text = string.Concat(Enumerable.Repeat(Words, 400));
        string inner = $"<div style='height: 20pt; -pdfpecker-scale-to-fit: shrink'>{text}</div>";
        string html = string.Concat(Enumerable.Repeat($"<div style='height: 60pt; -pdfpecker-scale-to-fit: shrink'>{inner}{inner}{inner}</div>", 5));
        var watch = Stopwatch.StartNew();
        var pdf = HtmlToPdf.Convert(html);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void A_box_that_would_cross_a_page_goes_to_the_next_page_whole()
    {
        string text = string.Concat(Enumerable.Repeat(Words, 20));
        var pages = PdfText.ExtractPages(HtmlToPdf.Convert(
            $"<div style='height: 700pt'></div><div style='height: 200pt; -pdfpecker-scale-to-fit: shrink'>{text}</div>"));
        Assert.Equal(2, pages.Count);
        Assert.Empty(pages[0].Lines);
    }
}
