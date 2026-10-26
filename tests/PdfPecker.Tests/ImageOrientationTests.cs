using System.Buffers.Binary;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Images;

namespace PdfPecker.Tests;

/// <summary>
/// image-orientation: photos are turned as their EXIF orientation says, as Chrome shows them, unless the property is
/// none.
/// </summary>
public class ImageOrientationTests
{
    private const uint Red = 0xFF0000FF, Blue = 0xFFFF0000;

    /// <summary>
    /// A JPEG photo 64 px wide and 32 px high, red in its top-left 16 by 16 corner and blue elsewhere, with an EXIF block
    /// that gives its orientation (none when 0).
    /// </summary>
    private static byte[] Photo(int orientation)
    {
        const int w = 64, h = 32;
        var pixels = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = x < 16 && y < 16 ? Red : Blue;
        var jpeg = JpegEncoder.Encode(pixels, w, h, 95, 0xFFFFFF);
        if (orientation == 0) return jpeg;
        // A big-endian TIFF block with one tag in its main directory: Orientation (0x0112), a SHORT.
        var exif = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
            0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0 };
        var segment = new byte[4 + exif.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(exif.Length + 2));
        exif.CopyTo(segment, 4);
        return [.. jpeg.Take(2), .. segment, .. jpeg.Skip(2)];
    }

    /// <summary>Whether the decoded image is red (rather than blue) at (x, y).</summary>
    private static bool RedAt(PdfImage image, int x, int y)
    {
        var (data, w, _, n) = JpegDecoder.Decode(image.Data)!.Value;
        Assert.Equal(3, n);
        int at = (y * w + x) * 3;
        return data[at] > 128 && data[at + 2] < 128;
    }

    [Theory]
    [InlineData(1, 64, 32, 0, 0)]
    [InlineData(2, 64, 32, 63, 0)]
    [InlineData(3, 64, 32, 63, 31)]
    [InlineData(4, 64, 32, 0, 31)]
    [InlineData(5, 32, 64, 0, 0)]
    [InlineData(6, 32, 64, 31, 0)]
    [InlineData(7, 32, 64, 31, 63)]
    [InlineData(8, 32, 64, 0, 63)]
    public void A_photo_is_turned_as_its_exif_orientation_says(int orientation, int w, int h, int redX, int redY)
    {
        var image = PdfImage.FromBytes(Photo(orientation))!;
        Assert.Equal(w, image.Width);
        Assert.Equal(h, image.Height);
        Assert.True(RedAt(image, redX, redY));
        // The opposite corner stays blue.
        Assert.False(RedAt(image, w - 1 - redX, h - 1 - redY));
        Assert.Equal(orientation == 1, image.AsStored == null);
    }

    [Fact]
    public void An_upright_photo_keeps_its_bytes()
    {
        var jpeg = Photo(0);
        Assert.Same(jpeg, PdfImage.FromBytes(jpeg)!.Data);
        jpeg = Photo(1);
        Assert.Same(jpeg, PdfImage.FromBytes(jpeg)!.Data);
    }

    [Fact]
    public void A_turned_photo_keeps_the_stored_image_for_image_orientation_none()
    {
        var jpeg = Photo(6);
        var image = PdfImage.FromBytes(jpeg)!;
        Assert.Same(jpeg, image.AsStored!.Data);
        Assert.Equal(64, image.AsStored.Width);
        var copy = image.Copy();
        Assert.NotSame(image.AsStored, copy.AsStored);
        Assert.Equal(64, copy.AsStored!.Width);
    }

    [Fact]
    public void Damaged_exif_leaves_the_photo_as_stored()
    {
        var jpeg = Photo(6);
        // The orientation tag's type becomes 9 (SLONG) and its count huge, which the reader does not take.
        jpeg[2 + 4 + 18] = 9;
        jpeg[2 + 4 + 20] = 0x7F;
        var image = PdfImage.FromBytes(jpeg)!;
        Assert.Equal(64, image.Width);
        Assert.Null(image.AsStored);
    }

    private static ImageOp Image(string html)
    {
        var doc = HtmlParser.Parse("<!DOCTYPE html><html><body style='margin:0'>" + html + "</body></html>");
        new StyleResolver(new Stylesheet(), 500, 750).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(new PageGrid(new PageSetup { Width = 500, Height = 750 }), new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        return Assert.Single(engine.Ops.OfType<ImageOp>());
    }

    private static string Src(int orientation) => "data:image/jpeg;base64," + Convert.ToBase64String(Photo(orientation));

    [Theory]
    [InlineData("", 24, 48)]
    [InlineData("image-orientation:from-image", 24, 48)]
    [InlineData("image-orientation:90deg", 24, 48)]
    [InlineData("image-orientation:none", 48, 24)]
    public void An_img_takes_the_size_of_the_turned_photo_unless_image_orientation_is_none(string style, float w, float h)
    {
        var op = Image($"<img src='{Src(6)}' style='{style}'>");
        Assert.Equal(w, op.W, 0.01f);
        Assert.Equal(h, op.H, 0.01f);
        Assert.Equal(style.EndsWith("none", StringComparison.Ordinal) ? 64 : 32, op.Image.Width);
    }

    [Fact]
    public void Image_orientation_is_inherited()
        => Assert.Equal(64, Image($"<div style='image-orientation:none'><img src='{Src(6)}'></div>").Image.Width);

    [Fact]
    public void A_background_photo_is_turned_too()
    {
        var pdf = HtmlToPdf.Convert($"<div style='width:100px;height:100px;background:url({Src(6)}) no-repeat'></div>");
        Assert.Contains("/Width 32 /Height 64", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        pdf = HtmlToPdf.Convert($"<div style='width:100px;height:100px;background:url({Src(6)}) no-repeat;image-orientation:none'></div>");
        Assert.Contains("/Width 64 /Height 32", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Images_to_pdf_puts_a_photo_upright()
        => Assert.Contains("/Width 32 /Height 64", System.Text.Encoding.Latin1.GetString(ImageToPdf.Convert([Photo(6)])), StringComparison.Ordinal);

    [Fact]
    public void Supports_knows_image_orientation()
        => Assert.True(CssSupports.Evaluate("(image-orientation: none)"));
}
