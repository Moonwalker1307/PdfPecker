using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker.Tests;

/// <summary>
/// The used size of img elements in boxes narrower or shorter than the image. The expected sizes are Chrome's, in CSS
/// pixels, on a page 666.67 px (500 pt) wide.
/// </summary>
public class ImageSizingTests
{
    private const float Width = 500, PageHeight = 750;

    private static SvgOp Image(string html)
    {
        var doc = HtmlParser.Parse("<!DOCTYPE html><html><body style='margin:0'>" + html + "</body></html>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(new PageGrid(new PageSetup { Width = Width, Height = PageHeight }), new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Assert.Single(All(engine.Ops).OfType<SvgOp>());

        static IEnumerable<DrawOp> All(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                yield return o;
                if (o is GroupOp g)
                    foreach (var c in All(g.Ops)) yield return c;
            }
        }
    }

    /// <summary>An SVG file of the given natural size in pixels, as an img src.</summary>
    private static string Src(int w, int h)
        => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}'><rect width='{w}' height='{h}' fill='red'/></svg>"));

    private static void AssertSize(float w, float h, SvgOp image)
    {
        Assert.Equal(w * 0.75f, image.W, 0.1f);
        Assert.Equal(h * 0.75f, image.H, 0.1f);
    }

    [Theory]
    [InlineData("<div style='width:100px'><img src='{0}'></div>", 400, 250)]
    [InlineData("<div style='width:100px'><img src='{0}' style='display:block'></div>", 400, 250)]
    [InlineData("<div style='width:100px'><img src='{0}' style='width:300px'></div>", 300, 187.5f)]
    [InlineData("<div style='width:100px'><img src='{0}' style='max-width:100%'></div>", 100, 62.5f)]
    public void An_image_wider_than_its_box_overflows_it(string html, float w, float h)
        => AssertSize(w, h, Image(string.Format(html, Src(400, 250))));

    [Fact]
    public void An_image_wider_than_the_page_is_scaled_to_fit_it()
        => AssertSize(666.67f, 166.67f, Image($"<div style='width:100px'><img src='{Src(2000, 500)}'></div>"));

    [Theory]
    [InlineData("max-height:100%", 128, 80)]
    [InlineData("height:100%", 128, 80)]
    [InlineData("max-height:50%", 64, 40)]
    public void A_percentage_height_limits_an_image_in_a_flex_box_of_fixed_height(string style, float w, float h)
        => AssertSize(w, h, Image($"<div style='display:flex;width:300px;height:80px'><img src='{Src(400, 250)}' style='{style}'></div>"));
}
