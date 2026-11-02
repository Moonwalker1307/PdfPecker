using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfPecker.Tests;

/// <summary>
/// image-rendering: pixelated and crisp-edges write an enlarged image with each pixel repeated as a block, so that it
/// keeps hard edges in every PDF viewer; auto, smooth and high-quality leave the image as it is.
/// </summary>
public class ImageRenderingTests
{
    // A 4x4 checkerboard of black and white pixels, the shape of a QR code's modules.
    private static readonly string Checker = PdfTestHelpers.DataUri(PdfTestHelpers.PixelPng(4, 4, (x, y) => (x + y) % 2 == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255)));

    private static PdfExtractedImage OnlyImage(string body)
        => Assert.Single(PdfImages.Extract(HtmlToPdf.Convert(body)), i => !i.IsMask);

    [Theory]
    [InlineData("pixelated")]
    [InlineData("crisp-edges")]
    [InlineData("-webkit-optimize-contrast")]
    public void An_enlarged_image_is_written_with_its_pixels_repeated(string value)
    {
        var image = OnlyImage($"<img src='{Checker}' style='width:200px;height:200px;image-rendering:{value}'>");
        // 200px is 150pt, 37.5pt a pixel: 32 image pixels per pixel, the most that is written.
        Assert.Equal(4 * 32, image.Width);
        Assert.Equal(4 * 32, image.Height);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("smooth")]
    [InlineData("high-quality")]
    public void Other_values_leave_the_image_as_it_is(string value)
    {
        var image = OnlyImage($"<img src='{Checker}' style='width:200px;height:200px;image-rendering:{value}'>");
        Assert.Equal(4, image.Width);
    }

    [Fact]
    public void An_image_at_its_natural_size_or_smaller_is_left_as_it_is()
    {
        Assert.Equal(4, OnlyImage($"<img src='{Checker}' style='image-rendering:pixelated'>").Width);
        Assert.Equal(4, OnlyImage($"<img src='{Checker}' style='width:2px;image-rendering:pixelated'>").Width);
    }

    [Fact]
    public void The_factor_follows_the_drawn_size()
    {
        // 16px is 12pt, 3pt a pixel: 12 image pixels per pixel.
        Assert.Equal(48, OnlyImage($"<img src='{Checker}' style='width:16px;image-rendering:pixelated'>").Width);
    }

    [Fact]
    public void The_pixels_keep_their_colours_and_edges()
    {
        var image = OnlyImage($"<img src='{Checker}' style='width:16px;image-rendering:pixelated'>");
        var pixels = PngPixels(image.Data, out int width);
        Assert.Equal(48, width);
        // Pixel (0, 0) is black and (1, 0) white, each now 12 image pixels wide with nothing in between.
        Assert.Equal(0, pixels[(5 * width + 11) * 3]);
        Assert.Equal(255, pixels[(5 * width + 12) * 3]);
        Assert.Equal(0, pixels[(12 * width + 12) * 3]);
    }

    [Fact]
    public void The_property_is_inherited_and_applies_to_inline_images_and_backgrounds()
    {
        Assert.Equal(48, OnlyImage($"<p style='image-rendering:pixelated'>Code <img src='{Checker}' style='width:16px'></p>").Width);
        var background = OnlyImage($"<div style='width:16px;height:16px;background:url({Checker}) 0 0/16px 16px;image-rendering:pixelated'></div>");
        Assert.Equal(48, background.Width);
    }

    [Fact]
    public void Transparency_is_kept()
    {
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.RgbaPng());
        var images = PdfImages.Extract(HtmlToPdf.Convert($"<img src='{png}' style='width:16px;image-rendering:pixelated'>"));
        var image = Assert.Single(images);
        Assert.Equal(48, image.Width);
        var data = PngPixels(image.Data, out _, rgba: true);
        Assert.Equal(0, data[3]);
        Assert.Equal(0, data[(11 * 48 + 11) * 4 + 3]);
        Assert.Equal(255, data[(12 * 48 + 12) * 4 + 3]);
    }

    [Fact]
    public void A_one_bit_image_is_enlarged_too()
    {
        // A 1-bit greyscale PNG, as QR code generators write them: the rows 0101 and 1010.
        var raw = new byte[] { 0, 0b0101_0000, 0, 0b1010_0000 };
        var image = OnlyImage($"<img src='{PdfTestHelpers.DataUri(PdfTestHelpers.Png(4, 2, 0, raw, bitDepth: 1))}' style='width:16px;image-rendering:pixelated'>");
        Assert.Equal(48, image.Width);
        Assert.Equal(24, image.Height);
        var pixels = PngPixels(image.Data, out _);
        Assert.Equal(0, pixels[0]);
        Assert.Equal(255, pixels[12 * 3]);
    }

    [Fact]
    public void Supports_knows_image_rendering()
    {
        Assert.True(Css.CssSupports.Evaluate("(image-rendering: pixelated)"));
        Assert.True(Css.CssSupports.Evaluate("(image-rendering: crisp-edges)"));
        Assert.False(Css.CssSupports.Evaluate("(image-rendering: blocky)"));
    }

    [Fact]
    public void An_invalid_value_is_ignored()
    {
        var image = OnlyImage($"<img src='{Checker}' style='width:16px;image-rendering:pixelated;image-rendering:blocky'>");
        Assert.Equal(48, image.Width);
    }

    /// <summary>The pixels of a PNG that <see cref="PdfImages"/> wrote: 8-bit RGB, or RGBA with <paramref name="rgba"/>.</summary>
    private static byte[] PngPixels(byte[] png, out int width, bool rgba = false)
    {
        width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        int colors = png[25] switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        Assert.Equal(8, png[24]);
        var idat = new MemoryStream();
        for (int at = 8; at < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) idat.Write(png, at + 8, length);
            at += 12 + length;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var filtered = new MemoryStream();
        z.CopyTo(filtered);
        var rows = filtered.ToArray();
        int stride = width * colors;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = rows[y * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= colors ? pixels[y * stride + i - colors] : 0, b = y > 0 ? pixels[(y - 1) * stride + i] : 0;
                int c = i >= colors && y > 0 ? pixels[(y - 1) * stride + i - colors] : 0;
                int predicted = filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                pixels[y * stride + i] = (byte)(rows[y * (stride + 1) + 1 + i] + predicted);
            }
        }
        int outColors = rgba ? 4 : 3;
        var result = new byte[width * height * outColors];
        for (int p = 0; p < width * height; p++)
        {
            for (int k = 0; k < 3; k++) result[p * outColors + k] = pixels[p * colors + (colors >= 3 ? k : 0)];
            if (rgba) result[p * 4 + 3] = colors is 2 or 4 ? pixels[p * colors + colors - 1] : (byte)255;
        }
        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
