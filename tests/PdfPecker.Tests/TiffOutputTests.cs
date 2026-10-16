using System.Buffers.Binary;
using PdfPecker.Images;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>
/// TIFF output. Files are read back with the library's own TIFF decoder, which was checked against Pillow's files;
/// the encoder's output was in turn checked pixel for pixel with Pillow (libtiff) while it was written.
/// </summary>
public class TiffOutputTests
{
    private static byte[] SamplePdf(int pages = 1) => HtmlToPdf.Convert(
        "<h1 style='color:#c03'>TIFF output</h1>" +
        "<div style='background:linear-gradient(90deg,#08f,#fc0);height:60pt'></div>" +
        "<p>The quick brown fox jumps over the lazy dog. 0123456789</p>" +
        "<div style='width:120pt;height:40pt;background:rgba(0,128,0,0.5)'></div>" +
        string.Concat(Enumerable.Range(2, pages - 1).Select(i => $"<p style='break-before:page'>Page {i}</p>")),
        new PdfOptions { PageSize = PageSize.FromMillimeters(105, 148) });

    private static PdfImage Decode(byte[] tiff, int page = 0)
    {
        var img = TiffDecoder.Decode(tiff, 1L << 30, page);
        Assert.NotNull(img);
        return img;
    }

    /// <summary>The tags of one directory as (type, count, value or offset).</summary>
    private static Dictionary<int, (int Type, long Count, long Value)> Tags(byte[] tiff, int page = 0)
    {
        Assert.Equal((byte)'I', tiff[0]);
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(2)));
        long ifd = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        for (int i = 0; i < page; i++) ifd = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)ifd + 2 + 12 * BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)ifd))));
        int n = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)ifd));
        var tags = new Dictionary<int, (int, long, long)>();
        int last = -1;
        for (int i = 0; i < n; i++)
        {
            var e = tiff.AsSpan((int)ifd + 2 + i * 12, 12);
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(e);
            Assert.True(tag > last, "Tags must be in ascending order.");
            last = tag;
            tags[tag] = (BinaryPrimitives.ReadUInt16LittleEndian(e[2..]), BinaryPrimitives.ReadUInt32LittleEndian(e[4..]), BinaryPrimitives.ReadUInt32LittleEndian(e[8..]));
        }
        return tags;
    }

    private static int PageCount(byte[] tiff)
    {
        int count = 0;
        long ifd = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        while (ifd != 0)
        {
            Assert.Equal(0, ifd & 1); // directories start on a word boundary
            count++;
            ifd = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)ifd + 2 + 12 * BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)ifd))));
        }
        return count;
    }

    private static int Luma(byte[] rgba, int i) => (rgba[i * 4] * 299 + rgba[i * 4 + 1] * 587 + rgba[i * 4 + 2] * 114 + 500) / 1000;

    [Theory]
    [InlineData(TiffCompression.None)]
    [InlineData(TiffCompression.Lzw)]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.PackBits)]
    public void Color_is_lossless_with_and_without_alpha(TiffCompression compression)
    {
        var r = PdfRenderer.Open(SamplePdf());
        foreach (bool transparent in new[] { false, true })
        {
            var img = r.RenderPage(0, new ImageOptions { Dpi = 72, Transparent = transparent });
            var tiff = img.ToTiff(compression);
            var tags = Tags(tiff);
            Assert.Equal(transparent ? 4 : 3, tags[277].Value);
            Assert.Equal(transparent, tags.ContainsKey(338));
            Assert.Equal(compression is TiffCompression.Lzw or TiffCompression.Deflate, tags.ContainsKey(317));
            var decoded = BmpTiffTests.Rgba(Decode(tiff));
            var expected = img.Rgba;
            if (transparent)
                for (int i = 0; i < expected.Length; i += 4)
                    if (expected[i + 3] == 0) expected[i] = expected[i + 1] = expected[i + 2] = 0; // colour under alpha 0 is undefined
            Assert.Equal(expected, decoded);
        }
    }

    [Fact]
    public void Grayscale_is_the_luma_of_the_page()
    {
        var img = PdfRenderer.Open(SamplePdf()).RenderPage(0, new ImageOptions { Dpi = 60, TiffColorMode = TiffColorMode.Grayscale });
        var tiff = img.ToTiff();
        var tags = Tags(tiff);
        Assert.Equal(1, tags[262].Value); // BlackIsZero
        Assert.Equal(5, tags[259].Value); // LZW by default
        var rgba = img.Rgba;
        var decoded = BmpTiffTests.Rgba(Decode(tiff));
        for (int i = 0; i < img.Width * img.Height; i++) Assert.Equal(Luma(rgba, i), decoded[i * 4]);
    }

    [Theory]
    [InlineData(TiffCompression.CcittGroup4)]
    [InlineData(TiffCompression.None)]
    [InlineData(TiffCompression.Lzw)]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.PackBits)]
    public void Black_and_white_thresholds_the_page(TiffCompression compression)
    {
        foreach (int threshold in new[] { 128, 200 })
        {
            var img = PdfRenderer.Open(SamplePdf()).RenderPage(0, new ImageOptions
            {
                Dpi = 100, TiffColorMode = TiffColorMode.BlackAndWhite, TiffThreshold = threshold, TiffCompression = compression,
            });
            var tiff = img.ToTiff();
            var tags = Tags(tiff);
            Assert.Equal(1, tags[258].Value);
            Assert.Equal(0, tags[262].Value); // WhiteIsZero, the fax convention
            var decoded = BmpTiffTests.Rgba(Decode(tiff));
            var rgba = img.Rgba;
            int black = 0;
            for (int i = 0; i < img.Width * img.Height; i++)
            {
                Assert.Equal(Luma(rgba, i) < threshold ? 0 : 255, decoded[i * 4]);
                if (decoded[i * 4] == 0) black++;
            }
            Assert.True(black > 1000);
        }
    }

    [Fact]
    public void Black_and_white_defaults_to_group_4_and_group_4_implies_black_and_white()
    {
        var r = PdfRenderer.Open(SamplePdf());
        var bw = r.RenderPage(0, new ImageOptions { Dpi = 150, TiffColorMode = TiffColorMode.BlackAndWhite }).ToTiff();
        Assert.Equal(4, Tags(bw)[259].Value);
        Assert.Equal(0, Tags(bw)[293].Value);
        var forced = r.RenderPage(0, new ImageOptions { Dpi = 150, TiffCompression = TiffCompression.CcittGroup4 }).ToTiff();
        Assert.Equal(bw, forced);
        var color = r.RenderPage(0, new ImageOptions { Dpi = 150 }).ToTiff();
        Assert.Equal(5, Tags(color)[259].Value);
        Assert.True(bw.Length * 10 < color.Length, $"Group 4 {bw.Length} bytes, colour LZW {color.Length} bytes");
    }

    [Fact]
    public void ConvertToTiff_writes_every_page_into_one_file()
    {
        var pdf = SamplePdf(3);
        var options = new ImageOptions { Dpi = 50, TiffColorMode = TiffColorMode.BlackAndWhite };
        var tiff = PdfToImage.ConvertToTiff(pdf, options);
        Assert.Equal(3, PageCount(tiff));
        var renderer = PdfRenderer.Open(pdf);
        for (int p = 0; p < 3; p++)
        {
            var tags = Tags(tiff, p);
            Assert.Equal(2, tags[254].Value); // a page of a multi-page document
            Assert.Equal((long)p | 3L << 16, tags[297].Value); // PageNumber: p of 3
            var single = renderer.RenderPage(p, options);
            Assert.Equal(BmpTiffTests.Rgba(Decode(single.ToTiff())), BmpTiffTests.Rgba(Decode(tiff, p)));
        }
        Assert.Null(TiffDecoder.Decode(tiff, 1L << 30, 3));
        Assert.Equal(tiff, renderer.RenderAllToTiff(options));
        Assert.Equal(tiff, PdfToImage.ConvertToTiffAsync(pdf, options).GetAwaiter().GetResult());
        Assert.False(Tags(PdfToImage.ConvertToTiff(SamplePdf())).ContainsKey(254)); // one page: not marked as part of several
    }

    [Fact]
    public void Resolution_is_recorded()
    {
        var r = PdfRenderer.Open(SamplePdf());
        var tiff = r.RenderPage(0, new ImageOptions { Dpi = 300, TiffColorMode = TiffColorMode.BlackAndWhite }).ToTiff();
        var tags = Tags(tiff);
        Assert.Equal(2, tags[296].Value); // inch
        foreach (int tag in new[] { 282, 283 })
        {
            Assert.Equal(5, tags[tag].Type);
            long at = tags[tag].Value;
            Assert.Equal(300.0, (double)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)at)) / BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)at + 4)));
        }
        // A fitted width records the resolution that width gives: 105 mm at 413 pixels is about 99.9 dpi.
        var fittedTiff = r.RenderPage(0, new ImageOptions { Width = 413 }).ToTiff();
        long x = Tags(fittedTiff)[282].Value;
        double dpi = (double)BinaryPrimitives.ReadUInt32LittleEndian(fittedTiff.AsSpan((int)x)) / BinaryPrimitives.ReadUInt32LittleEndian(fittedTiff.AsSpan((int)x + 4));
        Assert.Equal(413 / (105 / 25.4), dpi, 0.01);
    }

    [Fact]
    public void Format_option_and_ConvertFile_write_tiff()
    {
        var pdf = SamplePdf(2);
        var page = PdfToImage.ConvertPage(pdf, 1, new ImageOptions { Dpi = 30, Format = PdfImageFormat.Tiff });
        Assert.Equal(1, PageCount(page));
        Assert.Equal(".tif", RenderedImage.GetFileExtension(PdfImageFormat.Tiff));

        var dir = Path.Combine(Path.GetTempPath(), "pdfpecker-tiff-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var pdfPath = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(pdfPath, pdf);
            var tiffPath = Path.Combine(dir, "doc.tif");
            PdfToImage.ConvertFileToTiff(pdfPath, tiffPath, new ImageOptions { Dpi = 30 });
            Assert.Equal(2, PageCount(File.ReadAllBytes(tiffPath)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void Invalid_threshold_throws(int threshold)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ImageOptions { TiffThreshold = threshold });

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 5)]
    [InlineData(64, 3)]
    [InlineData(1728, 40)]
    [InlineData(6000, 4)] // runs longer than the largest make-up code
    public void Group4_round_trips_through_the_decoder(int width, int height)
    {
        var rnd = new Random(width * 31 + height);
        foreach (var pattern in new Func<int, int, int>[]
        {
            (_, _) => 0,
            (_, _) => 1,
            (x, y) => (x + y) & 1,
            (_, _) => rnd.Next(2),
            (x, y) => (x / 3 + y) % 5 == 0 ? 1 : 0,
            (x, y) => x > width / 3 && x < width * 2 / 3 + y ? 1 : 0,
        })
        {
            var pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)pattern(x, y);
            var encoded = CcittFaxEncoder.EncodeGroup4(pixels, width, height);
            var decoded = CcittFaxDecoder.Decode(encoded, -1, width, height, false, blackIs1: true);
            int rowBytes = (width + 7) / 8;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    Assert.Equal(pixels[y * width + x], (decoded[y * rowBytes + x / 8] >> (7 - x % 8)) & 1);
        }
    }

    [Fact]
    public void Lzw_round_trips_through_the_decoder()
    {
        var rnd = new Random(7);
        var inputs = new List<byte[]> { Array.Empty<byte>(), new byte[] { 5 }, new byte[100_000] };
        var noise = new byte[200_000];
        rnd.NextBytes(noise);
        inputs.Add(noise);
        // Mixed text-like data passes every code width and several table resets.
        inputs.Add(Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251 < 120 ? 'a' + (i * 7 % 13) : rnd.Next(256))).ToArray());
        for (int n = 250; n < 270; n++) inputs.Add(Enumerable.Range(0, n * 20).Select(i => (byte)rnd.Next(4)).ToArray());
        foreach (var input in inputs)
            Assert.Equal(input, Filters.Lzw(TiffEncoder.Lzw(input), true));
    }
}
