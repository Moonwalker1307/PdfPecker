using PdfPecker.Images;
using PdfPecker.Rendering.Images;
using DecodedImage = PdfPecker.Images.DecodedImage;

namespace PdfPecker.Tests;

public class ImageOutputFormatTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static byte[] SamplePdf() => HtmlToPdf.Convert(
        "<h1 style='color:#c03'>Output formats</h1>" +
        "<div style='background:linear-gradient(90deg,#08f,#fc0);height:60pt'></div>" +
        "<p>The quick brown fox jumps over the lazy dog. 0123456789</p>" +
        "<div style='width:120pt;height:40pt;background:rgba(0,128,0,0.5)'></div>",
        new PdfOptions { PageSize = PageSize.FromMillimeters(105, 148) });

    private static void AssertExact(RenderedImage img, DecodedImage d)
    {
        Assert.Equal(img.Width, d.Width);
        Assert.Equal(img.Height, d.Height);
        var rgba = img.Rgba;
        for (int i = 0; i < img.Width * img.Height; i++)
        {
            byte a = d.Alpha?[i] ?? 255;
            if (d.Rgb[i * 3] != rgba[i * 4] || d.Rgb[i * 3 + 1] != rgba[i * 4 + 1] || d.Rgb[i * 3 + 2] != rgba[i * 4 + 2] || a != rgba[i * 4 + 3])
                Assert.Fail($"Pixel {i % img.Width},{i / img.Width} differs.");
        }
    }

    /// <summary>Peak signal-to-noise ratio of decoded RGB against the image flattened onto <paramref name="matte"/>.</summary>
    private static double Psnr(RenderedImage img, byte[] rgb, int matte = 255)
    {
        var rgba = img.Rgba;
        double se = 0;
        int n = img.Width * img.Height;
        for (int i = 0; i < n; i++)
        {
            int a = rgba[i * 4 + 3];
            for (int k = 0; k < 3; k++)
            {
                double e = (rgba[i * 4 + k] * a + matte * (255.0 - a)) / 255.0 - rgb[i * 3 + k];
                se += e * e;
            }
        }
        double mse = se / (n * 3.0);
        return mse == 0 ? 100 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    [Fact]
    public void Jpeg_round_trips_through_the_decoder()
    {
        var img = PdfRenderer.Open(SamplePdf()).RenderPage(0, new ImageOptions { Dpi = 100 });
        var jpeg = img.ToJpeg(90);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, jpeg[..4]);
        Assert.Equal("JFIF", System.Text.Encoding.ASCII.GetString(jpeg, 6, 4));
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
        var d = JpegDecoder.Decode(jpeg);
        Assert.NotNull(d);
        Assert.Equal(img.Width, d.Value.Width);
        Assert.Equal(img.Height, d.Value.Height);
        Assert.Equal(3, d.Value.Components);
        Assert.True(Psnr(img, d.Value.Data) > 35, $"PSNR {Psnr(img, d.Value.Data):F1} dB");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(85)]
    [InlineData(100)]
    public void Jpeg_quality_controls_size_and_fidelity(int quality)
    {
        // Odd sizes exercise partial MCUs in both the 4:2:0 and the 4:4:4 layouts.
        var img = PdfRenderer.Open(SamplePdf()).RenderPage(0, new ImageOptions { Width = 333, Height = 471 });
        var d = JpegDecoder.Decode(img.ToJpeg(quality))!.Value;
        Assert.Equal((img.Width, img.Height), (d.Width, d.Height));
        double psnr = Psnr(img, d.Data);
        Assert.True(psnr > (quality >= 85 ? 30 : 18), $"q{quality}: PSNR {psnr:F1} dB");
        if (quality < 100) Assert.True(img.ToJpeg(quality).Length < img.ToJpeg(100).Length);
    }

    [Fact]
    public void Jpeg_flattens_transparency_onto_the_background_colour()
    {
        var pdf = SamplePdf();
        var r = PdfRenderer.Open(pdf);
        var transparent = r.RenderPage(0, new ImageOptions { Dpi = 50, Transparent = true });
        Assert.Equal(0, transparent.Rgba[3]); // the corner is transparent
        var onWhite = JpegDecoder.Decode(transparent.ToJpeg(95))!.Value;
        Assert.True(onWhite.Data[0] > 245 && onWhite.Data[1] > 245 && onWhite.Data[2] > 245);
        Assert.True(Psnr(transparent, onWhite.Data) > 30);

        // Transparent, with a background colour set: that colour is the matte.
        var blue = r.RenderPage(0, new ImageOptions { Dpi = 50, Transparent = true, BackgroundColor = 0xFF0000FF });
        var onBlue = JpegDecoder.Decode(blue.ToJpeg(95))!.Value;
        Assert.True(onBlue.Data[0] < 10 && onBlue.Data[1] < 10 && onBlue.Data[2] > 245, $"{onBlue.Data[0]},{onBlue.Data[1]},{onBlue.Data[2]}");
    }

    [Fact]
    public void WebP_is_lossless_and_keeps_alpha()
    {
        var r = PdfRenderer.Open(SamplePdf());
        foreach (bool transparent in new[] { false, true })
        {
            var img = r.RenderPage(0, new ImageOptions { Dpi = 72, Transparent = transparent });
            foreach (int quality in new[] { 1, 85, 100 })
            {
                var webp = img.ToWebP(quality);
                Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(webp, 0, 4));
                Assert.Equal("WEBPVP8L", System.Text.Encoding.ASCII.GetString(webp, 8, 8));
                Assert.Equal(webp.Length - 8, BitConverter.ToInt32(webp, 4));
                Assert.Equal(transparent ? 0x10 : 0, webp[24] & 0x10); // alpha_is_used
                var d = WebPDecoder.Decode(webp, 1L << 32);
                Assert.NotNull(d);
                AssertExact(img, d.Value);
            }
            if (transparent) Assert.Contains(img.Rgba.Where((_, i) => i % 4 == 3), a => a is > 0 and < 255);
        }
    }

    [Fact]
    public void WebP_is_lossless_for_photographs_and_many_colours()
    {
        // More than 256 colours: the predictor path rather than the palette.
        var jpg = Convert.ToBase64String(Fixture("rgb.jpg"));
        var pdf = HtmlToPdf.Convert($"<img src='data:image/jpeg;base64,{jpg}' style='width:200pt;height:150pt'>", new PdfOptions { PageSize = PageSize.FromMillimeters(105, 148) });
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96, Transparent = true });
        var webp = img.ToWebP();
        AssertExact(img, WebPDecoder.Decode(webp, 1L << 32)!.Value);
        Assert.True(webp.Length < img.ToPng().Length * 1.1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(17, 5)]
    [InlineData(1, 40)]
    public void Tiny_and_odd_sizes_encode(int width, int height)
    {
        var img = PdfRenderer.Open(SamplePdf()).RenderPage(0, new ImageOptions { Width = width, Height = height, Transparent = true });
        Assert.True(img.Width <= width && img.Height <= height);
        AssertExact(img, WebPDecoder.Decode(img.ToWebP(), 1L << 32)!.Value);
        var j = JpegDecoder.Decode(img.ToJpeg())!.Value;
        Assert.Equal((img.Width, img.Height), (j.Width, j.Height));
    }

    [Fact]
    public void Format_option_selects_the_encoder()
    {
        var pdf = SamplePdf();
        var png = PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 30 });
        Assert.Equal(0x89, png[0]);
        var jpeg = PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 30, Format = PdfImageFormat.Jpeg, Quality = 70 });
        Assert.Equal(0xD8, jpeg[1]);
        var all = PdfToImage.Convert(pdf, new ImageOptions { Dpi = 30, Format = PdfImageFormat.WebP });
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(all[0], 0, 4));

        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 30, Format = PdfImageFormat.Jpeg, Quality = 70 });
        Assert.Equal(PdfImageFormat.Jpeg, img.Format);
        Assert.Equal(jpeg, img.Encode());
        Assert.Equal(PdfImageFormat.Png, new ImageOptions().Format);
        Assert.Equal(85, new ImageOptions().Quality);
    }

    [Theory]
    [InlineData(PdfImageFormat.Png, ".png")]
    [InlineData(PdfImageFormat.Jpeg, ".jpg")]
    [InlineData(PdfImageFormat.WebP, ".webp")]
    [InlineData(PdfImageFormat.Tiff, ".tif")]
    public void ConvertFile_uses_the_format_extension(PdfImageFormat format, string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfpecker-fmt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var pdfPath = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(pdfPath, HtmlToPdf.Convert("<p>one</p><p style='page-break-before:always'>two</p>"));
            var paths = PdfToImage.ConvertFile(pdfPath, Path.Combine(dir, "out"), new ImageOptions { Dpi = 20, Format = format });
            Assert.Equal(2, paths.Count);
            Assert.Equal(Path.Combine(dir, "out", "doc-2" + extension), paths[1]);
            var bytes = File.ReadAllBytes(paths[0]);
            Assert.Equal(extension, RenderedImage.GetFileExtension(format));
            switch (format)
            {
                case PdfImageFormat.Png: Assert.Equal(0x89, bytes[0]); break;
                case PdfImageFormat.Jpeg: Assert.NotNull(JpegDecoder.Decode(bytes)); break;
                case PdfImageFormat.Tiff: Assert.NotNull(TiffDecoder.Decode(bytes, 1L << 32)); break;
                default: Assert.NotNull(WebPDecoder.Decode(bytes, 1L << 32)); break;
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void Invalid_quality_throws(int quality)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageOptions { Quality = quality });
        var img = PdfRenderer.Open(SamplePdf()).RenderPage(0, new ImageOptions { Dpi = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => img.ToJpeg(quality));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.ToWebP(quality));
    }

    [Fact]
    public void WebP_rejects_images_beyond_its_size_limit()
    {
        Assert.Throws<NotSupportedException>(() => PdfPecker.Rendering.WebPEncoder.Encode(new uint[16385], 16385, 1, 85));
        var widest = PdfPecker.Rendering.WebPEncoder.Encode(new uint[16384], 16384, 1, 85);
        var d = WebPDecoder.Decode(widest, 1L << 32)!.Value;
        Assert.Equal((16384, 1), (d.Width, d.Height));
        Assert.All(d.Alpha!, a => Assert.Equal(0, a));
    }
}
