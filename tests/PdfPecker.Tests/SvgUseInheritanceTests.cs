namespace PdfPecker.Tests;

/// <summary>
/// What a use element draws inherits from the use element, as its copy in the use element's shadow tree does, not
/// from where it is defined. MathJax draws every glyph of a formula with use, under a group painted currentColor.
/// </summary>
public class SvgUseInheritanceTests
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
    private static byte[] Svg(string content, string style = "")
        => HtmlToPdf.Convert($"<body style='margin:0;{style}'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    [Fact]
    public void A_shape_without_paint_takes_the_fill_of_the_use_element()
    {
        var pdf = Svg("<defs><rect id='r' width='100' height='100'/></defs><g fill='#f00'><use href='#r'/></g><use href='#r' x='200' fill='#00f'/>");
        Color(255, 0, 0, At(pdf, 50, 50));
        Color(0, 0, 255, At(pdf, 250, 50));
    }

    [Fact]
    public void Current_color_reaches_glyphs_drawn_by_use()
    {
        // The shape of MathJax's SVG output: glyph paths in defs, drawn by use elements under a currentColor group.
        var pdf = Svg("<defs><path id='g' d='M0 0H100V100H0Z'/></defs><g fill='currentColor' stroke='currentColor' stroke-width='0'>"
            + "<g transform='translate(150,0)'><use xlink:href='#g'/></g></g>", "color:#0a0");
        Color(0, 170, 0, At(pdf, 200, 50));
    }

    [Fact]
    public void The_paint_where_a_shape_is_defined_does_not_reach_its_use()
    {
        var pdf = Svg("<defs fill='#f00'><g fill='#0f0'><rect id='r' width='100' height='100'/></g></defs><use href='#r' fill='#00f'/>");
        Color(0, 0, 255, At(pdf, 50, 50));
    }

    [Fact]
    public void A_shape_keeps_its_own_fill_and_its_children_theirs()
    {
        var pdf = Svg("<defs><rect id='own' width='100' height='100' fill='#0a0'/>"
            + "<g id='group' fill='#f00'><rect width='100' height='100'/></g>"
            + "<symbol id='s'><rect width='100' height='100'/></symbol></defs>"
            + "<use href='#own' fill='#00f'/><use href='#group' x='150' fill='#00f'/><use href='#s' y='150' width='100' height='100' fill='#00f'/>");
        Color(0, 170, 0, At(pdf, 50, 50));
        Color(255, 0, 0, At(pdf, 200, 50));
        Color(0, 0, 255, At(pdf, 50, 200));
    }

    [Fact]
    public void A_use_inside_a_used_group_inherits_from_its_own_use()
    {
        var pdf = Svg("<defs><rect id='r' width='100' height='100'/><g id='twice'><use href='#r'/><use href='#r' x='150' fill='#0a0'/></g></defs>"
            + "<use href='#twice' fill='#00f'/>");
        Color(0, 0, 255, At(pdf, 50, 50));
        Color(0, 170, 0, At(pdf, 200, 50));
    }
}
