using System.Buffers.Binary;
using System.Text;
using PdfPecker.Images;

namespace PdfPecker.Tests;

public class StripImageMetadataTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A segment with the given marker and payload.</summary>
    private static byte[] Segment(byte marker, string payload)
    {
        var bytes = Encoding.Latin1.GetBytes(payload);
        var segment = new byte[4 + bytes.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(bytes.Length + 2));
        bytes.CopyTo(segment, 4);
        return segment;
    }

    /// <summary>The fixture JPEG with EXIF, XMP, IPTC and a comment after its start-of-image marker.</summary>
    private static byte[] TaggedJpeg(string fixture = "rgb.jpg")
    {
        var jpeg = Fixture(fixture);
        var metadata = new[]
        {
            Segment(0xE1, "Exif\0\0MM\0*\0\0\0\0SecretCamera GPS 55.6761 12.5683"),
            Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>SecretXmp</x:xmpmeta>"),
            Segment(0xED, "Photoshop 3.0\08BIMSecretIptc"),
            Segment(0xFE, "SecretComment"),
        }.SelectMany(s => s);
        return jpeg.Take(2).Concat(metadata).Concat(jpeg.Skip(2)).ToArray();
    }

    private static string Html(byte[] jpeg) => $"<img src='data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}' style='width:100pt'>";

    [Fact]
    public void JpegKeepsItsMetadataByDefault()
    {
        var pdf = Encoding.Latin1.GetString(HtmlToPdf.Convert(Html(TaggedJpeg())));
        foreach (var secret in new[] { "SecretCamera", "SecretXmp", "SecretIptc", "SecretComment" }) Assert.Contains(secret, pdf);
    }

    [Fact]
    public void StripImageMetadataRemovesEverySegmentAndKeepsThePixels()
    {
        var tagged = TaggedJpeg();
        var stripped = HtmlToPdf.Convert(Html(tagged), new PdfOptions { StripImageMetadata = true });
        var text = Encoding.Latin1.GetString(stripped);
        foreach (var secret in new[] { "SecretCamera", "SecretXmp", "SecretIptc", "SecretComment", "Exif" }) Assert.DoesNotContain(secret, text);

        // The image left in the PDF is the original JPEG byte for byte.
        Assert.Contains(Encoding.Latin1.GetString(Fixture("rgb.jpg")), text);
        var plain = HtmlToPdf.Convert(Html(Fixture("rgb.jpg")));
        Assert.Equal(PdfToImage.ConvertPage(plain, 0), PdfToImage.ConvertPage(stripped, 0));
    }

    [Fact]
    public void AdobeColourTransformIsKept()
    {
        var cmyk = Fixture("cmyk.jpg");
        var plain = HtmlToPdf.Convert(Html(cmyk));
        var stripped = HtmlToPdf.Convert(Html(TaggedJpeg("cmyk.jpg")), new PdfOptions { StripImageMetadata = true });
        Assert.Equal(PdfToImage.ConvertPage(plain, 0), PdfToImage.ConvertPage(stripped, 0));
    }

    [Fact]
    public void WorksWithEncryption()
    {
        var pdf = HtmlToPdf.Convert(Html(TaggedJpeg()), new PdfOptions { StripImageMetadata = true, Security = new PdfSecurity { UserPassword = "pw" } });
        var plain = HtmlToPdf.Convert(Html(Fixture("rgb.jpg")));
        Assert.Equal(PdfToImage.ConvertPage(plain, 0), PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Password = "pw" }));
    }

    [Fact]
    public void JpegWithoutMetadataOrWithBrokenSegmentsIsLeftAlone()
    {
        var jpeg = Fixture("rgb.jpg");
        Assert.Same(jpeg, PdfImage.WithoutMetadata(jpeg));

        // A segment whose length runs past the end of the data: nothing can be followed safely.
        var broken = jpeg.Take(2).Concat(new byte[] { 0xFF, 0xE1, 0xFF, 0xFF, 1, 2, 3 }).ToArray();
        Assert.Same(broken, PdfImage.WithoutMetadata(broken));
        var notJpeg = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Same(notJpeg, PdfImage.WithoutMetadata(notJpeg));
    }

    [Fact]
    public void ImageToPdfStripsToo()
    {
        var pdf = ImageToPdf.Convert(new[] { TaggedJpeg() }, new PdfOptions { StripImageMetadata = true });
        Assert.DoesNotContain("SecretCamera", Encoding.Latin1.GetString(pdf));
    }
}
