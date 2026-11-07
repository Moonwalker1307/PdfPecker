using System.Text;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// The @page bleed and marks properties (CSS Paged Media 3, 7.1 and 7.2): the media box reaches past the paper by the
/// bleed, the paper is the trim box, and crop marks and registration crosses are drawn in the bleed.
/// </summary>
public class PageBleedTests
{
    private static byte[] Convert(string pageRule, string body = "<p>Text</p>", string css = "")
        => HtmlToPdf.Convert($"<style>@page {{ size: 200pt 100pt; margin: 10pt; {pageRule} }} body {{ margin: 0 }} {css}</style><body>{body}</body>",
            new PdfOptions { Compress = false });

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y, int page = 0)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 40)
        => Assert.True(Math.Abs(actual.R - r) < tolerance && Math.Abs(actual.G - g) < tolerance && Math.Abs(actual.B - b) < tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    [Fact]
    public void Bleed_widens_the_media_box_and_the_paper_becomes_the_trim_box()
    {
        var pdf = Convert("bleed: 9pt;");
        AssertStructurallyValid(pdf);
        var text = Text(pdf);
        Assert.Contains("/MediaBox [-9 -9 209 109] /BleedBox [-9 -9 209 109] /TrimBox [0 0 200 100]", text);
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
    }

    [Fact]
    public void A_page_without_bleed_or_crop_marks_keeps_its_media_box()
    {
        var text = Text(Convert("marks: cross;"));
        Assert.Contains("/MediaBox [0 0 200 100]", text);
        Assert.DoesNotContain("/TrimBox", text);
        Assert.DoesNotContain("/BleedBox", text);
    }

    [Fact]
    public void Crop_marks_give_an_auto_bleed_of_6pt()
    {
        var text = Text(Convert("marks: crop;"));
        Assert.Contains("/MediaBox [-6 -6 206 106]", text);
        // The marks continue the trim lines in the outer half of the bleed: here the top left one, above the page.
        Assert.Contains("0 103 m 0 106 l S", text);
        Assert.Contains("-6 0 m -3 0 l S", text);
    }

    [Fact]
    public void Bleed_none_overrides_the_auto_bleed_of_crop_marks()
    {
        Assert.Contains("/MediaBox [0 0 200 100]", Text(Convert("marks: crop; bleed: 0;")));
    }

    [Fact]
    public void Cross_marks_sit_at_the_middle_of_each_side()
    {
        var text = Text(Convert("bleed: 8pt; marks: cross;"));
        // The cross above the page: 6pt above it, arms of 2pt.
        Assert.Contains("98 106 m 102 106 l S", text);
        Assert.Contains("100 104 m 100 108 l S", text);
    }

    [Theory]
    [InlineData("marks: crop crop;")]
    [InlineData("marks: none crop;")]
    [InlineData("marks: dashed;")]
    [InlineData("bleed: 10%;")]
    [InlineData("bleed: red;")]
    public void Invalid_values_are_ignored(string rule)
    {
        Assert.Contains("/MediaBox [0 0 200 100]", Text(Convert(rule)));
    }

    [Fact]
    public void Bleed_is_at_most_an_inch_and_not_negative()
    {
        Assert.Contains("/MediaBox [-72 -72 272 172]", Text(Convert("bleed: 10in;")));
        Assert.Contains("/MediaBox [0 0 200 100]", Text(Convert("bleed: -5pt;")));
    }

    [Fact]
    public void The_page_background_fills_the_bleed()
    {
        var pdf = Convert("bleed: 10pt; background: #0000ff;");
        // The rendered page is the media box: 220 x 120 pixels at 72 dpi.
        Color(0, 0, 255, Pixel(pdf, 2, 2));
        Color(0, 0, 255, Pixel(pdf, 217, 117));
    }

    [Fact]
    public void Content_runs_into_the_bleed_without_a_second_page()
    {
        const string band = "position:absolute; left:-20pt; width:240pt; height:30pt; background:#ff0000";
        var pdf = Convert("bleed: 10pt;", $"<div style='{band}; top:-20pt'></div><div style='{band}; bottom:-20pt'></div><p>Text</p>");
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
        Color(255, 0, 0, Pixel(pdf, 2, 2));     // the bleed above the page, left of it
        Color(255, 0, 0, Pixel(pdf, 217, 117)); // and below it, right of it
        Color(255, 255, 255, Pixel(pdf, 110, 60));
    }

    [Fact]
    public void Without_a_bleed_content_past_the_page_is_cut_as_before()
    {
        var pdf = Convert("", "<div style='position:absolute; left:0; top:-20pt; width:50pt; height:30pt; background:#ff0000'></div>");
        Color(255, 0, 0, Pixel(pdf, 15, 15));
        Color(255, 255, 255, Pixel(pdf, 15, 25));
    }

    [Fact]
    public void Supports_knows_bleed_and_marks()
    {
        var pdf = Text(Convert("", "<p class=a>Text</p>", "@supports (bleed: 3mm) and (marks: crop) { .a { color: #ff0000 } }"));
        Assert.Contains("1 0 0 rg", pdf);
    }
}
