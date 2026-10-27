namespace PdfPecker.Tests;

/// <summary>HTML inside an inline SVG's foreignObject: laid out, placed through the SVG's transforms and clipped.</summary>
public class SvgForeignObjectTests
{
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

    // An SVG of 400 × 400 px drawn in 300 × 300 pt; At takes CSS pixels of the page.
    private static byte[] Svg(string content, string attributes = "")
        => HtmlToPdf.Convert($"<body style='margin:0'><svg width='400' height='400' style='display:block' {attributes}>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    [Fact]
    public void Html_content_is_drawn_in_the_foreign_object_box()
    {
        var pdf = Svg("<foreignObject x='100' y='50' width='200' height='100'><div style='height:100px;background:#f00'></div></foreignObject>");
        Color(255, 0, 0, At(pdf, 200, 100));
        Color(255, 255, 255, At(pdf, 50, 100));
        Color(255, 255, 255, At(pdf, 200, 200));
    }

    [Fact]
    public void Content_is_clipped_to_the_foreign_object()
    {
        var pdf = Svg("<foreignObject x='0' y='0' width='100' height='100'><div style='width:300px;height:300px;background:#00f'></div></foreignObject>");
        Color(0, 0, 255, At(pdf, 50, 50));
        Color(255, 255, 255, At(pdf, 200, 50));
        Color(255, 255, 255, At(pdf, 50, 200));
    }

    [Fact]
    public void The_view_box_and_transforms_place_the_content()
    {
        // viewBox 0 0 200 200 in 400 px doubles everything; the group then moves it 50 user units right.
        var pdf = Svg("<g transform='translate(50,0)'><foreignObject x='0' y='50' width='50' height='50'>" +
                      "<div style='height:50px;background:#0a0'></div></foreignObject></g>", "viewBox='0 0 200 200'");
        Color(0, 170, 0, At(pdf, 150, 150));
        Color(0, 170, 0, At(pdf, 195, 195));
        Color(255, 255, 255, At(pdf, 50, 150));
        Color(255, 255, 255, At(pdf, 250, 150));
    }

    [Fact]
    public void Shapes_after_the_foreign_object_paint_over_it()
    {
        var pdf = Svg("<rect width='400' height='100' fill='#00f'/>" +
                      "<foreignObject width='400' height='400'><div style='height:200px;background:#f00'></div></foreignObject>" +
                      "<rect y='150' width='400' height='100' fill='#0a0'/>");
        Color(255, 0, 0, At(pdf, 200, 50));
        Color(255, 0, 0, At(pdf, 200, 125));
        Color(0, 170, 0, At(pdf, 200, 175));
    }

    [Fact]
    public void Opacity_applies_to_the_content()
    {
        var pdf = Svg("<foreignObject width='400' height='400' opacity='0.5'><div style='height:200px;background:#000'></div></foreignObject>");
        Color(128, 128, 128, At(pdf, 200, 100));
    }

    [Fact]
    public void An_inline_svg_draws_its_foreign_object_too()
    {
        var pdf = HtmlToPdf.Convert("<body style='margin:0;line-height:0'><span><svg width='200' height='200'>" +
                                    "<foreignObject width='200' height='200'><div style='height:200px;background:#f00'></div></foreignObject>" +
                                    "</svg></span></body>", NoMargins);
        Color(255, 0, 0, At(pdf, 100, 100));
    }

    [Fact]
    public void An_svg_inside_its_own_foreign_object_ends()
    {
        // The use draws the outer svg again, whose foreignObject holds the svg again: laid out to a fixed depth only.
        var html = "<body><svg id='outer' width='200' height='200'><foreignObject width='200' height='200'>" +
                   "<p>text</p><svg width='100' height='100'><use href='#outer'/></svg></foreignObject></svg></body>";
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        Assert.NotEmpty(pdf);
    }
}
