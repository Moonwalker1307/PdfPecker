using System.Buffers.Binary;
using System.Text;

namespace PdfPecker.Tests;

public class ImageExtractionTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string JpegUri(byte[] jpeg) => "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

    [Fact]
    public void JpegComesOutAsItsOriginalBytes()
    {
        var jpeg = Fixture("rgb.jpg");
        var pdf = HtmlToPdf.Convert($"<img src='{JpegUri(jpeg)}' style='width:144pt;height:72pt;margin:0'>",
            new PdfOptions { Margins = new PageMargins(72, 36, 36, 36) });

        var image = Assert.Single(PdfImages.Extract(pdf));
        Assert.Equal(PdfImageFormat.Jpeg, image.Format);
        Assert.Equal(".jpg", image.FileExtension);
        Assert.Equal(jpeg, image.Data);
        Assert.False(image.IsMask);
        var placement = Assert.Single(image.Placements);
        Assert.Equal(1, placement.PageNumber);
        Assert.Equal(144, placement.Width, 1);
        Assert.Equal(72, placement.Height, 1);
        Assert.Equal(36, placement.X, 1);
        Assert.Equal(72, placement.Y, 1);
    }

    [Fact]
    public void KeepJpegFalseDecodesToPng()
    {
        var jpeg = Fixture("rgb.jpg");
        var pdf = HtmlToPdf.Convert($"<img src='{JpegUri(jpeg)}'>");
        var image = Assert.Single(PdfImages.Extract(pdf, new PdfImagesOptions { KeepJpeg = false }));
        Assert.Equal(PdfImageFormat.Png, image.Format);
        Assert.Equal(".png", image.FileExtension);
        Assert.True(image.Data.AsSpan().StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }));
    }

    [Fact]
    public void CmykJpegIsDecodedToPng()
    {
        var pdf = HtmlToPdf.Convert($"<img src='{JpegUri(Fixture("cmyk.jpg"))}'>");
        var image = Assert.Single(PdfImages.Extract(pdf));
        Assert.Equal(PdfImageFormat.Png, image.Format);
    }

    [Fact]
    public void PngKeepsItsPixelsAndAlpha()
    {
        var png = PdfTestHelpers.PixelPng(3, 2, (x, y) => ((byte)(x * 100), (byte)(y * 200), 7));
        var alpha = PdfTestHelpers.AlphaPng(4, 4, (x, y) => (byte)(x * 80));
        var pdf = HtmlToPdf.Convert($"<img src='{PdfTestHelpers.DataUri(png)}'><img src='{PdfTestHelpers.DataUri(alpha)}'>");

        var images = PdfImages.Extract(pdf);
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal(PdfImageFormat.Png, i.Format));
        var first = RenderedPixels(images[0].Data);
        Assert.Equal((3, 2), (images[0].Width, images[0].Height));
        Assert.Equal(0xFF07C864u, first[1 * 3 + 1]); // x 1, y 1: R 100, G 200, B 7, opaque
        var second = RenderedPixels(images[1].Data);
        Assert.Equal(0u, second[0] >> 24);
        Assert.Equal(240u, second[3] >> 24);
    }

    /// <summary>Decodes a PNG back to 0xAABBGGRR pixels by wrapping it in a PDF of its own and extracting it.</summary>
    private static uint[] RenderedPixels(byte[] png)
    {
        var decoded = PdfPecker.Images.PdfImage.FromBytes(png)!;
        var rgb = decoded.DecodeRgb()!.Value;
        decoded.TryAlpha(out var alpha);
        var pixels = new uint[rgb.Width * rgb.Height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (uint)(rgb.Rgb[i * 3] | rgb.Rgb[i * 3 + 1] << 8 | rgb.Rgb[i * 3 + 2] << 16) | (uint)(alpha?[i] ?? 255) << 24;
        return pixels;
    }

    [Fact]
    public void AnImageDrawnOnEveryPageIsReturnedOnceWithEachPlacement()
    {
        var uri = PdfTestHelpers.DataUri(PdfTestHelpers.RgbPng());
        var pdf = HtmlToPdf.Convert($"<img src='{uri}'><div style='break-before:page'><img src='{uri}'><img src='{uri}'></div>");

        var image = Assert.Single(PdfImages.Extract(pdf));
        Assert.Equal(new[] { 1, 2, 2 }, image.Placements.Select(p => p.PageNumber));
        var second = Assert.Single(PdfImages.Extract(pdf, new PdfImagesOptions { Pages = PdfPageRange.Parse("2") }));
        Assert.Equal(2, second.Placements.Count);
    }

    [Fact]
    public void InlineImagesStencilMasksAndFormsAreFound()
    {
        var fixture = new FixturePdf();
        int mask = fixture.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true", new byte[] { 0b1010_1010 });
        int form = fixture.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /M {mask} 0 R >> >>", "/M Do");
        var pdf = fixture.Build(200, 100,
            "q 20 0 0 10 10 80 cm BI /W 2 /H 1 /CS /RGB /BPC 8 ID \xff\x00\x00\x00\xff\x00 EI Q " +
            "q 0 0 1 rg 80 0 0 8 100 20 cm /F Do Q",
            $"/XObject << /F {form} 0 R >>");

        var images = PdfImages.Extract(pdf);
        Assert.Equal(2, images.Count);
        var inline = images[0];
        Assert.Equal((2, 1), (inline.Width, inline.Height));
        Assert.Equal(10, inline.Placements[0].X, 3);
        Assert.Equal(10, inline.Placements[0].Y, 3);
        var stencil = images[1];
        Assert.True(stencil.IsMask);
        Assert.Equal((8, 1), (stencil.Width, stencil.Height));
        Assert.Equal(100, stencil.Placements[0].X, 3);
        Assert.Equal(72, stencil.Placements[0].Y, 3);
        Assert.Equal(80, stencil.Placements[0].Width, 3);
    }

    [Fact]
    public void HiddenLayersSoftMasksAndType3GlyphsAreLeftOut()
    {
        var fixture = new FixturePdf();
        int image = fixture.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[] { 128 });
        int smask = fixture.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[] { 255 });
        int masked = fixture.AddStream($"/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /SMask {smask} 0 R", new byte[] { 9 });
        int layer = fixture.Add("<< /Type /OCG /Name (Off) >>");
        fixture.Add($"<< /Type /Catalog >>"); // unused; keeps the numbering simple
        var pdf = fixture.Build(100, 100,
            "/OC /L BDC q 10 0 0 10 0 0 cm /Hidden Do Q EMC q 10 0 0 10 0 0 cm /Masked Do Q",
            $"/XObject << /Hidden {image} 0 R /Masked {masked} 0 R >> /Properties << /L {layer} 0 R >>");
        // Turn the layer off through the catalog's /OCProperties.
        pdf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(pdf).Replace("<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [{layer} 0 R] /D << /OFF [{layer} 0 R] >> >> >>"));

        var only = Assert.Single(PdfImages.Extract(pdf));
        Assert.Equal(PdfImageFormat.Png, only.Format);
        Assert.Single(only.Placements);
    }

    [Fact]
    public void ExifSummaryIsRead()
    {
        var jpeg = WithExif(Fixture("rgb.jpg"), Exif());
        var pdf = HtmlToPdf.Convert($"<img src='{JpegUri(jpeg)}'>");

        var image = Assert.Single(PdfImages.Extract(pdf));
        Assert.Equal(jpeg, image.Data);
        var exif = image.Exif!;
        Assert.Equal("PeckerCam", exif.Make);
        Assert.Equal("Model 7", exif.Model);
        Assert.Equal(6, exif.Orientation);
        Assert.Equal(new DateTime(2026, 10, 23, 14, 5, 9), exif.DateTaken);
        Assert.Null(exif.DateModified);
        Assert.Equal(55 + 0.5 / 60 + 30 / 3600.0, exif.Latitude!.Value, 6);
        Assert.Equal(-(12 + 15 / 60.0), exif.Longitude!.Value, 6);
        Assert.Equal(-3.5, exif.Altitude!.Value, 6);
        Assert.Equal((byte)'M', exif.Data[0]);

        // A decoded image keeps the EXIF summary of its JPEG.
        Assert.NotNull(Assert.Single(PdfImages.Extract(pdf, new PdfImagesOptions { KeepJpeg = false })).Exif);
    }

    [Fact]
    public void ExifFixtureHasSoftwareAndModifiedDate()
    {
        var jpeg = Fixture(Path.Combine("images", "exif.jpg"));
        var exif = Assert.Single(PdfImages.Extract(HtmlToPdf.Convert($"<img src='{JpegUri(jpeg)}'>"))).Exif!;
        Assert.Equal("PdfPecker fuzz seed", exif.Software);
        Assert.Equal(new DateTime(2026, 10, 23, 15, 0, 0), exif.DateModified);
        Assert.Equal(new DateTime(2026, 10, 23, 14, 5, 9), exif.DateTaken);
    }

    [Fact]
    public void DamagedExifIsIgnored()
    {
        var exif = Exif();
        // Point the EXIF and GPS directories far outside the block, and cut it short.
        var damaged = exif.AsSpan(0, exif.Length - 20).ToArray();
        for (int i = 8; i + 12 <= 80 && i + 12 <= damaged.Length; i++)
            if (damaged[i] == 0x87 && damaged[i + 1] == 0x69 || damaged[i] == 0x88 && damaged[i + 1] == 0x25)
                BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(i + 8), 0x7FFF_FFF0);
        var pdf = HtmlToPdf.Convert($"<img src='{JpegUri(WithExif(Fixture("rgb.jpg"), damaged))}'>");

        var image = Assert.Single(PdfImages.Extract(pdf));
        Assert.Equal("PeckerCam", image.Exif!.Make);
        Assert.Null(image.Exif.DateTaken);
        Assert.Null(image.Exif.Latitude);
    }

    [Fact]
    public void ImageXmpIsReturned()
    {
        const string Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'>photo</x:xmpmeta>";
        var fixture = new FixturePdf();
        int metadata = fixture.AddStream("/Type /Metadata /Subtype /XML", Xmp);
        int image = fixture.AddStream($"/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Metadata {metadata} 0 R", new byte[] { 1 });
        var pdf = fixture.Build(10, 10, "q 10 0 0 10 0 0 cm /I Do Q", $"/XObject << /I {image} 0 R >>");
        Assert.Equal(Xmp, Assert.Single(PdfImages.Extract(pdf)).XmpMetadata);
    }

    [Fact]
    public void TooManyImagesThrowsLimitException()
    {
        var html = string.Concat(Enumerable.Range(0, 3).Select(i => $"<img src='{PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(2, 2, (byte)i, 0, 0))}'>"));
        var pdf = HtmlToPdf.Convert(html);
        Assert.Equal(3, PdfImages.Extract(pdf).Count);
        Assert.Throws<PdfLimitException>(() => PdfImages.Extract(pdf, new PdfImagesOptions { MaxImages = 2 }));
    }

    [Fact]
    public void StreamAndFileOverloadsMatch()
    {
        var pdf = HtmlToPdf.Convert($"<img src='{JpegUri(Fixture("rgb.jpg"))}'>");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            Assert.Equal(PdfImages.Extract(pdf)[0].Data, PdfImages.ExtractFile(path)[0].Data);
            Assert.Equal(PdfImages.Extract(pdf)[0].Data, PdfImages.Extract(new MemoryStream(pdf))[0].Data);
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Throws<ArgumentNullException>(() => PdfImages.Extract((byte[])null!));
        Assert.Throws<InvalidDataException>(() => PdfImages.Extract(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ThirdPartyPdfsGiveTheirImages()
    {
        foreach (var name in new[] { "chromium.pdf", "libreoffice.pdf", "reportlab.pdf", "ghostscript.pdf" })
        {
            var pdf = Fixture(Path.Combine("third-party", name));
            foreach (var image in PdfImages.Extract(pdf))
            {
                Assert.True(image.Width > 0 && image.Height > 0, name);
                Assert.NotEmpty(image.Placements);
                Assert.NotEmpty(image.Data);
            }
        }
    }

    /// <summary>Inserts an EXIF APP1 segment holding <paramref name="tiff"/> after the start-of-image marker.</summary>
    private static byte[] WithExif(byte[] jpeg, byte[] tiff)
    {
        var segment = new byte[4 + 6 + tiff.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(segment.Length - 2));
        "Exif\0\0"u8.CopyTo(segment.AsSpan(4));
        tiff.CopyTo(segment.AsSpan(10));
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }

    /// <summary>
    /// A big-endian EXIF block: maker, model and orientation in IFD0, the time taken in the EXIF directory, and a
    /// position west of Greenwich below sea level in the GPS directory.
    /// </summary>
    private static byte[] Exif()
    {
        var ifd0 = new List<Entry> { Ascii(0x010F, "PeckerCam"), Ascii(0x0110, "Model 7"), new(0x0112, 3, 1, Short(6)), new(0x8769, 4, 1, null), new(0x8825, 4, 1, null) };
        var exif = new List<Entry> { Ascii(0x9003, "2026:10:23 14:05:09") };
        var gps = new List<Entry>
        {
            Ascii(1, "N"), new(2, 5, 3, Rationals((55, 1), (30, 60), (30, 1))),
            Ascii(3, "W"), new(4, 5, 3, Rationals((12, 1), (15, 1), (0, 1))),
            new(5, 1, 1, new byte[] { 1, 0, 0, 0 }), new(6, 5, 1, Rationals((7, 2))),
        };
        var data = new MemoryStream();
        data.Write("MM\0*"u8);
        data.Write(new byte[] { 0, 0, 0, 8 });
        int Size(List<Entry> d) => 2 + d.Count * 12 + 4;
        int ifd0At = 8, exifAt = ifd0At + Size(ifd0), gpsAt = exifAt + Size(exif), valuesAt = gpsAt + Size(gps);
        ifd0[3] = ifd0[3] with { Value = Long(exifAt) };
        ifd0[4] = ifd0[4] with { Value = Long(gpsAt) };
        var values = new MemoryStream();
        foreach (var directory in new[] { ifd0, exif, gps })
        {
            data.Write(Short(directory.Count).AsSpan(0, 2));
            foreach (var e in directory)
            {
                data.Write(Short(e.Tag).AsSpan(0, 2));
                data.Write(Short(e.Type).AsSpan(0, 2));
                data.Write(Long(e.Count));
                if (e.Value!.Length <= 4) data.Write(e.Value.Concat(new byte[4 - e.Value.Length]).ToArray());
                else
                {
                    data.Write(Long(valuesAt + (int)values.Length));
                    values.Write(e.Value);
                }
            }
            data.Write(Long(0));
        }
        data.Write(values.ToArray());
        return data.ToArray();
    }

    private sealed record Entry(int Tag, int Type, int Count, byte[]? Value);

    private static Entry Ascii(int tag, string text) => new(tag, 2, text.Length + 1, Encoding.ASCII.GetBytes(text + "\0"));

    private static byte[] Short(int v) => new byte[] { (byte)(v >> 8), (byte)v, 0, 0 };

    private static byte[] Long(int v) => new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] Rationals(params (int Numerator, int Denominator)[] values)
        => values.SelectMany(v => Long(v.Numerator).Concat(Long(v.Denominator))).ToArray();
}
