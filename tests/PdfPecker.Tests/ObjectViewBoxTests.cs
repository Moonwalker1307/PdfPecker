using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// object-view-box (CSS Images 5): the part of an image it names becomes the image's natural size, and object-fit and
/// object-position place that part, as Chrome draws it.
/// </summary>
public class ObjectViewBoxTests
{
    // A 40 x 40 pixel image (30 x 30 pt) of four quadrants: red, green on top, blue, yellow below.
    private static readonly string Quadrants = DataUri(PixelPng(40, 40, (x, y) => (x < 20, y < 20) switch
    {
        (true, true) => ((byte)255, (byte)0, (byte)0),
        (false, true) => ((byte)0, (byte)255, (byte)0),
        (true, false) => ((byte)0, (byte)0, (byte)255),
        _ => ((byte)255, (byte)255, (byte)0),
    }));

    private static byte[] Convert(string style)
        => HtmlToPdf.Convert($"<style>@page {{ size: 200pt 200pt; margin: 0 }} body {{ margin: 0 }} img {{ display: block }}</style><img src='{Quadrants}' style='{style}'>",
            new PdfOptions { Compress = false });

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual)
        => Assert.True(Math.Abs(actual.R - r) < 40 && Math.Abs(actual.G - g) < 40 && Math.Abs(actual.B - b) < 40, $"Expected ({r}, {g}, {b}), got {actual}");

    [Fact]
    public void The_view_box_is_the_natural_size()
    {
        // The top right quadrant alone: 15 x 15 pt, all green.
        var pdf = Convert("object-view-box: inset(0 0 50% 50%)");
        Color(0, 255, 0, Pixel(pdf, 2, 2));
        Color(0, 255, 0, Pixel(pdf, 13, 13));
        Color(255, 255, 255, Pixel(pdf, 17, 7));
        Color(255, 255, 255, Pixel(pdf, 7, 17));
    }

    [Fact]
    public void The_view_box_fills_a_sized_box()
    {
        // xywh() of the bottom left quadrant, stretched to 100 x 50 pt.
        var pdf = Convert("width: 100pt; height: 50pt; object-view-box: xywh(0 50% 50% 50%)");
        Color(0, 0, 255, Pixel(pdf, 5, 5));
        Color(0, 0, 255, Pixel(pdf, 95, 45));
        Color(255, 255, 255, Pixel(pdf, 50, 55));
    }

    [Fact]
    public void Contain_shows_the_rest_of_the_image_inside_the_content_box()
    {
        // The right half (15 x 30 pt) contained in 60 x 60 pt: 30 x 60 at x = 15..45. The left half of the image
        // falls at x = -15..15 and shows from 0 to 15, as in Chrome.
        var pdf = Convert("width: 60pt; height: 60pt; object-view-box: inset(0 0 0 50%); object-fit: contain");
        Color(0, 255, 0, Pixel(pdf, 20, 10));
        Color(255, 255, 0, Pixel(pdf, 40, 50));
        Color(255, 0, 0, Pixel(pdf, 10, 10));
        Color(255, 255, 255, Pixel(pdf, 50, 30));
    }

    [Fact]
    public void Rect_takes_edges_and_none_turns_it_off()
    {
        var pdf = Convert("object-view-box: rect(0 40px 20px 0)"); // the top half: 30 x 15 pt
        Color(255, 0, 0, Pixel(pdf, 5, 5));
        Color(0, 255, 0, Pixel(pdf, 25, 10));
        Color(255, 255, 255, Pixel(pdf, 5, 20));
        var none = Convert("object-view-box: inset(10%); object-view-box: none");
        Color(0, 0, 255, Pixel(none, 5, 25));
    }

    [Theory]
    [InlineData("circle(10px)")]
    [InlineData("inset(10% round 5px)")]
    [InlineData("inset(10%) border-box")]
    [InlineData("xywh(0 0 -1px 10px)")]
    public void Other_values_are_ignored(string value)
    {
        var pdf = Convert($"object-view-box: {value}");
        Color(0, 0, 255, Pixel(pdf, 5, 25)); // the whole image, 30 x 30 pt
        Color(255, 255, 0, Pixel(pdf, 25, 25));
    }

    [Fact]
    public void A_view_box_of_nothing_draws_the_whole_image()
    {
        var pdf = Convert("object-view-box: inset(50%)");
        Color(255, 255, 0, Pixel(pdf, 25, 25));
    }
}
