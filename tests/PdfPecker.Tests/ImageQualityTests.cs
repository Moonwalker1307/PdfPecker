namespace PdfPecker.Tests;

/// <summary>
/// -pdfpecker-image-quality writes images as JPEGs at a quality, and -pdfpecker-image-max-resolution downsamples images
/// drawn denser than a resolution; both are inherited, and an image is only replaced when the result is smaller.
/// </summary>
public class ImageQualityTests
{
    // A 400x400 photo-like image: smooth gradients with fine noise, which PNG compresses badly and JPEG well.
    private static readonly byte[] PhotoPng = PdfTestHelpers.PixelPng(400, 400, (x, y) =>
    {
        int noise = (x * 7919 + y * 104729) % 23;
        return ((byte)(x * 255 / 400), (byte)((y * 255 / 400 + noise) % 256), (byte)((x + y + noise) % 256));
    });

    private static readonly string Photo = PdfTestHelpers.DataUri(PhotoPng);

    private static PdfExtractedImage OnlyImage(string body)
        => Assert.Single(PdfImages.Extract(HtmlToPdf.Convert(body)), i => !i.IsMask);

    [Fact]
    public void Without_the_properties_the_image_keeps_its_pixels()
    {
        var image = OnlyImage($"<img src='{Photo}' style='width:100px'>");
        Assert.Equal(PdfImageFormat.Png, image.Format);
        Assert.Equal(400, image.Width);
    }

    [Fact]
    public void A_quality_writes_the_image_as_a_jpeg()
    {
        var image = OnlyImage($"<img src='{Photo}' style='width:100px;-pdfpecker-image-quality:50'>");
        Assert.Equal(PdfImageFormat.Jpeg, image.Format);
        Assert.Equal(400, image.Width);
        Assert.True(HtmlToPdf.Convert($"<img src='{Photo}' style='width:100px;-pdfpecker-image-quality:50'>").Length
            < HtmlToPdf.Convert($"<img src='{Photo}' style='width:100px'>").Length);
    }

    [Fact]
    public void A_lower_quality_makes_a_smaller_file()
    {
        int high = OnlyImage($"<img src='{Photo}' style='width:100px;-pdfpecker-image-quality:90'>").Data.Length;
        int low = OnlyImage($"<img src='{Photo}' style='width:100px;-pdfpecker-image-quality:10'>").Data.Length;
        Assert.True(low < high, $"{low} < {high}");
    }

    [Theory]
    [InlineData("144dpi")]
    [InlineData("1.5dppx")]
    [InlineData("1.5x")]
    public void A_max_resolution_downsamples_an_image_drawn_denser(string resolution)
    {
        // 100px is 75pt, about one inch: 144 dpi keeps 150 of the 400 pixels across.
        var image = OnlyImage($"<img src='{Photo}' style='width:100px;-pdfpecker-image-max-resolution:{resolution}'>");
        Assert.Equal(150, image.Width);
        Assert.Equal(150, image.Height);
        Assert.Equal(PdfImageFormat.Png, image.Format);
    }

    [Fact]
    public void An_image_drawn_less_dense_than_the_resolution_keeps_its_pixels()
    {
        Assert.Equal(400, OnlyImage($"<img src='{Photo}' style='width:400px;-pdfpecker-image-max-resolution:300dpi'>").Width);
    }

    [Fact]
    public void Both_together_downsample_and_write_a_jpeg()
    {
        var image = OnlyImage($"<img src='{Photo}' style='width:100px;-pdfpecker-image-max-resolution:72dpi;-pdfpecker-image-quality:75'>");
        Assert.Equal(PdfImageFormat.Jpeg, image.Format);
        Assert.Equal(75, image.Width);
    }

    [Fact]
    public void A_downsampled_jpeg_stays_a_jpeg()
    {
        var jpeg = PdfImages.Extract(HtmlToPdf.Convert($"<img src='{Photo}' style='-pdfpecker-image-quality:90'>")).Single().Data;
        var image = OnlyImage($"<img src='data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}' style='width:100px;-pdfpecker-image-max-resolution:72dpi'>");
        Assert.Equal(PdfImageFormat.Jpeg, image.Format);
        Assert.Equal(75, image.Width);
    }

    [Fact]
    public void A_flat_image_whose_jpeg_would_be_larger_is_kept()
    {
        var logo = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(64, 64, 200, 30, 30));
        Assert.Equal(PdfImageFormat.Png, OnlyImage($"<img src='{logo}' style='-pdfpecker-image-quality:90'>").Format);
    }

    [Fact]
    public void The_properties_are_inherited_and_none_turns_them_off()
    {
        Assert.Equal(PdfImageFormat.Jpeg, OnlyImage($"<div style='-pdfpecker-image-quality:50'><p><img src='{Photo}' style='width:100px'></p></div>").Format);
        var off = OnlyImage($"<div style='-pdfpecker-image-quality:50;-pdfpecker-image-max-resolution:72dpi'><img src='{Photo}' style='width:100px;-pdfpecker-image-quality:none;-pdfpecker-image-max-resolution:none'></div>");
        Assert.Equal(PdfImageFormat.Png, off.Format);
        Assert.Equal(400, off.Width);
    }

    [Fact]
    public void Transparency_is_kept_as_a_lossless_mask()
    {
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.AlphaPng(400, 400, (x, y) => (byte)(x < 200 ? 0 : 255)));
        var images = PdfImages.Extract(HtmlToPdf.Convert($"<img src='{png}' style='width:100px;-pdfpecker-image-max-resolution:72dpi'>"));
        var image = Assert.Single(images);
        Assert.Equal(75, image.Width);
        Assert.Equal(PdfImageFormat.Png, image.Format);
    }

    [Fact]
    public void Background_images_follow_the_properties_too()
    {
        var image = OnlyImage($"<div style='width:100px;height:100px;background:url({Photo}) 0 0/100px 100px;-pdfpecker-image-max-resolution:72dpi'></div>");
        Assert.Equal(75, image.Width);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        var html = $"<img src='{Photo}' style='width:100px;-pdfpecker-image-quality:60;-pdfpecker-image-max-resolution:150dpi'>";
        var options = new PdfOptions { CreationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        Assert.Equal(HtmlToPdf.Convert(html, options), HtmlToPdf.Convert(html, options));
    }

    [Theory]
    [InlineData("-pdfpecker-image-quality: 80", true)]
    [InlineData("-pdfpecker-image-quality: none", true)]
    [InlineData("-pdfpecker-image-quality: 0", false)]
    [InlineData("-pdfpecker-image-quality: 101", false)]
    [InlineData("-pdfpecker-image-quality: 50.5", false)]
    [InlineData("-pdfpecker-image-max-resolution: 150dpi", true)]
    [InlineData("-pdfpecker-image-max-resolution: none", true)]
    [InlineData("-pdfpecker-image-max-resolution: 150", false)]
    [InlineData("-pdfpecker-image-max-resolution: -2dpi", false)]
    public void Supports_knows_the_properties(string declaration, bool supported)
        => Assert.Equal(supported, Css.CssSupports.Evaluate($"({declaration})"));

    [Fact]
    public void An_invalid_value_is_ignored()
    {
        var image = OnlyImage($"<img src='{Photo}' style='width:100px;-pdfpecker-image-quality:40;-pdfpecker-image-quality:500'>");
        Assert.Equal(PdfImageFormat.Jpeg, image.Format);
    }
}
