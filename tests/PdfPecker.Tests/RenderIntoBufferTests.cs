namespace PdfPecker.Tests;

public class RenderIntoBufferTests
{
    private static readonly byte[] Page = HtmlToPdf.Convert(
        "<body style='margin:0'><div style='background:#c03;width:40px;height:30px'></div><p style='color:#126;opacity:.6'>Text in grey and colour</p>"
        + "<img src='" + PdfTestHelpers.DataUri(PdfTestHelpers.AlphaPng(16, 16, (x, y) => (byte)(x * 16))) + "'></body>",
        new PdfOptions { PageSize = new PageSize(200, 120), Margins = new PageMargins(0) });

    private static readonly byte[] GrayPage = HtmlToPdf.Convert("<p style='color:#555'>Grey text on white</p>", new PdfOptions { PageSize = new PageSize(200, 60), Margins = new PageMargins(4) });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rgba_is_the_rendered_images_rgba(bool transparent)
    {
        var renderer = PdfRenderer.Open(Page);
        var options = new ImageOptions { Dpi = 100, Transparent = transparent };
        var image = renderer.RenderPage(0, options);
        Assert.Equal((image.Width, image.Height), renderer.GetPixelSize(0, options));
        var buffer = new byte[image.Width * image.Height * 4 + 7];
        Assert.Equal((image.Width, image.Height), renderer.RenderPage(0, buffer, ImagePixelFormat.Rgba, options));
        Assert.True(image.Rgba.AsSpan().SequenceEqual(buffer.AsSpan(0, image.Rgba.Length)));
        Assert.All(buffer[^7..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Rgb_of_an_opaque_page_drops_alpha()
    {
        var renderer = PdfRenderer.Open(Page);
        var image = renderer.RenderPage(0, new ImageOptions { Dpi = 100 });
        var rgba = image.Rgba;
        var rgb = new byte[image.Width * image.Height * 3];
        renderer.RenderPage(0, rgb, ImagePixelFormat.Rgb, new ImageOptions { Dpi = 100 });
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            Assert.Equal(255, rgba[i * 4 + 3]);
            Assert.True(rgba.AsSpan(i * 4, 3).SequenceEqual(rgb.AsSpan(i * 3, 3)), $"pixel {i}");
        }
    }

    [Fact]
    public void Gray_of_a_grey_page_is_its_level_and_of_colour_its_luma()
    {
        var renderer = PdfRenderer.Open(GrayPage);
        var image = renderer.RenderPage(0, new ImageOptions { Dpi = 150 });
        var rgba = image.Rgba;
        var gray = new byte[image.Width * image.Height];
        renderer.RenderPage(0, gray, ImagePixelFormat.Gray, new ImageOptions { Dpi = 150 });
        for (int i = 0; i < gray.Length; i++) Assert.Equal(rgba[i * 4], gray[i]);
        Assert.Contains(gray, g => g < 128);

        var colour = PdfRenderer.Open(Page);
        var (w, h) = colour.GetPixelSize(0, new ImageOptions { Dpi = 100 });
        var colourGray = new byte[w * h];
        colour.RenderPage(0, colourGray, ImagePixelFormat.Gray, new ImageOptions { Dpi = 100 });
        // The red box: Rec. 601 luma of #cc0033.
        Assert.Equal((0xCC * 299 + 0x33 * 114 + 500) / 1000, colourGray[w * 5 + 5]);
    }

    [Fact]
    public void A_transparent_page_is_flattened_onto_the_background_as_jpeg_is()
    {
        var renderer = PdfRenderer.Open(Page);
        var options = new ImageOptions { Dpi = 100, Transparent = true, BackgroundColor = 0xFF2040A0 };
        var (w, h) = renderer.GetPixelSize(0, options);
        var rgb = new byte[w * h * 3];
        renderer.RenderPage(0, rgb, ImagePixelFormat.Rgb, options);
        var gray = new byte[w * h];
        renderer.RenderPage(0, gray, ImagePixelFormat.Gray, options);
        // A corner the page leaves empty is the background; the red box is drawn over it.
        Assert.Equal(new byte[] { 0x20, 0x40, 0xA0 }, rgb[^3..]);
        Assert.Equal((0x20 * 299 + 0x40 * 587 + 0xA0 * 114 + 500) / 1000, gray[^1]);
        Assert.Equal(new byte[] { 0xCC, 0x00, 0x33 }, rgb.AsSpan((w * 5 + 5) * 3, 3).ToArray());
        // Translucent text over nothing comes out as the text over the background.
        var straight = renderer.RenderPage(0, options).Rgba;
        for (int i = 0; i < w * h; i++)
        {
            int a = straight[i * 4 + 3];
            if (a is 0 or 255) continue;
            int expected = Math.Min(255, (straight[i * 4] * a + 127) / 255 + (0x20 * (255 - a) + 127) / 255);
            Assert.InRange(rgb[i * 3], expected - 1, expected + 1);
        }
    }

    [Fact]
    public void Size_and_resolution_options_give_the_same_size_as_RenderPage()
    {
        var renderer = PdfRenderer.Open(Page);
        foreach (var options in new[]
                 {
                     new ImageOptions { Width = 333 }, new ImageOptions { Height = 77 }, new ImageOptions { Width = 50, Height = 50 },
                     new ImageOptions { Dpi = 600, MaxDimension = 1000 }, new ImageOptions { Dpi = 13.7f },
                 })
        {
            var image = renderer.RenderPage(0, options);
            Assert.Equal((image.Width, image.Height), renderer.GetPixelSize(0, options));
        }
    }

    [Fact]
    public void Wrong_arguments_fail_with_documented_exceptions()
    {
        var renderer = PdfRenderer.Open(Page);
        var (w, h) = renderer.GetPixelSize(0);
        Assert.Throws<ArgumentException>(() => renderer.RenderPage(0, new byte[w * h * 3 - 1], ImagePixelFormat.Rgb));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderPage(0, new byte[w * h * 4], (ImagePixelFormat)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderPage(1, new byte[w * h * 4], ImagePixelFormat.Rgba));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetPixelSize(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetPixelSize(0, new ImageOptions { Dpi = 0 }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.RenderPage(0, new byte[w * h], ImagePixelFormat.Gray, null, cancelled.Token));
    }
}
