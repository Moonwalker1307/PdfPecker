using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PdfPecker.Tests;

public class RenderingTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static (int R, int G, int B, int A) Pixel(RenderedImage img, int x, int y)
    {
        int o = (y * img.Width + x) * 4;
        return (img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2], img.Rgba[o + 3]);
    }

    private static void AssertColor((int R, int G, int B, int A) actual, int r, int g, int b, int tolerance = 12)
    {
        Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"Expected about ({r},{g},{b}) but got ({actual.R},{actual.G},{actual.B})");
    }

    [Fact]
    public void Renders_own_html_output()
    {
        var pdf = HtmlToPdf.Convert(
            "<div style='background:#ff0000;width:200pt;height:100pt'></div><p style='font-size:20pt'>Hello renderer</p>",
            new PdfOptions { PageSize = PageSize.A4 });
        var renderer = PdfRenderer.Open(pdf);
        Assert.Equal(1, renderer.PageCount);
        var img = renderer.RenderPage(0, new ImageOptions { Dpi = 72 });
        Assert.Equal(595, img.Width);
        Assert.Equal(842, img.Height);
        int red = 0, dark = 0, white = 0;
        for (int i = 0; i < img.Width * img.Height; i++)
        {
            int r = img.Rgba[i * 4], g = img.Rgba[i * 4 + 1], b = img.Rgba[i * 4 + 2];
            if (r > 240 && g < 15 && b < 15) red++;
            else if (r < 100 && g < 100 && b < 100) dark++;
            else if (r == 255 && g == 255 && b == 255) white++;
        }
        Assert.InRange(red, 200 * 100 - 800, 200 * 100 + 800); // the 200 x 100 pt box
        Assert.True(dark > 200, "text should leave dark pixels");
        Assert.True(white > img.Width * img.Height / 2);
    }

    [Fact]
    public void Dpi_and_size_options_set_the_image_size()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { PageSize = PageSize.Letter });
        var r = PdfRenderer.Open(pdf);
        var (w, h) = r.GetPageSize(0);
        Assert.Equal(612, w, 1);
        Assert.Equal(792, h, 1);
        var a = r.RenderPage(0, new ImageOptions { Dpi = 144 });
        Assert.Equal((1224, 1584), (a.Width, a.Height));
        var b = r.RenderPage(0, new ImageOptions { Width = 300 });
        Assert.Equal((300, 388), (b.Width, b.Height));
        var c = r.RenderPage(0, new ImageOptions { Width = 1000, Height = 500 });
        Assert.Equal((386, 500), (c.Width, c.Height));
    }

    [Fact]
    public void Transparent_background_leaves_empty_pixels_clear()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>");
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 36, Transparent = true });
        Assert.Equal(0, Pixel(img, 1, 1).A);
        var png = img.ToPng();
        var (w, h, colorType, _) = DecodePng(png);
        Assert.Equal((img.Width, img.Height, 6), (w, h, colorType));
    }

    [Fact]
    public void Png_output_round_trips()
    {
        var pdf = HtmlToPdf.Convert("<h1 style='color:#123456'>PNG</h1><div style='background:linear-gradient(red,blue);height:50pt'></div>");
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 50 });
        var (w, h, colorType, rgba) = DecodePng(img.ToPng());
        Assert.Equal(img.Width, w);
        Assert.Equal(img.Height, h);
        Assert.Equal(2, colorType); // opaque pages are written as RGB
        Assert.Equal(img.Rgba, rgba);
    }

    [Fact]
    public void Renders_patterns_and_shadings()
    {
        var img = PdfRenderer.Open(Fixture("features.pdf")).RenderPage(0, new ImageOptions { Dpi = 72 });
        // Tiling pattern cell: red square at the bottom-left of each 20 pt cell, blue at the top-right.
        AssertColor(Pixel(img, 65, 842 - 765), 255, 0, 0);
        AssertColor(Pixel(img, 55, 842 - 755), 0, 0, 255);
        // Radial shading (gray, extended outward): black outside the outer circle.
        AssertColor(Pixel(img, 560, 842 - 90), 0, 0, 0);
        // Free-form triangle mesh: the first vertex is red.
        AssertColor(Pixel(img, 60, 842 - 474), 255, 0, 0, 40);
    }

    [Fact]
    public void Renders_transparency_and_blend_modes()
    {
        var img = PdfRenderer.Open(Fixture("features.pdf")).RenderPage(1, new ImageOptions { Dpi = 72 });
        AssertColor(Pixel(img, 300, 842 - 575), 0, 153, 0); // plain green bar
        AssertColor(Pixel(img, 380, 842 - 440), 46, 77, 23); // Multiply over orange
        AssertColor(Pixel(img, 100, 842 - 500), 255, 128, 128); // red at 50% alpha
    }

    [Fact]
    public void Renders_type3_fonts_and_ccitt_images()
    {
        var img = PdfRenderer.Open(Fixture("features.pdf")).RenderPage(2, new ImageOptions { Dpi = 72 });
        AssertColor(Pixel(img, 55, 842 - 565), 0, 0, 0); // CCITT image corner
        AssertColor(Pixel(img, 90, 842 - 610), 255, 255, 255); // inside the white ellipse
        AssertColor(Pixel(img, 70, 842 - 725), 0, 0, 0); // Type 3 triangle glyph
    }

    [Fact]
    public void Applies_rotation_crop_box_and_optional_content()
    {
        var r = PdfRenderer.Open(Fixture("features.pdf"));
        var img = r.RenderPage(3, new ImageOptions { Dpi = 72 });
        Assert.Equal((555, 802), (img.Width, img.Height));
        // Rotated 180 degrees: page (x, y) lands at (575 - x, y - 20).
        AssertColor(Pixel(img, 575 - 150, 650 - 20), 0, 128, 0); // visible layer
        AssertColor(Pixel(img, 575 - 400, 650 - 20), 255, 255, 255); // hidden layer
        // Annotation appearance (stamp) is drawn.
        AssertColor(Pixel(img, 575 - 400, 340 - 20), 255, 255, 153);
    }

    [Fact]
    public void Reads_object_streams_and_embedded_fonts()
    {
        var img = PdfRenderer.Open(Fixture("objstm-cairo.pdf")).RenderPage(0, new ImageOptions { Dpi = 72 });
        Assert.Equal((200, 100), (img.Width, img.Height));
        AssertColor(Pixel(img, 150, 70), 0, 0, 255);
        int dark = 0;
        for (int y = 20; y < 45; y++)
            for (int x = 10; x < 100; x++)
                if (Pixel(img, x, y).R < 80) dark++;
        Assert.True(dark > 150, "the word Hello should be drawn");
    }

    [Fact]
    public void Opens_encrypted_files_with_the_password()
    {
        var pdf = Fixture("password.pdf");
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(pdf));
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(pdf, "wrong"));
        var img = PdfRenderer.Open(pdf, "secret").RenderPage(0, new ImageOptions { Dpi = 20 });
        Assert.True(img.Width > 0);
        Assert.Single(PdfToImage.Convert(pdf, new ImageOptions { Password = "owner", Dpi = 10 }));
    }

    [Fact]
    public void Rejects_garbage_and_bad_page_indexes()
    {
        Assert.Throws<InvalidDataException>(() => PdfRenderer.Open(Encoding.ASCII.GetBytes("this is not a pdf")));
        var r = PdfRenderer.Open(HtmlToPdf.Convert("<p>one</p>"));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.RenderPage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.RenderPage(-1));
    }

    [Fact]
    public void Survives_damaged_files()
    {
        var original = HtmlToPdf.Convert("<h1>Damage</h1><p>Some text to render.</p><img src='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='>");
        var rnd = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var b = (byte[])original.Clone();
            for (int k = 0; k < 8; k++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
            if (i % 5 == 0) b = b[..rnd.Next(b.Length / 2, b.Length)];
            try
            {
                var r = PdfRenderer.Open(b);
                for (int p = 0; p < r.PageCount; p++) r.RenderPage(p, new ImageOptions { Dpi = 20 });
            }
            catch (InvalidDataException)
            {
                // Unreadable is fine; anything else is a bug.
            }
        }
    }

    [Fact]
    public void Converts_all_pages_in_order()
    {
        var html = string.Concat(Enumerable.Range(1, 3).Select(i => $"<h1 style='page-break-before:always'>Page {i}</h1>"));
        var pdf = HtmlToPdf.Convert(html);
        var pngs = PdfToImage.Convert(pdf, new ImageOptions { Dpi = 30 });
        Assert.Equal(PdfToImage.GetPageCount(pdf), pngs.Length);
        Assert.Equal(PdfToImage.ConvertPage(pdf, 1, new ImageOptions { Dpi = 30 }), pngs[1]);
    }

    // ------------------------------------------------------------------ minimal PNG decoder for the assertions

    private static (int Width, int Height, int ColorType, byte[] Rgba) DecodePng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int pos = 8, width = 0, height = 0, colorType = 0;
        var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                colorType = data[9];
            }
            else if (type == "IDAT") idat.Write(data);
            pos += 12 + len;
        }
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var bytes = raw.ToArray();
        int bpp = colorType == 6 ? 4 : 3, stride = width * bpp;
        var prev = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int f = bytes[y * (stride + 1)];
            var cur = bytes.AsSpan(y * (stride + 1) + 1, stride).ToArray();
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int pred = f switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                cur[i] = (byte)(cur[i] + pred);
            }
            for (int x = 0; x < width; x++)
            {
                rgba[(y * width + x) * 4] = cur[x * bpp];
                rgba[(y * width + x) * 4 + 1] = cur[x * bpp + 1];
                rgba[(y * width + x) * 4 + 2] = cur[x * bpp + 2];
                rgba[(y * width + x) * 4 + 3] = bpp == 4 ? cur[x * bpp + 3] : (byte)255;
            }
            prev = cur;
        }
        return (width, height, colorType, rgba);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
