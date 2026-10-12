using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Images;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// GIF, WebP and SVG files as images. The fixtures in Fixtures/images were written by Pillow and ImageMagick; each
/// *.rgba.gz holds the first frame as decoded by Pillow (libwebp for WebP): width and height as 32-bit little-endian
/// numbers, then RGBA bytes.
/// </summary>
public class ImageFormatTests
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

    private static DecodedImage? Decode(byte[] data)
        => GifDecoder.IsGif(data) ? GifDecoder.Decode(data, 100_000_000) : WebPDecoder.Decode(data, 100_000_000);

    private static string DataUri(string mime, byte[] data) => $"data:{mime};base64,{Convert.ToBase64String(data)}";

    private static string SvgUri(string svg) => "data:image/svg+xml," + Uri.EscapeDataString(svg);

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual)
        => Assert.True(Math.Abs(actual.R - r) < 40 && Math.Abs(actual.G - g) < 40 && Math.Abs(actual.B - b) < 40,
            $"Expected ({r}, {g}, {b}), got {actual}");

    // ---- decoders ----

    [Theory]
    [InlineData("lossy.webp")]
    [InlineData("lossy-alpha.webp")]
    [InlineData("lossy-simple-filter.webp")]
    [InlineData("lossless.webp")]
    [InlineData("lossless-palette.webp")]
    [InlineData("animated.webp")]
    [InlineData("interlaced.gif")]
    [InlineData("animated.gif")]
    public void Decodes_the_same_pixels_as_the_reference_decoder(string name)
    {
        var (w, h, rgba) = Reference(name);
        var img = Decode(Fixture(name));
        Assert.NotNull(img);
        Assert.Equal(w, img!.Value.Width);
        Assert.Equal(h, img.Value.Height);
        for (int i = 0; i < w * h; i++)
        {
            int alpha = img.Value.Alpha?[i] ?? 255;
            Assert.True(alpha == rgba[i * 4 + 3], $"{name}: alpha of pixel ({i % w}, {i / w}) is {alpha}, expected {rgba[i * 4 + 3]}");
            if (alpha == 0) continue; // the colour of a see-through pixel does not matter
            for (int c = 0; c < 3; c++)
                Assert.True(img.Value.Rgb[i * 3 + c] == rgba[i * 4 + c],
                    $"{name}: channel {c} of pixel ({i % w}, {i / w}) is {img.Value.Rgb[i * 3 + c]}, expected {rgba[i * 4 + c]}");
        }
    }

    [Theory]
    [InlineData("lossy.webp")]
    [InlineData("lossy-alpha.webp")]
    [InlineData("lossless.webp")]
    [InlineData("animated.webp")]
    [InlineData("interlaced.gif")]
    public void Damaged_files_never_throw(string name)
    {
        var original = Fixture(name);
        var rnd = new Random(42);
        for (int round = 0; round < 300; round++)
        {
            var data = round < 60 ? original.AsSpan(0, original.Length * round / 60).ToArray() : (byte[])original.Clone();
            if (round >= 60)
                for (int k = 0; k < 1 + round % 8; k++)
                {
                    int p = 12 + rnd.Next(data.Length - 12);
                    data[p] ^= (byte)(1 << rnd.Next(8));
                }
            PdfImage.FromBytes(data); // null or an image, never an exception
        }
    }

    [Fact]
    public void A_gif_with_a_huge_screen_and_no_data_is_not_allocated()
    {
        // A 16000 x 16000 logical screen with a 1 x 1 frame of a few bytes.
        var gif = new byte[]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x80, 0x3E, 0x80, 0x3E, 0x80, 0, 0,
            0, 0, 0, 255, 255, 255,
            0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0, 0x3B,
        };
        Assert.Null(GifDecoder.Decode(gif, 400_000_000));
        // The same frame on a 1 x 1 screen decodes.
        gif[6] = 1; gif[7] = 0; gif[8] = 1; gif[9] = 0;
        Assert.NotNull(GifDecoder.Decode(gif, 400_000_000));
    }

    [Fact]
    public void A_lossy_webp_claiming_a_huge_size_with_no_data_is_rejected()
    {
        var data = Fixture("lossy.webp");
        int vp8 = Encoding.ASCII.GetString(data).IndexOf("VP8 ", StringComparison.Ordinal) + 8;
        // Width and height to 9000 x 9000: the first partition is far too short for 318,096 macroblocks.
        data[vp8 + 6] = 9000 & 0xFF; data[vp8 + 7] = 9000 >> 8;
        data[vp8 + 8] = 9000 & 0xFF; data[vp8 + 9] = 9000 >> 8;
        Assert.Null(WebPDecoder.Decode(data, 100_000_000));
    }

    // ---- raster images in documents ----

    [Theory]
    [InlineData("image/webp", "lossy-alpha.webp", true)]
    [InlineData("image/webp", "lossy.webp", false)]
    [InlineData("image/gif", "interlaced.gif", true)]
    [InlineData("image/gif", "animated.gif", false)]
    public void Webp_and_gif_images_are_embedded(string mime, string name, bool transparent)
    {
        var pdf = Latin1(ConvertUncompressed($"<img src='{DataUri(mime, Fixture(name))}'>"));
        Assert.Contains("/Subtype /Image", pdf);
        Assert.Equal(transparent, pdf.Contains("/SMask"));
    }

    [Fact]
    public void A_webp_image_is_painted_with_its_colours()
    {
        // The lossy fixture is white with coloured shapes: its top-left corner is white, and something is drawn.
        var html = $"<body style='margin:0'><img src='{DataUri("image/webp", Fixture("lossy.webp"))}' style='width:200pt;height:150pt'></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        var page = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        var (w, _, rgba) = Reference("lossy.webp");
        int differing = 0;
        for (int y = 5; y < 145; y += 10)
            for (int x = 5; x < 195; x += 10)
            {
                int sx = x * w / 200, sy = y * 45 / 150, i = (sy * w + sx) * 4, p = (y * page.Width + x) * 4;
                if (Math.Abs(page.Rgba[p] - rgba[i]) > 60 || Math.Abs(page.Rgba[p + 1] - rgba[i + 1]) > 60) differing++;
            }
        Assert.True(differing < 20, $"{differing} sampled pixels differ from the image");
    }

    // ---- SVG files ----

    private const string Logo =
        "<?xml version='1.0'?><!-- logo --><svg xmlns='http://www.w3.org/2000/svg' width='40' height='20' viewBox='0 0 4 2'>"
        + "<style>.b { fill: #0000ff }</style><rect width='2' height='2' fill='#ff0000'/><rect class='b' x='2' width='2' height='2'/></svg>";

    [Fact]
    public void An_svg_file_is_drawn_as_vectors_at_its_size()
    {
        var html = $"<body style='margin:0'><img src=\"{SvgUri(Logo)}\" style='width:200pt'></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        Assert.DoesNotContain("/Subtype /Image", Latin1(ConvertUncompressed(html)));
        // 200pt wide, 100pt high by the file's aspect ratio: red on the left half, blue (from its own style sheet) on the right.
        Color(255, 0, 0, Pixel(pdf, 50, 50));
        Color(0, 0, 255, Pixel(pdf, 150, 50));
        Color(255, 255, 255, Pixel(pdf, 150, 110));
    }

    [Fact]
    public void An_svg_file_takes_its_width_and_height_attributes()
    {
        var html = $"<body style='margin:0'><img src=\"{SvgUri(Logo)}\"><div style='height:10pt;background:#00ff00'></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        // 40 x 20 px is 30 x 15 pt; the green block follows the image's line.
        Color(255, 0, 0, Pixel(pdf, 5, 5));
        Color(0, 0, 255, Pixel(pdf, 25, 5));
        Color(255, 255, 255, Pixel(pdf, 40, 5));
    }

    [Fact]
    public void The_page_css_does_not_reach_into_an_svg_image()
    {
        var html = $"<style>rect {{ fill: #00ff00 }}</style><body style='margin:0'><img src=\"{SvgUri(Logo)}\" style='width:200pt'></body>";
        Color(255, 0, 0, Pixel(HtmlToPdf.Convert(html, NoMargins), 50, 50));
    }

    [Fact]
    public void An_svg_file_with_only_a_viewbox_fills_its_container()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 5'><rect width='10' height='5' fill='#ff0000'/></svg>";
        var html = $"<body style='margin:0'><div style='width:100pt'><img src=\"{SvgUri(svg)}\"></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        Color(255, 0, 0, Pixel(pdf, 95, 45));
        Color(255, 255, 255, Pixel(pdf, 105, 25));
    }

    [Fact]
    public void An_svg_file_in_a_line_of_text_sits_on_the_line()
    {
        var html = $"<body style='margin:0;font-size:20pt;line-height:1'>Ab<img src=\"{SvgUri(Logo)}\" style='height:20pt'>cd</body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        // 40 x 20 pt after the text "Ab", on the first line: red, then blue.
        int x = 0;
        while (x < 300 && Pixel(pdf, x, 10) is not { R: > 200, G: < 80, B: < 80 }) x++;
        Assert.InRange(x, 15, 40);
        Color(0, 0, 255, Pixel(pdf, x + 30, 10));
        Color(255, 255, 255, Pixel(pdf, x + 30, 30));
    }

    [Fact]
    public void A_missing_or_broken_svg_file_draws_nothing()
    {
        var html = $"<body style='margin:0'><img src=\"{SvgUri("<svg><rect width='10' height='10'")}\">"
                   + "<img src='data:image/svg+xml,not%20an%20image'>after</body>";
        var pdf = ConvertUncompressed(html, NoMargins);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void A_repeated_svg_background_is_a_tiling_pattern_of_vectors()
    {
        const string dot = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><rect width='10' height='20' fill='#ff0000'/></svg>";
        var html = $"<body style='margin:0'><div style=\"height:60pt;background:url('{SvgUri(dot)}')\"></div></body>";
        var raw = Latin1(ConvertUncompressed(html, NoMargins));
        Assert.Matches(new Regex(@"/PatternType 1 [^>]*/Resources \d+ 0 R"), raw);
        Assert.DoesNotContain("/Subtype /Image", raw);
        // 20 px = 15 pt tiles: red in the left half of each, nothing in the right half.
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        Color(255, 0, 0, Pixel(pdf, 3, 3));
        Color(255, 255, 255, Pixel(pdf, 12, 3));
        Color(255, 0, 0, Pixel(pdf, 18, 33));
        Color(255, 255, 255, Pixel(pdf, 27, 33));
    }

    [Fact]
    public void An_svg_background_with_only_a_viewbox_is_contained_in_the_box()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><rect width='1' height='1' fill='#ff0000'/></svg>";
        var html = $"<body style='margin:0'><div style=\"width:200pt;height:100pt;background:url('{SvgUri(svg)}') no-repeat center\"></div></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        // A 100 x 100 pt square in the middle of the 200 x 100 pt box.
        Color(255, 0, 0, Pixel(pdf, 100, 50));
        Color(255, 0, 0, Pixel(pdf, 55, 95));
        Color(255, 255, 255, Pixel(pdf, 45, 50));
        Color(255, 255, 255, Pixel(pdf, 155, 50));
    }

    [Fact]
    public void Svg_text_in_an_svg_file_uses_the_document_fonts()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='200' height='40'><text x='5' y='30' font-size='20'>Hello SVG</text></svg>";
        var pdf = Latin1(ConvertUncompressed($"<img src=\"{SvgUri(svg)}\">"));
        Assert.Contains("(Hello SVG)", pdf);
        Assert.Contains("/BaseFont /Times-Roman", pdf);
    }
}
