using PdfPecker.Images;

namespace PdfPecker.Tests;

public class ImageToPdfTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static (int Left, int Top, int Right, int Bottom) InkBounds(byte[] pdf, int page)
    {
        var image = PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });
        var rgba = image.Rgba;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                if (rgba[i] + rgba[i + 1] + rgba[i + 2] < 600)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }
        return (left, top, right, bottom);
    }

    [Fact]
    public void Each_image_becomes_a_page_of_its_own_size()
    {
        var images = new[] { Fixture("rgb.jpg"), Fixture("images/lossy.webp"), Fixture("images/rgb24.bmp"), Fixture("images/pal-lzw.tif") };
        var pdf = ImageToPdf.Convert(images);
        var renderer = PdfRenderer.Open(pdf);
        Assert.Equal(4, renderer.PageCount);
        for (var i = 0; i < images.Length; i++)
        {
            var decoded = PdfImage.FromBytes(images[i])!;
            // One image pixel is one CSS pixel, three quarters of a point.
            Assert.Equal((decoded.Width * 0.75, decoded.Height * 0.75), renderer.GetPageSize(i));
        }
    }

    [Fact]
    public void Dpi_sets_the_printed_size()
    {
        var jpeg = Fixture("rgb.jpg");
        var size = PdfImage.FromBytes(jpeg)!;
        var pdf = ImageToPdf.Convert(new[] { jpeg }, null, new ImageToPdfOptions { Dpi = 300 });
        var (width, height) = PdfRenderer.Open(pdf).GetPageSize(0);
        Assert.Equal(size.Width * 72 / 300.0, width, 2);
        Assert.Equal(size.Height * 72 / 300.0, height, 2);
    }

    [Fact]
    public void Images_on_a_fixed_page_are_scaled_down_and_centred()
    {
        var wide = PdfTestHelpers.SolidPng(1200, 400, 0, 0, 160);
        var small = PdfTestHelpers.SolidPng(100, 50, 160, 0, 0);
        var pdf = ImageToPdf.Convert(new[] { wide, small }, new PdfOptions { CreationDate = DateTimeOffset.UnixEpoch }, new ImageToPdfOptions { FitPageToImage = false });
        Assert.Equal(2, PdfToImage.GetPageCount(pdf));

        // A4 at 72 dpi is 595 x 842 pixels with 36 points of margin: the wide image fills the 523-point content width.
        var (left, top, right, bottom) = InkBounds(pdf, 0);
        Assert.InRange(left, 35, 37);
        Assert.InRange(right, 557, 559);
        Assert.InRange((top + bottom) / 2, 419, 422);
        Assert.InRange(bottom - top, 172, 176);

        // The small image keeps its 75 x 37.5 points.
        (left, top, right, bottom) = InkBounds(pdf, 1);
        Assert.InRange(right - left, 73, 76);
        Assert.InRange((left + right) / 2, 296, 299);
    }

    [Fact]
    public void Bad_input_fails_with_documented_exceptions()
    {
        Assert.Throws<ArgumentException>(() => ImageToPdf.Convert(Array.Empty<byte[]>()));
        Assert.Throws<ArgumentNullException>(() => ImageToPdf.Convert(new byte[][] { null! }));
        var ex = Assert.Throws<InvalidDataException>(() => ImageToPdf.Convert(new[] { Fixture("rgb.jpg"), "not an image"u8.ToArray() }));
        Assert.Contains("Image 2", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ImageToPdf.Convert(new[] { Fixture("rgb.jpg") }, null, new ImageToPdfOptions { Dpi = 0 }));
        Assert.Throws<ArgumentException>(() => ImageToPdf.Convert(Enumerable.Repeat(Fixture("rgb.jpg"), ImageToPdf.MaxImages + 1)));
        Assert.Throws<PdfLimitException>(() => ImageToPdf.Convert(new[] { Fixture("rgb.jpg"), Fixture("rgb.jpg") }, new PdfOptions { MaxPages = 1 }));
    }

    [Fact]
    public void Output_settings_still_apply_and_files_are_written()
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var options = new PdfOptions { Title = "Receipts", CreationDate = created, IgnoreCssPageRules = true };
        var images = new[] { Fixture("rgb.jpg"), Fixture("rgb.jpg") };
        var pdf = ImageToPdf.Convert(images, options);
        Assert.Equal(2, PdfToImage.GetPageCount(pdf));
        Assert.Contains("(Receipts)", PdfTestHelpers.Latin1(pdf), StringComparison.Ordinal);
        Assert.Equal(pdf, ImageToPdf.Convert(images, options));
        Assert.True(options.IgnoreCssPageRules);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = Path.Combine(dir.FullName, "a.jpg");
            File.WriteAllBytes(input, images[0]);
            var output = Path.Combine(dir.FullName, "out.pdf");
            ImageToPdf.ConvertFiles(new[] { input, input }, output, options);
            Assert.Equal(pdf, File.ReadAllBytes(output));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Html_converts_to_one_image_per_page()
    {
        const string Html = "<p>One</p><p style=\"break-before:page\">Two</p>";
        var expected = PdfToImage.Convert(HtmlToPdf.Convert(Html), new ImageOptions { Format = PdfImageFormat.Jpeg });
        var images = HtmlToImage.Convert(Html, null, new ImageOptions { Format = PdfImageFormat.Jpeg });
        Assert.Equal(expected, images);

        // A password or signature for the intermediate PDF does not get in the way.
        var secured = new PdfOptions { Security = new PdfSecurity { UserPassword = "secret" } };
        Assert.Equal(2, HtmlToImage.Convert(Html, secured).Length);
        Assert.NotNull(secured.Security);
    }

    [Fact]
    public async Task Html_to_image_async_and_cancellation()
    {
        Assert.Equal(2, (await HtmlToImage.ConvertAsync("<p>One</p><p style=\"break-before:page\">Two</p>")).Length);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => HtmlToImage.Convert("<p>x</p>", null, null, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ImageToPdf.Convert(new[] { Fixture("rgb.jpg") }, null, null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlToImage.ConvertAsync("<p>x</p>", cancellationToken: cts.Token));
    }
}
