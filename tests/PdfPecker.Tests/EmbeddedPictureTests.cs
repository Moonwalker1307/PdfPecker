using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>embed and object elements show the picture they point at, as Chrome prints them.</summary>
public class EmbeddedPictureTests
{
    private const string Head = "<!doctype html><style>@page{margin:0} body{margin:0} embed, object{display:block}</style>";

    // Squares of 40 and 80 px: 30 and 60 pt.
    private static readonly Dictionary<string, byte[]> Files = new()
    {
        ["red.png"] = SolidPng(40, 40, 255, 0, 0),
        ["green.png"] = SolidPng(80, 80, 0, 160, 0),
        ["blue.svg"] = "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'><rect width='40' height='40' fill='#00f'/></svg>"u8.ToArray(),
    };

    private static byte[] Convert(string body)
        => HtmlToPdf.Convert(Head + body, new PdfOptions { ResourceResolver = url => Files.GetValueOrDefault(url) });

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Shows(byte[] pdf, (int R, int G, int B) color, int size)
    {
        Assert.Equal(color, Pixel(pdf, size - 2, size - 2));
        Assert.Equal((255, 255, 255), Pixel(pdf, size + 2, 2));
        Assert.Equal((255, 255, 255), Pixel(pdf, 2, size + 2));
    }

    private static readonly (int, int, int) Red = (255, 0, 0), Green = (0, 160, 0), Blue = (0, 0, 255);

    [Theory]
    [InlineData("<embed src='red.png' type='image/png'>")]
    [InlineData("<embed src='red.png'>")]
    [InlineData("<object data='red.png' type='image/png'>Fallback</object>")]
    public void An_embedded_picture_is_shown_at_its_own_size(string body)
    {
        var pdf = Convert(body);
        Shows(pdf, Red, 30);
        Assert.DoesNotContain("Fallback", PdfText.Extract(pdf));
    }

    [Fact]
    public void An_embedded_svg_is_drawn()
        => Shows(Convert("<embed src='blue.svg' type='image/svg+xml'>"), Blue, 30);

    [Fact]
    public void Width_and_height_attributes_size_the_picture()
        => Shows(Convert("<object data='red.png' width='80' height='80'>Fallback</object>"), Red, 60);

    [Theory]
    [InlineData("<object data='missing.png'>Fallback</object>")]
    [InlineData("<object data='green.png' type='application/pdf'>Fallback</object>")]
    [InlineData("<object>Fallback</object>")]
    public void An_object_without_a_picture_shows_its_fallback_content(string body)
        => Assert.Contains("Fallback", PdfText.Extract(Convert(body)));

    [Fact]
    public void A_nested_object_in_the_fallback_content_shows_its_picture()
        => Shows(Convert("<object data='missing.png'><object data='green.png'>Inner fallback</object></object>"), Green, 60);

    [Fact]
    public void A_video_shows_its_poster_and_never_its_fallback_content()
    {
        var pdf = Convert("<video poster='red.png' style='display:block'>Fallback<source src='movie.mp4'></video>");
        Shows(pdf, Red, 30);
        Assert.DoesNotContain("Fallback", PdfText.Extract(pdf));
    }

    [Fact]
    public void A_video_without_a_poster_is_a_black_box_of_the_default_size()
    {
        // 300 by 150 px: 225 by 112.5 pt.
        var pdf = Convert("<video style='display:block'>Fallback</video>");
        Assert.Equal((0, 0, 0), Pixel(pdf, 220, 110));
        Assert.Equal((255, 255, 255), Pixel(pdf, 230, 50));
        Assert.Equal((255, 255, 255), Pixel(pdf, 50, 116));
        Assert.DoesNotContain("Fallback", PdfText.Extract(pdf));
    }

    [Fact]
    public void An_iframe_shows_its_srcdoc_in_its_own_styles()
    {
        // The frame's document keeps its body margin and its own style sheet, and the page's styles do not reach in.
        var pdf = Convert("<style>p{color:red;display:none}</style><iframe srcdoc=\"<style>body{background:#00f}</style><p>Inside the frame</p>\" "
                          + "style='display:block;border:0' width=200 height=100></iframe><p>Hidden outside</p>");
        Assert.Contains("Inside the frame", PdfText.Extract(pdf));
        Assert.DoesNotContain("Hidden outside", PdfText.Extract(pdf));
        // The body's background fills the frame's viewport, 150 by 75 pt, and nothing past it.
        Assert.Equal(Blue, Pixel(pdf, 2, 2));
        Assert.Equal(Blue, Pixel(pdf, 148, 73));
        Assert.Equal((255, 255, 255), Pixel(pdf, 152, 40));
    }

    [Fact]
    public void An_iframe_clips_its_document_to_its_box()
    {
        var pdf = Convert("<iframe srcdoc=\"<div style='height:1000px;background:#00f'></div>\" style='display:block;border:0' width=100 height=50></iframe>");
        Assert.Equal(Blue, Pixel(pdf, 40, 30));
        Assert.Equal((255, 255, 255), Pixel(pdf, 40, 60));
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
    }

    [Fact]
    public void A_tagged_pdf_draws_an_iframes_document_as_an_artifact()
    {
        var pdf = HtmlToPdf.Convert("<!doctype html><p>Outside</p><iframe srcdoc='<p>Inside</p>'></iframe>",
            new PdfOptions { Accessibility = PdfAccessibility.Tagged });
        Assert.Contains("Inside", PdfText.Extract(pdf));
        Assert.Contains("/Artifact BMC", PdfTestHelpers.AllContent(pdf));
    }
}
