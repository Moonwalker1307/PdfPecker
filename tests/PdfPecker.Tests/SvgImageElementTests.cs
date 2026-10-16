using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>The SVG image element (raster and SVG images, in inline SVG and in SVG files) and SVG watermarks.</summary>
public class SvgImageElementTests
{
    // A 20 × 10 px PNG: red on the left half, blue on the right.
    private static byte[] RedBlue => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", "red-blue.png"));

    private static string PngUri => "data:image/png;base64," + Convert.ToBase64String(RedBlue);

    private static string SvgUri(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

    private static PdfOptions Base(string dir) => new() { Margins = new PageMargins(0), Compress = false, BaseDirectory = dir };

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    /// <summary>The RGB of a pixel of the first page rendered at 72 dpi (1 px = 1 pt).</summary>
    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual)
        => Assert.True(Math.Abs(actual.R - r) < 40 && Math.Abs(actual.G - g) < 40 && Math.Abs(actual.B - b) < 40,
            $"Expected ({r}, {g}, {b}), got {actual}");

    // An SVG of 400 × 400 px drawn in 300 × 300 pt; At takes user units.
    private static byte[] Svg(string content)
        => HtmlToPdf.Convert($"<body style='margin:0'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    [Fact]
    public void A_raster_image_fills_its_viewport()
    {
        var pdf = Svg($"<image href='{PngUri}' x='0' y='0' width='400' height='200' preserveAspectRatio='none'/>");
        Color(255, 0, 0, At(pdf, 100, 100));
        Color(0, 0, 255, At(pdf, 300, 100));
        Color(255, 255, 255, At(pdf, 200, 300));
    }

    [Fact]
    public void The_image_keeps_its_aspect_ratio_centred_by_default()
    {
        // 2:1 into a square: 400 × 200, centred vertically from y = 100.
        var pdf = Svg($"<image xlink:href='{PngUri}' width='400' height='400'/>");
        Color(255, 255, 255, At(pdf, 100, 50));
        Color(255, 0, 0, At(pdf, 100, 200));
        Color(0, 0, 255, At(pdf, 300, 200));
        Color(255, 255, 255, At(pdf, 300, 350));
    }

    [Fact]
    public void Slice_fills_the_viewport_and_is_clipped_to_it()
    {
        // Scaled to 400 × 200 high: 800 wide, centred, so the viewport (0..200) shows only the middle of the image,
        // red to x = 100 and blue after; nothing is drawn outside the viewport.
        var pdf = Svg($"<image href='{PngUri}' x='100' y='100' width='200' height='200' preserveAspectRatio='xMidYMid slice'/>");
        Color(255, 0, 0, At(pdf, 150, 200));
        Color(0, 0, 255, At(pdf, 250, 200));
        Color(255, 255, 255, At(pdf, 50, 200));
        Color(255, 255, 255, At(pdf, 350, 200));
    }

    [Fact]
    public void Width_and_height_default_to_the_image_size()
    {
        var pdf = Svg($"<image href='{PngUri}' transform='scale(10)'/>"); // 20 × 10 user units, scaled to 200 × 100
        Color(255, 0, 0, At(pdf, 50, 50));
        Color(0, 0, 255, At(pdf, 150, 50));
        Color(255, 255, 255, At(pdf, 250, 50));
    }

    [Fact]
    public void An_image_drawn_twice_is_written_once()
    {
        var pdf = Svg($"<image href='{PngUri}' width='100' height='50'/><image href='{PngUri}' y='100' width='100' height='50'/>");
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Single(Regex.Matches(text, @"/Subtype /Image /Width 20 "));
        Assert.Equal(2, Regex.Matches(text, @"/Xr1 Do").Count);
    }

    [Fact]
    public void An_svg_image_is_drawn_as_vectors_and_clipped_to_its_viewport()
    {
        var inner = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10' fill='#0a0'/><rect x='-50' width='50' height='10' fill='red'/></svg>";
        var pdf = Svg($"<image href='{SvgUri(inner)}' x='100' y='100' width='200' height='200'/>");
        Color(0, 170, 0, At(pdf, 200, 200));
        Color(255, 255, 255, At(pdf, 50, 200)); // the red rectangle is outside the drawing's viewport
        Assert.DoesNotContain("/Subtype /Image", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void A_translucent_svg_image_is_one_group()
    {
        // Two overlapping black squares at opacity 0.5: as a group the overlap is the same grey as the rest.
        var inner = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 10'><rect width='15' height='10'/><rect x='5' width='15' height='10'/></svg>";
        var pdf = Svg($"<image href='{SvgUri(inner)}' width='400' height='200' opacity='0.5'/>");
        var edge = At(pdf, 20, 100);
        var middle = At(pdf, 200, 100);
        Color(128, 128, 128, edge);
        Assert.Equal(edge, middle);
    }

    [Fact]
    public void An_svg_file_draws_images_in_data_uris_only()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "red-blue.png"), RedBlue);
            var logo = $"<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='200' height='200'>"
                + $"<image xlink:href='{PngUri}' width='200' height='100' preserveAspectRatio='none'/>"
                + "<image href='red-blue.png' y='100' width='200' height='100' preserveAspectRatio='none'/></svg>";
            File.WriteAllText(Path.Combine(dir.FullName, "logo.svg"), logo);
            var pdf = HtmlToPdf.Convert("<body style='margin:0'><img src='logo.svg' style='display:block'></body>", Base(dir.FullName));
            Color(255, 0, 0, Pixel(pdf, 30, 30));
            Color(0, 0, 255, Pixel(pdf, 120, 30));
            // Like a browser, the file's drawing loads nothing outside it.
            Color(255, 255, 255, Pixel(pdf, 30, 120));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Inline_svg_loads_images_like_an_img_element()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "red-blue.png"), RedBlue);
            var pdf = HtmlToPdf.Convert("<body style='margin:0'><svg width='400' height='200' style='display:block'>"
                + "<image href='red-blue.png' width='400' height='200'/></svg></body>", Base(dir.FullName));
            Color(255, 0, 0, Pixel(pdf, 75, 75));
            Color(0, 0, 255, Pixel(pdf, 225, 75));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void An_svg_file_embedding_itself_does_not_loop()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "self.svg"),
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'><rect width='50' height='50' fill='red'/><image href='self.svg' width='100' height='100'/></svg>");
            var pdf = HtmlToPdf.Convert("<body style='margin:0'><svg width='100' height='100'><image href='self.svg' width='100' height='100'/></svg></body>",
                Base(dir.FullName));
            Color(255, 0, 0, Pixel(pdf, 10, 10));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void An_svg_watermark_stays_vector_graphics()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 50'><rect width='100' height='50' fill='#00f'/><text x='5' y='30'>DRAFT</text></svg>";
        var options = new PdfOptions
        {
            Margins = new PageMargins(0), Compress = false,
            Watermark = new PdfWatermark { Image = Encoding.UTF8.GetBytes(svg), Opacity = 0.5f },
        };
        var pdf = HtmlToPdf.Convert("<p>Text</p>", options);
        var text = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("/Subtype /Image", text);
        Assert.Contains("(DRAFT)", text);
        // Half the page wide (A4: 297.6 pt), centred, at opacity 0.5: light blue.
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int cx = img.Width / 2, cy = img.Height / 2;
        int i = (cy * img.Width + cx - 60) * 4;
        Color(128, 128, 255, (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]));
        i = (cy * img.Width + 10) * 4;
        Color(255, 255, 255, (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]));
    }

    [Fact]
    public void A_watermark_that_is_no_image_is_refused()
    {
        var options = new PdfOptions { Watermark = new PdfWatermark { Image = Encoding.UTF8.GetBytes("not an image") } };
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", options));
    }
}
