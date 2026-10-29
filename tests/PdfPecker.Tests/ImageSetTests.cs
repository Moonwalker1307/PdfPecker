using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>image-set() chooses an image for backgrounds, list markers and generated content as Chrome does when it prints.</summary>
public class ImageSetTests
{
    private const string Head = "<!doctype html><style>@page{margin:0} body{margin:0} .b{width:200px;height:200px;background-repeat:no-repeat}</style>";

    // Squares of 40 and 80 px: 30 and 60 pt at 1x.
    private static readonly Dictionary<string, byte[]> Files = new()
    {
        ["red.png"] = SolidPng(40, 40, 255, 0, 0),
        ["green.png"] = SolidPng(80, 80, 0, 160, 0),
    };

    private static byte[] Convert(string body)
        => HtmlToPdf.Convert(Head + body, new PdfOptions { ResourceResolver = url => Files.GetValueOrDefault(url) });

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    /// <summary>Asserts that a background of <paramref name="image"/> is a square of <paramref name="size"/> points in the given colour.</summary>
    private static void Shows(string image, (int R, int G, int B) color, int size)
    {
        var pdf = Convert($"<div class=b style=\"background-image:{image}\"></div>");
        Assert.Equal(color, Pixel(pdf, size - 2, size - 2));
        Assert.Equal((255, 255, 255), Pixel(pdf, size + 2, 2));
        Assert.Equal((255, 255, 255), Pixel(pdf, 2, size + 2));
    }

    private static readonly (int, int, int) Red = (255, 0, 0), Green = (0, 160, 0);

    [Fact]
    public void A_2x_option_shows_its_pixels_at_half_size()
        => Shows("image-set(url(green.png) 2x)", Green, 30);

    [Fact]
    public void The_lowest_resolution_of_1x_or_more_wins()
    {
        Shows("image-set(url(green.png) 2x, url(red.png) 1x)", Red, 30);
        // Unlike srcset, there is no geometric mean: 1.5x wins over 0.8x.
        Shows("image-set(url(red.png) 0.8x, 'green.png' 1.5x)", Green, 40);
    }

    [Fact]
    public void Below_1x_the_highest_resolution_wins()
        => Shows("image-set(url(green.png) 0.5x, url(red.png) 0.8x)", Red, 38);

    [Fact]
    public void An_option_of_a_type_PdfPecker_does_not_decode_is_skipped()
        => Shows("-webkit-image-set(url(red.png) type('image/x-unknown'), url(green.png) 192dpi type('image/png'))", Green, 30);

    [Fact]
    public void Resolutions_and_options_parse_as_in_Chrome()
    {
        Assert.Equal(("url(a)", 2f), CssImageSet.Choose("image-set(url(a) 2dppx)"));
        Assert.Equal(("url(\"a\")", 1f), CssImageSet.Choose("image-set(\"a\")"));
        Assert.Equal(("linear-gradient(red, blue)", 1f), CssImageSet.Choose("image-set(linear-gradient(red, blue) 1x)"));
        Assert.Equal(2.54f, CssImageSet.Choose("image-set(url(a) 96dpcm)")!.Value.Resolution, 3);
        Assert.Equal("url(a)", CssImageSet.Choose("image-set(url(a) type(\"image/png\"))")?.Image);
        Assert.Null(CssImageSet.Choose("image-set(url(a) 2x foo)"));
        Assert.Null(CssImageSet.Choose("image-set(url(a) 2x 3x)"));
        Assert.Null(CssImageSet.Choose("image-set(red 1x)"));
        Assert.Null(CssImageSet.Choose("image-set(url(a) 0x)"));
        Assert.Null(CssImageSet.Choose("image-set(url(a) type('image/x-unknown'))"));
        Assert.True(CssSupports.Evaluate("(background-image: image-set(url(a) 1x))"));
    }

    [Fact]
    public void List_markers_and_generated_content_show_the_chosen_option_at_its_resolution()
    {
        var marker = Convert("<ul style='margin:0;padding-left:100px'><li style='list-style-image:image-set(url(green.png) 2x, url(red.png) 0.5x)'>a</li></ul>");
        var content = Convert("<style>p::before{content:image-set(url(green.png) 2x)}</style><p style='margin:0'></p>");
        Assert.Equal(Green, Pixel(content, 28, 28));
        Assert.Equal((255, 255, 255), Pixel(content, 32, 2));
        // The marker sits left of the list item's text, 30pt wide.
        var page = PdfRenderer.Open(marker).RenderPage(0, new ImageOptions { Dpi = 72 });
        int greenColumns = 0;
        for (int x = 0; x < 75; x++)
        {
            bool any = false;
            for (int y = 0; y < 40 && !any; y++)
            {
                int i = (y * page.Width + x) * 4;
                any = page.Rgba[i] == 0 && page.Rgba[i + 1] == 160 && page.Rgba[i + 2] == 0;
            }
            if (any) greenColumns++;
        }
        Assert.InRange(greenColumns, 29, 31);
    }
}

/// <summary>image-set() values that are huge, tiny, many or malformed.</summary>
[Collection(HostileInputCollection.Name)]
public class ImageSetHostileInputTests
{
    [Fact]
    public void Extreme_resolutions_and_many_options_stay_bounded()
    {
        string png = DataUri(SolidPng(4, 4, 255, 0, 0));
        string many = string.Join(", ", Enumerable.Range(1, 5000).Select(i => $"url({png}) {i}x"));
        var html = $"<div style='width:50px;height:50px;background-image:image-set(url({png}) 1e-30x)'></div>"
            + $"<div style='width:50px;height:50px;background:image-set(url({png}) 0.000001x) repeat'></div>"
            + $"<div style='width:50px;height:50px;background-image:image-set(url({png}) 1e30dpi, url({png}) NaNx, ((((, type(()'></div>"
            + $"<div style='width:50px;height:50px;background-image:image-set({many})'></div>"
            + $"<ul><li style='list-style-image:image-set(url({png}) 0.000001x)'>a</li></ul>"
            + $"<style>p::after{{content:image-set(url({png}) 0.000001x) image-set(type('x'))}}</style><p>b</p>";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        PdfTestHelpers.AssertStructurallyValid(HtmlToPdf.Convert(html, null, cts.Token));
    }
}
