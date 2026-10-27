namespace PdfPecker.Tests;

/// <summary>context-fill and context-stroke: the paint of a use element or of the shape a marker is on.</summary>
public class SvgContextPaintTests
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

    // An SVG of 400 × 400 px drawn in 300 × 300 pt; At takes user units.
    private static byte[] Svg(string content)
        => HtmlToPdf.Convert($"<body style='margin:0'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    [Fact]
    public void Each_use_paints_the_shape_with_its_own_fill_and_stroke()
    {
        var pdf = Svg("<defs><g id='b'><rect width='100' height='100' fill='context-fill' stroke='context-stroke' stroke-width='20'/></g></defs>" +
                      "<use href='#b' x='20' y='20' fill='#f00' stroke='#00f'/><use href='#b' x='220' y='20' fill='#0a0' stroke='#000'/>");
        Color(255, 0, 0, At(pdf, 70, 70));
        Color(0, 0, 255, At(pdf, 22, 70));
        Color(0, 170, 0, At(pdf, 270, 70));
        Color(0, 0, 0, At(pdf, 222, 70));
    }

    [Fact]
    public void Without_a_context_element_context_paint_is_none()
    {
        var pdf = Svg("<rect width='100' height='100' fill='context-fill'/>");
        Color(255, 255, 255, At(pdf, 50, 50));
    }

    [Fact]
    public void A_marker_paints_with_the_shape_it_is_on()
    {
        var pdf = Svg("<defs><marker id='m' markerWidth='40' markerHeight='40' refX='20' refY='20' markerUnits='userSpaceOnUse'>" +
                      "<rect width='40' height='40' fill='context-stroke'/></marker></defs>" +
                      "<path d='M50,100 L300,100' stroke='#00f' stroke-width='4' marker-end='url(#m)'/>");
        Color(0, 0, 255, At(pdf, 310, 90));
    }

    [Fact]
    public void A_gradient_context_paint_spans_the_whole_use()
    {
        // The use's bounds are both halves: the gradient's left half falls on the grey half, so the right one starts mid-way.
        var pdf = Svg("<defs><linearGradient id='g'><stop offset='0' stop-color='#fff'/><stop offset='1' stop-color='#000'/></linearGradient>" +
                      "<g id='p'><rect width='100' height='100' fill='#ccc'/><rect x='100' width='100' height='100' fill='context-fill'/></g></defs>" +
                      "<use href='#p' fill='url(#g)'/>");
        Color(128, 128, 128, At(pdf, 102, 50));
        Color(0, 0, 0, At(pdf, 198, 50));
    }

    [Fact]
    public void A_use_sizes_the_nested_svg_it_draws()
    {
        var pdf = Svg("<defs><svg id='s' viewBox='0 0 10 10' width='10' height='10'><rect width='10' height='10' fill='context-fill'/></svg></defs>" +
                      "<use href='#s' width='200' height='200' fill='#f00'/>");
        Color(255, 0, 0, At(pdf, 190, 190));
        Color(255, 255, 255, At(pdf, 210, 210));
    }
}
