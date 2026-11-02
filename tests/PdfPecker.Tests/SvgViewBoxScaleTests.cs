namespace PdfPecker.Tests;

/// <summary>
/// The scale that maps an inline SVG's viewBox to its box. Formula renderers such as MathJax draw in thousands of user
/// units per em, so a scale written with too few decimals moves the right edge of the drawing out of its box.
/// </summary>
public class SvgViewBoxScaleTests
{
    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    /// <summary>The RGB of a pixel of the first page rendered at 288 dpi (4 px = 1 pt).</summary>
    private static (int R, int G, int B) Pixel(byte[] pdf, float xPt, float yPt)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 288 });
        int i = ((int)(yPt * 4) * img.Width + (int)(xPt * 4)) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual)
        => Assert.True(Math.Abs(actual.R - r) < 40 && Math.Abs(actual.G - g) < 40 && Math.Abs(actual.B - b) < 40,
            $"Expected ({r}, {g}, {b}), got {actual}");

    // A red viewBox with a blue strip along its right edge, the last 2.8% of its width.
    private const string Strip = "<rect x='0' y='-1019.2' width='7099.8' height='1372' fill='red'/>"
        + "<rect x='6900' y='-1019.2' width='199.8' height='1372' fill='blue'/>";

    [Fact]
    public void A_large_view_box_keeps_its_right_edge_inside_the_box()
    {
        // 147.45 x 28.48 px is 110.59 x 21.36 pt, a scale of 0.015577 that three decimals would round to 0.016 and so
        // push the blue strip past the clip.
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><svg width='147.45' height='28.48' viewBox='0 -1019.2 7099.8 1372' style='display:block'>"
            + Strip + "</svg></body>", NoMargins);
        Color(0, 0, 255, Pixel(pdf, 109.8f, 10));
        Color(255, 0, 0, Pixel(pdf, 100, 10));
    }

    [Fact]
    public void A_formula_sized_in_ex_units_keeps_its_right_edge()
    {
        // MathJax's SVG output: a size in ex of the surrounding font and a viewBox of about 1,000 units per em.
        var pdf = HtmlToPdf.Convert("<body style='margin:0;font:14pt Liberation Serif'><svg width='16.063ex' height='3.104ex' "
            + "viewBox='0 -1019.2 7099.8 1372' style='display:block'>" + Strip + "</svg></body>", NoMargins);
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 288 });
        int row = 20 * img.Width * 4, blue = 0;
        for (int x = 0; x < img.Width; x++)
        {
            int i = row + x * 4;
            if (img.Rgba[i] < 60 && img.Rgba[i + 2] > 200) blue++;
        }
        // The strip is 2.8% of the drawing, about 2.3 pt or 9 px here; clipped, almost none of it shows.
        Assert.True(blue >= 7, $"Only {blue} px of the right edge are drawn");
    }
}
