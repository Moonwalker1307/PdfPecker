using System.IO.Compression;
using System.Text;
using PdfPecker.Images;

namespace PdfPecker.Tests;

/// <summary>
/// BMP and TIFF images. The fixtures in Fixtures/images were written by Pillow and ImageMagick from one 37 × 23 test
/// image; each *.rgba.gz holds the pixels as Pillow decodes them: width and height as 32-bit little-endian numbers,
/// then RGBA bytes.
/// </summary>
public class BmpTiffTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", name));

    private static (int W, int H, byte[] Rgba) Reference(string name)
    {
        using var gz = new GZipStream(new MemoryStream(Fixture(name + ".rgba.gz")), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var b = ms.ToArray();
        return (BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4), b.AsSpan(8).ToArray());
    }

    private static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The RGBA pixels of a decoded image, read back from its PDF image data.</summary>
    private static byte[] Rgba(PdfImage img)
    {
        Assert.Equal("/FlateDecode", img.Filter);
        var data = Inflate(img.Data);
        var alpha = img.SMask is { } mask ? Inflate(mask.Data) : null;
        int w = img.Width, h = img.Height;
        var rgba = new byte[w * h * 4];
        int rowBytes = (w * (img.ColorSpace == "/DeviceRGB" ? 3 : 1) * img.BitsPerComponent + 7) / 8;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (img.ColorSpace == "/DeviceRGB")
                {
                    Assert.Equal(8, img.BitsPerComponent);
                    Array.Copy(data, y * rowBytes + x * 3, rgba, o, 3);
                }
                else
                {
                    Assert.Equal("/DeviceGray", img.ColorSpace);
                    int v = img.BitsPerComponent == 1 ? ((data[y * rowBytes + x / 8] >> (7 - x % 8)) & 1) * 255 : data[y * rowBytes + x];
                    if (img.Decode == "[1 0]") v = 255 - v;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)v;
                }
                rgba[o + 3] = alpha != null ? alpha[y * w + x] : (byte)255;
            }
        return rgba;
    }

    [Theory]
    [InlineData("mono.bmp", 0)]
    [InlineData("pal4.bmp", 0)]
    [InlineData("pal8.bmp", 0)]
    [InlineData("rle8.bmp", 0)]
    [InlineData("rgb24.bmp", 0)]
    [InlineData("core24.bmp", 0)]
    [InlineData("rgb565.bmp", 4)]
    [InlineData("argb32.bmp", 0)]
    [InlineData("rgb-raw.tif", 0)]
    [InlineData("rgb-lzw.tif", 0)]
    [InlineData("rgb-packbits.tif", 0)]
    [InlineData("rgba-deflate.tif", 0)]
    [InlineData("rgb-planar.tif", 0)]
    [InlineData("rgb-tiled-predictor.tif", 0)]
    [InlineData("rgb16-predictor.tif", 1)]
    [InlineData("rgb-bigendian.tif", 0)]
    [InlineData("rgb-jpeg.tif", 6)]
    [InlineData("gray-lzw.tif", 0)]
    [InlineData("gray4.tif", 0)]
    [InlineData("pal-lzw.tif", 0)]
    [InlineData("mono-raw.tif", 0)]
    [InlineData("mono-g3.tif", 0)]
    [InlineData("mono-g4.tif", 0)]
    [InlineData("fax-g4.tif", 0)]
    [InlineData("fax-white-is-zero-g3.tif", 0)]
    public void Decodes_the_same_pixels_as_the_reference_decoder(string name, int tolerance)
    {
        var (w, h, expected) = Reference(name);
        var img = PdfImage.FromBytes(Fixture(name));
        Assert.NotNull(img);
        Assert.Equal(w, img!.Width);
        Assert.Equal(h, img.Height);
        var actual = Rgba(img);
        int worst = 0, at = -1;
        for (int i = 0; i < actual.Length; i++)
        {
            // Colour under full transparency does not matter.
            if (expected[i | 3] == 0 && actual[i | 3] == 0) continue;
            int diff = Math.Abs(actual[i] - expected[i]);
            if (diff > worst) { worst = diff; at = i; }
        }
        Assert.True(worst <= tolerance, $"{name}: byte {at} (pixel {at / 4}) differs by {worst}");
    }

    [Fact]
    public void Bilevel_tiffs_stay_one_bit_per_pixel()
    {
        foreach (var name in new[] { "mono-g4.tif", "fax-g4.tif", "mono-raw.tif" })
            Assert.Equal(1, PdfImage.FromBytes(Fixture(name))!.BitsPerComponent);
    }

    [Fact]
    public void A_cmyk_tiff_becomes_rgb()
    {
        var img = PdfImage.FromBytes(Fixture("cmyk.tif"));
        Assert.NotNull(img);
        Assert.Equal("/DeviceRGB", img!.ColorSpace);
        var (_, _, reference) = Reference("cmyk.tif");
        var actual = Rgba(img);
        // A naive conversion in the reference, a nicer one here: only roughly the same.
        double sum = 0;
        for (int i = 0; i < actual.Length; i++) sum += Math.Abs(actual[i] - reference[i]);
        Assert.True(sum / actual.Length < 40, $"Mean difference {sum / actual.Length}");
    }

    [Fact]
    public void Rle4_bitmaps_decode_and_skipped_pixels_are_transparent()
    {
        // 4 × 2, bottom-up: the bottom row is red, blue, red, blue; the top row starts with a delta past two pixels,
        // then two blue pixels.
        var bmp = new List<byte>();
        bmp.AddRange("BM"u8.ToArray());
        bmp.AddRange(new byte[12]);
        var header = new byte[40];
        BitConverter.GetBytes(40).CopyTo(header, 0);
        BitConverter.GetBytes(4).CopyTo(header, 4);
        BitConverter.GetBytes(2).CopyTo(header, 8);
        BitConverter.GetBytes((short)1).CopyTo(header, 12);
        BitConverter.GetBytes((short)4).CopyTo(header, 14);
        BitConverter.GetBytes(2).CopyTo(header, 16); // RLE4
        BitConverter.GetBytes(2).CopyTo(header, 32); // colours used
        bmp.AddRange(header);
        bmp.AddRange(new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 }); // red, blue (BGRx)
        int dataOffset = bmp.Count;
        bmp.AddRange(new byte[] { 4, 0x01, 0, 0, 0, 2, 2, 0, 2, 0x11, 0, 1 });
        var d = bmp.ToArray();
        BitConverter.GetBytes(d.Length).CopyTo(d, 2);
        BitConverter.GetBytes(dataOffset).CopyTo(d, 10);

        var img = BmpDecoder.Decode(d, 1000);
        Assert.NotNull(img);
        var (w, _, rgb, alpha) = img!.Value;
        Assert.Equal(4, w);
        Assert.NotNull(alpha);
        // Top row (y = 0): transparent, transparent, blue, blue.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, alpha!.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255 }, rgb.AsSpan(2 * 3, 3).ToArray());
        // Bottom row: red, blue, red, blue.
        Assert.Equal(new byte[] { 255, 0, 0 }, rgb.AsSpan(4 * 3, 3).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255 }, rgb.AsSpan(5 * 3, 3).ToArray());
    }

    [Fact]
    public void Plain_32_bit_bitmaps_with_zero_alpha_bytes_are_opaque()
    {
        // A 40-byte header and 32-bit pixels whose fourth byte is 0: most writers mean opaque.
        var d = new byte[54 + 4];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BitConverter.GetBytes(d.Length).CopyTo(d, 2);
        BitConverter.GetBytes(54).CopyTo(d, 10);
        BitConverter.GetBytes(40).CopyTo(d, 14);
        BitConverter.GetBytes(1).CopyTo(d, 18);
        BitConverter.GetBytes(1).CopyTo(d, 22);
        BitConverter.GetBytes((short)1).CopyTo(d, 26);
        BitConverter.GetBytes((short)32).CopyTo(d, 28);
        d[54] = 255; // blue
        var img = PdfImage.FromBytes(d);
        Assert.NotNull(img);
        Assert.Null(img!.SMask);
    }

    [Fact]
    public void Bmp_and_tiff_images_work_in_html()
    {
        string Uri(string mime, string name) => $"data:{mime};base64,{Convert.ToBase64String(Fixture(name))}";
        var pdf = HtmlToPdf.Convert($"<img src='{Uri("image/bmp", "rgb24.bmp")}'><img src='{Uri("image/tiff", "fax-g4.tif")}'>",
            new PdfOptions { Compress = false });
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Width 37 /Height 23", text);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "/Subtype /Image /Width 37").Count);
    }

    [Theory]
    [InlineData("BM")]
    [InlineData("II*\0")]
    [InlineData("MM\0*")]
    public void Damaged_files_do_not_throw(string magic)
    {
        var rng = new Random(7);
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "images");
        foreach (var name in Directory.GetFiles(folder, "*.bmp").Concat(Directory.GetFiles(folder, "*.tif")).Select(Path.GetFileName))
        {
            var good = Fixture(name!);
            if (!Encoding.Latin1.GetString(good, 0, magic.Length).Equals(magic, StringComparison.Ordinal)) continue;
            for (int i = 0; i < 100; i++)
            {
                var bad = (byte[])good.Clone();
                for (int k = 0; k < 4; k++) bad[magic.Length + rng.Next(bad.Length - magic.Length)] = (byte)rng.Next(256);
                if (i % 3 == 0) bad = bad.AsSpan(0, rng.Next(magic.Length, bad.Length)).ToArray();
                PdfImage.FromBytes(bad); // null or an image, never an exception
            }
        }
    }
}
