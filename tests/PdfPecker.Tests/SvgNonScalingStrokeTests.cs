using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Svg;

namespace PdfPecker.Tests;

/// <summary>vector-effect: non-scaling-stroke, which keeps a stroke at the width it has on the page however the user space is scaled.</summary>
public class SvgNonScalingStrokeTests
{
    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    private static SvgShape[] Shapes(string svgAttributes, string content)
    {
        var doc = HtmlParser.Parse($"<svg id='s' {svgAttributes}>{content}</svg>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var svg = Descendants(doc).First(e => e.Id == "s");
        return SvgImage.Build(svg).Shapes.ToArray();
    }

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>Whether the pixel at (xPt, yPt) of the first page, rendered at 288 dpi (4 px = 1 pt), is red.</summary>
    private static bool Red(byte[] pdf, float xPt, float yPt)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 288 });
        int i = ((int)(yPt * 4) * img.Width + (int)(xPt * 4)) * 4;
        return img.Rgba[i] > 200 && img.Rgba[i + 1] < 80 && img.Rgba[i + 2] < 80;
    }

    [Fact]
    public void The_stroke_width_is_a_page_length_and_not_scaled_with_the_user_space()
    {
        var shapes = Shapes("width='200' height='100' viewBox='0 0 50 25'",
            "<line x2='50' stroke='red' stroke-width='2' vector-effect='non-scaling-stroke'/>"
            + "<line x2='50' stroke='red' stroke-width='2'/>"
            + "<line x2='50' stroke='red' stroke-width='2' vector-effect='none'/>");
        Assert.True(shapes[0].NonScalingStroke);
        Assert.Equal(1.5f, shapes[0].StrokeWidth, 3); // 2 CSS px, in points
        Assert.False(shapes[1].NonScalingStroke);
        Assert.Equal(2f, shapes[1].StrokeWidth, 3); // user units, scaled with the viewBox when drawn
        Assert.False(shapes[2].NonScalingStroke);
    }

    [Fact]
    public void Transforms_of_the_element_and_its_ancestors_do_not_scale_the_stroke()
    {
        var shape = Shapes("width='200' height='100'",
            "<g transform='scale(3)'><line x2='10' stroke='red' stroke-width='2' vector-effect='non-scaling-stroke' transform='scale(2)'/></g>").Single();
        Assert.Equal(1.5f, shape.StrokeWidth, 3);
        Assert.Equal(60f, shape.MaxX, 3); // the geometry is still scaled
    }

    [Fact]
    public void Dashes_keep_their_length_too()
    {
        var shape = Shapes("width='200' height='100' viewBox='0 0 50 25'",
            "<line x2='50' stroke='red' stroke-dasharray='4 2' stroke-dashoffset='1' style='vector-effect:non-scaling-stroke'/>").Single();
        Assert.Equal(new[] { 3f, 1.5f }, shape.Dash);
        Assert.Equal(0.75f, shape.DashPhase, 3);
    }

    [Fact]
    public void The_property_is_not_inherited_and_the_last_declaration_wins()
    {
        var shapes = Shapes("width='200' height='100'",
            "<g style='vector-effect:non-scaling-stroke'><line x2='50' stroke='red'/></g>"
            + "<line x2='50' stroke='red' vector-effect='non-scaling-stroke' style='vector-effect:none'/>"
            + "<line x2='50' stroke='red' style='vector-effect:non-scaling-stroke non-rotation'/>");
        Assert.False(shapes[0].NonScalingStroke);
        Assert.False(shapes[1].NonScalingStroke);
        Assert.True(shapes[2].NonScalingStroke);
    }

    [Fact]
    public void A_scaled_chart_keeps_its_hairline_in_the_pdf()
    {
        // 50 x 25 units in 200 x 100 px: a user unit is 3 pt. The plain line is 3 pt thick, the non-scaling one 0.75 pt.
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><svg width='200' height='100' viewBox='0 0 50 25' style='display:block'>"
            + "<line x1='0' y1='5' x2='50' y2='5' stroke='red'/>"
            + "<line x1='0' y1='15' x2='50' y2='15' stroke='red' vector-effect='non-scaling-stroke'/></svg></body>", NoMargins);
        Assert.True(Red(pdf, 75, 15), "the scaled stroke covers 13.5 to 16.5 pt");
        Assert.True(Red(pdf, 75, 14), "the scaled stroke covers 13.5 to 16.5 pt");
        Assert.True(Red(pdf, 75, 45), "the hairline is centred on 45 pt");
        Assert.False(Red(pdf, 75, 44.2f));
        Assert.False(Red(pdf, 75, 45.8f));
    }

    [Fact]
    public void A_stretched_viewBox_gives_an_even_stroke()
    {
        // 50 x 100 units stretched to 200 x 100 px: 4 times wider than high. Both lines keep 3 pt (4 px at 0.75).
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><svg width='200' height='100' viewBox='0 0 50 100' preserveAspectRatio='none' style='display:block'>"
            + "<path d='M20 5V95M5 50H45' stroke='red' stroke-width='4' vector-effect='non-scaling-stroke'/></svg></body>", NoMargins);
        Assert.True(Red(pdf, 60 + 1.2f, 20) && Red(pdf, 60 - 1.2f, 20), "the vertical line is 3 pt wide");
        Assert.False(Red(pdf, 60 + 2f, 20) || Red(pdf, 60 - 2f, 20));
        Assert.True(Red(pdf, 20, 37.5f + 1.2f) && Red(pdf, 20, 37.5f - 1.2f), "the horizontal line is 3 pt high");
        Assert.False(Red(pdf, 20, 37.5f + 2f) || Red(pdf, 20, 37.5f - 2f));
    }

    [Fact]
    public void A_filtered_stroke_keeps_its_width()
    {
        // 30 x 10 units in 300 x 100 px: a unit is 7.5 pt, and the stroke 3 pt, as without the filter.
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><svg width='300' height='100' viewBox='0 0 30 10' style='display:block'>"
            + "<filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='30' height='10'><feOffset dx='0' dy='0'/></filter>"
            + "<path d='M2 5H28' stroke='red' stroke-width='4' vector-effect='non-scaling-stroke' filter='url(#f)'/></svg></body>", NoMargins);
        Assert.True(Red(pdf, 100, 37.5f + 0.6f) && Red(pdf, 100, 37.5f - 0.6f), "about 3 pt wide, not 0.75 pt");
        Assert.False(Red(pdf, 100, 37.5f + 2.4f) || Red(pdf, 100, 37.5f - 2.4f));
    }

    [Fact]
    public void A_stroke_in_a_pattern_tile_keeps_its_width()
    {
        // 2 px is 1.5 pt, not the 2 units of 7.5 pt (15 pt) the pattern and the viewBox would make it.
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><svg width='300' height='100' viewBox='0 0 30 10' style='display:block'>"
            + "<pattern id='p' width='10' height='10' patternUnits='userSpaceOnUse'><path d='M0 5H10' stroke='red' stroke-width='2' vector-effect='non-scaling-stroke'/></pattern>"
            + "<rect width='30' height='10' fill='url(#p)'/></svg></body>", NoMargins);
        Assert.True(Red(pdf, 100, 37.5f + 0.5f) && Red(pdf, 100, 37.5f - 0.5f));
        Assert.False(Red(pdf, 100, 37.5f + 1.5f) || Red(pdf, 100, 37.5f - 1.5f));
    }
}
