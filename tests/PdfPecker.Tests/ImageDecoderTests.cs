namespace PdfPecker.Tests;

public class ImageDecoderTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>
    /// The image as <see cref="ImageToPdf"/> embeds it, read back by the renderer's image decoder and put on white with
    /// the renderer's compositing (colour times alpha/256 rounded down, plus white's share rounded): what the decoder
    /// must give without the PDF.
    /// </summary>
    private static byte[] EmbeddedRgb(byte[] image, out int width, out int height)
    {
        var embedded = Images.PdfImage.FromBytes(image, asStored: true)!;
        var (rgb, w, h) = embedded.DecodeRgb()!.Value;
        (width, height) = (w, h);
        if (embedded.SMask?.DecodeRgb() is not { } mask) return rgb;
        var flat = new byte[w * h * 3];
        for (int i = 0; i < w * h * 3; i++)
        {
            int a = mask.Rgb[i / 3 * 3];
            flat[i] = (byte)((rgb[i] * (a + (a >> 7)) >> 8) + (255 * (255 - a) + 127) / 255);
        }
        return flat;
    }

    /// <summary>
    /// The page <see cref="ImageToPdf"/> makes of the image at 96 dpi, rendered there: the image again, give or take
    /// the rounding of drawing it at one pixel per image pixel.
    /// </summary>
    private static byte[] RenderedRgb(byte[] image, out int width, out int height)
    {
        var pdf = ImageToPdf.Convert(new[] { image }, new PdfOptions { Compress = false }, new ImageToPdfOptions { Dpi = 96 });
        var page = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96, MaxDimension = int.MaxValue });
        (width, height) = (page.Width, page.Height);
        var rgba = page.Rgba;
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            rgb[i * 3] = rgba[i * 4];
            rgb[i * 3 + 1] = rgba[i * 4 + 1];
            rgb[i * 3 + 2] = rgba[i * 4 + 2];
        }
        return rgb;
    }

    private static void AssertSameImage(byte[] image, string name)
    {
        var decoded = ImageDecoder.Decode(image, new ImageDecodeOptions { Format = ImagePixelFormat.Rgb, ApplyOrientation = false });
        var expected = EmbeddedRgb(image, out int w, out int h);
        Assert.Equal((w, h), (decoded.Width, decoded.Height));
        Assert.True(expected.AsSpan().SequenceEqual(decoded.Pixels), name);

        var rendered = RenderedRgb(image, out int rw, out int rh);
        if (rw != w || rh != h) return; // an image smaller than the least page sits on a larger one
        int worst = 0;
        for (int i = 0; i < rendered.Length; i++) worst = Math.Max(worst, Math.Abs(rendered[i] - decoded.Pixels[i]));
        Assert.True(worst <= 2, $"{name}: the rendered page differs by {worst} levels");
    }

    /// <summary>Grey, RGB, palette, 16-bit, 1-bit, translucent and interlaced PNGs, from their filtered rows.</summary>
    public static IEnumerable<object[]> GeneratedPngs()
    {
        static byte[] Rows(int w, int h, int bytesPerRow, Func<int, int, byte> value)
        {
            var raw = new byte[(bytesPerRow + 1) * h];
            for (int y = 0; y < h; y++)
                for (int i = 0; i < bytesPerRow; i++) raw[y * (bytesPerRow + 1) + 1 + i] = value(i, y);
            return raw;
        }
        const int W = 37, H = 23;
        yield return new object[] { "gray8", PdfTestHelpers.Png(W, H, 0, Rows(W, H, W, (x, y) => (byte)(x * 7 + y * 3))) };
        yield return new object[] { "rgb8", PdfTestHelpers.Png(W, H, 2, Rows(W, H, W * 3, (x, y) => (byte)(x * 11 + y * 5))) };
        yield return new object[] { "gray16", PdfTestHelpers.Png(W, H, 0, Rows(W, H, W * 2, (x, y) => (byte)(x * 13 + y)), 16) };
        yield return new object[] { "rgb16", PdfTestHelpers.Png(W, H, 2, Rows(W, H, W * 6, (x, y) => (byte)(x * 3 + y * 17)), 16) };
        yield return new object[] { "gray1", PdfTestHelpers.Png(W, H, 0, Rows(W, H, (W + 7) / 8, (x, y) => (byte)(x * 37 + y * 91)), 1) };
        yield return new object[] { "gray4", PdfTestHelpers.Png(W, H, 0, Rows(W, H, (W + 1) / 2, (x, y) => (byte)(x * 29 + y * 7)), 4) };
        yield return new object[] { "gray-alpha", PdfTestHelpers.Png(W, H, 4, Rows(W, H, W * 2, (x, y) => (byte)(x * 19 + y * 23))) };
        yield return new object[] { "rgba", PdfTestHelpers.Png(W, H, 6, Rows(W, H, W * 4, (x, y) => (byte)(x * 31 + y * 7))) };
        yield return new object[] { "rgba16", PdfTestHelpers.Png(W, H, 6, Rows(W, H, W * 8, (x, y) => (byte)(x * 5 + y * 41)), 16) };
        yield return new object[] { "alpha-ramp", PdfTestHelpers.AlphaPng(256, 16, (x, y) => (byte)x) };
        yield return new object[] { "solid-translucent", PdfTestHelpers.SolidPng(20, 20, 200, 100, 1, 128) };
    }

    [Theory]
    [MemberData(nameof(GeneratedPngs))]
    public void A_png_decodes_to_the_pixels_the_pdf_embeds(string name, byte[] png) => AssertSameImage(png, name);

    public static IEnumerable<object[]> FixtureImages()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "images");
        foreach (var path in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".gz", StringComparison.Ordinal)) continue;
            yield return new object[] { "images/" + name };
        }
        yield return new object[] { "rgb.jpg" };
        yield return new object[] { "cmyk.jpg" };
    }

    [Theory]
    [MemberData(nameof(FixtureImages))]
    public void Every_format_decodes_to_the_pixels_the_pdf_embeds(string fixture) => AssertSameImage(Fixture(fixture), fixture);

    [Fact]
    public void The_natural_format_keeps_grey_grey_and_transparency()
    {
        var gray = ImageDecoder.Decode(Fixture("images/gray-lzw.tif"));
        Assert.Equal(ImagePixelFormat.Gray, gray.Format);
        Assert.Equal(gray.Width * gray.Height, gray.Pixels.Length);
        Assert.Equal(gray.Width, gray.Stride);

        var rgb = ImageDecoder.Decode(Fixture("rgb.jpg"));
        Assert.Equal(ImagePixelFormat.Rgb, rgb.Format);
        Assert.Equal(rgb.Width * rgb.Height * 3, rgb.Pixels.Length);

        var rgba = ImageDecoder.Decode(PdfTestHelpers.RgbaPng());
        Assert.Equal(ImagePixelFormat.Rgba, rgba.Format);
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, rgba.Pixels[..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba.Pixels[4..8]);

        // An alpha channel that is opaque everywhere is no transparency.
        Assert.Equal(ImagePixelFormat.Rgb, ImageDecoder.Decode(PdfTestHelpers.SolidPng(3, 3, 1, 2, 3)).Format);
    }

    [Fact]
    public void Transparency_is_flattened_onto_the_background()
    {
        var png = PdfTestHelpers.SolidPng(2, 2, 255, 255, 255, 0);
        var rgb = ImageDecoder.Decode(png, new ImageDecodeOptions { Format = ImagePixelFormat.Rgb, BackgroundColor = 0xFF102030 });
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, rgb.Pixels[..3]);
        var gray = ImageDecoder.Decode(png, new ImageDecodeOptions { Format = ImagePixelFormat.Gray, BackgroundColor = 0xFF808080 });
        Assert.All(gray.Pixels, p => Assert.Equal(0x80, p));
        var rgba = ImageDecoder.Decode(PdfTestHelpers.RgbPng(), new ImageDecodeOptions { Format = ImagePixelFormat.Rgba });
        Assert.Equal(new byte[] { 0, 128, 255, 255 }, rgba.Pixels[..4]);
    }

    [Fact]
    public void Colour_becomes_luma_and_grey_stays_exact()
    {
        var png = PdfTestHelpers.PixelPng(3, 1, (x, _) => x switch { 0 => (255, 0, 0), 1 => (0, 255, 0), _ => (77, 77, 77) });
        var gray = ImageDecoder.Decode(png, new ImageDecodeOptions { Format = ImagePixelFormat.Gray });
        Assert.Equal(new byte[] { 76, 150, 77 }, gray.Pixels);
    }

    [Fact]
    public void A_photo_is_turned_as_its_exif_orientation_says_unless_asked_not_to()
    {
        var jpeg = Fixture("images/exif.jpg");
        var stored = ImageDecoder.Decode(jpeg, new ImageDecodeOptions { ApplyOrientation = false });
        var upright = ImageDecoder.Decode(jpeg);
        var embedded = Images.PdfImage.FromBytes(jpeg)!;
        Assert.Equal((embedded.Width, embedded.Height), (upright.Width, upright.Height));
        int orientation = Images.ExifReader.ReadOrientation(jpeg);
        Assert.InRange(orientation, 2, 8);
        Assert.Equal(orientation >= 5 ? (stored.Height, stored.Width) : (stored.Width, stored.Height), (upright.Width, upright.Height));
        // Lossless: every stored pixel is somewhere in the turned image, the same number of times.
        int bpp = stored.Stride / stored.Width;
        Assert.Equal(Histogram(stored.Pixels, bpp), Histogram(upright.Pixels, bpp));
    }

    private static Dictionary<int, int> Histogram(byte[] pixels, int bpp)
    {
        var counts = new Dictionary<int, int>();
        for (int i = 0; i + bpp <= pixels.Length; i += bpp)
        {
            int key = 0;
            for (int c = 0; c < bpp; c++) key = key << 8 | pixels[i + c];
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    [Fact]
    public void Every_page_of_a_multi_page_tiff_is_read()
    {
        var pages = new[] { PdfTestHelpers.SolidPng(30, 20, 255, 0, 0), PdfTestHelpers.SolidPng(10, 40, 0, 0, 255) };
        var pdf = ImageToPdf.Convert(pages);
        var tiff = PdfToImage.ConvertToTiff(pdf, new ImageOptions { Dpi = 72 });
        Assert.Equal(2, ImageDecoder.GetFrameCount(tiff));
        var first = ImageDecoder.Decode(tiff);
        var second = ImageDecoder.Decode(tiff, new ImageDecodeOptions { Frame = 1 });
        Assert.Equal(new byte[] { 255, 0, 0 }, first.Pixels[..3]);
        Assert.Equal(new byte[] { 0, 0, 255 }, second.Pixels[..3]);
        Assert.NotEqual((first.Width, first.Height), (second.Width, second.Height));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageDecoder.Decode(tiff, new ImageDecodeOptions { Frame = 2 }));
        Assert.Equal(1, ImageDecoder.GetFrameCount(Fixture("rgb.jpg")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageDecoder.Decode(Fixture("rgb.jpg"), new ImageDecodeOptions { Frame = 1 }));
    }

    [Fact]
    public void Bad_input_fails_with_documented_exceptions()
    {
        Assert.Throws<ArgumentNullException>(() => ImageDecoder.Decode((byte[])null!));
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<InvalidDataException>(() => ImageDecoder.GetFrameCount("GIF89a"u8.ToArray()[..3]));
        var png = PdfTestHelpers.RgbPng(8);
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(png[..(png.Length / 2)]));
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(png, new ImageDecodeOptions { MaxPixels = 63 }));
        Assert.Equal(64 * 3, ImageDecoder.Decode(png, new ImageDecodeOptions { MaxPixels = 64 }).Pixels.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageDecodeOptions { MaxPixels = ImageDecodeOptions.MaxPixelLimit + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageDecodeOptions { Frame = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageDecoder.Decode(png, new ImageDecodeOptions { Format = (ImagePixelFormat)7 }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ImageDecoder.Decode(png, null, cancelled.Token));
    }
}

/// <summary>Image files that are damaged or claim more than they hold.</summary>
[Collection(HostileInputCollection.Name)]
public class ImageDecoderHostileInputTests
{
    [Fact]
    public void A_header_that_claims_more_pixels_than_allowed_fails_before_inflating()
    {
        // 50,000 by 50,000 pixels of nothing: over the limit, so the data is never inflated.
        var raw = new byte[1024];
        var png = PdfTestHelpers.Png(50_000, 50_000, 0, raw);
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(png));
        Assert.Equal(1, ImageDecoder.GetFrameCount(png));
    }

    [Fact]
    public void Damaged_zlib_data_in_an_rgb_png_is_drawn_as_far_as_it_goes()
    {
        // Found by the fuzzer: a 20 x 10 RGB PNG whose IDAT is not valid zlib let ZLibException escape the row-by-row
        // inflation. It goes to the renderer's decoder instead, as the page ImageToPdf makes of it would.
        byte[] png = Convert.FromHexString(
            "89504E470D0A1A0A0000000D49484452000000140000000A08020000003B37E9B10000001B49444154782055"
            + "91AC752B62DCC03184C78F203D761849B2A4C99324" + "45A85C");
        var decoded = ImageDecoder.Decode(png);
        Assert.Equal((20, 10, ImagePixelFormat.Rgb), (decoded.Width, decoded.Height, decoded.Format));
    }
}
