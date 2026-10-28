using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// The border, padding and background of the @page box: the border sits inside the page margins and the content inside
/// the border and padding, as Chrome prints them, and the background layers cover the whole sheet, positioned in the
/// page box's padding box (CSS Paged Media 3, 3.1).
/// </summary>
public class PageBoxTests
{
    // A 400 x 400 pt sheet, rendered at 72 dpi so that pixels are points.
    private const string Sheet = "size: 400pt 400pt; margin: 40pt;";

    private static byte[] Convert(string pageRule, string body, string css = "")
        => HtmlToPdf.Convert($"<style>@page {{ {Sheet} {pageRule} }} body {{ margin: 0 }} {css}</style><body>{body}</body>",
            new PdfOptions { Compress = false });

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y, int page = 0)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 40)
        => Assert.True(Math.Abs(actual.R - r) < tolerance && Math.Abs(actual.G - g) < tolerance && Math.Abs(actual.B - b) < tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    private const string Block = "<div style='height:20pt; background:#00ff00'></div>";

    [Fact]
    public void Border_sits_inside_the_margins_and_the_content_inside_border_and_padding()
    {
        var pdf = Convert("border: 4pt solid #0000ff; padding: 10pt;", Block);
        AssertStructurallyValid(pdf);
        Color(255, 255, 255, Pixel(pdf, 20, 200)); // the margin
        Color(0, 0, 255, Pixel(pdf, 42, 200));     // the border, 40..44
        Color(0, 0, 255, Pixel(pdf, 200, 357));    // the bottom border, 356..360
        Color(255, 255, 255, Pixel(pdf, 49, 200)); // the padding, 44..54
        Color(0, 255, 0, Pixel(pdf, 57, 60));      // the content starts at 54
        Color(0, 255, 0, Pixel(pdf, 343, 60));     // and ends at 346
        Color(255, 255, 255, Pixel(pdf, 349, 60));
    }

    [Fact]
    public void Background_layers_cover_the_whole_sheet()
    {
        var pdf = Convert("background: linear-gradient(#ff0000, #ff0000);", "<p>Text</p>");
        Color(255, 0, 0, Pixel(pdf, 5, 5));
        Color(255, 0, 0, Pixel(pdf, 395, 395));
        Color(255, 0, 0, Pixel(pdf, 200, 200));
    }

    [Fact]
    public void Background_layers_are_positioned_in_the_padding_box()
    {
        var pdf = Convert("border: 5pt solid #0000ff; padding: 10pt; background: #ffff00 linear-gradient(#ff0000, #ff0000) no-repeat 0 0 / 20pt 20pt;", "<p>Text</p>");
        Color(255, 255, 0, Pixel(pdf, 20, 20));  // the colour fills the margins too
        Color(255, 0, 0, Pixel(pdf, 50, 50));    // the tile, 45..65
        Color(255, 255, 0, Pixel(pdf, 70, 70));
        var origin = Convert("padding: 10pt; background: linear-gradient(#ff0000, #ff0000) no-repeat 0 0 / 20pt 20pt; background-origin: content-box;", "");
        Color(255, 255, 255, Pixel(origin, 45, 45));
        Color(255, 0, 0, Pixel(origin, 55, 55)); // the content box starts at 50
    }

    [Fact]
    public void Margin_boxes_keep_to_the_margins_outside_the_border()
    {
        var pdf = Convert("border: 10pt solid #0000ff; @top-center { content: 'Head'; background: #ffff00 }", "<p>Text</p>");
        Color(255, 255, 0, Pixel(pdf, 200, 35));
        Color(0, 0, 255, Pixel(pdf, 200, 45));
    }

    [Fact]
    public void Canvas_background_fills_the_padding_box_under_the_border()
    {
        var pdf = Convert("border: 4pt solid #0000ff; padding: 10pt;", "<p>Text</p>", "html { background: #00ff00 }");
        Color(255, 255, 255, Pixel(pdf, 20, 200));
        Color(0, 0, 255, Pixel(pdf, 42, 200));
        Color(0, 255, 0, Pixel(pdf, 48, 200));
    }

    [Fact]
    public void First_page_rule_gives_the_first_page_its_own_border()
    {
        var lines = string.Concat(Enumerable.Repeat("<p style='margin:0; height:30pt'>Line</p>", 20));
        var pdf = HtmlToPdf.Convert($"<style>@page {{ {Sheet} border: 4pt solid #0000ff }} @page :first {{ border-color: #ff0000; padding: 20pt }} body {{ margin: 0 }}</style><body>{lines}</body>",
            new PdfOptions { Compress = false });
        Assert.True(PdfRenderer.Open(pdf).PageCount >= 2);
        Color(255, 0, 0, Pixel(pdf, 42, 200));
        Color(0, 0, 255, Pixel(pdf, 42, 200, page: 1));
    }

    [Fact]
    public void Page_box_wider_than_the_page_still_converts()
    {
        var pdf = HtmlToPdf.Convert("<style>@page { size: 100pt 100pt; margin: 10pt; border: 500pt solid red; padding: 1000pt 20% }</style><p>Text</p>",
            new PdfOptions());
        AssertStructurallyValid(pdf);
    }
}
